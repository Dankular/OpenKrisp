//! `agc_demo <enhanced48k.wav> <vad_source16k.wav> <out.wav>`
//!
//! Test harness for tasks 2+3: TEN VAD (hop 160 = 10 ms) on a 16 kHz mono file drives `agc::Agc2` on
//! the 48 kHz file, 10 ms per step. The VAD source is NOT the enhanced signal (no 48->16 kHz
//! decimator yet) and is assumed time-aligned with the 48 kHz file (the enhancer's own delay is ignored).
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
    let [enh, vadsrc, out] = a.as_slice() else { bail!("usage: agc_demo <enhanced48k.wav> <vad16k.wav> <out.wav>") };
    let mut audio = read(enh, 48000)?;
    let vad_audio: Vec<i16> = read(vadsrc, 16000)?.iter().map(|v| (v * 32768.0).round().clamp(-32768.0, 32767.0) as i16).collect();

    let mut vad = tenvad::TenVad::new(160, 0.5)?;
    let mut agc = agc::Agc2::new(48000, agc::AdaptiveDigitalConfig::default())?;
    let frames = (audio.len() / 480).min(vad_audio.len() / 160);

    let (mut n95, mut nflag, mut n_undecided) = (0usize, 0usize, 0usize);
    let (mut gain_sum, mut gain_n) = (0.0f64, 0usize);
    let mut last = None;
    for i in 0..frames {
        let (p, flag) = vad.process(&vad_audio[i * 160..(i + 1) * 160])?;
        // First call at hop 160 returns (-1.0, -1): no decision yet; treated here as probability 0.
        let prob = if p < 0.0 { n_undecided += 1; 0.0 } else { p };
        n95 += (prob >= 0.95) as usize;
        nflag += (flag == 1) as usize;
        let frame = &mut audio[i * 480..(i + 1) * 480];
        let (in_e, _) = (frame.iter().map(|v| v * v).sum::<f32>(), ());
        let st = agc.process(frame, prob)?;
        if prob >= 0.95 {
            let out_e = frame.iter().map(|v| v * v).sum::<f32>();
            if in_e > 0.0 && out_e > 0.0 {
                gain_sum += 10.0 * (out_e / in_e).log10() as f64;
                gain_n += 1;
            }
        }
        last = Some(st);
    }
    let mut w = hound::WavWriter::create(out, hound::WavSpec { channels: 1, sample_rate: 48000, bits_per_sample: 32, sample_format: hound::SampleFormat::Float })?;
    for s in &audio[..frames * 480] {
        w.write_sample(*s)?;
    }
    w.finalize()?;

    println!("frames {frames} ({:.1}s), undecided {n_undecided}", frames as f32 / 100.0);
    println!("TEN VAD flag==1 (thr 0.5): {:.1}%   prob>=0.95 (AGC2 VAD_CONFIDENCE_THRESHOLD): {:.1}%", 100.0 * nflag as f64 / frames as f64, 100.0 * n95 as f64 / frames as f64);
    if gain_n > 0 {
        println!("mean applied gain over prob>=0.95 frames: {:.1} dB", gain_sum / gain_n as f64);
    }
    println!("final: {:?}", last.unwrap());
    Ok(())
}
