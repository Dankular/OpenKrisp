//! Streaming DPDFNet enhancer, a port of `dpdfnet.StreamEnhancer` (dpdfnet 0.6.0 `stream.py`).
//!
//! Causal STFT (no centering), Vorbis window, one ONNX call per hop with the recurrent state
//! carried across calls, per-frame iSTFT and overlap-add. The first `win - 1` input samples
//! produce no output; after that every `hop` input samples yield `hop` output samples.

use std::path::Path;

use anyhow::{Context, Result, anyhow, bail};
use realfft::num_complex::Complex;
use realfft::{ComplexToReal, RealFftPlanner, RealToComplex};
use std::sync::Arc;
use tract_onnx::prelude::*;

type Plan = Arc<TypedRunnableModel>;

pub struct StreamEnhancer {
    plan: Plan,
    sample_rate: u32,
    win: usize,
    hop: usize,
    bins: usize,
    window: Vec<f32>,
    init_state: Vec<f32>,
    state: Vec<f32>,
    in_buf: Vec<f32>,
    out_buf: Vec<f32>,
    r2c: Arc<dyn RealToComplex<f64>>,
    c2r: Arc<dyn ComplexToReal<f64>>,
    time: Vec<f64>,
    spec: Vec<Complex<f64>>,
}

impl StreamEnhancer {
    /// Load an ONNX streaming model. `sample_rate` is the model's native rate
    /// (it is not stored in the ONNX file; see `dpdfnet.models.MODEL_REGISTRY`).
    pub fn new(onnx_path: impl AsRef<Path>, sample_rate: u32) -> Result<Self> {
        let path = onnx_path.as_ref();
        let onnx = tract_onnx::onnx();
        let proto = onnx
            .proto_model_for_path(path)
            .with_context(|| format!("reading {}", path.display()))?;

        let meta = |key: &str| -> Result<&str> {
            proto
                .metadata_props
                .iter()
                .find(|e| e.key == key)
                .map(|e| e.value.as_str())
                .ok_or_else(|| anyhow!("ONNX model is missing metadata key '{key}'"))
        };
        let size = |key: &str| -> Result<usize> { Ok(meta(key)?.trim().parse()?) };
        let floats = |key: &str| -> Result<Vec<f32>> {
            meta(key)?
                .split(',')
                .map(|x| x.trim().parse::<f32>().map_err(Into::into))
                .collect()
        };

        let state_size = size("state_size")?;
        let erb_n = size("erb_norm_state_size")?;
        let spec_n = size("spec_norm_state_size")?;
        let erb_init = floats("erb_norm_init")?;
        let spec_init = floats("spec_norm_init")?;
        if erb_init.len() != erb_n || spec_init.len() != spec_n || erb_n + spec_n > state_size {
            bail!(
                "inconsistent state metadata: state_size={state_size} erb={erb_n}/{} spec={spec_n}/{}",
                erb_init.len(),
                spec_init.len()
            );
        }
        let mut init_state = vec![0.0f32; state_size];
        init_state[..erb_n].copy_from_slice(&erb_init);
        init_state[erb_n..erb_n + spec_n].copy_from_slice(&spec_init);

        // Window length from the declared spec input shape [1, 1, bins, 2], as infer_win_len does.
        let typed_in = onnx.model_for_proto_model(&proto)?;
        let spec_shape = typed_in.input_fact(0)?.shape.clone();
        let bins = spec_shape
            .as_concrete_finite()?
            .and_then(|s| s.get(s.len().checked_sub(2)?).copied())
            .filter(|&b| b > 1)
            .context("spec input has no concrete frequency dimension")?;
        let win = (bins - 1) * 2;
        let hop = win / 2;

        let plan = typed_in
            .with_input_fact(0, f32::fact([1, 1, bins, 2]).into())?
            .with_input_fact(1, f32::fact([state_size]).into())?
            .into_optimized()?
            .into_runnable()?;

        let mut planner = RealFftPlanner::<f64>::new();
        let r2c = planner.plan_fft_forward(win);
        let c2r = planner.plan_fft_inverse(win);

        Ok(Self {
            plan,
            sample_rate,
            win,
            hop,
            bins,
            window: vorbis_window(win),
            state: init_state.clone(),
            init_state,
            in_buf: Vec::new(),
            out_buf: vec![0.0; win],
            r2c,
            c2r,
            time: vec![0.0; win],
            spec: vec![Complex::new(0.0, 0.0); bins],
        })
    }

    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    pub fn hop(&self) -> usize {
        self.hop
    }

