//! Mono AGC2 chain wired the same way as `GainController2::process` in `sonora` 0.2.0
//! (`src/gain_controller2.rs`), except the speech probability comes from the caller (e.g. TEN VAD)
//! instead of sonora's internal RNN VAD (`rnn_vad` is `pub(crate)` in sonora-agc2).
//!
//! Per 10 ms frame, on the pre-gain signal in float-S16 scale (+/-32768):
//! rms/peak dBFS -> noise estimator -> speech level estimator -> saturation protector (headroom)
//! -> adaptive digital gain controller (includes the noise-aware gain cap) -> limiter.
//! The fixed digital gain stage is omitted (sonora default is 0 dB).

use anyhow::{Result, bail};
use sonora_agc2::adaptive_digital_gain_controller::{AdaptiveDigitalGainController, FrameInfo};
use sonora_agc2::common::{
    ADJACENT_SPEECH_FRAMES_THRESHOLD, MAXIMAL_NUMBER_OF_SAMPLES_PER_CHANNEL,
    SATURATION_PROTECTOR_INITIAL_HEADROOM_DB, SUB_FRAMES_IN_FRAME, float_s16_to_dbfs,
};
pub use sonora_agc2::speech_level_estimator::AdaptiveDigitalConfig;
use sonora_agc2::limiter::Limiter;
use sonora_agc2::noise_level_estimator::NoiseLevelEstimator;
use sonora_agc2::saturation_protector::SaturationProtector;
use sonora_agc2::speech_level_estimator::SpeechLevelEstimator;

const S16: f32 = 32768.0;

/// Per-frame analysis values (all pre-gain, dBFS unless noted).
#[derive(Debug, Clone, Copy)]
pub struct FrameStats {
    pub speech_probability: f32,
    pub rms_dbfs: f32,
    pub peak_dbfs: f32,
    pub noise_rms_dbfs: f32,
    pub speech_level_dbfs: f32,
    pub speech_level_reliable: bool,
    pub headroom_db: f32,
    pub limiter_envelope_dbfs: f32,
}

pub struct Agc2 {
    frame_len: usize,
    speech_level: SpeechLevelEstimator,
    noise: NoiseLevelEstimator,
    saturation: SaturationProtector,
    adaptive: AdaptiveDigitalGainController,
    limiter: Limiter,
}

impl Agc2 {
    /// `sample_rate_hz / 100` samples per frame must be <= 480 and divisible by 20.
    pub fn new(sample_rate_hz: usize, config: AdaptiveDigitalConfig) -> Result<Self> {
        let frame_len = sample_rate_hz / 100;
        if sample_rate_hz % 100 != 0
            || frame_len == 0
            || frame_len > MAXIMAL_NUMBER_OF_SAMPLES_PER_CHANNEL
            || frame_len % SUB_FRAMES_IN_FRAME as usize != 0
        {
            bail!("unsupported sample rate {sample_rate_hz} Hz for a 10 ms AGC2 frame");
        }
        Ok(Self {
            frame_len,
            speech_level: SpeechLevelEstimator::new(&config, ADJACENT_SPEECH_FRAMES_THRESHOLD),
            noise: NoiseLevelEstimator::default(),
            saturation: SaturationProtector::new(
                SATURATION_PROTECTOR_INITIAL_HEADROOM_DB,
                ADJACENT_SPEECH_FRAMES_THRESHOLD,
            ),
            adaptive: AdaptiveDigitalGainController::new(config, ADJACENT_SPEECH_FRAMES_THRESHOLD),
            limiter: Limiter::new(frame_len),
        })
    }

    pub fn frame_len(&self) -> usize {
        self.frame_len
    }

