//! Live audio session: devices -> engine lanes -> sinks. Compile-checked only (Linux/ALSA and Windows-gnu); never run
//! against real audio devices in this repo's sandbox.
//!
//! Mic lane:     capture device -> `Lane::Mic` -> MicyWMD virtual microphone (or a named render device, or discarded).
//! Speaker lane: loopback capture of a render device -> `Lane::Speaker` -> a different render device.
//!
//! The speaker lane captures what a render device *plays*. If the capture device were also the playback device, the
//! processed audio would be captured again and loop; `start` refuses that. In practice it needs a virtual speaker
//! (applications play into it; we capture it and play the cleaned audio to the real speakers).
//!
//! All cpal streams are created and dropped on one dedicated thread (cpal streams are not `Send` on every platform).

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, mpsc};
use std::thread::JoinHandle;
use std::time::Duration;

use anyhow::{Result, anyhow, bail};
use live::devices::{self, IoStats};
use live::{BlockProcessor, Worker, ring};
use virtualmic::convert::SampleFormat;

use crate::{Engine, Lane};

#[derive(Clone, Debug)]
pub enum MicSink {
    /// Capture nothing; the mic lane is off.
    Discard,
    /// The MicyWMD virtual microphone (Windows only), written as PCM in the given format.
    Micy(SampleFormat),
    /// A render device by name (`None` = default output).
    Output(Option<String>),
}

#[derive(Clone, Debug)]
pub struct SessionConfig {
    /// Capture device for the mic lane (`None` = default input).
    pub mic_input: Option<String>,
    pub mic_sink: MicSink,
    /// Render device to loop back for the incoming-audio lane; `None` turns that lane off.
    pub speaker_loopback: Option<String>,
    /// Where the cleaned incoming audio is played (`None` = default output).
    pub speaker_output: Option<String>,
}

#[derive(Clone, Copy, Debug, Default)]
pub struct SessionStats {
    pub running: bool,
    pub mic_input_dropped: u64,
    pub mic_output_underrun_frames: u64,
    pub speaker_input_dropped: u64,
    pub speaker_output_underrun_frames: u64,
    pub mic_worker_max_block_us: u64,
    pub speaker_worker_max_block_us: u64,
}

struct LaneProcessor {
    engine: Arc<Engine>,
    lane: Lane,
}

impl BlockProcessor for LaneProcessor {
    fn process(&mut self, input: &[f32], out: &mut Vec<f32>) -> Result<()> {
        self.engine.process(self.lane, input, out)
    }
}

pub struct Session {
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
    mic_io: Arc<IoStats>,
    speaker_io: Arc<IoStats>,
    mic_worker: Arc<live::Stats>,
    speaker_worker: Arc<live::Stats>,
}

/// Everything that must stay alive while the session runs; dropped on the audio thread.
struct Running {
    _inputs: Vec<devices::Input>,
    _outputs: Vec<devices::Output>,
    workers: Vec<Worker>,
    pump: Option<JoinHandle<()>>,
}

