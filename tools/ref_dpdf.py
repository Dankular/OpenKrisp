import sys, numpy as np, soundfile as sf, librosa, time
from dpdfnet.stream import StreamEnhancer
src, in48, ref = sys.argv[1:4]
x, sr = sf.read(src, dtype="float32")
x48 = librosa.resample(x, orig_sr=sr, target_sr=48000).astype(np.float32)
sf.write(in48, x48, 48000, subtype="FLOAT")
x48 = sf.read(in48, dtype="float32")[0]
m = f"{sys.argv[4]}"
e = StreamEnhancer(onnx_path=m)
hop = 480
out = []
t = time.time()
for i in range(0, len(x48), hop):
    out.append(e.process(x48[i:i+hop]))
out.append(e.flush())
y = np.concatenate(out)
print("py", len(x48), len(y), "rtf", (time.time()-t)/(len(x48)/48000))
sf.write(ref, y, 48000, subtype="FLOAT")
