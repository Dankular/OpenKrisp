# Evaluation: pipeline variants on 6 DPDFNet_EvalSet clips

Run with `tools/eval.py` (raw numbers in [eval_results.csv](eval_results.csv)). Linux x86-64 sandbox, 2026-10-27.

**Clip set.** Six clips chosen by us from `Ceva-IP/DPDFNet_EvalSet` at SNR 5: german_airport, french_pub, spanish_restaurant, arabic_office, polish_street,
turkish_car. This is not the six-clip set used in the earlier Python proof of concept (that set is not in this repo), so these numbers are not
comparable with the earlier ones. Six clips is a small sample; no significance testing was done.

**Method.** Noisy 16 kHz clip -> 48 kHz -> pipeline (TEN VAD hop 160, threshold 0.5) -> back to 16 kHz. Each output is aligned to the clean reference by
cross-correlation (+-150 ms), then scored: SI-SDR, STOI, PESQ wide-band, and "noise-only level". The noise-only level is the mean-power level of the output
in frames where the clean reference is at least 40 dB below its own 95th-percentile frame level (74% of frames on average). That mask definition is ours.

## Averages

First table: the whole clip. Second table: the first 8 s excluded (AGC2's start-up transient; see below).

| variant | SI-SDR (dB) | STOI | PESQ-wb | noise-only (dBFS) |
|---|---|---|---|---|
| noisy input | 0.54 | 0.823 | 1.24 | -30.1 |
| enhancement only | 13.73 | 0.930 | 3.06 | -63.6 |
| + gate, no lookahead | 13.63 | 0.929 | 3.03 | -63.9 |
| + gate, 30 ms lookahead | 13.73 | 0.930 | 3.06 | -63.7 |
| full, AGC2 stock +15 dB start | 5.00 | 0.929 | 3.03 | -58.3 |
| full, 0 dB start (the default now) | 12.90 | 0.930 | 3.06 | -63.1 |

| first 8 s excluded | SI-SDR (dB) | STOI | PESQ-wb | noise-only (dBFS) |
|---|---|---|---|---|
| noisy input | 0.52 | 0.824 | 1.24 | -30.1 |
| enhancement only | 13.84 | 0.930 | 3.05 | -65.5 |
| + gate, 30 ms lookahead | 13.84 | 0.930 | 3.06 | -65.6 |
| full, +15 dB start | 11.61 | 0.930 | 3.05 | -62.8 |
| full, 0 dB start | 12.99 | 0.930 | 3.05 | -64.6 |

Measured signal delay (from the alignment): 40 ms for enhancement alone, 70 ms with the 30 ms lookahead.

## What the numbers show

- **The enhancer does most of the work.** SI-SDR 0.5 -> 13.7 dB, PESQ 1.24 -> 3.06, noise-only level down by about 33 dB.
- **The gate adds almost nothing here.** Noise-only level improves by 0.1 to 0.3 dB over enhancement alone. The earlier Python proof of concept reported about 3.4 dB
  from its gate; why this port differs is not investigated (different clips, mask definition and gate implementation are all candidates, none checked).
- **Lookahead effect is small.** With 30 ms vs 0 ms: SI-SDR +0.10 dB, STOI +0.001, PESQ +0.03. The direction matches the unit test (onsets kept), but with six clips
  and differences this small it is not evidence on its own.

## The gain-control regression, diagnosed

The first full-pipeline run lost 8.7 dB of SI-SDR and raised the noise-only level 5.3 dB. Findings:

1. **Most of it was AGC2's +15 dB starting gain.** The applied gain (frame-energy ratio of `full` over `gate_la30`, which differ only by the AGC) peaks at about +15 dB on every
   clip and averages 1.3 dB on speech. SI-SDR and the noise-only level are energy-weighted over the whole clip, so a 15 dB boost on the first seconds dominates them.
   Excluding the first 8 s cut the SI-SDR loss from 8.7 to 2.2 dB and the noise rise from 5.3 to 2.8 dB.
2. **Starting at 0 dB fixes it.** SI-SDR 12.90 vs 13.73 (enhancement only), noise-only -63.1 vs -63.6. This is now the pipeline default (`pipeline::Config::default`). Checked that the new
   default is byte-identical to an explicit `--agc-initial-gain-db 0` run, and that `--agc-initial-gain-db 15` reproduces the old output.
3. **What remains (about 0.8 dB SI-SDR, 0.5 dB noise) is ongoing level adaptation,** which any AGC does and these level-referenced metrics penalise. Whether anyone would hear it is unknown: no listening test.
4. **AGC2's noise-aware cap never binds in this chain.** The controller's noise estimate sits at its measurement floor (-84.3 dBFS) because DPDFNet leaves near-silent frames between speech,
   so the cap `max_output_noise (-50) - noise (-84)` allows 34 dB of gain. Setting the cap to -60 dBFS produced a byte-identical output to the default; -80 changed the output, so the option works.
   The cap therefore does not protect the residual noise here.

## Suggested next steps

- Listening test of `enh_only` vs `full`.
- A noise estimate that sees the noise present during speech, if the cap is to be useful.
- Re-run on the original six-clip set from the earlier proof of concept (not available in this repo) for a like-for-like comparison.
