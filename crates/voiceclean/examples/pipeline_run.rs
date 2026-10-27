//! `pipeline_run <model.onnx> <in48k.wav> <out48k.wav> [--no-gate] [--no-agc] [--lookahead-ms N] [--agc-flag]`
//!
//! Runs the whole chain (DPDFNet -> TEN VAD -> gate -> AGC2/limiter). With `--no-gate --no-agc` it is enhancement only.
//! Output is delayed relative to the input by one 10 ms hop plus the gate lookahead; the delay is printed.
use anyhow::{Result, bail};
use pipeline::{AgcSpeech, Config, GateConfig, Pipeline, Vad};

struct Ten(tenvad::TenVad);
impl Vad for Ten {
    fn process(&mut self, f: &[i16]) -> Result<(f32, bool)> {
        let (p, flag) = self.0.process(f)?;
        Ok((p, flag == 1))
    }
}

fn main() -> Result<()> {
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    let mut cfg = Config::default();
    let mut lookahead = 30.0f32;
    // start from the pipeline's own default AGC settings, then apply flags
    let mut agc_cfg = cfg.agc.expect("the default config has AGC");
    let mut gate_on = true;
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--no-gate" => gate_on = false,
            "--no-agc" => cfg.agc = None,
            "--agc-flag" => cfg.agc_speech = AgcSpeech::Flag,
            "--lookahead-ms"
            | "--agc-initial-gain-db"
            | "--agc-max-noise-dbfs"
            | "--agc-max-gain-db" => {
                let v: f32 = args
                    .get(i + 1)
                    .and_then(|v| v.parse().ok())
                    .ok_or_else(|| anyhow::anyhow!("{} needs a number", args[i]))?;
                match args[i].as_str() {
                    "--lookahead-ms" => lookahead = v,
                    "--agc-initial-gain-db" => agc_cfg.initial_gain_db = v,
                    "--agc-max-noise-dbfs" => agc_cfg.max_output_noise_level_dbfs = v,
                    _ => agc_cfg.max_gain_db = v,
                }
                args.remove(i + 1);
            }
            _ => {
                i += 1;
                continue;
            }
        }
        args.remove(i);
    }
    if cfg.agc.is_some() {
        cfg.agc = Some(agc_cfg);
    }
    cfg.gate = gate_on.then(|| GateConfig {
        lookahead_ms: lookahead,
        ..GateConfig::default()
    });
    let [model, input, output] = args.as_slice() else {
        bail!("usage: pipeline_run <model.onnx> <in48k.wav> <out48k.wav> [flags]")
    };

    let mut r = hound::WavReader::open(input)?;
    let s = r.spec();
    if s.sample_rate != 48000 || s.channels != 1 {
        bail!("need 48 kHz mono, got {s:?}");
    }
    let x: Vec<f32> = match s.sample_format {
        hound::SampleFormat::Float => r.samples::<f32>().collect::<Result<_, _>>()?,
        hound::SampleFormat::Int => r
            .samples::<i16>()
            .map(|v| v.map(|v| v as f32 / 32768.0))
            .collect::<Result<_, _>>()?,
    };

    let mut p = Pipeline::new(
        std::path::Path::new(model),
        Ten(tenvad::TenVad::new(160, 0.5)?),
        cfg,
    )?;
    let t = std::time::Instant::now();
    let mut out = Vec::with_capacity(x.len());
    for c in x.chunks(480) {
        p.process(c, &mut out)?;
    }
    p.finish(&mut out)?;
    let secs = t.elapsed().as_secs_f32();
    eprintln!(
        "{} -> {} samples, RTF {:.3}, speech frames {:.1}%, extra gate latency {} samples ({:.0} ms)",
        x.len(),
        out.len(),
        secs / (x.len() as f32 / 48000.0),
        100.0 * p.frames_speech as f64 / p.frames_total.max(1) as f64,
        p.extra_latency_samples(),
        p.extra_latency_samples() as f32 / 48.0
    );
    let mut w = hound::WavWriter::create(
        output,
        hound::WavSpec {
            channels: 1,
            sample_rate: 48000,
            bits_per_sample: 32,
            sample_format: hound::SampleFormat::Float,
        },
    )?;
    for v in out {
        w.write_sample(v)?;
    }
    w.finalize()?;
    Ok(())
}
