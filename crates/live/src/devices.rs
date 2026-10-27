//! cpal device layer: named device selection, loopback capture of a render device, sample-format and channel
//! handling, and resampling to/from the engine's 48 kHz mono. Compile-checked (Linux/ALSA and x86_64-pc-windows-gnu);
//! never run against real audio hardware in this repo's sandbox.
//!
//! cpal's WASAPI backend (cpal 0.18.2, `src/host/wasapi/mod.rs`): "If you use a WASAPI output device as an input
//! device it will transparently enable loopback mode". That is how `open_input(.., loopback = true, ..)` works.
//!
//! `cpal::Stream` is not `Send` on every platform, so callers should create and drop streams on one thread
//! (see `Session` in the engine crate).

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use anyhow::{Context, Result, bail};
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{FromSample, Sample, SampleFormat, SizedSample};
use resample::Resampler;
use ringbuf::traits::{Consumer, Producer};

use crate::{Rx, Tx};

pub const ENGINE_RATE: u32 = 48000;

/// Counters shared with the audio callbacks.
#[derive(Default)]
pub struct IoStats {
    /// Input samples dropped because the worker was not keeping up.
    pub input_dropped: AtomicU64,
    /// Output frames filled with silence because the worker had not produced enough audio.
    pub output_underrun_frames: AtomicU64,
}

fn name_of(d: &cpal::Device) -> String {
    d.description()
        .map(|x| x.name().to_string())
        .unwrap_or_default()
}

pub fn list_inputs() -> Result<Vec<String>> {
    Ok(cpal::default_host()
        .input_devices()?
        .map(|d| name_of(&d))
        .collect())
}

pub fn list_outputs() -> Result<Vec<String>> {
    Ok(cpal::default_host()
        .output_devices()?
        .map(|d| name_of(&d))
        .collect())
}

fn find(name: Option<&str>, input: bool) -> Result<cpal::Device> {
    let host = cpal::default_host();
    match name {
        None => (if input {
            host.default_input_device()
        } else {
            host.default_output_device()
        })
        .with_context(|| {
            format!(
                "no default {} device",
                if input { "input" } else { "output" }
            )
        }),
        Some(n) => {
            let mut devices = if input {
                host.input_devices()?
            } else {
                host.output_devices()?
            };
            devices.find(|d| name_of(d) == n).with_context(|| {
                format!(
                    "{} device \"{n}\" not found",
                    if input { "input" } else { "output" }
                )
            })
        }
    }
}

/// An open capture stream (keep it alive; dropping stops capture).
pub struct Input {
    _stream: cpal::Stream,
    pub device: String,
    pub device_rate: u32,
}

/// An open playback stream.
pub struct Output {
    _stream: cpal::Stream,
    pub device: String,
    pub device_rate: u32,
}

/// Capture `name` (default if `None`). With `loopback`, `name` is a *render* device whose output is captured.
/// Samples are downmixed to mono, resampled to 48 kHz if needed, and pushed to `tx`.
pub fn open_input(
    name: Option<&str>,
    loopback: bool,
    tx: Tx,
    stats: Arc<IoStats>,
) -> Result<Input> {
    let dev = find(name, !loopback)?;
    let supported = if loopback {
        dev.default_output_config()
    } else {
        dev.default_input_config()
    }
    .with_context(|| format!("reading the format of \"{}\"", name_of(&dev)))?;
    let (rate, channels, format) = (
        supported.sample_rate(),
        supported.channels() as usize,
        supported.sample_format(),
    );
    let config = supported.config();
    let stream = match format {
        SampleFormat::F32 => build_in::<f32>(&dev, config, channels, rate, tx, stats)?,
        SampleFormat::I16 => build_in::<i16>(&dev, config, channels, rate, tx, stats)?,
        SampleFormat::I32 => build_in::<i32>(&dev, config, channels, rate, tx, stats)?,
        other => bail!(
            "\"{}\" uses sample format {other:?}, which is not supported yet",
            name_of(&dev)
        ),
    };
    stream.play()?;
    Ok(Input {
        _stream: stream,
        device: name_of(&dev),
        device_rate: rate,
    })
}

