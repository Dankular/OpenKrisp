"""Official multilingual E5 sentence embeddings, offline on Tract; no Torch/ORT."""
from functools import lru_cache
from pathlib import Path
import numpy as np

REPO='intfloat/multilingual-e5-small'
REVISION='614241f622f53c4eeff9890bdc4f31cfecc418b3'
MODEL_SHA256='ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665'
TOKENIZER_SHA256='0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39'
VERSION='e5-small:'+MODEL_SHA256+':'+TOKENIZER_SHA256+':mean-l2-v1'


class SemanticEncoder:
    def __init__(self, config, home):
        from tokenizers import Tokenizer
        directory=Path(config.get('semanticDirectory',Path(home)/'models/semantic-e5-onnx/onnx'))
        self.path=directory/'model.onnx'
        if not self.path.is_file(): raise RuntimeError('The semantic retrieval model is missing. Run local model setup to download it.')
        self.tokenizer=Tokenizer.from_file(str(directory/'tokenizer.json'))
        self.tokenizer.no_truncation(); self.tokenizer.no_padding()
        self.model=None

    def __enter__(self):
        import tract
        model=tract.onnx().load(str(self.path))
        self.inputs=[model.input_name(i) for i in range(model.input_count())]
        for index in range(model.input_count()): model.set_input_fact(index,'1,S,i64')
        self.model=model.into_model().into_runnable()
        return self

    def __exit__(self,*unused):
        self.model=None

    @lru_cache(maxsize=2048)
    def token_count(self,text):
        return len(self.tokenizer.encode(text).ids)

    def encode(self,text,query=False):
        tokens=self.tokenizer.encode(('query: ' if query else 'passage: ')+text)
        if len(tokens.ids)>512: raise ValueError('A semantic passage exceeded the encoder token limit.')
        ids=np.asarray([tokens.ids],dtype=np.int64)
        mask=np.asarray([tokens.attention_mask],dtype=np.int64)
        arrays={'input_ids':ids,'attention_mask':mask,'token_type_ids':np.zeros_like(ids)}
        outputs=self.model.run([np.ascontiguousarray(arrays[name]) for name in self.inputs])
        hidden=outputs[0].to_numpy()
        vector=(hidden*mask[...,None]).sum(axis=1)[0]/max(int(mask.sum()),1)
        vector=np.asarray(vector,dtype=np.float32); norm=float(np.linalg.norm(vector))
        if vector.shape!=(384,) or not np.isfinite(vector).all() or norm<=0:
            raise ValueError('The semantic encoder returned an invalid embedding.')
        return vector/norm