    /// Process one 10 ms frame in place. `frame` is float audio in [-1, 1] and must hold exactly
    /// `frame_len()` samples. `speech_probability` is clamped to [0, 1].
    pub fn process(&mut self, frame: &mut [f32], speech_probability: f32) -> Result<FrameStats> {
        if frame.len() != self.frame_len {
            bail!("frame has {} samples, expected {}", frame.len(), self.frame_len);
        }
        let prob = speech_probability.clamp(0.0, 1.0);
        for x in frame.iter_mut() {
            *x *= S16;
        }

        let (mut peak, mut sq) = (0.0f32, 0.0f32);
        for &x in frame.iter() {
            peak = peak.max(x.abs());
            sq += x * x;
        }
        let peak_dbfs = float_s16_to_dbfs(peak);
        let rms_dbfs = float_s16_to_dbfs((sq / frame.len() as f32).sqrt());

        let noise_rms_dbfs = self.noise.analyze(&[&*frame]);
        self.speech_level.update(rms_dbfs, prob);
        let speech_level_dbfs = self.speech_level.level_dbfs();
        let speech_level_reliable = self.speech_level.is_confident();
        self.saturation.analyze(prob, peak_dbfs, speech_level_dbfs);
        let headroom_db = self.saturation.headroom_db();
        let limiter_envelope_dbfs = float_s16_to_dbfs(self.limiter.last_audio_level());

        let info = FrameInfo {
            speech_probability: prob,
            speech_level_dbfs,
            speech_level_reliable,
            noise_rms_dbfs,
            headroom_db,
            limiter_envelope_dbfs,
        };
        self.adaptive.process(&info, &mut [&mut *frame]);
        self.limiter.process(&mut [&mut *frame]);

        for x in frame.iter_mut() {
            *x /= S16;
        }
        Ok(FrameStats {
            speech_probability: prob,
            rms_dbfs,
            peak_dbfs,
            noise_rms_dbfs,
            speech_level_dbfs,
            speech_level_reliable,
            headroom_db,
            limiter_envelope_dbfs,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Deterministic noise in [-1, 1).
    struct Lcg(u32);
    impl Lcg {
        fn next(&mut self) -> f32 {
            self.0 = self.0.wrapping_mul(1664525).wrapping_add(1013904223);
            (self.0 >> 8) as f32 / (1 << 23) as f32 - 1.0
        }
    }

    fn db(x: f32) -> f32 {
        20.0 * x.log10()
    }

    fn rms(x: &[f32]) -> f32 {
        (x.iter().map(|v| v * v).sum::<f32>() / x.len() as f32).sqrt()
    }

    /// Alternate 1 s of "speech" (1 kHz sine at `speech_dbfs` rms, prob 1) with 1 s of noise at
    /// `noise_dbfs` rms (prob 0) for `seconds`; returns (input_rms_db, output_rms_db) averaged over
    /// the speech frames of the last speech second.
    fn run(speech_dbfs: f32, noise_dbfs: f32, seconds: usize) -> (f32, f32) {
        let mut agc = Agc2::new(48000, AdaptiveDigitalConfig::default()).unwrap();
        let mut rng = Lcg(1);
        let n = agc.frame_len();
        let sine_amp = 10f32.powf(speech_dbfs / 20.0) * std::f32::consts::SQRT_2;
        // uniform noise in [-1,1) has rms 1/sqrt(3)
        let noise_amp = 10f32.powf(noise_dbfs / 20.0) * 3f32.sqrt();
        let mut phase = 0usize;
        let (mut in_sq, mut out_sq, mut cnt) = (0.0f32, 0.0f32, 0);
        for s in 0..seconds {
            let speech = s % 2 == 0;
            for f in 0..100 {
                let mut frame: Vec<f32> = (0..n)
                    .map(|_| {
                        phase += 1;
                        if speech {
                            sine_amp * (2.0 * std::f32::consts::PI * 1000.0 * phase as f32 / 48000.0).sin()
                        } else {
                            noise_amp * rng.next()
                        }
                    })
                    .collect();
                let inp = rms(&frame);
                agc.process(&mut frame, if speech { 1.0 } else { 0.0 }).unwrap();
                if speech && s + 2 >= seconds && f >= 50 {
                    in_sq += inp * inp;
                    out_sq += rms(&frame).powi(2);
                    cnt += 1;
                }
            }
        }
        (db((in_sq / cnt as f32).sqrt()), db((out_sq / cnt as f32).sqrt()))
    }

    #[test]
    fn rejects_unsupported_rates() {
        assert!(Agc2::new(48000, AdaptiveDigitalConfig::default()).is_ok());
        assert!(Agc2::new(16000, AdaptiveDigitalConfig::default()).is_ok());
        assert!(Agc2::new(96000, AdaptiveDigitalConfig::default()).is_err());
    }

    #[test]
    fn quiet_speech_over_quiet_floor_is_boosted() {
        let (i, o) = run(-45.0, -80.0, 40);
        eprintln!("quiet floor: in {i:.1} dB, out {o:.1} dB, gain {:.1} dB", o - i);
        assert!(o - i > 10.0);
    }

    #[test]
    fn noisy_floor_caps_the_gain() {
        // Noise at -45 dBFS: max_output_noise -50 dBFS allows no gain at all (cap = max(-5, 0) = 0 dB).
        let (i, o) = run(-30.0, -45.0, 40);
        eprintln!("noisy floor: in {i:.1} dB, out {o:.1} dB, gain {:.1} dB", o - i);
        assert!(o - i < 1.0);
    }
}
