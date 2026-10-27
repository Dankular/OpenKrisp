//! Streaming rational sample-rate converter (polyphase windowed-sinc), for matching device rates (44.1/48/96 kHz...)
//! to the engine's 48 kHz. Mono f32.
//!
//! For `in_rate -> out_rate` with `g = gcd`, output sample `k` sits at input time `k * (in/g) / (out/g)`; its value is a
//! Kaiser-windowed-sinc interpolation over `taps` input samples around that time. The lowpass cutoff is 0.46 of the
//! lower of the two rates, so down-conversion is alias-protected. Output sample `k` is aligned with input time `k`
//! (no shift); the cost is look-ahead latency: an output needs `taps / 2` input samples beyond its own time.

use anyhow::{Result, bail};

const KAISER_BETA: f64 = 8.0;
const MAX_PHASES: usize = 1024;

fn gcd(a: u32, b: u32) -> u32 {
    if b == 0 { a } else { gcd(b, a % b) }
}

fn bessel_i0(x: f64) -> f64 {
    let (mut sum, mut term) = (1.0, 1.0);
    for k in 1..50 {
        term *= (x / (2.0 * k as f64)).powi(2);
        sum += term;
    }
    sum
}

pub struct Resampler {
    num: u64, // input samples per `den` output samples (reduced)
    den: u64,
    taps: usize,
    /// `den` phases, each `taps` coefficients.
    table: Vec<f32>,
    /// Input history/pending samples; `buf[0]` is input sample index `base`.
    buf: Vec<f32>,
    base: i64,
    /// Next output index.
    k: u64,
}

impl Resampler {
    pub fn new(in_rate: u32, out_rate: u32) -> Result<Self> {
        if in_rate == 0 || out_rate == 0 {
            bail!("sample rates must be non-zero");
        }
        let g = gcd(in_rate, out_rate);
        let (num, den) = ((in_rate / g) as u64, (out_rate / g) as u64);
        if den as usize > MAX_PHASES {
            bail!("unsupported rate pair {in_rate} -> {out_rate} (too many phases)");
        }
        // Cutoff relative to the input rate, and kernel width in input samples.
        let ratio = out_rate as f64 / in_rate as f64;
        let fc = 0.46 * ratio.min(1.0);
        let zero_crossings = 32.0;
        let half = (zero_crossings / (2.0 * fc)).ceil();
        let taps = 2 * half as usize;

        let mut table = vec![0.0f32; den as usize * taps];
        for p in 0..den as usize {
            let frac = p as f64 / den as f64;
            let mut row = vec![0.0f64; taps];
            for (j, v) in row.iter_mut().enumerate() {
                // tap j multiplies input sample (i0 - half + 1 + j); distance from the output time:
                let x = (j as f64) - (half - 1.0) - frac;
                let sinc = if x == 0.0 {
                    2.0 * fc
                } else {
                    (2.0 * std::f64::consts::PI * fc * x).sin() / (std::f64::consts::PI * x)
                };
                let r = x / half;
                let w = if r.abs() >= 1.0 {
                    0.0
                } else {
                    bessel_i0(KAISER_BETA * (1.0 - r * r).sqrt()) / bessel_i0(KAISER_BETA)
                };
                *v = sinc * w;
            }
            let sum: f64 = row.iter().sum();
            for (j, v) in row.iter().enumerate() {
                table[p * taps + j] = (v / sum) as f32; // unity DC gain per phase
            }
        }
        // `taps - 1` zeros of history stand in for the samples before input index 0.
        Ok(Self {
            num,
            den,
            taps,
            table,
            buf: vec![0.0; taps - 1],
            base: -((taps - 1) as i64),
            k: 0,
        })
    }

    /// Look-ahead latency in input samples: an output is available once the input is this far past its time.
    pub fn latency_input_samples(&self) -> usize {
        self.taps / 2
    }

