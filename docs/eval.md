# Evaluation: pipeline variants on 6 DPDFNet_EvalSet clips

Run with `tools/eval.py` (raw numbers in [eval_results.csv](eval_results.csv)). Linux x86-64 sandbox, 2026-10-27.

**Clip set.** Six clips chosen by us from `Ceva-IP/DPDFNet_EvalSet` at SNR 5: german_airport, french_pub, spanish_restaurant, arabic_office, polish_street,
turkish_car. This is not the six-clip set used in the earlier Python proof of concept (that set is not in this repo), so these numbers are not
comparable with the earlier ones. Six clips is a small sample; no significance testing was done.

**Method.** Noisy 16 kHz clip -> 48 kHz -> pipeline (TEN VAD hop 160, threshold 0.5) -> back to 16 kHz. Each output is aligned to the clean reference by
cross-correlation (+-150 ms), then scored: SI-SDR, STOI, PESQ wide-band, and "noise-only level". The noise-only level is the mean-power level of the output
in frames where the clean reference is at least 40 dB below its own 95th-percentile frame level (74% of frames on average). That mask definition is ours.

## Averages

| variant | SI-SDR (dB) | STOI | PESQ-wb | noise-only (dBFS) |
|---|---|---|---|---|
| noisy input | 0.54 | 0.823 | 1.24 | -30.1 |
| enhancement only | 13.73 | 0.930 | 3.06 | -63.6 |
| + gate, no lookahead | 13.63 | 0.929 | 3.03 | -63.9 |
| + gate, 30 ms lookahead | 13.73 | 0.930 | 3.06 | -63.7 |
| full (gate + AGC + limiter) | 5.00 | 0.929 | 3.03 | -58.3 |

Measured signal delay (from the alignment): 40 ms for enhancement alone, 70 ms with the 30 ms lookahead.

## What the numbers do and do not show

- **The enhancer does most of the work.** SI-SDR 0.5 -> 13.7 dB, PESQ 1.24 -> 3.06, noise-only level down by about 33 dB.
- **The gate adds almost nothing here.** Noise-only level improves by 0.1 to 0.3 dB over enhancement alone. The earlier Python proof of concept reported about 3.4 dB
  from its gate; why this port differs is not investigated (different clips, mask definition and gate implementation are all candidates, none checked).
- **Lookahead effect is small.** With 30 ms vs 0 ms: SI-SDR +0.10 dB, STOI +0.001, PESQ +0.03. The direction matches the unit test (onsets kept), but with six clips
  and differences this small it is not evidence on its own.
- **The AGC chain hurts these metrics.** SI-SDR drops by 8 to 10 dB on every one of the six clips, and the noise-only level rises 5.3 dB (-63.7 -> -58.3 dBFS).
  SI-SDR ignores a constant gain, so the drop is not just a level change. The cause is **not determined**. One fact from the code: AGC2's noise cap allows
  output noise up to -50 dBFS (`max_output_noise_level_dbfs`), and the enhanced noise sits near -64 dBFS, so the cap permits roughly 14 dB of gain on noise.
  That is consistent with the noise increase but has not been shown to be the cause.
- STOI and PESQ are unchanged by the AGC (0.930 -> 0.929, 3.06 -> 3.03). Whether listeners would notice the SI-SDR drop is unknown: there has been no listening test.

## Suggested next diagnostics

1. Run `full` with `--no-gate` and with `--agc-flag` to separate the gate/AGC interaction and the probability mapping.
2. Print the AGC gain trajectory for one clip and compare it with the SI-SDR loss over time.
3. Re-run with a lower `max_output_noise_level_dbfs` (for example -70) to see whether the noise rise and the SI-SDR drop move together.
