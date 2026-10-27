# voiceclean: local Krisp-like voice cleanup (CPU), Rust workspace

Goal: on-device voice/noise cancellation. Stack: DPDFNet (enhancement) -> TEN VAD -> AGC (WebRTC AGC2-style) -> limiter, later exposed as a virtual mic.
Target OS: **unknown** (asked, not answered). It decides the virtual-mic approach, so do not pick one without asking.

## Working rules (from the user)
- Never speculate about what code/API/system does. Verify (source, docs, output) or say it is unknown.
- Keep confirmed vs assumed explicit. Say what is needed to verify instead of guessing.

## Layout
- `crates/dpdf`: `StreamEnhancer`, Rust port of `dpdfnet.StreamEnhancer` (tract-onnx, realfft).
- `crates/voiceclean`: CLI. `voiceclean <model.onnx> <model_sample_rate> <in.wav> <out.wav>` (input must already be at the model rate).
- `tools/ref_dpdf.py`: builds a 48 kHz input and the Python reference output (needs `pip install dpdfnet`). `tools/parity.py`: compares two wavs.
- The earlier Python POC (`voiceclean.py`, `eval_set.py`, `ten-vad/`) is NOT in this repo.

## Confirmed (this repo, run 2026-10-27)
- 48 kHz HR model `dpdfnet2_48khz_hr` (from `Ceva-IP/DPDFNet` `onnx/`), clip `Noisy/arabic_office_snr5_rt60none_0_mixture_noisy.wav` of Ceva-IP/DPDFNet_EvalSet, resampled 16->48 kHz with librosa (129 s):
  Rust vs dpdfnet 0.6.0 `StreamEnhancer`: same length (6,188,160), max abs diff 8.9e-7 (ref peak 0.971), SNR 127 dB.
- Speed, 4-core sandbox, same clip: Rust RTF 0.324, Python/onnxruntime RTF 0.226 (measured in one loop each, chunk = hop). Model load 0.17 s. Other hardware untested.
- Algorithm read from dpdfnet 0.6.0 `stream.py`/`audio.py`/`onnx_backend.py`: win 960, hop 480, 481 bins, causal STFT, Vorbis window, state vector initialised from ONNX metadata_props, per-frame irfft with DC/Nyquist imag ignored, overlap-add, commit `hop` samples per frame.
- tract-onnx 0.23.9 API: `TypedRunnableModel` is not generic; outputs read via `try_as_plain_ram()?.as_slice::<f32>()`.
- Only the 48 kHz HR model has been run. The 16 kHz model is untested (the sample rate is passed in by the caller; it is not in the ONNX file).

## TEN VAD / sonora-agc2 (not started in this repo; from earlier notes, unverified here)
- TEN VAD C ABI: `ten_vad_create/process/destroy`, 16 kHz only, hop 160 or 256. Linking from Rust unverified.
- sonora-agc2 0.2.0 (pure Rust, edition 2024) exists on crates.io; its source was read previously but how upstream computes per-frame rms/peak/limiter-envelope inputs was NOT verified. Read the sources before wiring.

## Task list
1. [done] Port `StreamEnhancer`, parity check.
2. Link TEN VAD from Rust (FFI) and match the Python flags on the same audio.
3. Read sonora sources, wire AGC2 components with VAD probabilities; noise-aware gain cap.
4. Soft gate (floor -25 dB, attack 5 ms, release 80 ms, hangover 250 ms) + lookahead (start 30 ms).
5. 48 -> 16 kHz decimation for the VAD only.
6. Eval with the Rust pipeline; quiet/loud AGC test.
7. Real-time I/O (cpal) once the OS is known; then the virtual mic.
8. Later: AEC, WER check, listening tests.

## Build/run
```
cargo build --release
./target/release/voiceclean ~/.cache/dpdfnet/models/dpdfnet2_48khz_hr.onnx 48000 in48.wav out.wav
```
