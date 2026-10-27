//! Real-time plumbing, split in two so most of it is testable without audio hardware:
//! - [`Worker`]: a thread that pulls 10 ms blocks from an input ring, runs the [`pipeline::Pipeline`], and pushes
//!   the result to an output ring. Device callbacks only touch the rings (no locks, no allocation in this crate).
//! - `devices` (feature `audio`): cpal input/output streams that feed those rings.
//!
//! Inference (tract) allocates, so it must stay on the worker thread, never in a device callback.

#[cfg(feature = "audio")]
pub mod devices;

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::thread::JoinHandle;
use std::time::Duration;

use anyhow::{Result, anyhow};
use pipeline::{Pipeline, Vad};
use ringbuf::traits::{Consumer, Observer, Producer, Split};
use ringbuf::{HeapCons, HeapProd, HeapRb};

pub const BLOCK: usize = 480; // 10 ms at 48 kHz

pub type Tx = HeapProd<f32>;
pub type Rx = HeapCons<f32>;

/// A mono ring of `capacity` samples.
pub fn ring(capacity: usize) -> (Tx, Rx) {
    HeapRb::<f32>::new(capacity).split()
}

#[derive(Default)]
pub struct Stats {
    /// Blocks processed.
    pub blocks: AtomicU64,
    /// Samples the output ring had no room for (consumer too slow).
    pub output_dropped: AtomicU64,
    /// Longest time spent processing one block, in microseconds.
    pub max_block_us: AtomicU64,
}

pub struct Worker {
    stop: Arc<AtomicBool>,
    handle: Option<JoinHandle<Result<()>>>,
    pub stats: Arc<Stats>,
}

impl Worker {
    /// `input` supplies 48 kHz mono samples; processed samples go to `output`. The output ring is first primed
    /// with `prefill_samples` of silence so a playback callback does not start dry.
    pub fn spawn<V: Vad + Send + 'static>(
        mut pipeline: Pipeline<V>,
        mut input: Rx,
        mut output: Tx,
        prefill_samples: usize,
    ) -> Worker {
        let stop = Arc::new(AtomicBool::new(false));
        let stats = Arc::new(Stats::default());
        let (s, st) = (stop.clone(), stats.clone());
        let handle = std::thread::Builder::new()
            .name("openkrisp-worker".into())
            .spawn(move || -> Result<()> {
                let silence = vec![0.0f32; prefill_samples];
                output.push_slice(&silence);
                let (mut block, mut out) = (vec![0.0f32; BLOCK], Vec::with_capacity(BLOCK * 2));
                while !s.load(Ordering::Relaxed) {
                    if input.occupied_len() < BLOCK {
                        std::thread::sleep(Duration::from_millis(1));
                        continue;
                    }
                    input.pop_slice(&mut block);
                    let t = std::time::Instant::now();
                    out.clear();
                    pipeline.process(&block, &mut out)?;
                    let pushed = output.push_slice(&out);
                    st.output_dropped
                        .fetch_add((out.len() - pushed) as u64, Ordering::Relaxed);
                    st.max_block_us
                        .fetch_max(t.elapsed().as_micros() as u64, Ordering::Relaxed);
                    st.blocks.fetch_add(1, Ordering::Relaxed);
                }
                Ok(())
            })
            .expect("spawn worker thread");
        Worker {
            stop,
            handle: Some(handle),
            stats,
        }
    }

    /// Stop and join; returns the worker's error if it died.
    pub fn stop(mut self) -> Result<()> {
        self.stop.store(true, Ordering::Relaxed);
        match self.handle.take() {
            Some(h) => h.join().map_err(|_| anyhow!("worker panicked"))?,
            None => Ok(()),
        }
    }
}

impl Drop for Worker {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use pipeline::{Config, GateConfig};

    struct AlwaysSpeech;
    impl Vad for AlwaysSpeech {
        fn process(&mut self, _: &[i16]) -> Result<(f32, bool)> {
            Ok((1.0, true))
        }
    }

    #[test]
    fn worker_moves_every_sample_through_the_rings_in_order() {
        let Some(model) = std::env::var_os("OPENKRISP_MODEL") else {
            eprintln!("SKIPPED: set OPENKRISP_MODEL to dpdfnet2_48khz_hr.onnx");
            return;
        };
        // enhancement only, gate/AGC off: output count must equal input count minus the enhancer's one-hop delay
        let cfg = Config {
            gate: None::<GateConfig>,
            agc: None,
            ..Config::default()
        };
        let p = Pipeline::new(std::path::Path::new(&model), AlwaysSpeech, cfg).unwrap();
        let (mut in_tx, in_rx) = ring(48000);
        let (out_tx, mut out_rx) = ring(48000);
        let w = Worker::spawn(p, in_rx, out_tx, 960);

        let total = 48000usize; // 1 s
        let mut sent = 0;
        let mut got = Vec::new();
        let start = std::time::Instant::now();
        while got.len() < 960 + total - 2 * BLOCK && start.elapsed() < Duration::from_secs(20) {
            if sent < total {
                let x: Vec<f32> = (sent..(sent + BLOCK).min(total))
                    .map(|i| 0.1 * (i as f32 * 0.05).sin())
                    .collect();
                sent += in_tx.push_slice(&x);
            }
            let mut buf = vec![0.0f32; 4096];
            let n = out_rx.pop_slice(&mut buf);
            got.extend_from_slice(&buf[..n]);
            std::thread::sleep(Duration::from_millis(2));
        }
        let blocks = w.stats.blocks.load(Ordering::Relaxed);
        let dropped = w.stats.output_dropped.load(Ordering::Relaxed);
        let max_us = w.stats.max_block_us.load(Ordering::Relaxed);
        w.stop().unwrap();
        eprintln!(
            "blocks {blocks}, got {} samples, dropped {dropped}, worst block {max_us} us",
            got.len()
        );
        assert_eq!(sent, total);
        assert_eq!(dropped, 0);
        assert!(
            got.len() >= 960 + total - 2 * BLOCK,
            "only {} samples came out",
            got.len()
        );
        // the first 960 are the silence prefill
        assert!(got[..960].iter().all(|v| *v == 0.0));
    }
}
