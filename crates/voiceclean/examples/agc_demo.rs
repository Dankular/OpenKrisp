//! `agc_demo <enhanced48k.wav> <out.wav> [dump16k.wav]`
//!
//! 48 kHz audio -> `resample::Decimate3` -> TEN VAD (hop 160 = 10 ms) -> speech probability driving `agc::Agc2`
//! on the 48 kHz audio, 10 ms per step. The decimator's 1 ms group delay is ignored when pairing VAD frames
//! with AGC frames. Optionally writes the decimated 16 kHz stream (16-bit) for external comparison.
use anyhow::{Result, bail};

fn read(path: &str, rate: u32) -> Result<Vec<f32>> {
    let mut r = hound::WavReader::open(path)?;
    let s = r.spec();
    if s.sample_rate != rate || s.channels != 1 {
        bail!("{path}: need {rate} Hz mono, got {s:?}");
    }
    Ok(match s.sample_format {
        hound::SampleFormat::Float => r.samples::<f32>().collect::<Result<_, _>>()?,
        hound::SampleFormat::Int => r.samples::<i16>().map(|v| v.map(|v| v as f32 / 32768.0)).collect::<Result<_, _>>()?,
    })
}

fn main() -> Result<()> {
    let a: Vec<String> = std::env::args().skip(1).collect();
    if a.len() < 2 || a.len() > 3 {
        bail!("usage: agc_demo <enhanced48k.wav> <out.wav> [dump16k.wav]");
    }
    let mut audio = read(&a[0], 48000)?;
    // Experiment knobs: AGC_DEMO_ATTEN_DB attenuates the input; AGC_DEMO_MAP=flag feeds the AGC 1.0/0.0 from TEN VAD's flag.
    let atten_db: f32 = std::env::var("AGC_DEMO_ATTEN_DB").ok().and_then(|v| v.parse().ok()).unwrap_or(0.0);
    let map_flag = std::env::var("AGC_DEMO_MAP").is_ok_and(|v| v == "flag");
    let g = 10f32.powf(-atten_db / 20.0);
    audio.iter_mut().for_each(|v| *v *= g);

    let mut dec = resample::Decimate3::new();
    let mut x16 = Vec::with_capacity(audio.len() / 3 + 1);
    dec.process(&audio, &mut x16);
    let pcm16: Vec<i16> = x16.iter().map(|v| (v * 32768.0).round().clamp(-32768.0, 32767.0) as i16).collect();
    if let Some(path) = a.get(2) {
        let mut w = hound::WavWriter::create(path, hound::WavSpec { channels: 1, sample_rate: 16000, bits_per_sample: 16, sample_format: hound::SampleFormat::Int })?;
        for s in &pcm16 {
            w.write_sample(*s)?;
        }
        w.finalize()?;
    }

    let mut vad = tenvad::TenVad::new(160, 0.5)?;
    let mut agc = agc::Agc2::new(48000, agc::AdaptiveDigitalConfig::default())?;
    let frames = (audio.len() / 480).min(pcm16.len() / 160);

    let mut hist = [0usize; 10]; // probability deciles
    let (mut n95, mut nflag, mut n_undecided) = (0usize, 0usize, 0usize);
    let mut gain_sum = [0.0f64; 3];
    let mut gain_n = [0usize; 3];
    let mut last = None;
    for i in 0..frames {
        let (p, flag) = vad.process(&pcm16[i * 160..(i + 1) * 160])?;
        // First call at hop 160 returns (-1.0, -1): no decision yet; treated here as probability 0.
        let prob = if p < 0.0 { n_undecided += 1; 0.0 } else { p };
        hist[((prob * 10.0) as usize).min(9)] += 1;
        n95 += (prob >= 0.95) as usize;
        nflag += (flag == 1) as usize;
        let frame = &mut audio[i * 480..(i + 1) * 480];
        let in_e = frame.iter().map(|v| v * v).sum::<f32>();
        let st = agc.process(frame, if map_flag { flag.max(0) as f32 } else { prob })?;
        if flag == 1 {
            let out_e = frame.iter().map(|v| v * v).sum::<f32>();
            if in_e > 0.0 && out_e > 0.0 {
                let third = i * 3 / frames;
                gain_sum[third] += 10.0 * (out_e / in_e).log10() as f64;
                gain_n[third] += 1;
            }
        }
        last = Some(st);
    }
    let mut w = hound::WavWriter::create(&a[1], hound::WavSpec { channels: 1, sample_rate: 48000, bits_per_sample: 32, sample_format: hound::SampleFormat::Float })?;
    for s in &audio[..frames * 480] {
        w.write_sample(*s)?;
    }
    w.finalize()?;

    println!("frames {frames} ({:.1}s), undecided {n_undecided}", frames as f32 / 100.0);
    println!("TEN VAD flag==1 (thr 0.5): {:.1}%   prob>=0.95 (AGC2 VAD_CONFIDENCE_THRESHOLD): {:.1}%", 100.0 * nflag as f64 / frames as f64, 100.0 * n95 as f64 / frames as f64);
    println!("probability deciles [0-.1, .1-.2, ... .9-1.0] (% of frames):");
    println!("  {}", hist.iter().map(|&c| format!("{:.1}", 100.0 * c as f64 / frames as f64)).collect::<Vec<_>>().join("  "));
    println!("attenuation {atten_db} dB, AGC probability = {}", if map_flag { "TEN VAD flag (1/0)" } else { "TEN VAD probability" });
    println!("mean applied gain over flag==1 frames, by third of the clip: {}", (0..3).map(|t| format!("{:.1} dB", gain_sum[t] / gain_n[t].max(1) as f64)).collect::<Vec<_>>().join(" / "));
    println!("final: {:?}", last.unwrap());
    Ok(())
}
