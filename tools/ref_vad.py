"""Reference TEN VAD dump via the upstream ctypes wrapper.
usage: python tools/ref_vad.py <ten-vad checkout> <in16k.wav> <hop> <threshold> > out.txt"""
import sys
import numpy as np
import soundfile as sf

root, wav, hop, thr = sys.argv[1], sys.argv[2], int(sys.argv[3]), float(sys.argv[4])
sys.path.insert(0, root + "/include")
from ten_vad import TenVad

x, sr = sf.read(wav, dtype="int16")
assert sr == 16000 and x.ndim == 1
v = TenVad(hop, thr)
for i in range(len(x) // hop):
    p, f = v.process(x[i * hop:(i + 1) * hop])
    print("[%d] %0.6f, %d" % (i, p, f))
