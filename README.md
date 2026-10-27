# OpenKrisp

OpenKrisp is an open-source, on-device voice cleanup tool in the spirit of Krisp. It removes background noise from your
microphone, and from the audio you hear on a call, using a neural speech enhancer running on the CPU. Nothing leaves your machine.

It is written in Rust. A Windows control app (WinUI 3) is planned; see [Status](#status).

## What it does today

- **Noise removal.** [DPDFNet](https://huggingface.co/Ceva-IP/DPDFNet) (a small streaming speech-enhancement model) runs through a pure-Rust ONNX runtime.
  The Rust port matches the reference Python implementation to within rounding error.
- **Two independent lanes.** One for your microphone ("cancel my noise") and one for incoming audio ("cancel others' noise"), each switchable on and off
  at any time without a glitch in timing.
- **Level control.** An automatic gain stage and limiter (built from the WebRTC-derived [`sonora-agc2`](https://crates.io/crates/sonora-agc2) parts) driven by the
  [TEN VAD](https://github.com/TEN-framework/ten-vad) voice detector.
- **A file tool** to run the enhancer over a WAV file from the command line.

## What it does not do yet

- No live microphone or speaker capture, and no virtual microphone device. Today it processes files and blocks of samples handed to it.
- No separation of other people's voices ("voice isolation"). The setting exists in the engine, but nothing implements it yet.
- No meeting notes, transcription or action items yet. A local speech-to-text and local LLM summarizer are planned, with none chosen or tested so far.
- No Windows app yet. Nothing in this repo has been built or run on Windows.

## Try it

You need Rust 1.91 or newer, and the model file `dpdfnet2_48khz_hr.onnx` from the
[`Ceva-IP/DPDFNet`](https://huggingface.co/Ceva-IP/DPDFNet) repository (in its `onnx/` folder).

```
cargo build --release
./target/release/voiceclean dpdfnet2_48khz_hr.onnx 48000 noisy48k.wav clean.wav
```

The input must be a mono WAV already at 48 kHz (16 or 32-bit float PCM). Output is 32-bit float WAV.

### Level control demo (optional)

This needs the TEN VAD shared library, which is not included here because its license has extra conditions
(read the LICENSE in the [ten-vad repo](https://github.com/TEN-framework/ten-vad) before you ship anything). Download the library, then:

```
export TEN_VAD_LIB_DIR=/path/to/ten-vad/lib/Linux/x64     # folder containing libten_vad.so
export LD_LIBRARY_PATH=$TEN_VAD_LIB_DIR
cargo run --release -p voiceclean --features vad --example agc_demo -- enhanced48k.wav out.wav
```

On Linux the library also needs `libc++1` and `libc++abi1`.

## How it fits together

```
mic or call audio (48 kHz)
   |
   +--> DPDFNet enhancer ---------------------------> cleaned audio
   |                                                       |
   +--> 3:1 decimator --> TEN VAD --> speech probability --+--> gain control --> limiter
        (16 kHz, VAD only)
```

| crate | role |
|---|---|
| `crates/dpdf` | Streaming DPDFNet enhancer |
| `crates/engine` | The two lanes, on/off controls and a C API for a UI to call |
| `crates/tenvad` | Safe Rust wrapper over the TEN VAD C library |
| `crates/resample` | 48 to 16 kHz decimator for feeding the VAD |
| `crates/agc` | Gain control and limiter |
| `crates/voiceclean` | Command-line tool and demo |
| `tools/` | Python scripts used to check results against reference implementations |

## Status

Early. The enhancer, VAD wrapper, decimator and gain stage have each been checked against a reference or by tests; the details and numbers are in
[docs/measurements.md](docs/measurements.md). Open questions are listed there too, including how to drive the gain stage from TEN VAD's probabilities.

Planned next: soft gate and lookahead, an evaluation set run, live audio I/O, a virtual microphone, the Windows control app, and a local notes and action-items feature.

## Licenses

This repository does not have a license file yet, so the code's license is not decided (the `license` field in the Cargo manifests is a placeholder).
Dependencies and models carry their own licenses: `sonora-agc2` is BSD-3-Clause, TEN VAD has extra conditions (see above), and the DPDFNet model's
license should be checked before you redistribute it.