    pub fn win(&self) -> usize {
        self.win
    }

    /// Reset recurrent state and buffers.
    pub fn reset(&mut self) {
        self.state.copy_from_slice(&self.init_state);
        self.in_buf.clear();
        self.out_buf.fill(0.0);
    }

    /// Feed mono samples at the model rate; enhanced samples are appended to `out`.
    pub fn process(&mut self, chunk: &[f32], out: &mut Vec<f32>) -> Result<()> {
        self.in_buf.extend_from_slice(chunk);
        let mut pos = 0;
        while self.in_buf.len() - pos >= self.win {
            self.frame(pos, out)?;
            pos += self.hop;
        }
        self.in_buf.drain(..pos);
        Ok(())
    }

    /// Zero-pad the tail to one full window and emit up to one hop (matches `StreamEnhancer.flush`).
    pub fn flush(&mut self, out: &mut Vec<f32>) -> Result<()> {
        if self.in_buf.is_empty() {
            return Ok(());
        }
        let pad = vec![0.0f32; self.win - self.in_buf.len()];
        let mut tmp = Vec::new();
        self.process(&pad, &mut tmp)?;
        out.extend_from_slice(&tmp[..tmp.len().min(self.hop)]);
        Ok(())
    }

    fn frame(&mut self, pos: usize, out: &mut Vec<f32>) -> Result<()> {
        let (win, hop, bins) = (self.win, self.hop, self.bins);

        for i in 0..win {
            self.time[i] = (self.in_buf[pos + i] * self.window[i]) as f64;
        }
        self.r2c.process(&mut self.time, &mut self.spec)?;
        let mut ri = Vec::with_capacity(bins * 2);
        for c in &self.spec {
            ri.push(c.re as f32);
            ri.push(c.im as f32);
        }

        let result = self.plan.run(tvec!(
            Tensor::from_shape(&[1, 1, bins, 2], &ri)?.into(),
            Tensor::from_shape(&[self.state.len()], &self.state)?.into(),
        ))?;
        let spec_e = result[0].try_as_plain_ram()?.as_slice::<f32>()?;
        self.state
            .copy_from_slice(result[1].try_as_plain_ram()?.as_slice::<f32>()?);

        for (k, c) in self.spec.iter_mut().enumerate() {
            *c = Complex::new(spec_e[2 * k] as f64, spec_e[2 * k + 1] as f64);
        }
        // numpy irfft ignores the imaginary parts of DC and Nyquist.
        self.spec[0].im = 0.0;
        self.spec[bins - 1].im = 0.0;
        self.c2r.process(&mut self.spec, &mut self.time)?;

        let scale = 1.0 / win as f64;
        for i in 0..win {
            self.out_buf[i] += (self.time[i] * scale) as f32 * self.window[i];
        }
        out.extend_from_slice(&self.out_buf[..hop]);
        self.out_buf.copy_within(hop.., 0);
        self.out_buf[win - hop..].fill(0.0);
        Ok(())
    }
}

/// `dpdfnet.audio.vorbis_window`.
pub fn vorbis_window(len: usize) -> Vec<f32> {
    let half = len as f64 / 2.0;
    (0..len)
        .map(|i| {
            let s = (0.5 * std::f64::consts::PI * (i as f64 + 0.5) / half).sin();
            (0.5 * std::f64::consts::PI * s * s).sin() as f32
        })
        .collect()
}