fn build_in<T>(
    dev: &cpal::Device,
    config: cpal::StreamConfig,
    ch: usize,
    rate: u32,
    mut tx: Tx,
    stats: Arc<IoStats>,
) -> Result<cpal::Stream>
where
    T: SizedSample + Send + 'static,
    f32: FromSample<T>,
{
    let mut mono: Vec<f32> = Vec::with_capacity(8192);
    let mut converted: Vec<f32> = Vec::with_capacity(8192);
    let mut rs = (rate != ENGINE_RATE)
        .then(|| Resampler::new(rate, ENGINE_RATE))
        .transpose()?;
    let stream = dev.build_input_stream(
        config,
        move |data: &[T], _| {
            mono.clear();
            for frame in data.chunks_exact(ch) {
                mono.push(frame.iter().map(|&x| f32::from_sample(x)).sum::<f32>() / ch as f32);
            }
            let out: &[f32] = match rs.as_mut() {
                Some(r) => {
                    converted.clear();
                    r.process(&mono, &mut converted);
                    &converted
                }
                None => &mono,
            };
            let pushed = tx.push_slice(out);
            if pushed < out.len() {
                stats
                    .input_dropped
                    .fetch_add((out.len() - pushed) as u64, Ordering::Relaxed);
            }
        },
        |e| eprintln!("openkrisp: input stream error: {e}"),
        None,
    )?;
    Ok(stream)
}

/// Play 48 kHz mono samples from `rx` on `name` (default if `None`), resampled and copied to every channel.
pub fn open_output(name: Option<&str>, rx: Rx, stats: Arc<IoStats>) -> Result<Output> {
    let dev = find(name, false)?;
    let supported = dev
        .default_output_config()
        .with_context(|| format!("reading the format of \"{}\"", name_of(&dev)))?;
    let (rate, channels, format) = (
        supported.sample_rate(),
        supported.channels() as usize,
        supported.sample_format(),
    );
    let config = supported.config();
    let stream = match format {
        SampleFormat::F32 => build_out::<f32>(&dev, config, channels, rate, rx, stats)?,
        SampleFormat::I16 => build_out::<i16>(&dev, config, channels, rate, rx, stats)?,
        SampleFormat::I32 => build_out::<i32>(&dev, config, channels, rate, rx, stats)?,
        other => bail!(
            "\"{}\" uses sample format {other:?}, which is not supported yet",
            name_of(&dev)
        ),
    };
    stream.play()?;
    Ok(Output {
        _stream: stream,
        device: name_of(&dev),
        device_rate: rate,
    })
}

fn build_out<T>(
    dev: &cpal::Device,
    config: cpal::StreamConfig,
    ch: usize,
    rate: u32,
    mut rx: Rx,
    stats: Arc<IoStats>,
) -> Result<cpal::Stream>
where
    T: SizedSample + FromSample<f32> + Send + 'static,
{
    let mut pending: Vec<f32> = Vec::with_capacity(8192); // device-rate mono samples not yet played
    let mut tmp = vec![0.0f32; 480];
    let mut rs = (rate != ENGINE_RATE)
        .then(|| Resampler::new(ENGINE_RATE, rate))
        .transpose()?;
    let stream = dev.build_output_stream(
        config,
        move |data: &mut [T], _| {
            let frames = data.len() / ch;
            while pending.len() < frames {
                let n = rx.pop_slice(&mut tmp);
                if n == 0 {
                    break;
                }
                match rs.as_mut() {
                    Some(r) => r.process(&tmp[..n], &mut pending),
                    None => pending.extend_from_slice(&tmp[..n]),
                }
            }
            let have = pending.len().min(frames);
            for (i, frame) in data.chunks_exact_mut(ch).enumerate() {
                let v = if i < have { pending[i] } else { 0.0 };
                frame.fill(T::from_sample(v));
            }
            if have < frames {
                stats
                    .output_underrun_frames
                    .fetch_add((frames - have) as u64, Ordering::Relaxed);
            }
            pending.drain(..have);
        },
        |e| eprintln!("openkrisp: output stream error: {e}"),
        None,
    )?;
    Ok(stream)
}
