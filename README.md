# OpenKrisp (voiceclean)

Local, on-device voice/noise cleanup on CPU, written in Rust. Pipeline target:
DPDFNet (enhancement) -> TEN VAD (voice activity) -> AGC2-style gain -> limiter, later exposed as a virtual mic.

Status: early. File-based processing only; there is no real-time audio I/O or virtual mic yet.

## Workspace

| crate | what it is |
|---|---|
| `crates/dpdf` | Streaming DPDFNet enhancer (`StreamEnhancer`), a port of `dpdfnet.StreamEnhancer` on tract-onnx |
| `crates/tenvad` | Safe FFI wrapper over the TEN VAD C library (`libten_vad`) |
| `crates/resample` | Streaming 48 -> 16 kHz decimator (Kaiser-windowed-sinc, 97 taps) for the VAD side-path |
| `crates/agc` | AGC2 chain built from `sonora-agc2` components, driven by an external speech probability |
| `crates/voiceclean` | CLI (`voiceclean <model.onnx> <model_rate> <in.wav> <out.wav>`) and the `agc_demo` example |
| `tools/` | Python reference scripts used to check parity (`ref_dpdf.py`, `ref_vad.py`, `parity.py`) |

## Build

```
cargo build --release
```

Needs Rust >= 1.91 (sonora-agc2). The DPDFNet model is not in the repo; download
`dpdfnet2_48khz_hr.onnx` from `Ceva-IP/DPDFNet` (`onnx/` folder) on Hugging Face.

```
./target/release/voiceclean dpdfnet2_48khz_hr.onnx 48000 in48.wav out.wav
```

Input must already be at the model's sample rate (no resampling yet).

### TEN VAD (optional `vad` feature)

`libten_vad` is not vendored: its license adds conditions beyond Apache-2.0 (see the LICENSE in
https://github.com/TEN-framework/ten-vad; read it before deploying). Get the library yourself, then:

```
export TEN_VAD_LIB_DIR=/path/to/ten-vad/lib/Linux/x64   # dir containing libten_vad.so
export LD_LIBRARY_PATH=$TEN_VAD_LIB_DIR
cargo run --release -p voiceclean --features vad --example agc_demo -- enhanced48k.wav out.wav [dump16k.wav]
```

On Linux the `.so` also needs `libc++1` and `libc++abi1`.

## Verified so far (Linux x86-64, 4-core sandbox, 2026-10-27)

- **DPDFNet port:** vs dpdfnet 0.6.0 `StreamEnhancer` on a 129 s clip (DPDFNet_EvalSet `arabic_office_snr5`, resampled to 48 kHz):
  same length, max abs diff 8.9e-7, SNR 127 dB. RTF 0.324 (Python/onnxruntime 0.226). Only the 48 kHz HR model has been run.
- **TEN VAD wrapper:** per-frame output is byte-identical to the upstream Python wrapper on the same 16 kHz clip at hop 256 and hop 160.
  At hop 160 the first call returns `(-1.0, -1)`; at hop 256 it returned a normal decision.
- **AGC wiring:** mirrors `GainController2::process` from `sonora` 0.2.0 (read from source). The noise-aware gain cap is part of
  `AdaptiveDigitalGainController`. Unit tests: +26.2 dB gain over a -80 dBFS floor; 0.0 dB over a -45 dBFS floor
  (`max_output_noise_level_dbfs -50` minus noise, clamped at 0), as the code predicts.
- **Decimator:** passband (200 Hz-6 kHz) within 0.2 dB, response below -80 dB from 9 kHz up (computed from the taps), 7.5 kHz is the -6 dB point,
  group delay 48 input samples (1 ms), output independent of chunking. Against scipy `resample_poly` on the same enhanced audio, TEN VAD flags agree on
  99.2% of frames (mean |dprob| 0.048, max 0.33; scipy's output is delay-compensated, ours is not, so part of that is the 1 ms offset).
- **VAD on the enhanced signal (129 s clip, `agc_demo`):** TEN VAD flags 31.4% of frames at threshold 0.5 and 12.5% reach probability 0.95
  (on the noisy source it was 34.4% / 3.2%). Probability deciles are bimodal (17.8% in 0-0.1, 40.1% in 0.1-0.2, 19.1% in 0.9-1.0).

## AGC speech probability: what is and is not known

AGC2 treats frames as speech only at probability >= 0.95 (`VAD_CONFIDENCE_THRESHOLD`, tuned for sonora's RNN VAD).
- The small gain on the test clip (mean ~2 dB) is explained by the loud input: final frame speech level -25.8 dBFS + headroom 19.0 dB gives
  target gain = -5 - (-25.8 + 19.0) = +1.8 dB (formula from `adaptive_digital_gain_controller.rs`).
- Same clip attenuated by 30 dB: with the raw probability the gain rises 18 -> 29 -> 30.5 dB (thirds of the clip, 6 dB/s slew limit);
  feeding the TEN VAD flag (1/0) instead gives 32-34 dB and a lower speech-level estimate (-63 vs -54 dBFS) because lower-confidence frames count as speech.
- Which mapping is better is **not determined**: it needs a listening test or a reference. `agc_demo` has the knobs
  (`AGC_DEMO_ATTEN_DB`, `AGC_DEMO_MAP=flag`).

## Not done yet

Soft gate and lookahead, evaluation set, real-time I/O, virtual mic (target OS undecided), AEC.
