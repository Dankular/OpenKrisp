//! `live <model.onnx>`: default microphone -> full pipeline -> default speakers, until Enter is pressed.
//! Prints worker statistics once per second. USE HEADPHONES (speakers into the mic will feed back; there is no AEC).
//! Not tested on real audio hardware in this repo's sandbox: compile-checked only.
use anyhow::Result;
use pipeline::{Config, Pipeline, Vad};

struct Ten(tenvad::TenVad);
impl Vad for Ten {
    fn process(&mut self, f: &[i16]) -> Result<(f32, bool)> {
        let (p, flag) = self.0.process(f)?;
        Ok((p, flag == 1))
    }
}

fn main() -> Result<()> {
    let model = std::env::args()
        .nth(1)
        .ok_or_else(|| anyhow::anyhow!("usage: live <model.onnx>"))?;
    let p = Pipeline::new(
        std::path::Path::new(&model),
        Ten(tenvad::TenVad::new(160, 0.5)?),
        Config::default(),
    )?;
    let (in_tx, in_rx) = live::ring(48000);
    let (out_tx, out_rx) = live::ring(48000);
    let worker = live::Worker::spawn(p, in_rx, out_tx, 960);
    let dev = live::devices::open_default(in_tx, out_rx)?;
    println!(
        "in:  {}\nout: {}\nrunning, press Enter to stop (use headphones)",
        dev.input_name, dev.output_name
    );

    let stats = worker.stats.clone();
    let stop = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    let s2 = stop.clone();
    std::thread::spawn(move || {
        let mut line = String::new();
        let _ = std::io::stdin().read_line(&mut line);
        s2.store(true, std::sync::atomic::Ordering::Relaxed);
    });
    use std::sync::atomic::Ordering::Relaxed;
    while !stop.load(Relaxed) {
        std::thread::sleep(std::time::Duration::from_secs(1));
        println!(
            "blocks {}  output_dropped {}  worst block {} us",
            stats.blocks.load(Relaxed),
            stats.output_dropped.load(Relaxed),
            stats.max_block_us.load(Relaxed)
        );
    }
    drop(dev);
    worker.stop()
}
