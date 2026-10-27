# OpenKrisp (voiceclean)

Local, on-device voice/noise cleanup on CPU, written in Rust. Pipeline target:
DPDFNet (enhancement) -> TEN VAD (voice activity) -> AGC2-style gain -> limiter, later exposed as a virtual mic.

Status: early. File-based processing only; there is no real-time audio I/O or virtual mic yet.

## Workspace

| crate | what it is |
|---|---|
| `crates/dpdf` | Streaming DPDFNet enhancer (`StreamEnhancer`), a port of `dpdfnet.StreamEnhancer` on tract-onnx |
| `crates/tenvad` | Safe FFI wrapper over the TEN VAD C library (`libten_vad`) |
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
cargo run --release -p voiceclean --features vad --example agc_demo -- enhanced48k.wav vad_source16k.wav out.wav
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

## Open issue

AGC2 treats frames as speech only when probability >= 0.95 (`VAD_CONFIDENCE_THRESHOLD`). That constant was tuned
for sonora's RNN VAD. On the test clip TEN VAD flags 34.4% of frames at its own 0.5 threshold but only 3.2% reach 0.95, and the mean
applied gain on those frames was 1.2 dB. Why, and what mapping is right, has not been investigated.

## Not done yet

48->16 kHz decimation for the VAD, soft gate and lookahead, evaluation set, real-time I/O, virtual mic (target OS undecided), AEC.
