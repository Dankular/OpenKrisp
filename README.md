# OpenKrisp

OpenKrisp is an open-source, on-device voice cleanup tool in the spirit of Krisp. It removes background noise from your
microphone, and from the audio you hear on a call, using a neural speech enhancer running on the CPU. Nothing leaves your machine.

It is written in Rust, with a Windows control app (WinUI 3) on top. It is an early project: the audio processing is measured and tested,
the Windows app has been built and launched, and local CUDA ASR has processed audio. Acoustic echo attenuation on a selected microphone/speaker setup remains unmeasured.

## What it does today

- **Noise removal.** [DPDFNet](https://huggingface.co/Ceva-IP/DPDFNet) (a small streaming speech-enhancement model) runs through a pure-Rust ONNX runtime.
  The Rust port matches the reference Python implementation to within rounding error.
- **Two independent lanes.** One for your microphone ("cancel my noise") and one for incoming audio ("cancel others' noise"), each switchable on and off
  at any time without a glitch in timing.
- **Level control.** A soft noise gate with lookahead, then an automatic gain stage and limiter (built from the WebRTC-derived
  [`sonora-agc2`](https://crates.io/crates/sonora-agc2) parts), driven by the [TEN VAD](https://github.com/TEN-framework/ten-vad) voice detector.
- **Live audio plumbing (not yet run on real devices).** Capture a microphone, clean it, and send it to the MicyWMD virtual microphone driver or a playback device;
  optionally capture a virtual speaker, clean it, and play it on your real speakers. Handles 44.1/48/96 kHz devices by resampling.
- **A Windows app** with a Noise cancellation tab (the two switches, voice isolation Off/Auto, device choices, Start/Stop) and a Note taker tab
  (action items grouped by meeting, entered by hand for now).
- **A file tool** to run the noise removal over a WAV file from the command line.

## What it does not do yet

- **Personal voiceprint isolation.** The Windows app has a selectable microphone / speaker-reference / virtual-output path using Windows acoustic echo cancellation followed by DPDFNet. Filtering unrelated nearby voices by personal voice identity is not implemented.
- **Cleaning incoming audio needs a virtual speaker.** The incoming lane captures what a playback device plays. Capturing and playing on the same device would loop,
  so applications must play into a virtual speaker, which OpenKrisp captures and cleans before playing on your real speakers. No virtual speaker driver is chosen or included.
  (The microphone side uses MicyWMD, which provides only a virtual microphone.)
- **Meeting and lecture notes.** Local recording, Whistle transcription on Tract CUDA in seven languages, Smart Turn semantic turn detection, live/final speaker labels, searchable transcripts, tags and filters. Each transcript has a Summarize button for local BART key points and topic sections; automatic source-backed tasks, decisions and follow-ups have their own dashboard; see [app/README.md](app/README.md).
- **A downloadable release.** There is none yet; see [Getting a Windows build](#getting-a-windows-build).
- **Hardware validation limits.** Build/launch and real CUDA transcription have been checked. Acoustic echo attenuation for the selected devices and the MicyWMD driver path have not been validated.

## Getting a Windows build

A GitHub Actions workflow that tests everything, builds the Windows app and publishes a pre-release zip when a `v*` tag is pushed is written and ready:
[`packaging/github-workflow-ci.yml`](packaging/github-workflow-ci.yml). It could not be added at `.github/workflows/ci.yml` by the tooling that wrote it
(the credential lacked GitHub's `workflow` scope). To switch it on, copy that file to `.github/workflows/ci.yml` and push (the GitHub web editor works).
Then push a tag such as `v0.1.0-alpha.1` and the release appears with `OpenKrisp-win-x64.zip`. Until that has run, the Windows build is unproven.

## Try it

You need Rust 1.91 or newer, and the model file `dpdfnet2_48khz_hr.onnx` from the
[`Ceva-IP/DPDFNet`](https://huggingface.co/Ceva-IP/DPDFNet) repository (in its `onnx/` folder).

```
cargo build --release
./target/release/voiceclean dpdfnet2_48khz_hr.onnx 48000 noisy48k.wav clean.wav
```

The input must be a mono WAV already at 48 kHz (16 or 32-bit float PCM). Output is 32-bit float WAV.

### Full pipeline (optional)

This needs the TEN VAD shared library, which is not included here because its license has extra conditions
(read the LICENSE in the [ten-vad repo](https://github.com/TEN-framework/ten-vad) before you ship anything). Download the library, then:

```
export TEN_VAD_LIB_DIR=/path/to/ten-vad/lib/Linux/x64     # folder containing libten_vad.so
export LD_LIBRARY_PATH=$TEN_VAD_LIB_DIR
cargo run --release -p voiceclean --features vad --example pipeline_run -- dpdfnet2_48khz_hr.onnx in48k.wav out48k.wav
```

Flags: `--no-gate`, `--no-agc`, `--lookahead-ms N`, `--agc-initial-gain-db N`. On Linux the library also needs `libc++1` and `libc++abi1`.

### The Windows app

See [app/README.md](app/README.md). In short: build the engine library (`cargo build --release -p openkrisp-engine --features audio`), then `dotnet build app/OpenKrisp.App`.

## How it fits together

```
 microphone ─► resample to 48 kHz ─► engine mic lane ────────► MicyWMD virtual mic  (or a playback device)
                                      (noise removal on/off)

 virtual speaker ─► loopback ─► resample ─► engine speaker lane ─► resample ─► your real speakers
 (apps play here)                            (noise removal on/off)

 offline / file pipeline:
 audio ─► DPDFNet ─► (3:1 decimator ─► TEN VAD ─► speech decision) ─► soft gate ─► gain control ─► limiter
```

| crate | role |
|---|---|
| `crates/dpdf` | Streaming DPDFNet enhancer |
| `crates/engine` | The two lanes, on/off controls, live audio sessions, and a C API for the app |
| `crates/live` | Lock-free ring buffers, worker threads, and the cpal device layer |
| `crates/resample` | Sample-rate converters (48 to 16 kHz for the VAD; general rates for devices) |
| `crates/tenvad` | Safe Rust wrapper over the TEN VAD C library |
| `crates/gate` | Soft noise gate with lookahead |
| `crates/agc` | Gain control and limiter |
| `crates/pipeline` | The full chain above, wired together |
| `crates/virtualmic` | Sender for the MicyWMD virtual microphone driver (Windows) |
| `crates/voiceclean` | Command-line tool and examples |
| `app/` | Windows control app and its tested logic library (see [app/README.md](app/README.md)) |
| `tools/` | Python scripts for the evaluation and for checking against reference implementations |

## Status

What has been measured (Linux, 6 noisy test clips, details in [docs/eval.md](docs/eval.md) and [docs/measurements.md](docs/measurements.md)):

| | SI-SDR | PESQ-wb | noise-only level |
|---|---|---|---|
| noisy input | 0.5 dB | 1.24 | -30.1 dBFS |
| noise removal only | 13.7 dB | 3.06 | -63.6 dBFS |
| full pipeline (gate + gain control) | 12.9 dB | 3.06 | -63.1 dBFS |

- The gain stage cost most of its SI-SDR by default (5.0 dB) because AGC2 starts at +15 dB; starting at 0 dB, now the default, recovers it. A remaining 0.8 dB
  loss is the gain adapting to level. The noise gate adds almost nothing on these clips.
- The noise-removal port, VAD wrapper, decimator and general resampler each match a reference or pass tone tests. The app's logic is covered by 24 tests (Linux),
  including real calls into the Rust library.
- Compiles for Windows (checked from Linux), but nothing has been run there. Live audio, the virtual microphone path, and the WinUI app are unverified.
- Open questions: which sample format the MicyWMD driver wants (test plan in [docs/virtual-mic.md](docs/virtual-mic.md)), and whether TEN VAD's probabilities suit AGC2's 0.95 speech threshold.

## Licenses

This repository does not have a license file yet, so the code's license is not decided (the `license` field in the Cargo manifests is a placeholder).
Dependencies and models carry their own licenses: the Windows app bundles the Inter font (SIL Open Font License 1.1, text in `app/OpenKrisp.App/Assets/Fonts/Inter-OFL.txt`), `sonora-agc2` is BSD-3-Clause, TEN VAD has extra conditions (see above), and the DPDFNet model's
license should be checked before you redistribute it.
