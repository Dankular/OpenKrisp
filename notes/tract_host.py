"""App-owned local model host. Loopback only, per-process bearer token, no telemetry."""
import base64
import gc
import json
import os
import secrets
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import numpy as np

lock=threading.RLock()
models={}
status={'state':'starting','backend':'Tract CUDA','message':'Starting local model host'}
token=secrets.token_urlsafe(32)


def emit(**values):
    status.update(values)
    if 'home' in globals():
        public={k:v for k,v in status.items() if k not in ('token','port')}
        public['pid']=os.getpid()
        temporary=home/('runtime-status-'+str(os.getpid())+'.tmp')
        temporary.write_text(json.dumps(public),encoding='utf-8')
        temporary.replace(home/'runtime-status.json')
    print(json.dumps(status),flush=True)


def load():
    from whistle_tract import WhistleTract
    from smart_turn import SmartTurn
    import tract
    with lock:
        if models: return
        try:
            emit(state='loading',message='Loading Whistle on GPU')
            models['asr']=WhistleTract(config['whistleOnnxDirectory'],lambda message: emit(message=message))
            emit(message='Loading Smart Turn on GPU')
            models['turn']=SmartTurn(config['smartTurnDirectory'])
            emit(message='Loading speech segmentation')
            models['speech']=tract.onnx().load(config['segmentation']).into_model().into_runnable()
            emit(state='loaded',message='Whistle ASR + Smart Turn on CUDA; speech segmentation on Tract CPU')
        except Exception as error:
            models.clear(); gc.collect()
            emit(state='error',message=str(error))
            raise


def analyze(audio):
    size=min(len(audio),160000)
    if size==0: return {'segments':[],'speechProbability':0,'speechDetected':False,'turnProbability':0,'turnComplete':False}
    x=np.zeros((1,1,160000),np.float32); x[0,0,:size]=audio[-size:]
    scores=models['speech'].run([x])[0].to_numpy()[0]
    centers=(np.arange(len(scores))*270+991/2)/16000
    valid=centers<=size/16000
    probabilities=np.clip(1-np.exp(scores[:,0]),0,1)
    # Native pyannote powerset classification: class zero is the empty/silent set.
    active=(np.argmax(scores,axis=-1)!=0)&valid
    segments=[]
    for i in np.flatnonzero(active):
        start=max(0,(i*270)/16000)
        end=min(size/16000,(i*270+991)/16000)
        if segments and start-segments[-1]['end']<=.5: segments[-1]['end']=end
        else: segments.append({'start':start,'end':end})
    segments=[s for s in segments if s['end']-s['start']>=.3]
    recent=valid&(centers>=max(0,size/16000-.25))
    confidence=float(np.max(probabilities[recent])) if recent.any() else 0
    speech_detected=bool(np.any(active&recent))
    turn=models['turn'].predict(audio)
    return {'segments':segments,'speechProbability':confidence,'speechDetected':speech_detected,
            'turnProbability':turn['probability'],'turnComplete':turn['complete'] and not speech_detected}


class Handler(BaseHTTPRequestHandler):
    def log_message(self,*args): pass
    def do_POST(self):
        if self.headers.get('Authorization')!='Bearer '+token:
            self.send_error(403); return
        try:
            length=int(self.headers.get('Content-Length','0'))
            if length>4_000_000: raise ValueError('Audio request too large')
            data=json.loads(self.rfile.read(length) or b'{}')
            with lock:
                if self.path=='/load': load(); result=dict(status)
                elif self.path=='/unload':
                    if 'asr' in models: models['asr'].row.cache_clear()
                    models.clear(); gc.collect(); emit(state='unloaded',message='Models unloaded'); result=dict(status)
                elif self.path=='/status': result=dict(status)
                elif self.path in ('/infer','/analyze'):
                    if status['state']!='loaded': raise RuntimeError('Load ASR models in Status before recording')
                    audio=np.frombuffer(base64.b64decode(data['audio'],validate=True),'<f4').copy()
                    result=models['asr'].transcribe(audio) if self.path=='/infer' else analyze(audio)
                else: raise ValueError('Unknown model command')
            payload=json.dumps(result).encode()
            self.send_response(200)
        except Exception as error:
            payload=json.dumps({'error':str(error)}).encode(); self.send_response(500)
        self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(payload)))
        self.end_headers(); self.wfile.write(payload)


if __name__=='__main__':
    home=Path(sys.argv[1])
    config=json.loads((home/'config.json').read_text(encoding='utf-8-sig'))
    server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
    emit(port=server.server_port,token=token,state='unloaded',message='Model host started')
    threading.Thread(target=server.serve_forever,daemon=True).start()
    try: load()
    except Exception: pass
    for line in sys.stdin:
        if line.strip()=='quit': break
    server.shutdown()
    emit(state='stopped',message='Tract stopped')
