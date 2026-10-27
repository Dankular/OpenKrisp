"""Local Smart Turn v3 ONNX inference on Tract CUDA.

Uses the model's published 16 kHz / 8 second, normalized Slaney log-mel frontend.
The ONNX community classification export returns logits (unlike Pipecat's sigmoid export).
"""
from pathlib import Path
import numpy as np


class SmartTurn:
    def __init__(self, directory):
        from whistle_tract import cuda_environment
        cuda_environment()
        import tract
        model=tract.onnx().load(str(Path(directory)/'onnx/model.onnx')).into_model()
        self.model=tract.runtime_for_name('cuda').prepare(model)
        # Slaney filter bank, matching the model's WhisperFeatureExtractor config.
        log_step=np.log(6.4)/27
        max_mel=15+np.log(8)/log_step
        mels=np.linspace(0,max_mel,82)
        hz=np.where(mels<15,mels*(200/3),1000*np.exp(log_step*(mels-15)))
        fft_hz=np.linspace(0,8000,201)
        lower=(fft_hz[:,None]-hz[:-2])/(hz[1:-1]-hz[:-2])
        upper=(hz[2:]-fft_hz[:,None])/(hz[2:]-hz[1:-1])
        self.filters=np.maximum(0,np.minimum(lower,upper))*(2/(hz[2:]-hz[:-2]))
        self.window=np.hanning(401)[:-1]

    def predict(self, audio):
        audio=np.asarray(audio[-128000:],np.float64)
        audio=np.pad(audio,(128000-len(audio),0))
        audio=(audio-audio.mean())/np.sqrt(audio.var()+1e-7)
        padded=np.pad(audio,(200,200),mode='reflect')
        frames=np.lib.stride_tricks.sliding_window_view(padded,400)[::160][:-1]
        power=np.abs(np.fft.rfft(frames*self.window))**2
        mel=np.log10(np.maximum(power @ self.filters,1e-10))
        mel=(np.maximum(mel,mel.max()-8)+4)/4
        features=np.ascontiguousarray(mel.T[None],np.float32)
        logit=float(self.model.run([features])[0].to_numpy().reshape(-1)[0])
        return {'probability':float(1/(1+np.exp(-np.clip(logit,-80,80)))), 'complete':logit>0}
