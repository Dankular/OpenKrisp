"""Whistle ASR on Sonos Tract CUDA, using the original Whistle ONNX weights.

Host frontend/engram/DTW adapted from mrfakename/whistle-ONNX (Apache-2.0),
revision 14fc92cb09ad5c271fe9a4ec4be70274790050d2. No PyTorch runtime.
"""
import json
import os
import struct
from pathlib import Path
from functools import lru_cache
import numpy as np

LANGUAGES = ['en', 'de', 'fr', 'es', 'it', 'nl', 'pl']


def cuda_environment():
    global _dll_directories
    if '_dll_directories' in globals(): return
    if os.name != 'nt':
        return
    import ctypes
    ctypes.windll.kernel32.SetErrorMode(0x8007)
    directories = []
    root = Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'NVIDIA GPU Computing Toolkit' / 'CUDA'
    for version in sorted(root.glob('v*'), reverse=True):
        directories.extend([version / 'bin' / 'x64', version / 'bin'])
    os.environ['PATH'] = os.pathsep.join(str(p) for p in directories if p.exists()) + os.pathsep + os.environ.get('PATH', '')
    # Keep handles alive so Windows can resolve CUDA's dependent DLLs as well.
    _dll_directories = [os.add_dll_directory(str(p)) for p in directories if p.exists()]


