//! Soft gate: attenuates audio to `floor_db` when the speech decision is "closed", with smooth transitions.
//!
//! Semantics (this crate's own design; the earlier Python gate it replaces is not in this repo):
//! - One boolean speech decision per frame (e.g. TEN VAD flag). A closed decision only takes effect after
//!   `hangover_ms` frames without speech.
//! - The gain follows its target through a per-sample one-pole smoother. `attack_ms` is the time constant when
//!   the gain is rising (gate opening), `release_ms` when falling (gate closing). A time constant is the time
//!   to cover ~63% of the distance, not the full ramp.
//! - Lookahead: the audio is delayed by `lookahead_ms` while decisions apply immediately, so the gate starts
//!   opening that much earlier relative to the audio. This covers the detector's decision delay at an onset,
//!   at the cost of `lookahead_ms` of output latency.

#[derive(Clone, Copy, Debug)]
pub struct GateConfig {
    pub floor_db: f32,
    pub attack_ms: f32,
    pub release_ms: f32,
    pub hangover_ms: f32,
    pub lookahead_ms: f32,
}

impl Default for GateConfig {
    fn default() -> Self {
        Self {
            floor_db: -25.0,
            attack_ms: 5.0,
            release_ms: 80.0,
            hangover_ms: 250.0,
            lookahead_ms: 30.0,
        }
    }
}

pub struct SoftGate {
    frame_len: usize,
    frame_ms: f32,
    floor: f32,
    attack_coef: f32,
    release_coef: f32,
    hangover_frames: u32,
    hang: u32,
    gain: f32,
    delay: Vec<f32>,
    pos: usize,
}

fn coef(ms: f32, sample_rate: f32) -> f32 {
    if ms <= 0.0 {
        1.0
    } else {
        1.0 - (-1.0 / (ms * 0.001 * sample_rate)).exp()
    }
}

impl SoftGate {
    /// Frames are 10 ms (`sample_rate / 100` samples).
    pub fn new(sample_rate: u32, cfg: GateConfig) -> Self {
        let sr = sample_rate as f32;
        let frame_len = (sample_rate / 100) as usize;
        let floor = 10f32.powf(cfg.floor_db / 20.0);
        Self {
            frame_len,
            frame_ms: 10.0,
            floor,
            attack_coef: coef(cfg.attack_ms, sr),
            release_coef: coef(cfg.release_ms, sr),
            hangover_frames: (cfg.hangover_ms / 10.0).round() as u32,
            hang: 0,
            gain: floor,
            delay: vec![0.0; (cfg.lookahead_ms * 0.001 * sr).round() as usize],
            pos: 0,
        }
    }

    pub fn frame_len(&self) -> usize {
        self.frame_len
    }

    /// Output delay in samples.
    pub fn lookahead_samples(&self) -> usize {
        self.delay.len()
    }

    pub fn current_gain(&self) -> f32 {
        self.gain
    }

