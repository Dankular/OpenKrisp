"""Compare two float wavs sample-for-sample: python tools/parity.py ref.wav rust.wav"""
import sys
import numpy as np
import soundfile as sf

a = sf.read(sys.argv[1], dtype="float32")[0]
b = sf.read(sys.argv[2], dtype="float32")[0]
print("len", len(a), len(b))
n = min(len(a), len(b))
d = a[:n] - b[:n]
print("max abs diff", np.abs(d).max(), "ref peak", np.abs(a).max(),
      "SNR dB", 10 * np.log10((a[:n] ** 2).sum() / (d ** 2).sum()))