class WhistleTract:
    def __init__(self, directory, progress=lambda message: None):
        cuda_environment()
        # Tract 0.23.9's CUDA gather cannot launch over the empty initial KV cache.
        # Its supported CPU-op selection handles indexing; network matmuls stay on CUDA.
        os.environ.setdefault('TRACT_CUDA_FORCE_CPU','Gather')
        import tract
        directory = Path(directory)
        self.raw = (directory / 'whistle.pack').read_bytes()
        size = struct.unpack_from('<I', self.raw, 4)[0]
        if self.raw[:4] != b'WPK1':
            raise ValueError('Invalid Whistle weight pack')
        self.man = json.loads(self.raw[8:8+size])
        self.base = (8 + size + 15) // 16 * 16
        self.tok = self.man['tok']
        self.fb = np.frombuffer(self.raw, '<f4', 257*80, self.base+self.man['melfb']['src']).reshape(257,80).astype(np.float64)
        h = np.ones((1,1), np.float64)
        while len(h) < 128:
            h = np.block([[h,h],[h,-h]])
        self.hadamard = h / np.sqrt(128)
        runtime = tract.runtime_for_name('cuda')
        self.models = []
        self.outputs = []
        for name in ['encoder', 'decoder']:
            progress('Loading Whistle '+name+' on Tract CUDA…')
            model = tract.onnx().load(str(directory / 'onnx' / (name+'.onnx')))
            self.outputs.append(['cross_k','cross_v'] if name == 'encoder' else ['logits','prev_out','present_k','present_v','xattn'])
            self.models.append(runtime.prepare(model.into_model()))
        self.backend = 'Tract CUDA'
        progress('Whistle encoder and decoder loaded on Tract CUDA.')

    @lru_cache(maxsize=32768)
    def row(self, e, table, index):
        desc = self.man['engram'][e]
        row = table*18432+index
        bits, dim = desc['bits'], desc['in']
        stride = dim*bits//8
        offset = self.base+desc['src']
        packed = np.frombuffer(self.raw, np.uint8, stride, offset+row*stride)
        indices = ((packed[:,None].astype(np.uint16) >> np.arange(0,8,bits)) & ((1<<bits)-1)).reshape(-1)
        cb = np.asarray(self.man['codebooks'][str(bits)], np.float64)
        norm = float(np.frombuffer(self.raw, '<f2', 1, offset+desc['out']*stride+row*2)[0])
        return ((cb[indices]*norm) @ self.hadamard).astype(np.float32)

    def engram(self, tokens, t):
        features = np.zeros((2,4,512),np.float32)
        for tap in range(4):
            pos=t-3*tap
            if pos < 0: continue
            for table in range(4):
                order=2+table//2
                if pos < order-1: continue
                acc=(0x9E3779B9*(table+1)) & 0xffffffff
                for j in range(order):
                    acc=((acc^tokens[pos-j])*0x01000193) & 0xffffffff
                index=(acc^(acc>>15))%18432
                for e in range(2): features[e,tap,table*128:(table+1)*128]=self.row(e,table,index)
        return features

    def mel(self, audio):
        audio=np.asarray(audio,np.float64)
        count=len(audio)//320
        gain=1.0
        if count:
            levels=np.sqrt(np.mean(audio[:count*320].reshape(count,320)**2,axis=1))
            p=np.sort(levels)[int((count-1)*.99)]
            if p>1e-6: gain=.1/p
        frames=len(audio)//160
        padded=np.pad(audio*gain,(200,400))
        windows=np.lib.stride_tricks.sliding_window_view(padded,400)[::160][:frames]
        power=np.abs(np.fft.rfft(windows*np.hanning(400),n=512))**2
        logged=np.log(power @ self.fb+2**-24)
        return np.ascontiguousarray((logged-logged.mean(axis=0))/(logged.std(axis=0,ddof=1)+1e-5),np.float32)

    def decode_text(self, ids):
        result=bytearray()
        for index in ids:
            piece=self.tok['pieces'][index]
            kind=self.tok['types'][index]
            if kind==4: result.append(int(piece[3:5],16))
            elif kind in (0,3): result.extend(piece.replace('▁',' ').encode('utf-8'))
        return result.decode('utf-8',errors='replace')

    @staticmethod
    def token_frames(attention):
        a=np.asarray(attention,np.float64)
        a=a.reshape(len(a),-1,a.shape[-1])
        cost=-((a-a.mean(axis=0))/(a.std(axis=0)+1e-9)).mean(axis=1)
        n,m=cost.shape
        total=np.full((n+1,m+1),np.inf); total[0,0]=0
        trace=np.zeros((n+1,m+1),np.uint8)
        for i in range(1,n+1):
            for j in range(1,m+1):
                prev=(total[i-1,j-1],total[i-1,j],total[i,j-1])
                k=int(np.argmin(prev)); total[i,j]=cost[i-1,j-1]+prev[k]; trace[i,j]=k
        starts=np.full(n,-1,np.int32)
        i,j=n,m
        while i>0 and j>0:
            starts[i-1]=j-1
            k=trace[i,j]
            if k!=2: i-=1
            if k!=1: j-=1
        return starts

    def transcribe(self, audio):
        mel=self.mel(audio)
        if len(mel)<4: return {'text':'','words':[],'backend':self.backend}
        enc=dict(zip(self.outputs[0],self.models[0].run([mel])))
        # Tract keeps its native Tensor objects for recurrent GPU state.
        ck,cv=enc['cross_k'],enc['cross_v']
        prev=np.zeros((8,2,608),np.float32)
        pk=np.zeros((8,2,0,48),np.float32); pv=np.zeros((8,2,0,64),np.float32)
        tokens=[2]; ids=[]; attention=[]; probabilities=[]; language='en'
        limit=min(320,16+int(np.ceil(len(mel)/100))*12)
        for pos in range(limit+2):
            feeds=[np.array([tokens[pos]],np.int64),np.array([pos],np.int64),self.engram(tokens,pos),prev,pk,pv,ck,cv]
            out=dict(zip(self.outputs[1],self.models[1].run(feeds)))
            prev,pk,pv=out['prev_out'],out['present_k'],out['present_v']
            logits=out['logits'].to_numpy().reshape(-1)
            if pos==0:
                language_index=int(np.argmax(logits[8192:8199])); language=LANGUAGES[language_index]
                tokens.append(8192+language_index); continue
            attention.append(out['xattn'].to_numpy().copy())
            index=int(np.argmax(logits[:8192]))
            probabilities.append(float(1/np.exp(logits[:8192]-logits[index]).sum()))
            if index==1 or len(ids)>=limit: break
            ids.append(index); tokens.append(index)
        if len(attention)==len(ids): attention.append(attention[-1])
        frames=self.token_frames(attention) if ids else []
        groups=[]
        for i,index in enumerate(ids):
            if not groups or self.tok['pieces'][index].startswith('▁'): groups.append([])
            groups[-1].append(i)
        words=[]
        for wi,group in enumerate(groups):
            word=self.decode_text([ids[i] for i in group]).strip()
            if not word: continue
            start=max(0,float(frames[group[0]])*.08)
            end=max(start,float(frames[groups[wi+1][0] if wi+1<len(groups) else len(ids)])*.08)
            words.append({'word':word,'start':round(start,2),'end':round(min(end,len(audio)/16000),2),'probability':float(np.mean([probabilities[i] for i in group]))})
        return {'text':self.decode_text(ids).strip(),'language':language,'words':words,'backend':self.backend}