impl Session {
    pub fn start(engine: Arc<Engine>, cfg: SessionConfig) -> Result<Session> {
        if engine.sample_rate() != devices::ENGINE_RATE {
            bail!(
                "the engine runs at {} Hz; live audio needs the 48 kHz model",
                engine.sample_rate()
            );
        }
        if matches!(cfg.mic_sink, MicSink::Discard) && cfg.speaker_loopback.is_none() {
            bail!("nothing to run: the mic sink is discarded and the incoming-audio lane is off");
        }
        check_no_feedback(&cfg)?;

        let stop = Arc::new(AtomicBool::new(false));
        let (mic_io, speaker_io) = (Arc::new(IoStats::default()), Arc::new(IoStats::default()));
        let (mic_worker, speaker_worker) = (
            Arc::new(live::Stats::default()),
            Arc::new(live::Stats::default()),
        );
        let (ready_tx, ready_rx) = mpsc::channel::<std::result::Result<(), String>>();

        let thread = {
            let (stop, mic_io, speaker_io, mic_w, spk_w) = (
                stop.clone(),
                mic_io.clone(),
                speaker_io.clone(),
                mic_worker.clone(),
                speaker_worker.clone(),
            );
            std::thread::Builder::new()
                .name("openkrisp-audio".into())
                .spawn(move || {
                    match build(&engine, &cfg, &stop, &mic_io, &speaker_io, &mic_w, &spk_w) {
                        Err(e) => {
                            let _ = ready_tx.send(Err(format!("{e:#}")));
                        }
                        Ok(running) => {
                            let _ = ready_tx.send(Ok(()));
                            while !stop.load(Ordering::Relaxed) {
                                std::thread::sleep(Duration::from_millis(50));
                            }
                            // streams drop first (no more input), then workers, then the pump
                            let Running {
                                _inputs,
                                _outputs,
                                workers,
                                pump,
                            } = running;
                            drop(_inputs);
                            for w in workers {
                                let _ = w.stop();
                            }
                            drop(_outputs);
                            if let Some(p) = pump {
                                let _ = p.join();
                            }
                        }
                    }
                })?
        };
        match ready_rx.recv_timeout(Duration::from_secs(15)) {
            Ok(Ok(())) => Ok(Session {
                stop,
                thread: Some(thread),
                mic_io,
                speaker_io,
                mic_worker,
                speaker_worker,
            }),
            Ok(Err(msg)) => {
                let _ = thread.join();
                Err(anyhow!(msg))
            }
            Err(_) => {
                stop.store(true, Ordering::Relaxed);
                Err(anyhow!("timed out opening the audio devices"))
            }
        }
    }

    pub fn stats(&self) -> SessionStats {
        let o = Ordering::Relaxed;
        SessionStats {
            running: !self.stop.load(o),
            mic_input_dropped: self.mic_io.input_dropped.load(o),
            mic_output_underrun_frames: self.mic_io.output_underrun_frames.load(o),
            speaker_input_dropped: self.speaker_io.input_dropped.load(o),
            speaker_output_underrun_frames: self.speaker_io.output_underrun_frames.load(o),
            mic_worker_max_block_us: self.mic_worker.max_block_us.load(o),
            speaker_worker_max_block_us: self.speaker_worker.max_block_us.load(o),
        }
    }

    pub fn stop(mut self) {
        self.shutdown();
    }

    fn shutdown(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        self.shutdown();
    }
}

/// Refuses configurations where the loopback source is also the playback device (the cleaned audio would be captured again).
fn check_no_feedback(cfg: &SessionConfig) -> Result<()> {
    let Some(src) = cfg.speaker_loopback.as_deref() else {
        return Ok(());
    };
    match cfg.speaker_output.as_deref() {
        Some(out) if out == src => bail!(
            "capture and playback are the same device (\"{src}\"): the cleaned audio would loop. Choose different devices"
        ),
        _ => Ok(()),
    }
}

