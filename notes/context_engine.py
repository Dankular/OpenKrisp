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

    def status(self):
        import os
        values={r['name']:r['value'] for r in self.db.execute('SELECT * FROM counters')}
        values.update(indexedParagraphs=self.db.execute('SELECT count(*) FROM chunks').fetchone()[0],
                      cachedResponses=self.db.execute('SELECT count(*) FROM responses').fetchone()[0])
        temporary=self.home/f'context-status-{os.getpid()}.tmp'
        try:
            temporary.write_text(json.dumps(values),encoding='utf-8')
            temporary.replace(self.home/'context-status.json')
        except OSError: pass  # Status readers must never interrupt recording or notes.
