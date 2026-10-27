//! Engine behind the UI. Two independent lanes, each a mono stream at the model rate (48 kHz for the HR model):
//! - `Lane::Mic`: what you say (noise cancellation for "my noise")
//! - `Lane::Speaker`: incoming call audio (noise cancellation for "others' noise")
//!
//! Per lane the output stream is always the input delayed by exactly one hop (10 ms at 48 kHz), whether the
//! lane is enhancing or bypassing, so toggling never shifts timing. When a lane is (re)enabled the enhancer is
//! reset and pre-rolled with the previous block; the first block it emits is discarded and the bypassed block
//! is used for it, then enhanced blocks follow.
//!
//! Voice isolation (Off/Auto) is stored and reported but NOT implemented. Auto is meant to add echo cancellation and
//! nearby-voice removal on top of noise removal; `echo_cancel_active` and `nearby_voice_removal_active` report which of
//! those actually run, and today both are false.
//! Audio device I/O is not part of this crate; the host feeds blocks to `process`.

mod ffi;
#[cfg(feature = "audio")]
pub mod session;

use std::path::Path;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicU64, Ordering};

use anyhow::{Result, bail};
use dpdf::StreamEnhancer;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Lane {
    Mic = 0,
    Speaker = 1,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VoiceIsolation {
    Off = 0,
    Auto = 1,
}

struct LaneState {
    enh: StreamEnhancer,
    warm: bool,
    pending: Vec<f32>,
    history: Vec<f32>,
}

impl LaneState {
    fn new(model: &Path, rate: u32) -> Result<Self> {
        let enh = StreamEnhancer::new(model, rate)?;
        let hop = enh.hop();
        Ok(Self {
            enh,
            warm: false,
            pending: Vec::new(),
            history: vec![0.0; hop],
        })
    }

    fn run(&mut self, enabled: bool, input: &[f32], out: &mut Vec<f32>) -> Result<()> {
        let hop = self.enh.hop();
        self.pending.extend_from_slice(input);
        while self.pending.len() >= hop {
            let block: Vec<f32> = self.pending.drain(..hop).collect();
            if !enabled {
                self.warm = false;
                out.extend_from_slice(&self.history);
            } else if !self.warm {
                self.enh.reset();
                let mut sink = Vec::new();
                self.enh.process(&self.history, &mut sink)?; // pre-roll: one hop, emits nothing
                self.enh.process(&block, &mut sink)?; // first emitted block is the pre-roll slice: discard
                out.extend_from_slice(&self.history);
                self.warm = true;
            } else {
                let before = out.len();
                self.enh.process(&block, out)?;
                if out.len() - before != hop {
                    bail!(
                        "enhancer emitted {} samples for one hop of {hop}",
                        out.len() - before
                    );
                }
            }
            self.history.copy_from_slice(&block);
        }
        Ok(())
    }
}

pub struct Engine {
    sample_rate: u32,
    block: usize,
    mic_nc: AtomicBool,
    speaker_nc: AtomicBool,
    voice_isolation: AtomicU8,
    mic_samples: AtomicU64,
    speaker_samples: AtomicU64,
    mic: Mutex<LaneState>,
    speaker: Mutex<LaneState>,
}

impl Engine {
    /// `sample_rate` is the model's native rate (48000 for `dpdfnet2_48khz_hr`).
    pub fn new(model: impl AsRef<Path>, sample_rate: u32) -> Result<Self> {
        let model = model.as_ref();
        let mic = LaneState::new(model, sample_rate)?;
        let speaker = LaneState::new(model, sample_rate)?;
        Ok(Self {
            sample_rate,
            block: mic.enh.hop(),
            mic_nc: AtomicBool::new(false),
            speaker_nc: AtomicBool::new(false),
            voice_isolation: AtomicU8::new(VoiceIsolation::Off as u8),
            mic_samples: AtomicU64::new(0),
            speaker_samples: AtomicU64::new(0),
            mic: Mutex::new(mic),
            speaker: Mutex::new(speaker),
        })
    }

    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    pub fn block(&self) -> usize {
        self.block
    }

    pub fn set_noise_cancel(&self, lane: Lane, on: bool) {
        match lane {
            Lane::Mic => self.mic_nc.store(on, Ordering::Relaxed),
            Lane::Speaker => self.speaker_nc.store(on, Ordering::Relaxed),
        }
    }

    pub fn noise_cancel(&self, lane: Lane) -> bool {
        match lane {
            Lane::Mic => self.mic_nc.load(Ordering::Relaxed),
            Lane::Speaker => self.speaker_nc.load(Ordering::Relaxed),
        }
    }

    pub fn set_voice_isolation(&self, mode: VoiceIsolation) {
        self.voice_isolation.store(mode as u8, Ordering::Relaxed);
    }

    pub fn voice_isolation(&self) -> VoiceIsolation {
        if self.voice_isolation.load(Ordering::Relaxed) == VoiceIsolation::Auto as u8 {
            VoiceIsolation::Auto
        } else {
            VoiceIsolation::Off
        }
    }

    /// Voice isolation "Auto" is meant to remove echo and nearby voices on top of standard noise removal.
    /// Neither exists yet, so both capabilities report false.
    pub fn echo_cancel_active(&self) -> bool {
        false
    }

    pub fn nearby_voice_removal_active(&self) -> bool {
        false
    }

    /// True only if the requested isolation is actually being applied (never, until a capability exists).
    pub fn voice_isolation_active(&self) -> bool {
        self.voice_isolation() == VoiceIsolation::Auto
            && (self.echo_cancel_active() || self.nearby_voice_removal_active())
    }

    pub fn samples_processed(&self, lane: Lane) -> u64 {
        match lane {
            Lane::Mic => self.mic_samples.load(Ordering::Relaxed),
            Lane::Speaker => self.speaker_samples.load(Ordering::Relaxed),
        }
    }

    /// Feed any number of samples; whole blocks are processed, the remainder is held until the next call.
    /// Output samples are appended to `out` (the stream lags the input by one block).
    pub fn process(&self, lane: Lane, input: &[f32], out: &mut Vec<f32>) -> Result<()> {
        let (state, enabled, counter) = match lane {
            Lane::Mic => (
                &self.mic,
                self.mic_nc.load(Ordering::Relaxed),
                &self.mic_samples,
            ),
            Lane::Speaker => (
                &self.speaker,
                self.speaker_nc.load(Ordering::Relaxed),
                &self.speaker_samples,
            ),
        };
        let mut st = state
            .lock()
            .map_err(|_| anyhow::anyhow!("lane lock poisoned"))?;
        st.run(enabled, input, out)?;
        counter.fetch_add(input.len() as u64, Ordering::Relaxed);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn model() -> Option<std::path::PathBuf> {
        let p = std::env::var_os("OPENKRISP_MODEL").map(std::path::PathBuf::from);
        if p.is_none() {
            eprintln!("SKIPPED: set OPENKRISP_MODEL to dpdfnet2_48khz_hr.onnx");
        }
        p
    }

    fn signal(n: usize) -> Vec<f32> {
        (0..n)
            .map(|i| {
                0.3 * (2.0 * std::f64::consts::PI * 440.0 * i as f64 / 48000.0).sin() as f32
                    + 0.05 * ((i * 7919 % 1000) as f32 / 500.0 - 1.0)
            })
            .collect()
    }

    #[test]
    fn bypass_is_input_delayed_one_block() {
        let Some(m) = model() else { return };
        let e = Engine::new(m, 48000).unwrap();
        let x = signal(48000);
        let mut out = Vec::new();
        for c in x.chunks(333) {
            e.process(Lane::Mic, c, &mut out).unwrap();
        }
        let b = e.block();
        assert_eq!(out.len(), x.len() / b * b);
        assert!(out[..b].iter().all(|v| *v == 0.0));
        assert_eq!(&out[b..], &x[..out.len() - b]);
        assert_eq!(e.samples_processed(Lane::Mic), x.len() as u64);
        assert_eq!(e.samples_processed(Lane::Speaker), 0);
    }

    #[test]
    fn toggling_keeps_length_and_alignment() {
        let Some(m) = model() else { return };
        let e = Engine::new(m, 48000).unwrap();
        let b = e.block();
        let x = signal(b * 400);
        let mut out = Vec::new();
        for (i, c) in x.chunks(b).enumerate() {
            e.set_noise_cancel(Lane::Mic, (100..300).contains(&i));
            e.process(Lane::Mic, c, &mut out).unwrap();
        }
        assert_eq!(out.len(), x.len());
        // bypassed stretches are exact delayed input
        assert_eq!(&out[b..100 * b], &x[..99 * b]);
        assert_eq!(&out[300 * b..], &x[299 * b..399 * b]);
        // the first enabled block (index 100, the warm-up one) is still bypass
        assert_eq!(&out[100 * b..101 * b], &x[99 * b..100 * b]);
        // an enhanced stretch differs from plain delayed input
        assert!(out[200 * b..201 * b] != x[199 * b..200 * b]);
    }

    #[test]
    fn lanes_are_independent() {
        let Some(m) = model() else { return };
        let e = Engine::new(m, 48000).unwrap();
        e.set_noise_cancel(Lane::Speaker, true);
        assert!(e.noise_cancel(Lane::Speaker) && !e.noise_cancel(Lane::Mic));
        let x = signal(480 * 50);
        let (mut a, mut b) = (Vec::new(), Vec::new());
        e.process(Lane::Mic, &x, &mut a).unwrap();
        e.process(Lane::Speaker, &x, &mut b).unwrap();
        assert_eq!(&a[480..], &x[..a.len() - 480]);
        assert!(b[10 * 480..] != x[9 * 480..9 * 480 + b.len() - 10 * 480]);
    }

    #[test]
    fn voice_isolation_is_stored_but_never_active() {
        let Some(m) = model() else { return };
        let e = Engine::new(m, 48000).unwrap();
        assert_eq!(e.voice_isolation(), VoiceIsolation::Off);
        e.set_voice_isolation(VoiceIsolation::Auto);
        assert_eq!(e.voice_isolation(), VoiceIsolation::Auto);
        assert!(!e.voice_isolation_active());
        assert!(!e.echo_cancel_active() && !e.nearby_voice_removal_active());
    }
}