fn build(
    engine: &Arc<Engine>,
    cfg: &SessionConfig,
    stop: &Arc<AtomicBool>,
    mic_io: &Arc<IoStats>,
    speaker_io: &Arc<IoStats>,
    mic_w: &Arc<live::Stats>,
    spk_w: &Arc<live::Stats>,
) -> Result<Running> {
    let mut run = Running {
        _inputs: Vec::new(),
        _outputs: Vec::new(),
        workers: Vec::new(),
        pump: None,
    };

    if !matches!(cfg.mic_sink, MicSink::Discard) {
        let (in_tx, in_rx) = ring(48000);
        let (out_tx, out_rx) = ring(48000);
        // open the sink first so a missing driver/device fails before anything is capturing
        match &cfg.mic_sink {
            MicSink::Output(name) => run._outputs.push(devices::open_output(
                name.as_deref(),
                out_rx,
                mic_io.clone(),
            )?),
            MicSink::Micy(format) => {
                run.pump = Some(spawn_micy_pump(out_rx, *format, stop.clone())?)
            }
            MicSink::Discard => unreachable!(),
        }
        let worker = Worker::spawn_with_stats(
            LaneProcessor {
                engine: engine.clone(),
                lane: Lane::Mic,
            },
            in_rx,
            out_tx,
            0,
            mic_w.clone(),
        );
        run.workers.push(worker);
        run._inputs.push(devices::open_input(
            cfg.mic_input.as_deref(),
            false,
            in_tx,
            mic_io.clone(),
        )?);
    }

    if let Some(src) = cfg.speaker_loopback.as_deref() {
        let (in_tx, in_rx) = ring(48000);
        let (out_tx, out_rx) = ring(48000);
        run._outputs.push(devices::open_output(
            cfg.speaker_output.as_deref(),
            out_rx,
            speaker_io.clone(),
        )?);
        let worker = Worker::spawn_with_stats(
            LaneProcessor {
                engine: engine.clone(),
                lane: Lane::Speaker,
            },
            in_rx,
            out_tx,
            960,
            spk_w.clone(),
        );
        run.workers.push(worker);
        run._inputs.push(devices::open_input(
            Some(src),
            true,
            in_tx,
            speaker_io.clone(),
        )?);
    }
    Ok(run)
}

#[cfg(windows)]
fn spawn_micy_pump(
    mut rx: live::Rx,
    format: SampleFormat,
    stop: Arc<AtomicBool>,
) -> Result<JoinHandle<()>> {
    use live::traits::{Consumer, Observer};
    use virtualmic::convert::append_pcm;
    let dev = virtualmic::driver::MicyDevice::open()?; // fails here (not in the thread) if the driver is missing
    Ok(std::thread::Builder::new()
        .name("openkrisp-micy".into())
        .spawn(move || {
            let (mut block, mut bytes) = (vec![0.0f32; live::BLOCK], Vec::new());
            while !stop.load(Ordering::Relaxed) {
                if rx.occupied_len() < live::BLOCK {
                    std::thread::sleep(Duration::from_millis(1));
                    continue;
                }
                rx.pop_slice(&mut block);
                bytes.clear();
                append_pcm(&block, format, 2, &mut bytes); // the endpoint advertises 2 channels
                if dev.send(&bytes).is_err() {
                    std::thread::sleep(Duration::from_millis(10));
                }
            }
        })?)
}

#[cfg(not(windows))]
fn spawn_micy_pump(
    _rx: live::Rx,
    _format: SampleFormat,
    _stop: Arc<AtomicBool>,
) -> Result<JoinHandle<()>> {
    bail!("the MicyWMD virtual microphone is only available on Windows")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn cfg(loopback: Option<&str>, output: Option<&str>) -> SessionConfig {
        SessionConfig {
            mic_input: None,
            mic_sink: MicSink::Discard,
            speaker_loopback: loopback.map(String::from),
            speaker_output: output.map(String::from),
        }
    }

    #[test]
    fn refuses_capture_and_playback_on_the_same_device() {
        let e = check_no_feedback(&cfg(Some("Speakers"), Some("Speakers"))).unwrap_err();
        assert!(e.to_string().contains("would loop"), "{e}");
    }

    #[test]
    fn allows_different_devices_and_a_default_output() {
        assert!(check_no_feedback(&cfg(Some("Virtual Speaker"), Some("Speakers"))).is_ok());
        assert!(check_no_feedback(&cfg(Some("Virtual Speaker"), None)).is_ok());
        assert!(check_no_feedback(&cfg(None, Some("Speakers"))).is_ok());
    }

    #[test]
    fn start_refuses_when_there_is_nothing_to_run_before_touching_any_device() {
        let Some(model) = std::env::var_os("OPENKRISP_MODEL") else {
            eprintln!("SKIPPED: set OPENKRISP_MODEL to dpdfnet2_48khz_hr.onnx");
            return;
        };
        let engine = Arc::new(Engine::new(model, 48000).unwrap());
        let err = Session::start(engine, cfg(None, None))
            .err()
            .expect("must refuse");
        assert!(err.to_string().contains("nothing to run"), "{err}");
    }
}
