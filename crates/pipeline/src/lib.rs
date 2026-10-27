//! Chain, per 10 ms at 48 kHz: DPDFNet enhancement; the enhanced audio is decimated to 16 kHz for the VAD;
//! the VAD decision drives the soft gate; the gated audio goes through AGC2 and the limiter.
//! Stage order follows the earlier Python POC notes (VAD, gate, AGC).
//!
//! The VAD is injected so this crate does not link `libten_vad`.

use anyhow::{Result, bail};

pub use agc::AdaptiveDigitalConfig;
pub use gate::GateConfig;

const FRAME: usize = 480; // 10 ms at 48 kHz
const VAD_FRAME: usize = 160; // 10 ms at 16 kHz

/// A voice detector fed 160 samples of 16 kHz int16 per call.
pub trait Vad {
    /// Returns `(probability, flag)`. A negative probability means "no decision yet" (TEN VAD does this on
    /// its first call at hop 160); it is treated as 0 / not speech.
    fn process(&mut self, frame16k: &[i16]) -> Result<(f32, bool)>;
}

/// Which value feeds the AGC as the speech probability.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AgcSpeech {
    /// The VAD's raw probability (AGC2 counts a frame as speech at >= 0.95).
    Probability,
    /// 1.0 when the VAD flag is set, else 0.0.
    Flag,
}

#[derive(Clone, Copy, Debug)]
pub struct Config {
    /// `None` disables the gate (no lookahead delay either).
    pub gate: Option<GateConfig>,
    /// `None` disables AGC2 and the limiter.
    pub agc: Option<AdaptiveDigitalConfig>,
    pub agc_speech: AgcSpeech,
}

impl Default for Config {
    fn default() -> Self {
        // AGC2's stock initial gain is +15 dB (meant for the start of a call, when the level is unknown). Measured on
        // six eval clips it dominated the AGC's cost: SI-SDR 5.0 dB with it vs 12.9 dB with a 0 dB start (enhancement
        // alone: 13.7). See docs/eval.md. The gain still adapts upward at the controller's 6 dB/s limit.
        let agc = AdaptiveDigitalConfig {
            initial_gain_db: 0.0,
            ..AdaptiveDigitalConfig::default()
        };
        Self {
            gate: Some(GateConfig::default()),
            agc: Some(agc),
            agc_speech: AgcSpeech::Probability,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_agc_starts_at_zero_gain() {
        let cfg = Config::default();
        assert_eq!(cfg.agc.unwrap().initial_gain_db, 0.0);
        // the other AGC2 defaults are untouched
        assert_eq!(cfg.agc.unwrap().max_output_noise_level_dbfs, -50.0);
    }
}

pub struct Pipeline<V: Vad> {
    enh: dpdf::StreamEnhancer,
    dec: resample::Decimate3,
    vad: V,
    gate: Option<gate::SoftGate>,
    agc: Option<agc::Agc2>,
    agc_speech: AgcSpeech,
    enhanced: Vec<f32>,
    pub frames_speech: u64,
    pub frames_total: u64,
}

impl<V: Vad> Pipeline<V> {
    /// `model` must be a 48 kHz DPDFNet model.
    pub fn new(model: &std::path::Path, vad: V, cfg: Config) -> Result<Self> {
        let enh = dpdf::StreamEnhancer::new(model, 48000)?;
        if enh.hop() != FRAME {
            bail!(
                "model hop is {}, pipeline expects {FRAME} (48 kHz model)",
                enh.hop()
            );
        }
        Ok(Self {
            enh,
            dec: resample::Decimate3::new(),
            vad,
            gate: cfg.gate.map(|g| gate::SoftGate::new(48000, g)),
            agc: cfg.agc.map(|a| agc::Agc2::new(48000, a)).transpose()?,
            agc_speech: cfg.agc_speech,
            enhanced: Vec::new(),
            frames_speech: 0,
            frames_total: 0,
        })
    }

    /// Output delay in samples relative to the enhancer's own output (gate lookahead).
    pub fn extra_latency_samples(&self) -> usize {
        self.gate.as_ref().map_or(0, |g| g.lookahead_samples())
    }

    /// Feed 48 kHz mono samples; processed audio is appended to `out`. Whole 10 ms frames only; the remainder waits.
    pub fn process(&mut self, input: &[f32], out: &mut Vec<f32>) -> Result<()> {
        self.enh.process(input, &mut self.enhanced)?;
        self.drain(out)
    }

    /// Flush the enhancer tail (zero-padded) and process what is left.
    pub fn finish(&mut self, out: &mut Vec<f32>) -> Result<()> {
        self.enh.flush(&mut self.enhanced)?;
        self.drain(out)
    }

    fn drain(&mut self, out: &mut Vec<f32>) -> Result<()> {
        let mut pos = 0;
        while self.enhanced.len() - pos >= FRAME {
            let mut frame = [0.0f32; FRAME];
            frame.copy_from_slice(&self.enhanced[pos..pos + FRAME]);
            pos += FRAME;

            let mut x16 = Vec::with_capacity(VAD_FRAME);
            self.dec.process(&frame, &mut x16);
            let pcm: Vec<i16> = x16
                .iter()
                .map(|v| (v * 32768.0).round().clamp(-32768.0, 32767.0) as i16)
                .collect();
            let (p, flag) = if pcm.len() == VAD_FRAME {
                self.vad.process(&pcm)?
            } else {
                (0.0, false)
            };
            let prob = p.max(0.0);
            self.frames_total += 1;
            self.frames_speech += flag as u64;

            if let Some(g) = self.gate.as_mut() {
                g.process(&mut frame, flag);
            }
            if let Some(a) = self.agc.as_mut() {
                let sp = match self.agc_speech {
                    AgcSpeech::Probability => prob,
                    AgcSpeech::Flag => flag as u8 as f32,
                };
                a.process(&mut frame, sp)?;
            }
            out.extend_from_slice(&frame);
        }
        self.enhanced.drain(..pos);
        Ok(())
    }
}