    pub fn process(&mut self, input: &[f32], out: &mut Vec<f32>) {
        self.buf.extend_from_slice(input);
        let half_minus_1 = (self.taps / 2 - 1) as i64;
        loop {
            let t = self.k * self.num; // output time in units of 1/den input samples
            let i0 = (t / self.den) as i64; // input index at or before the output time
            let phase = (t % self.den) as usize;
            // taps cover input indices i0 - half + 1 ..= i0 + half
            let first = i0 - half_minus_1;
            let start = first - self.base;
            let end = start + self.taps as i64;
            if end > self.buf.len() as i64 {
                break;
            }
            let w = &self.buf[start as usize..end as usize];
            let h = &self.table[phase * self.taps..(phase + 1) * self.taps];
            out.push(w.iter().zip(h).map(|(x, c)| x * c).sum());
            self.k += 1;
        }
        // drop samples no future output needs
        let next_first = ((self.k * self.num) / self.den) as i64 - half_minus_1 - self.base;
        if next_first > 0 {
            self.buf.drain(..next_first as usize);
            self.base += next_first;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tone(freq: f64, rate: u32, n: usize) -> Vec<f32> {
        (0..n)
            .map(|i| (2.0 * std::f64::consts::PI * freq * i as f64 / rate as f64).sin() as f32)
            .collect()
    }

    fn rms_db(x: &[f32]) -> f32 {
        10.0 * (x.iter().map(|v| v * v).sum::<f32>() / x.len() as f32).log10()
    }

    fn convert(x: &[f32], a: u32, b: u32) -> Vec<f32> {
        let mut r = Resampler::new(a, b).unwrap();
        let mut out = Vec::new();
        r.process(x, &mut out);
        out
    }

    #[test]
    fn passband_gain_is_unity_for_common_pairs() {
        for (a, b) in [
            (44100, 48000),
            (48000, 44100),
            (48000, 16000),
            (16000, 48000),
            (96000, 48000),
            (48000, 96000),
            (32000, 48000),
        ] {
            let f = 1000.0;
            let x = tone(f, a, a as usize); // 1 s
            let y = convert(&x, a, b);
            let skip = 2000.min(y.len() / 4);
            let g = rms_db(&y[skip..y.len() - skip]) - rms_db(&x[skip..x.len() - skip]);
            eprintln!("{a}->{b}: 1 kHz gain {g:.3} dB, out len {}", y.len());
            assert!(g.abs() < 0.05, "{a}->{b} gain {g}");
        }
    }

    #[test]
    fn output_length_matches_the_rate_ratio() {
        for (a, b) in [(44100, 48000), (48000, 44100), (48000, 16000)] {
            let n = a as usize * 2;
            let y = convert(&tone(500.0, a, n), a, b);
            let expect = n as f64 * b as f64 / a as f64;
            assert!(
                (y.len() as f64 - expect).abs() < 70.0,
                "{a}->{b}: {} vs {expect}",
                y.len()
            );
        }
    }

    #[test]
    fn downconversion_rejects_aliasing_tones() {
        // 48k -> 44.1k: a 23 kHz tone is above the 22.05 kHz Nyquist and must not fold into the band (at 21.1 kHz).
        let x = tone(23000.0, 48000, 48000);
        let y = convert(&x, 48000, 44100);
        let g = rms_db(&y[2000..y.len() - 2000]) - rms_db(&x[2000..x.len() - 2000]);
        eprintln!("23 kHz through 48->44.1: {g:.1} dB");
        assert!(g < -50.0, "alias at {g} dB");
    }

    #[test]
    fn chunking_does_not_change_the_output() {
        let x: Vec<f32> = tone(1234.0, 44100, 20000)
            .iter()
            .zip(tone(5600.0, 44100, 20000))
            .map(|(a, b)| a + 0.3 * b)
            .collect();
        let whole = convert(&x, 44100, 48000);
        let mut r = Resampler::new(44100, 48000).unwrap();
        let mut parts = Vec::new();
        for c in x.chunks(441) {
            r.process(c, &mut parts);
        }
        assert_eq!(whole, parts);
    }

    #[test]
    fn output_is_time_aligned_with_the_input() {
        // 48k -> 48k: an impulse at input 1000 appears at output 1000 (no shift), once enough later input has arrived.
        let mut x = vec![0.0f32; 4000];
        x[1000] = 1.0;
        let y = convert(&x, 48000, 48000);
        let peak = y
            .iter()
            .enumerate()
            .max_by(|a, b| a.1.abs().total_cmp(&b.1.abs()))
            .unwrap()
            .0;
        assert_eq!(peak, 1000);
        assert!(y[1000] > 0.9);
        // the last `latency` input samples cannot be turned into output yet
        let lat = Resampler::new(48000, 48000)
            .unwrap()
            .latency_input_samples();
        assert_eq!(y.len(), 4000 - lat);
    }

    #[test]
    fn rejects_zero_and_huge_phase_counts() {
        assert!(Resampler::new(0, 48000).is_err());
        assert!(Resampler::new(44101, 48000).is_err()); // gcd 1 -> 48000 phases
    }
}
