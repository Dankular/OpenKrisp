//! Streaming 3:1 decimator: linear-phase Kaiser-windowed-sinc lowpass, then keep every 3rd sample.
//! Intended for the VAD side-path only (the audio path stays at 48 kHz).
//!
//! Group delay is `(TAPS - 1) / 2` input samples (48 at 48 kHz = 1 ms). The first output corresponds to
//! input sample 0 with the filter's history initialised to zeros.

const FACTOR: usize = 3;
/// Odd, so the filter is symmetric with an integer delay.
const TAPS: usize = 97;
/// Passband edge 0.14 * fs_in = 6.7 kHz; stopband starts at the 16 kHz Nyquist (8 kHz) minus margin via the window.
const CUTOFF: f64 = 7500.0 / 48000.0;
const KAISER_BETA: f64 = 8.0;

fn bessel_i0(x: f64) -> f64 {
    let (mut sum, mut term) = (1.0, 1.0);
    for k in 1..50 {
        term *= (x / (2.0 * k as f64)).powi(2);
        sum += term;
    }
    sum
}

fn design() -> [f32; TAPS] {
    let m = (TAPS - 1) as f64 / 2.0;
    let mut h = [0.0f64; TAPS];
    for (i, v) in h.iter_mut().enumerate() {
        let n = i as f64 - m;
        let sinc = if n == 0.0 {
            2.0 * CUTOFF
        } else {
            (2.0 * std::f64::consts::PI * CUTOFF * n).sin() / (std::f64::consts::PI * n)
        };
        let r = n / m;
        let w = bessel_i0(KAISER_BETA * (1.0 - r * r).max(0.0).sqrt()) / bessel_i0(KAISER_BETA);
        *v = sinc * w;
    }
    let sum: f64 = h.iter().sum();
    let mut out = [0.0f32; TAPS];
    for (o, v) in out.iter_mut().zip(h) {
        *o = (v / sum) as f32; // unity DC gain
    }
    out
}

pub struct Decimate3 {
    h: [f32; TAPS],
    /// Last TAPS-1 input samples (zeros initially) followed by pending input.
    buf: Vec<f32>,
    /// Phase: index into the stream (relative to `buf`) of the next output's newest sample.
    next: usize,
}

impl Default for Decimate3 {
    fn default() -> Self {
        Self::new()
    }
}

impl Decimate3 {
    pub fn new() -> Self {
        Self {
            h: design(),
            buf: vec![0.0; TAPS - 1],
            next: TAPS - 1,
        }
    }

    /// Input samples of group delay.
    pub fn delay_input_samples(&self) -> usize {
        (TAPS - 1) / 2
    }

    /// Feed 48 kHz samples; 16 kHz samples are appended to `out`.
    pub fn process(&mut self, input: &[f32], out: &mut Vec<f32>) {
        self.buf.extend_from_slice(input);
        // Output k uses buf[next-(TAPS-1) ..= next]; the first has index TAPS-1 (input sample 0).
        while self.next < self.buf.len() {
            let w = &self.buf[self.next + 1 - TAPS..=self.next];
            out.push(w.iter().zip(self.h.iter().rev()).map(|(x, h)| x * h).sum());
            self.next += FACTOR;
        }
        let drop = self.next + 1 - TAPS;
        self.buf.drain(..drop);
        self.next -= drop;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tone(freq: f32, n: usize) -> Vec<f32> {
        (0..n)
            .map(|i| (2.0 * std::f64::consts::PI * freq as f64 * i as f64 / 48000.0).sin() as f32)
            .collect()
    }

    fn rms_db(x: &[f32]) -> f32 {
        10.0 * (x.iter().map(|v| v * v).sum::<f32>() / x.len() as f32).log10()
    }

    fn run(x: &[f32]) -> Vec<f32> {
        let mut d = Decimate3::new();
        let mut out = Vec::new();
        d.process(x, &mut out);
        out
    }

    #[test]
    fn passband_is_flat() {
        for f in [200.0, 1000.0, 4000.0, 6000.0] {
            let y = run(&tone(f, 48000));
            let g = rms_db(&y[200..]) - rms_db(&tone(f, 48000)[600..]);
            eprintln!("{f} Hz: {g:.3} dB");
            assert!(g.abs() < 0.2, "{f} Hz gain {g}");
        }
    }

    #[test]
    fn aliasing_band_is_rejected() {
        // 10, 14, 20 kHz would alias into 6 / 2 / 4 kHz at 16 kHz without the lowpass.
        for f in [9000.0, 10000.0, 14000.0, 20000.0] {
            let y = run(&tone(f, 48000));
            let g = rms_db(&y[200..]) - rms_db(&tone(f, 48000)[600..]);
            eprintln!("{f} Hz: {g:.1} dB");
            assert!(g < -60.0, "{f} Hz only {g} dB");
        }
    }

    #[test]
    fn chunking_does_not_change_output() {
        let x: Vec<f32> = tone(1234.0, 9999)
            .iter()
            .zip(tone(5500.0, 9999))
            .map(|(a, b)| a + 0.3 * b)
            .collect();
        let whole = run(&x);
        let mut d = Decimate3::new();
        let mut parts = Vec::new();
        for c in x.chunks(480) {
            d.process(c, &mut parts);
        }
        assert_eq!(whole, parts);
        assert_eq!(whole.len(), x.len().div_ceil(3));
    }

    #[test]
    fn delay_is_as_documented() {
        let mut x = vec![0.0f32; 4800];
        x[2400] = 1.0;
        let y = run(&x);
        let peak = y
            .iter()
            .enumerate()
            .max_by(|a, b| a.1.abs().total_cmp(&b.1.abs()))
            .unwrap()
            .0;
        // impulse at 2400 + delay 48 = 2448 -> output index 816
        assert_eq!(peak, (2400 + Decimate3::new().delay_input_samples()) / 3);
    }
}

#[cfg(test)]
mod response {
    #[test]
    fn stopband_from_9khz_is_below_minus_80_db() {
        let h = super::design();
        for f in [
            7000.0f64, 7500.0, 8000.0, 9000.0, 12000.0, 16000.0, 20000.0, 23000.0,
        ] {
            let (mut re, mut im) = (0.0f64, 0.0f64);
            for (n, &c) in h.iter().enumerate() {
                let a = -2.0 * std::f64::consts::PI * f / 48000.0 * n as f64;
                re += c as f64 * a.cos();
                im += c as f64 * a.sin();
            }
            let db = 20.0 * (re * re + im * im).sqrt().log10();
            eprintln!("H({f}) = {db:.1} dB");
            if f >= 9000.0 {
                assert!(db < -80.0, "H({f}) = {db} dB");
            }
        }
    }
}
