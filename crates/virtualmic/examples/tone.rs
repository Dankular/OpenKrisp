//! Windows spike for the MicyWMD driver: `tone [i32|f32|i16] [seconds]` plays a 440 Hz tone into the virtual mic,
//! paced in real time (10 ms writes). Select "MicyAudio" as the microphone in another app (or Sound settings
//! level meter) and see whether you hear a clean tone. If it is noise or wrong pitch, try another format.
#[cfg(not(windows))]
fn main() {
    eprintln!("this example only runs on Windows");
}

#[cfg(windows)]
fn main() -> anyhow::Result<()> {
    use virtualmic::convert::{SampleFormat, append_pcm};
    let a: Vec<String> = std::env::args().collect();
    let format = match a.get(1).map(String::as_str) {
        Some("f32") => SampleFormat::F32,
        Some("i16") => SampleFormat::I16,
        _ => SampleFormat::I32,
    };
    let secs: u64 = a.get(2).and_then(|v| v.parse().ok()).unwrap_or(10);
    let dev = virtualmic::driver::MicyDevice::open()?;
    let start = std::time::Instant::now();
    let (mut n, mut bytes) = (0u64, Vec::new());
    while start.elapsed().as_secs() < secs {
        let block: Vec<f32> = (0..480)
            .map(|i| {
                0.3 * (2.0 * std::f64::consts::PI * 440.0 * (n + i) as f64 / 48000.0).sin() as f32
            })
            .collect();
        bytes.clear();
        append_pcm(&block, format, 2, &mut bytes);
        dev.send(&bytes)?;
        n += 480;
        let due = std::time::Duration::from_secs_f64(n as f64 / 48000.0);
        if let Some(wait) = due.checked_sub(start.elapsed()) {
            std::thread::sleep(wait);
        }
    }
    Ok(())
}
