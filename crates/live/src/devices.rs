//! cpal device layer. NOT exercised on real hardware in this repo's CI/sandbox (no audio devices there);
//! it has only been compile-checked. Limits: needs the device to run at exactly 48 kHz (no resampling yet);
//! multi-channel input is averaged to mono; mono output is copied to every channel.

use anyhow::{Context, Result, bail};
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use ringbuf::traits::{Consumer, Producer};

use crate::{Rx, Tx};

pub struct Devices {
    _input: cpal::Stream,
    _output: cpal::Stream,
    pub input_name: String,
    pub output_name: String,
}

fn check_rate(cfg: &cpal::SupportedStreamConfig, what: &str) -> Result<()> {
    if cfg.sample_rate() != 48000 {
        bail!(
            "{what} device runs at {} Hz; only 48000 Hz is supported (set the device format to 48 kHz)",
            cfg.sample_rate()
        );
    }
    if cfg.sample_format() != cpal::SampleFormat::F32 {
        bail!(
            "{what} device sample format is {:?}; only f32 streams are implemented",
            cfg.sample_format()
        );
    }
    Ok(())
}

/// Opens the default input and output devices. Input samples go to `to_worker`; playback samples come from `from_worker`.
pub fn open_default(mut to_worker: Tx, mut from_worker: Rx) -> Result<Devices> {
    let host = cpal::default_host();
    let input = host
        .default_input_device()
        .context("no default input device")?;
    let output = host
        .default_output_device()
        .context("no default output device")?;
    let in_cfg = input.default_input_config()?;
    let out_cfg = output.default_output_config()?;
    check_rate(&in_cfg, "input")?;
    check_rate(&out_cfg, "output")?;
    let (in_ch, out_ch) = (in_cfg.channels() as usize, out_cfg.channels() as usize);

    let input_stream = input.build_input_stream(
        in_cfg.config(),
        move |data: &[f32], _| {
            if in_ch == 1 {
                to_worker.push_slice(data);
            } else {
                for frame in data.chunks_exact(in_ch) {
                    let _ = to_worker.try_push(frame.iter().sum::<f32>() / in_ch as f32);
                }
            }
        },
        |e| eprintln!("input stream error: {e}"),
        None,
    )?;
    let output_stream = output.build_output_stream(
        out_cfg.config(),
        move |data: &mut [f32], _| {
            for frame in data.chunks_exact_mut(out_ch) {
                let v = from_worker.try_pop().unwrap_or(0.0); // underrun -> silence
                frame.fill(v);
            }
        },
        |e| eprintln!("output stream error: {e}"),
        None,
    )?;
    input_stream.play()?;
    output_stream.play()?;
    Ok(Devices {
        _input: input_stream,
        _output: output_stream,
        input_name: input
            .description()
            .map(|d| d.name().to_string())
            .unwrap_or_default(),
        output_name: output
            .description()
            .map(|d| d.name().to_string())
            .unwrap_or_default(),
    })
}
