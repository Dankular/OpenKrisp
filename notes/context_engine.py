"""Embedded, source-scoped RAG, response cache and durable context checkpoints.

Iris-inspired read/recall/record flow; SQLite FTS5 retrieval, not an embedding
similarity service. Changed facts never reuse a merely similar response.
"""
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import time
import unicodedata


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(',', ':')).encode()).hexdigest()


def normalized(text):
    return ' '.join(unicodedata.normalize('NFC', text).split())


def model_identity(config):
    files=[]
    for key in ('notesModel','notesServer'):
        path=Path(config[key]); stat=path.stat()
        files.append((str(path.resolve()),stat.st_size,stat.st_mtime_ns))
    return digest(files)


class NoteEvidenceError(ValueError):
    """The writer cited material outside the new source spans."""


class ContextEngine:
    def __init__(self, home, timeout=3):
        self.home=Path(home)
        self.db=sqlite3.connect(self.home/'context-engine.sqlite3',timeout=timeout)
        self.db.row_factory=sqlite3.Row
        self.db.executescript('''
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS chunks(id INTEGER PRIMARY KEY, scope TEXT NOT NULL, source_key TEXT NOT NULL,
 start REAL, end REAL, speaker TEXT, text TEXT, translated TEXT DEFAULT '', fingerprint TEXT,
 UNIQUE(scope,source_key));
CREATE VIRTUAL TABLE IF NOT EXISTS context_fts USING fts5(text,translated,scope UNINDEXED,chunk_id UNINDEXED,tokenize='unicode61');
CREATE TABLE IF NOT EXISTS checkpoints(scope TEXT, version TEXT, payload TEXT, updated REAL, PRIMARY KEY(scope,version));
CREATE TABLE IF NOT EXISTS responses(scope TEXT, cache_key TEXT, payload TEXT, expires REAL, accessed REAL, PRIMARY KEY(scope,cache_key));
CREATE TABLE IF NOT EXISTS counters(name TEXT PRIMARY KEY, value INTEGER);
CREATE TABLE IF NOT EXISTS note_jobs(scope TEXT, writer TEXT, job_key TEXT, sources TEXT, active INTEGER DEFAULT 1,
 PRIMARY KEY(scope,writer,job_key));
CREATE TABLE IF NOT EXISTS note_blocks(id INTEGER PRIMARY KEY, scope TEXT, writer TEXT, job_key TEXT,
 text TEXT, heading TEXT, sources TEXT, start REAL);
CREATE INDEX IF NOT EXISTS note_blocks_scope ON note_blocks(scope,writer,job_key);
''')
        with self.db:
            self.db.execute('DELETE FROM responses WHERE expires<?',(time.time(),))
            self.db.execute('DELETE FROM responses WHERE rowid IN (SELECT rowid FROM responses ORDER BY accessed DESC LIMIT -1 OFFSET 1500)')

    def count(self, name):
        self.db.execute('INSERT INTO counters VALUES(?,1) ON CONFLICT(name) DO UPDATE SET value=value+1',(name,))

    def get(self, scope, key):
        row=self.db.execute('SELECT payload FROM responses WHERE scope=? AND cache_key=? AND expires>?',(scope,key,time.time())).fetchone()
        with self.db:
            self.count('cacheHits' if row else 'cacheMisses')
            if row: self.db.execute('UPDATE responses SET accessed=? WHERE scope=? AND cache_key=?',(time.time(),scope,key))
        if row:
            self.status()
            return json.loads(row['payload'])

    def put(self, scope, key, value):
        with self.db:
            self.db.execute('INSERT OR REPLACE INTO responses VALUES(?,?,?,?,?)',(scope,key,json.dumps(value,ensure_ascii=False),time.time()+30*86400,time.time()))
            self.db.execute('''DELETE FROM responses WHERE rowid IN
 (SELECT rowid FROM (SELECT rowid,sum(length(payload)) OVER (ORDER BY accessed DESC) AS used FROM responses) WHERE used>33554432)''')
        self.status()

    def generated(self):
        with self.db: self.count('modelCalls')
        self.status()

    def load(self, scope, version):
        row=self.db.execute('SELECT payload FROM checkpoints WHERE scope=? AND version=?',(scope,version)).fetchone()
        return json.loads(row['payload']) if row else None

    def save(self, scope, version, value):
        with self.db:
            self.db.execute('INSERT OR REPLACE INTO checkpoints VALUES(?,?,?,?)',(scope,version,json.dumps(value,ensure_ascii=False),time.time()))
        self.status()

    def ingest(self, scope, turns, authoritative=False):
        if not scope or (not turns and not authoritative): return
        through=max((t['end'] for t in turns),default=0)
        old={r['source_key']:r for r in self.db.execute('SELECT * FROM chunks WHERE scope=?',(scope,))}
        # A delayed notes request must never roll newer ASR ingestion backwards.
        if not authoritative and old and through+0.1 < max(r['end'] for r in old.values()): return
        keys=set()
        with self.db:
            for turn in turns:
                key=digest((turn['start'],turn['speaker'])); keys.add(key)
                fingerprint=digest((turn['start'],turn['end'],turn['speaker'],turn['text']))
                before=old.get(key)
                translated=before['translated'] if before and turn['text'].startswith(before['text']) else ''
                if before and before['fingerprint']==fingerprint: continue
                if before:
                    ident=before['id']
                    self.db.execute('UPDATE chunks SET end=?,text=?,translated=?,fingerprint=? WHERE id=?',(turn['end'],turn['text'],translated,fingerprint,ident))
                    self.db.execute('DELETE FROM context_fts WHERE chunk_id=?',(ident,))
                else:
                    ident=self.db.execute('INSERT INTO chunks(scope,source_key,start,end,speaker,text,fingerprint) VALUES(?,?,?,?,?,?,?)',
                        (scope,key,turn['start'],turn['end'],turn['speaker'],turn['text'],fingerprint)).lastrowid
                self.db.execute('INSERT INTO context_fts VALUES(?,?,?,?)',(turn['text'],translated,scope,ident))
            for key,row in old.items():
                if key not in keys:
                    self.db.execute('DELETE FROM chunks WHERE id=?',(row['id'],))
                    self.db.execute('DELETE FROM context_fts WHERE chunk_id=?',(row['id'],))
        self.status()

    def translations(self, scope, annotations):
        rows=self.db.execute('SELECT * FROM chunks WHERE scope=?',(scope,)).fetchall()
        with self.db:
            for row in rows:
                text=' '.join(a['translation'] for a in annotations if a['start']<=row['end'] and a['end']>=row['start'] and a['text'] in row['text'])
                if text==row['translated']: continue
                self.db.execute('UPDATE chunks SET translated=? WHERE id=?',(text,row['id']))
                self.db.execute('DELETE FROM context_fts WHERE chunk_id=?',(row['id'],))
                self.db.execute('INSERT INTO context_fts VALUES(?,?,?,?)',(row['text'],text,scope,row['id']))

    def search(self, scope, query, before=1e12, limit=4):
        terms=list(dict.fromkeys(re.findall(r'[^\W_]{4,}',query.casefold())))[:24]
        match=' OR '.join('"'+term+'"' for term in terms)
        rows=[]
        if match:
            rows=self.db.execute('''SELECT c.* FROM context_fts f JOIN chunks c ON c.id=f.chunk_id
 WHERE context_fts MATCH ? AND c.scope=? AND c.end<=? ORDER BY bm25(context_fts) LIMIT ?''',(match,scope,before,limit)).fetchall()
        if not rows:
            rows=self.db.execute('SELECT * FROM chunks WHERE scope=? AND end<=? ORDER BY end DESC LIMIT ?',(scope,before,limit)).fetchall()
        return [dict(start=r['start'],end=r['end'],speaker=r['speaker'],text=r['text'],english=r['translated']) for r in rows]

    def latest_checkpoint(self, scope):
        row=self.db.execute('SELECT payload FROM checkpoints WHERE scope=? ORDER BY updated DESC LIMIT 1',(scope,)).fetchone()
        return json.loads(row['payload']) if row else None

    @staticmethod
    def source_key(turn):
        return digest((turn['start'],turn['speaker']))

    def migrate_notes(self, scope, writer, state, turns):
        """Import the old ledger once; never ask the writer to decide what to retain."""
        if self.db.execute('SELECT 1 FROM note_jobs WHERE scope=? AND writer=? LIMIT 1',(scope,writer)).fetchone(): return
        processed=state.get('summaryProcessed',{})
        sources=[dict(sourceId=i,sourceKey=self.source_key(t),offset=0,text=processed[self.source_key(t)],
                      start=t['start'],end=t['end'],speaker=t['speaker'])
                 for i,t in enumerate(turns) if processed.get(self.source_key(t)) and t['text'].startswith(processed[self.source_key(t)])]
        if not sources or len(sources)!=sum(bool(text) for text in processed.values()): return
        # A legacy note may combine several passages. Reprocess this imported group together if any source is revised.
        notes=[dict(text=p['text'],keyPoint=p['keyPoint'],sourceIds=[s['sourceId'] for s in sources])
               for p in state.get('memory',[]) if p['status']=='active']
        self.complete_notes(scope,writer,sources,notes,validate_evidence=False)

    def complete_notes(self, scope, writer, sources, notes, validate_evidence=True):
        if validate_evidence:
            allowed={s['passageId'] for s in sources}
            for note in notes:
                if not note.get('text','').strip() or not note.get('passageIds') or not set(note['passageIds'])<=allowed:
                    raise NoteEvidenceError('A note cited material outside the new source passages; source coverage was not advanced.')
        key=digest([(s['sourceKey'],s['offset'],s['text']) for s in sources])
        with self.db:
            self.db.execute('INSERT OR REPLACE INTO note_jobs VALUES(?,?,?,?,1)',(scope,writer,key,json.dumps(sources,ensure_ascii=False)))
            self.db.execute('DELETE FROM note_blocks WHERE scope=? AND writer=? AND job_key=?',(scope,writer,key))
            for note in notes:
                # Attach exact source text ourselves. The writer never reproduces or edits evidence quotes.
                support=[{**s,'quotes':[s['text']]} for s in sources
                         if (s.get('passageId') in note.get('passageIds',[]) if validate_evidence else s['sourceId'] in note.get('sourceIds',[]))]
                text=note.get('text','').strip()
                if not support or not text: continue
                self.db.execute('INSERT INTO note_blocks(scope,writer,job_key,text,heading,sources,start) VALUES(?,?,?,?,?,?,?)',
                    (scope,writer,key,text,note.get('keyPoint','').strip(),json.dumps(support,ensure_ascii=False),min(s['start'] for s in support)))
        self.status()

    def pending_notes(self, scope, writer, turns):
        """Reconcile source revisions and return every uncovered source span, in order."""
        current={self.source_key(t):t for t in turns}; covered={}
        rows=self.db.execute('SELECT * FROM note_jobs WHERE scope=? AND writer=?',(scope,writer)).fetchall()
        with self.db:
            for row in rows:
                sources=json.loads(row['sources'])
                valid=all(s['sourceKey'] in current and current[s['sourceKey']]['text'][s['offset']:s['offset']+len(s['text'])]==s['text'] for s in sources)
                self.db.execute('UPDATE note_jobs SET active=? WHERE scope=? AND writer=? AND job_key=?',(int(valid),scope,writer,row['job_key']))
                if valid:
                    for s in sources: covered.setdefault(s['sourceKey'],[]).append((s['offset'],s['offset']+len(s['text'])))
        pending=[]
        for index,turn in enumerate(turns):
            key=self.source_key(turn); cursor=0
            def add(start,end):
                text=turn['text'][start:end]
                if text.strip(): pending.append(dict(sourceId=index,sourceKey=key,offset=start,text=text,
                    start=turn['start'],end=turn['end'],speaker=turn['speaker']))
            for start,end in sorted(covered.get(key,[])):
                if start>cursor: add(cursor,start)
                cursor=max(cursor,end)
            if cursor<len(turn['text']): add(cursor,len(turn['text']))
        return pending

    def note_blocks(self, scope, writer):
        rows=self.db.execute('''SELECT n.* FROM note_blocks n JOIN note_jobs j
 ON n.scope=j.scope AND n.writer=j.writer AND n.job_key=j.job_key
 WHERE n.scope=? AND n.writer=? AND j.active=1 ORDER BY n.start,n.id''',(scope,writer)).fetchall()
        seen=set(); result=[]
        for row in rows:
            key=normalized(row['text'])
            if key in seen: continue
            seen.add(key)
            result.append(dict(text=row['text'],keyPoint=row['heading'],sources=json.loads(row['sources'])))
        return result

    @staticmethod
    def split_source(source):
        text=source['text']; middle=len(text)//2
        # Prefer a natural sentence boundary, then a word boundary, while guaranteeing progress.
        boundaries=[m.end() for m in re.finditer(r'[.!?。！？]\s+',text) if len(text)//4<=m.end()<=3*len(text)//4]
        cut=min(boundaries,key=lambda p:abs(p-middle)) if boundaries else text.rfind(' ',0,middle)
        if cut<=0: cut=middle
        if cut<=0: raise ValueError('A source span cannot be split further.')
        result=[{**source,'text':text[:cut]},{**source,'offset':source['offset']+cut,'text':text[cut:]}]
        for item in result: item['passageId']=digest((item['sourceKey'],item['offset'],item['text']))[:12]
        return result

    def note_batches(self, sources, token_count, budget=650):
        """Context engine owns segmentation; the LLM never plans or drops source spans."""
        pending=[]
        for source in sources:
            pieces=[source]
            while pieces:
                item=pieces.pop(0)
                item['passageId']=digest((item['sourceKey'],item['offset'],item['text']))[:12]
                if token_count(json.dumps(pending+[item],ensure_ascii=False))<=budget:
                    pending.append(item); continue
                if pending:
                    yield pending; pending=[]; pieces.insert(0,item)
                else: pieces[0:0]=self.split_source(item)
        if pending: yield pending

    def note_context(self, scope, sources, token_count, budget):
        """Retrieve across the entire indexed history, with chronological coverage anchors."""
        before=min(s['start'] for s in sources)
        query=' '.join(s['text'] for s in sources)
        ranked=self.search(scope,query,before=before,limit=8)
        history=self.db.execute('SELECT * FROM chunks WHERE scope=? AND end<=? ORDER BY start',(scope,before)).fetchall()
        anchors=[]
        if history:
            for index in dict.fromkeys((0,len(history)//2,len(history)-1)):
                row=history[index]
                anchors.append(dict(start=row['start'],end=row['end'],speaker=row['speaker'],text=row['text'],english=row['translated']))
        # Reserve coverage for the lecture's opening, middle and immediate lead-in, alongside relevance.
        lead_in=[]
        for source in sources:
            if source['offset']:
                row=self.db.execute('SELECT * FROM chunks WHERE scope=? AND source_key=?',(scope,source['sourceKey'])).fetchone()
                if row:
                    at=max(0,source['offset']-1200)
                    lead_in.append(dict(start=row['start'],end=row['end'],speaker=row['speaker'],
                        text=row['text'][at:source['offset']],english='',role='sourceLeadIn',sourceOffset=at))
        candidates=lead_in+ranked[:2]+anchors+ranked[2:]; result=[]; seen=set()
        per_passage=max(1,budget//max(1,min(5,len(candidates))))
        terms=re.findall(r'[^\W_]{4,}',query.casefold())[:24]
        for passage in candidates:
            key=(passage['start'],passage['speaker'])
            if key in seen: continue
            seen.add(key)
            text=passage['text']
            # Select an excerpt near matching words, rather than always truncating the start of a long passage.
            matches=[text.casefold().find(t) for t in terms if t in text.casefold()]
            at=max(0,min(matches)-150) if matches else 0
            item={**passage,'text':text[at:at+900],'english':passage['english'][:400],'excerptOffset':at}
            while (token_count(json.dumps(item,ensure_ascii=False))>per_passage or token_count(json.dumps(result+[item],ensure_ascii=False))>budget) and len(item['text'])>80:
                item['text']=item['text'][:len(item['text'])//2]; item['english']=''
            if token_count(json.dumps(result+[item],ensure_ascii=False))<=budget: result.append(item)
        return result

    def status(self):
        import os
        values={r['name']:r['value'] for r in self.db.execute('SELECT * FROM counters')}
        values.update(indexedParagraphs=self.db.execute('SELECT count(*) FROM chunks').fetchone()[0],
                      retainedNotes=self.db.execute('SELECT count(*) FROM note_blocks n JOIN note_jobs j ON n.scope=j.scope AND n.writer=j.writer AND n.job_key=j.job_key WHERE j.active=1').fetchone()[0],
                      cachedResponses=self.db.execute('SELECT count(*) FROM responses').fetchone()[0])
        temporary=self.home/f'context-status-{os.getpid()}.tmp'
        try:
            temporary.write_text(json.dumps(values),encoding='utf-8')
            temporary.replace(self.home/'context-status.json')
        except OSError: pass  # Status readers must never interrupt recording or notes.
