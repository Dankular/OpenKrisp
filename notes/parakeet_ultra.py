"""Local Parakeet Ultra FP16 inference through the native transcribe.cpp CUDA runtime."""
import os
import sys
import numpy as np
from cuda_runtime import cuda_environment


class ParakeetUltra:
    def __init__(self, model_path, progress=lambda message: None):
        cuda_environment()
        os.environ['TRANSCRIBE_NATIVE_PROVIDER'] = 'cu12'
        import transcribe_cpp as tc
        tc.set_log_callback(lambda level, message: print(message, file=sys.stderr, end='', flush=True) if level in (2, 3) else None)
        devices = [device for device in tc.backends() if device.kind == 'cuda' and device.device_type == 'gpu']
        if not devices: raise RuntimeError('Parakeet Ultra requires a working CUDA GPU; CPU fallback is disabled.')
        device = max(devices, key=lambda d: d.memory_free)
        progress('Loading Parakeet Ultra FP16 on ' + device.description + ' (native CUDA)…')
        self.model = tc.Model(model_path, backend='cuda', device=device)
        try:
            if self.model.device.kind != 'cuda': raise RuntimeError('Ultra did not select CUDA.')
            self.session = self.model.session(n_threads=min(6, os.cpu_count() or 2))
        except Exception:
            self.model.close()
            raise
        self.backend = 'Parakeet Ultra · native CUDA · ' + device.description

    def close(self):
        self.session.close()
        self.model.close()

    def transcribe(self, audio):
        audio = np.ascontiguousarray(audio, np.float32)
        if len(audio) < 320: return {'text': '', 'words': [], 'backend': self.backend}
        result = self.session.run(audio, timestamps='word')
        words = []
        for word in result.words:
            probabilities = [token.p for token in result.tokens[word.first_token:word.first_token + word.n_tokens] if np.isfinite(token.p)]
            start = max(0, word.t0_ms/1000)
            end = min(len(audio)/16000, max(start + .01, word.t1_ms/1000))
            if word.text.strip() and end > start:
                item = {'word': word.text.strip(), 'start': start, 'end': end}
                if probabilities: item['probability'] = float(np.mean(probabilities))
                words.append(item)
        if result.text.strip() and not words:
            raise RuntimeError('Ultra returned text without word timestamps; the transcript was not assigned fabricated timing.')
        return {'text': result.text.strip(), 'language': result.language, 'words': words, 'backend': self.backend}