    /// Process one frame in place. `speech` is this frame's decision. The output is the input delayed by
    /// `lookahead_samples()`, with the gate gain applied.
    pub fn process(&mut self, frame: &mut [f32], speech: bool) {
        debug_assert_eq!(frame.len(), self.frame_len);
        let _ = self.frame_ms;
        if speech {
            self.hang = self.hangover_frames;
        }
        let open = speech || self.hang > 0;
        if !speech && self.hang > 0 {
            self.hang -= 1;
        }
        let target = if open { 1.0 } else { self.floor };
        for x in frame.iter_mut() {
            let c = if target > self.gain {
                self.attack_coef
            } else {
                self.release_coef
            };
            self.gain += (target - self.gain) * c;
            let v = if self.delay.is_empty() {
                *x
            } else {
                let old = self.delay[self.pos];
                self.delay[self.pos] = *x;
                self.pos = (self.pos + 1) % self.delay.len();
                old
            };
            *x = v * self.gain;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SR: u32 = 48000;

    fn run_const(g: &mut SoftGate, frames: usize, speech: bool, level: f32) -> Vec<f32> {
        let mut out = Vec::new();
        for _ in 0..frames {
            let mut f = vec![level; g.frame_len()];
            g.process(&mut f, speech);
            out.extend(f);
        }
        out
    }

    fn no_lookahead() -> GateConfig {
        GateConfig {
            lookahead_ms: 0.0,
            ..GateConfig::default()
        }
    }

    #[test]
    fn closed_gate_settles_at_floor_and_open_at_unity() {
        let mut g = SoftGate::new(SR, no_lookahead());
        // closed from the start: gain starts at the floor
        let o = run_const(&mut g, 100, false, 1.0);
        let floor = 10f32.powf(-25.0 / 20.0);
        assert!((o[o.len() - 1] - floor).abs() < 1e-3);
        let o = run_const(&mut g, 100, true, 1.0);
        assert!((o[o.len() - 1] - 1.0).abs() < 1e-3);
    }

    #[test]
    fn attack_time_constant() {
        let mut g = SoftGate::new(SR, no_lookahead());
        run_const(&mut g, 50, false, 1.0);
        let floor = g.current_gain();
        let o = run_const(&mut g, 2, true, 1.0);
        // after one 5 ms time constant the gain has covered ~63% of the way from floor to 1
        let i = (0.005 * SR as f32) as usize;
        let frac = (o[i] - floor) / (1.0 - floor);
        eprintln!("attack fraction at 5 ms: {frac:.3}");
        assert!((frac - 0.632).abs() < 0.03);
    }

    #[test]
    fn hangover_holds_open_then_releases() {
        let mut g = SoftGate::new(SR, no_lookahead());
        run_const(&mut g, 100, true, 1.0);
        // 24 non-speech frames (240 ms): hangover still holding, gain stays at 1
        let o = run_const(&mut g, 24, false, 1.0);
        assert!(o.iter().all(|v| (*v - 1.0).abs() < 1e-4));
        // then it falls toward the floor with an 80 ms time constant
        let o = run_const(&mut g, 60, false, 1.0);
        let floor = 10f32.powf(-25.0 / 20.0);
        assert!(o[0] > 0.99);
        assert!((o[o.len() - 1] - floor).abs() < 5e-3);
    }

    #[test]
    fn lookahead_delays_audio_exactly() {
        let mut g = SoftGate::new(
            SR,
            GateConfig {
                floor_db: 0.0,
                ..GateConfig::default()
            },
        );
        let n = g.lookahead_samples();
        assert_eq!(n, (0.030 * SR as f32) as usize);
        let mut f = vec![0.0f32; g.frame_len()];
        f[0] = 1.0;
        let mut out = Vec::new();
        g.process(&mut f, true);
        out.extend(&f);
        for _ in 0..10 {
            let mut f = vec![0.0f32; g.frame_len()];
            g.process(&mut f, true);
            out.extend(f);
        }
        let peak = out.iter().position(|v| v.abs() > 0.5).unwrap();
        assert_eq!(peak, n);
    }

    /// Speech starts at time s0; the detector reports it `detect_delay_ms` later. Returns the amplitude of the
    /// first speech sample at the output relative to a unity open gate.
    fn onset_gain(lookahead_ms: f32, detect_delay_ms: f32) -> f32 {
        let mut g = SoftGate::new(
            SR,
            GateConfig {
                lookahead_ms,
                ..GateConfig::default()
            },
        );
        let fl = g.frame_len();
        let s0 = 100 * fl; // 1 s of silence first
        let detect = s0 + (detect_delay_ms * 0.001 * SR as f32) as usize;
        let total = s0 + 20 * fl;
        let mut out = Vec::new();
        let mut t = 0;
        while t < total {
            let mut f: Vec<f32> = (t..t + fl)
                .map(|i| if i >= s0 { 1.0 } else { 0.0 })
                .collect();
            let speech = t + fl > detect; // decision available once the frame containing the detection is complete
            g.process(&mut f, speech);
            out.extend(f);
            t += fl;
        }
        out[s0 + g.lookahead_samples()]
    }

    #[test]
    fn lookahead_preserves_onsets_that_the_detector_notices_late() {
        let without = onset_gain(0.0, 20.0);
        let with = onset_gain(30.0, 20.0);
        eprintln!(
            "onset gain, detector 20 ms late: lookahead 0 -> {without:.3}, 30 ms -> {with:.3}"
        );
        assert!(without < 0.15);
        assert!(with > 0.7);
    }
}
