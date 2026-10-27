//! `vad_dump <in16k.wav> <hop> <threshold>`: print `[i] prob, flag` per hop (same format as ten-vad examples/test.py).
use anyhow::{Result, bail};

fn main() -> Result<()> {
    let a: Vec<String> = std::env::args().skip(1).collect();
    let [path, hop, thr] = a.as_slice() else {
        bail!("usage: vad_dump <in16k.wav> <hop> <threshold>")
    };
    let hop: usize = hop.parse()?;
    let mut r = hound::WavReader::open(path)?;
    if r.spec().sample_rate != 16000 || r.spec().channels != 1 || r.spec().bits_per_sample != 16 {
        bail!("need 16 kHz mono 16-bit wav, got {:?}", r.spec());
    }
    let data: Vec<i16> = r.samples::<i16>().collect::<Result<_, _>>()?;
    let mut vad = tenvad::TenVad::new(hop, thr.parse()?)?;
    for (i, f) in data.chunks_exact(hop).enumerate() {
        let (p, flag) = vad.process(f)?;
        println!("[{i}] {p:.6}, {flag}");
    }
    Ok(())
}
