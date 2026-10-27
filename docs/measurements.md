# Measurements and verification notes

Everything here was run on a 4-core Linux x86-64 sandbox on 2026-10-27. Other hardware and Windows are untested.
Test clip: `arabic_office_snr5_rt60none_0_mixture_noisy.wav` from `Ceva-IP/DPDFNet_EvalSet`, 129 s, resampled 16 -> 48 kHz.

## DPDFNet enhancer (`crates/dpdf`)
- Against dpdfnet 0.6.0 `StreamEnhancer` on the same input: identical length (6,188,160 samples), max abs difference 8.9e-7 (peak 0.971), SNR 127 dB.
- Speed: real-time factor 0.324 for the Rust port vs 0.226 for Python/onnxruntime. Model load 0.17 s.
- Only the 48 kHz high-resolution model (`dpdfnet2_48khz_hr`) has been run.

## TEN VAD wrapper (`crates/tenvad`)
- Per-frame output is byte-identical to the upstream Python wrapper on the same 16 kHz clip at hop 256 and hop 160.
- At hop 160 the first call returns `(-1.0, -1)` (no decision yet). At hop 256 the first call returned a normal decision.

## Decimator (`crates/resample`)
- Passband 200 Hz to 6 kHz within 0.2 dB; response below -80 dB from 9 kHz up (computed from the filter taps); -6 dB at 7.5 kHz; group delay 48 input samples (1 ms).
- Against scipy `resample_poly` on the enhanced audio, TEN VAD flags agree on 99.2% of frames (mean probability difference 0.048, max 0.33). Part of that is the 1 ms delay, which scipy compensates and this filter does not.

## AGC (`crates/agc`)
- The wiring follows `GainController2::process` in `sonora` 0.2.0, read from source.
- Unit tests: +26.2 dB gain over a -80 dBFS noise floor; 0.0 dB over a -45 dBFS floor. That matches the noise cap in the controller (max output noise -50 dBFS minus noise level, floored at 0).

## Which speech probability should drive the AGC?
AGC2 only counts a frame as speech at probability 0.95 or higher. That number was tuned for sonora's own VAD, not TEN VAD.

On the enhanced clip, TEN VAD flags 31.4% of frames at its own 0.5 threshold, and 12.5% reach 0.95. The probabilities are bimodal (17.8% of frames under 0.1, 40.1% in 0.1 to 0.2, 19.1% above 0.9). On the unenhanced source the same figures were 34.4% and 3.2%.

The low gain on the test clip (about 2 dB) is explained by the loud input. At the last frame the speech level was -25.8 dBFS and the headroom 19.0 dB, so target gain = -5 - (-25.8 + 19.0) = +1.8 dB.

The same clip attenuated by 30 dB, by third of the clip:

| AGC driven by | gain |
|---|---|
| raw probability (0.95 rule) | 18.3, 29.0, 30.5 dB |
| TEN VAD flag as 1/0 | 31.8, 34.2, 33.0 dB |

With the flag, the speech-level estimate came out lower (-63 vs -54 dBFS) because lower-confidence frames count as speech. Which is better is not determined; it needs a listening test or a reference. `agc_demo` has the switches (`AGC_DEMO_ATTEN_DB`, `AGC_DEMO_MAP=flag`).
