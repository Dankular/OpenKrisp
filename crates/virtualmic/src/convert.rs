//! Mono f32 (our pipeline's format) -> interleaved little-endian PCM bytes for the driver endpoint.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SampleFormat {
    /// 32-bit signed integer PCM (what the endpoint's PCM subtype GUID suggests).
    I32,
    /// 32-bit IEEE float (what a source comment suggests).
    F32,
    /// 16-bit signed integer PCM.
    I16,
}

impl SampleFormat {
    pub fn bytes_per_sample(self) -> usize {
        match self {
            SampleFormat::I32 | SampleFormat::F32 => 4,
            SampleFormat::I16 => 2,
        }
    }
}

/// Appends `mono` to `out` as `channels` identical interleaved channels. Input is clamped to [-1, 1].
pub fn append_pcm(mono: &[f32], format: SampleFormat, channels: usize, out: &mut Vec<u8>) {
    out.reserve(mono.len() * channels * format.bytes_per_sample());
    for &x in mono {
        let x = x.clamp(-1.0, 1.0);
        for _ in 0..channels {
            match format {
                SampleFormat::I32 => {
                    out.extend_from_slice(&((x as f64 * 2147483647.0).round() as i32).to_le_bytes())
                }
                SampleFormat::F32 => out.extend_from_slice(&x.to_le_bytes()),
                SampleFormat::I16 => {
                    out.extend_from_slice(&((x * 32767.0).round() as i16).to_le_bytes())
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stereo_i32_layout_and_scale() {
        let mut b = Vec::new();
        append_pcm(&[0.0, 1.0, -1.0], SampleFormat::I32, 2, &mut b);
        assert_eq!(b.len(), 3 * 2 * 4);
        let s: Vec<i32> = b
            .chunks_exact(4)
            .map(|c| i32::from_le_bytes(c.try_into().unwrap()))
            .collect();
        assert_eq!(s, vec![0, 0, i32::MAX, i32::MAX, -i32::MAX, -i32::MAX]);
    }

    #[test]
    fn f32_roundtrip_and_clamp() {
        let mut b = Vec::new();
        append_pcm(&[0.25, 3.0], SampleFormat::F32, 1, &mut b);
        let s: Vec<f32> = b
            .chunks_exact(4)
            .map(|c| f32::from_le_bytes(c.try_into().unwrap()))
            .collect();
        assert_eq!(s, vec![0.25, 1.0]);
    }

    #[test]
    fn i16_scale() {
        let mut b = Vec::new();
        append_pcm(&[0.5], SampleFormat::I16, 1, &mut b);
        assert_eq!(i16::from_le_bytes([b[0], b[1]]), 16384);
    }
}
