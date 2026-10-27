"""Evaluate pipeline variants on a subset of Ceva-IP/DPDFNet_EvalSet.

usage: python tools/eval.py <workdir> <pipeline_run binary> <model.onnx>
Needs: numpy scipy soundfile pystoi pesq, and TEN_VAD_LIB_DIR/LD_LIBRARY_PATH set for the binary.

Metrics are computed at 16 kHz against the clean reference after aligning each output by cross-correlation
(search +-150 ms). 'noise-only dBFS' is the mean-power level of the output in frames where the CLEAN signal is
>= 40 dB below its own 95th-percentile frame level (the fraction of frames used is reported).
"""
import csv, os, subprocess, sys, urllib.request
from concurrent.futures import ThreadPoolExecutor
import numpy as np, soundfile as sf
from scipy.signal import resample_poly, correlate
from pesq import pesq
from pystoi import stoi

CLIPS = [  # language/noise/snr chosen by the implementer; the original 6-clip set is not in this repo
    "german_airport_snr5", "french_pub_snr5", "spanish_restaurant_snr5",
    "arabic_office_snr5", "polish_street_snr5", "turkish_car_snr5",
]
VARIANTS = {
    "enh_only": ["--no-gate", "--no-agc"],
    "gate_la0": ["--no-agc", "--lookahead-ms", "0"],
    "gate_la30": ["--no-agc", "--lookahead-ms", "30"],
    # AGC2's stock start is +15 dB (meant for the start of a call). "full" is the pipeline default: a 0 dB start.
    "full_init15": ["--lookahead-ms", "30", "--agc-initial-gain-db", "15"],
    "full": ["--lookahead-ms", "30"],
}
SKIPS_S = (0, 8)  # seconds excluded from the start; 8 s removes AGC2's start-up transient
BASE = "https://huggingface.co/datasets/Ceva-IP/DPDFNet_EvalSet/resolve/main"


def fetch(kind, name, dst):
    if not os.path.exists(dst):
        urllib.request.urlretrieve(f"{BASE}/{kind.capitalize()}/{name}_rt60none_0_mixture_{kind}.wav", dst)
    return sf.read(dst, dtype="float32")[0]


def sisdr(ref, est):
    ref = ref - ref.mean(); est = est - est.mean()
    a = (est @ ref) / (ref @ ref + 1e-12)
    t = a * ref
    return 10 * np.log10((t @ t + 1e-12) / ((est - t) @ (est - t) + 1e-12))


def align(clean, est, max_lag=2400):
    n = min(len(clean), len(est))
    c = correlate(est[:n], clean[:n], mode="full", method="fft")
    mid = n - 1
    seg = c[mid - max_lag: mid + max_lag + 1]
    lag = int(np.argmax(seg)) - max_lag  # est is delayed by `lag` samples
    if lag >= 0:
        e, c_ = est[lag:], clean
    else:
        e, c_ = est, clean[-lag:]
    m = min(len(e), len(c_))
    return c_[:m], e[:m], lag


def frame_rms_db(x, n=320):
    k = len(x) // n
    f = x[: k * n].reshape(k, n)
    return 10 * np.log10((f ** 2).mean(1) + 1e-12)


def metrics(clean, est, skip_s=0):
    clean, est = clean[skip_s * 16000:], est[skip_s * 16000:]
    clean, est, lag = align(clean, est)
    cdb = frame_rms_db(clean)
    mask = cdb < np.percentile(cdb, 95) - 40
    edb = frame_rms_db(est)
    k = min(len(mask), len(edb))
    mask = mask[:k]
    noise_only = 10 * np.log10(np.mean(10 ** (edb[:k][mask] / 10))) if mask.any() else float("nan")
    return dict(
        sisdr=sisdr(clean, est), stoi=stoi(clean, est, 16000), pesq=pesq(16000, clean, est, "wb"),
        noise_only_dbfs=noise_only, noise_frames=float(mask.mean()), lag16k=lag,
    )


def main():
    work, binary, model = sys.argv[1:4]
    os.makedirs(work, exist_ok=True)
    jobs, data = [], {}
    for c in CLIPS:
        noisy = fetch("noisy", c, f"{work}/{c}_noisy16.wav")
        clean = fetch("clean", c, f"{work}/{c}_clean16.wav")
        data[c] = (noisy, clean)
        in48 = f"{work}/{c}_in48.wav"
        sf.write(in48, resample_poly(noisy, 3, 1).astype(np.float32), 48000, subtype="FLOAT")
        for v, flags in VARIANTS.items():
            out = f"{work}/{c}_{v}.wav"
            if not os.path.exists(out):
                jobs.append([binary, model, in48, out, *flags])
    with ThreadPoolExecutor(int(os.environ.get("EVAL_JOBS", "3"))) as ex:
        for r in ex.map(lambda j: subprocess.run(j, capture_output=True, text=True), jobs):
            if r.returncode:
                raise SystemExit(r.stderr)
    rows = []
    for skip in SKIPS_S:
        for c, (noisy, clean) in data.items():
            n = min(len(noisy), len(clean))
            rows.append(dict(clip=c, variant="noisy", skip_s=skip, **metrics(clean[:n], noisy[:n], skip)))
            for v in VARIANTS:
                y = sf.read(f"{work}/{c}_{v}.wav", dtype="float32")[0]
                rows.append(dict(clip=c, variant=v, skip_s=skip, **metrics(clean, resample_poly(y, 1, 3).astype(np.float32), skip)))
    with open(f"{work}/results.csv", "w", newline="") as f:
        w = csv.DictWriter(f, rows[0].keys()); w.writeheader(); w.writerows(rows)
    for skip in SKIPS_S:
        print(f"\n--- first {skip} s excluded ---")
        print(f"{'variant':16s} {'SI-SDR':>7s} {'STOI':>6s} {'PESQ-wb':>8s} {'noise-only dBFS':>16s}")
        for v in ["noisy", *VARIANTS]:
            r = [x for x in rows if x["variant"] == v and x["skip_s"] == skip]
            print(f"{v:16s} {np.mean([x['sisdr'] for x in r]):7.2f} {np.mean([x['stoi'] for x in r]):6.3f} "
                  f"{np.mean([x['pesq'] for x in r]):8.2f} {np.mean([x['noise_only_dbfs'] for x in r]):16.1f}")
    print("\nnoise-only frame fraction (clean-based mask): mean %.2f" % np.mean([x["noise_frames"] for x in rows]))


if __name__ == "__main__":
    main()
