"""Persistent contextual sentence vectors and query-dependent hybrid/MMR reranking."""
import json
import re
import time
import numpy as np
from context_engine import digest, normalized
from semantic_encoder import VERSION


class SemanticContext:
    def __init__(self, engine, encoder, progress):
        self.engine,self.encoder,self.progress=engine,encoder,progress
        self.db=engine.db
        self.db.executescript('''
CREATE TABLE IF NOT EXISTS semantic_vectors(model TEXT, vector_key TEXT, vector BLOB, PRIMARY KEY(model,vector_key));
CREATE TABLE IF NOT EXISTS semantic_documents(scope TEXT, source_key TEXT, model TEXT, fingerprint TEXT, PRIMARY KEY(scope,source_key,model));
CREATE TABLE IF NOT EXISTS semantic_sentences(scope TEXT, source_key TEXT, model TEXT, focus_offset INTEGER, focus_end INTEGER,
 context_offset INTEGER, context_end INTEGER, text TEXT, context TEXT, vector_key TEXT, start REAL, end REAL, speaker TEXT, kind TEXT, provenance TEXT,
 PRIMARY KEY(scope,source_key,model,focus_offset));
CREATE INDEX IF NOT EXISTS semantic_scope ON semantic_sentences(scope,model);
''')

    def spans(self, text, budget=192):
        """Preserve every character, including long/unpunctuated and non-Latin speech."""
        boundaries=[0]+[m.end() for m in re.finditer(r'[.!?。！？]+[\"\u201d\u00bb\)]*(?:\s+|$)',text)]
        if boundaries[-1]!=len(text): boundaries.append(len(text))
        result=[]
        for left,right in zip(boundaries,boundaries[1:]):
            pending=[(left,right)]
            while pending:
                start,end=pending.pop(0); value=text[start:end]
                if not value.strip(): continue
                if self.encoder.token_count('passage: '+value)<=budget:
                    result.append((start,end)); continue
                middle=(start+end)//2; cut=text.rfind(' ',start,middle)
                if cut<=start: cut=middle
                if cut<=start: raise ValueError('Unable to segment a semantic source span.')
                pending[0:0]=[(start,cut),(cut,end)]
        return result

    def vector(self, text, query=False):
        # Query vectors are cached too; a repeated context packet need not encode the same query again.
        key=digest(('query' if query else 'passage',text))
        row=self.db.execute('SELECT vector FROM semantic_vectors WHERE model=? AND vector_key=?',(VERSION,key)).fetchone()
        if row: return key,np.frombuffer(row['vector'],dtype='<f4')
        vector=self.encoder.encode(text,query=query)
        with self.db:
            self.db.execute('INSERT OR IGNORE INTO semantic_vectors VALUES(?,?,?)',(VERSION,key,vector.astype('<f4').tobytes()))
            self.engine.count('embeddingCalls')
        return key,vector

    def sync(self, scope, writer=None):
        documents=[]
        for row in self.db.execute('SELECT * FROM chunks WHERE scope=? ORDER BY start',(scope,)):
            documents.append(dict(key=row['source_key'],text=row['text'],start=row['start'],end=row['end'],
                                  speaker=row['speaker'],kind='transcript',provenance=[]))
        if writer:
            rows=self.db.execute('''SELECT n.* FROM note_blocks n JOIN note_jobs j ON n.scope=j.scope AND n.writer=j.writer AND n.job_key=j.job_key
 WHERE n.scope=? AND n.writer=? AND j.active=1''',(scope,writer)).fetchall()
            for row in rows:
                sources=json.loads(row['sources'])
                documents.append(dict(key='note:'+str(row['id']),text=(row['heading']+'\n'+row['text']).strip(),start=row['start'],
                    end=max((s['end'] for s in sources),default=row['start']),speaker='',kind='note',provenance=sources))
        previous={r['source_key']:r['fingerprint'] for r in self.db.execute('SELECT * FROM semantic_documents WHERE scope=? AND model=?',(scope,VERSION))}
        keys={d['key'] for d in documents}; reported=time.monotonic()
        if writer is None: keys.update(k for k in previous if k.startswith('note:'))
        for number,doc in enumerate(documents):
            fingerprint=digest(doc)
            if previous.get(doc['key'])==fingerprint: continue
            text=doc['text']; spans=self.spans(text); rows=[]
            for index,(start,end) in enumerate(spans):
                left=spans[index-1][0] if index else start
                right=spans[index+1][1] if index+1<len(spans) else end
                # Neighboring sentences make short references and returning topics interpretable.
                if self.encoder.token_count('passage: '+text[left:right])>480: right=end
                if self.encoder.token_count('passage: '+text[left:right])>480: left=start
                context=text[left:right]
                vector_key,_=self.vector(context)
                rows.append((scope,doc['key'],VERSION,start,end,left,right,text[start:end],context,vector_key,
                             doc['start'],doc['end'],doc['speaker'],doc['kind'],json.dumps(doc['provenance'],ensure_ascii=False)))
            # Embedding work is outside the write transaction so ASR ingestion is never locked by inference.
            with self.db:
                self.db.execute('DELETE FROM semantic_sentences WHERE scope=? AND source_key=? AND model=?',(scope,doc['key'],VERSION))
                self.db.executemany('INSERT INTO semantic_sentences VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)',rows)
                self.db.execute('INSERT OR REPLACE INTO semantic_documents VALUES(?,?,?,?)',(scope,doc['key'],VERSION,fingerprint))
            if time.monotonic()-reported>2:
                self.progress(f'Indexing contextual sentences: {number+1}/{len(documents)} source sections.'); reported=time.monotonic()
        with self.db:
            for key in previous.keys()-keys:
                self.db.execute('DELETE FROM semantic_sentences WHERE scope=? AND source_key=? AND model=?',(scope,key,VERSION))
                self.db.execute('DELETE FROM semantic_documents WHERE scope=? AND source_key=? AND model=?',(scope,key,VERSION))
        self.engine.status()

    def retrieve(self, scope, sources, limit=8):
        before=min(s['start'] for s in sources); current={}
        for source in sources: current[source['sourceKey']]=min(current.get(source['sourceKey'],source['offset']),source['offset'])
        candidates=[]
        for row in self.db.execute('''SELECT s.*,v.vector FROM semantic_sentences s JOIN semantic_vectors v
 ON s.model=v.model AND s.vector_key=v.vector_key WHERE s.scope=? AND s.model=?''',(scope,VERSION)):
            # No future ASR material or the new source itself can enter as recalled background.
            if row['kind']=='note':
                refs=json.loads(row['provenance'])
                if not refs or not all(s['offset']+len(s['text'])<=current[s['sourceKey']]
                    if s['sourceKey'] in current else s['end']<=before for s in refs): continue
            elif row['source_key'] in current:
                if row['context_end']>current[row['source_key']]: continue
            elif row['end']>before: continue
            candidates.append(row)
        if not candidates: return []
        queries=[]
        for source in sources:
            row=self.db.execute('SELECT text FROM chunks WHERE scope=? AND source_key=?',(scope,source['sourceKey'])).fetchone()
            lead=row['text'][:source['offset']] if row else ''
            lead_spans=self.spans(lead)
            previous=lead[lead_spans[-1][0]:] if lead_spans else ''
            for start,end in self.spans(source['text']):
                focus=source['text'][start:end]
                value=(previous+' '+focus).strip()
                if self.encoder.token_count('query: '+value)>480: value=focus
                queries.append(self.vector(value,query=True)[1]); previous=focus
        if not queries: return []
        vectors=np.asarray([np.frombuffer(r['vector'],dtype='<f4') for r in candidates])
        query_vectors=np.asarray(queries)
        similarities=(vectors@query_vectors.T).max(axis=1)
        # Preserve exact names/numbers as a small lexical tie-breaker; meaning remains the main signal.
        terms=set(re.findall(r'[^\W_]{3,}',' '.join(s['text'] for s in sources).casefold()))
        lexical=np.asarray([len(terms & set(re.findall(r'[^\W_]{3,}',r['text'].casefold())))/max(1,len(terms)) for r in candidates])
        scores=similarities+.025*lexical
        relevance=(scores-scores.min())/max(float(np.ptp(scores)),1e-6)
        pool=list(np.argsort(scores)[-64:][::-1]); chosen=[]; seen=set()
        while pool and len(chosen)<limit:
            def rank(i):
                redundancy=max((float(vectors[i]@vectors[j]) for j in chosen),default=0)
                return .78*float(relevance[i])-.22*redundancy
            index=max(pool,key=rank); pool.remove(index); row=candidates[index]
            key=normalized(row['text'])
            if key in seen: continue
            if any(candidates[j]['source_key']==row['source_key'] and
                   max(row['context_offset'],candidates[j]['context_offset'])<min(row['context_end'],candidates[j]['context_end']) for j in chosen): continue
            seen.add(key); chosen.append(index)
        with self.db: self.engine.count('semanticReranks')
        return [dict(start=candidates[i]['start'],end=candidates[i]['end'],speaker=candidates[i]['speaker'],
                     text=candidates[i]['context'],english='',sourceKey=candidates[i]['source_key'],
                     excerptOffset=candidates[i]['context_offset'],focusText=candidates[i]['text'],
                     focusOffset=candidates[i]['focus_offset'],
                     semanticScore=round(float(similarities[i]),5),rerankScore=round(float(scores[i]),5),kind=candidates[i]['kind']) for i in chosen]
