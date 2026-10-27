use std::time::Instant;

use anyhow::{Context, Result, bail};
use dpdf::StreamEnhancer;

const USAGE: &str = "usage: voiceclean <model.onnx> <model_sample_rate> <in.wav> <out.wav>";

fn main() -> Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let [model, rate, input, output] = args.as_slice() else {
        bail!(USAGE);
    };
    let rate: u32 = rate.parse().context("model_sample_rate")?;

    let t = Instant::now();
    let mut enh = StreamEnhancer::new(model, rate)?;
    eprintln!(
        "model load {:.2}s (win {}, hop {})",
        t.elapsed().as_secs_f32(),
        enh.win(),
        enh.hop()
    );

    let (samples, spec) = read_mono(input)?;
    if spec.sample_rate != rate {
        bail!(
            "input is {} Hz, model expects {rate} Hz (resampling not implemented)",
            spec.sample_rate
        );
    }

    let t = Instant::now();
    let mut out = Vec::with_capacity(samples.len());
    for chunk in samples.chunks(enh.hop()) {
        enh.process(chunk, &mut out)?;
    }
    enh.flush(&mut out)?;
    let secs = t.elapsed().as_secs_f32();
    let audio_secs = samples.len() as f32 / rate as f32;
    eprintln!(
        "{} -> {} samples, {secs:.2}s, RTF {:.3}",
        samples.len(),
        out.len(),
        secs / audio_secs
    );

    let mut w = hound::WavWriter::create(
        output,
        hound::WavSpec {
            channels: 1,
            sample_rate: rate,
            bits_per_sample: 32,
            sample_format: hound::SampleFormat::Float,
        },
    )?;
    for s in out {
        w.write_sample(s)?;
    }
    w.finalize()?;
    Ok(())
}

fn read_mono(path: &str) -> Result<(Vec<f32>, hound::WavSpec)> {
    let mut r = hound::WavReader::open(path)?;
    let spec = r.spec();
    let ch = spec.channels as usize;
    let all: Vec<f32> = match (spec.sample_format, spec.bits_per_sample) {
        (hound::SampleFormat::Float, 32) => r.samples::<f32>().collect::<Result<_, _>>()?,
        (hound::SampleFormat::Int, 16) => r
            .samples::<i16>()
            .map(|s| s.map(|v| v as f32 / 32768.0))
            .collect::<Result<_, _>>()?,
        (f, b) => bail!("unsupported wav format {f:?} {b}-bit"),
    };
    let mono = all
        .chunks(ch)
        .map(|f| f.iter().sum::<f32>() / ch as f32)
        .collect();
    Ok((mono, spec))
}
