"""Small, local models: Supra titles, quantized BART summaries, quoted action extraction."""
import json
import os
from pathlib import Path
import re
import subprocess
import uuid


def sentences(text):
    return [s.strip() for s in re.split(r"(?<=[.!?])\s+|\n+", text) if s.strip()]


def chunk_lines(lines, limit=3500):
    chunks, current = [], ""
    for line in lines:
        for at in range(0, max(1, len(line)), limit):
            part = line[at:at + limit]
            if current and len(current) + len(part) + 1 > limit:
                chunks.append(current)
                current = ""
            current += part + "\n"
    if current:
        chunks.append(current)
    return chunks


class SupraTitle:
    def __init__(self, config, home):
        self.config, self.home = config, Path(home)

    def heading(self, text):
        prompt = self.home / ("title-" + uuid.uuid4().hex + ".txt")
        try:
            # Exact training format; this is a title model, not an instruction/chat model.
            prompt.write_text("User: " + text.strip() + "\nTitle: ", encoding="utf-8")
            result = subprocess.run([
                self.config["titleCli"], "-m", self.config["titleModel"], "-f", str(prompt),
                "-n", "24", "-c", "4096", "-ngl", "0", "--threads", "4",
                "--temp", "0.4", "--top-p", "0.85", "--top-k", "40", "--repeat-penalty", "1.2",
                "--seed", "1", "--no-conversation", "--no-display-prompt", "--simple-io", "--no-warmup", "--color", "off"],
                capture_output=True, encoding="utf-8", timeout=90,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            if result.returncode != 0:
                raise RuntimeError("Supra title generation failed: " + result.stderr[-1000:])
            title = result.stdout.split("[end of text]", 1)[0].split("\n", 1)[0].strip().strip('"')
            if not title:
                raise RuntimeError("Supra returned an empty title.")
            return title.rstrip(".!? ")
        finally:
            prompt.unlink(missing_ok=True)

    def transcript_title(self, turns):
        chunks = chunk_lines([t["text"] for t in turns])
        if not chunks:
            return "No speech was detected in this recording."
        titles = [self.heading(c) for c in chunks]
        while len(titles) > 1:
            merged = chunk_lines(titles)
            if len(merged) >= len(titles):
                raise RuntimeError("The title model could not reduce the transcript headings.")
            titles = [self.heading(c) for c in merged]
        # Supra emits a topic phrase; keep the product's requested single-sentence title.
        return "The transcript discusses " + titles[0] + "."


class BartSummary:
    """BART encoder + merged cached decoder, without PyTorch or a model server."""
    def __init__(self, directory):
        import onnxruntime as ort
        from tokenizers import Tokenizer
        directory = Path(directory)
        self.config = json.loads((directory / "config.json").read_text(encoding="utf-8"))
        self.tokenizer = Tokenizer.from_file(str(directory / "tokenizer.json"))
        options = ort.SessionOptions()
        options.intra_op_num_threads = min(4, os.cpu_count() or 2)
        options.inter_op_num_threads = 1
        options.log_severity_level = 3
        self.encoder = ort.InferenceSession(str(directory / "onnx/encoder_model_quantized.onnx"), options, providers=["CPUExecutionProvider"])
        self.decoder = ort.InferenceSession(str(directory / "onnx/decoder_model_merged_quantized.onnx"), options, providers=["CPUExecutionProvider"])
        self.output_names = [o.name for o in self.decoder.get_outputs()]
        self.cache_names = [i.name for i in self.decoder.get_inputs() if i.name.startswith("past_key_values.")]

    def chunks(self, text):
        ids = self.tokenizer.encode(text, add_special_tokens=False).ids
        return [ids[i:i + 896] for i in range(0, len(ids), 896)]

    def summarize_ids(self, ids):
        import numpy as np
        if not ids:
            return ""
        if len(ids) < 24:
            return self.tokenizer.decode(ids, skip_special_tokens=True).strip()
        config = self.config
        input_ids = np.asarray([[config["bos_token_id"], *ids, config["eos_token_id"]]], dtype=np.int64)
        attention = np.ones_like(input_ids)
        hidden = self.encoder.run(None, {"input_ids": input_ids, "attention_mask": attention})[0]
        heads = config["decoder_attention_heads"]
        width = config["d_model"] // heads
        cache = {name: np.zeros((1, heads, 0, width), dtype=np.float32) for name in self.cache_names}
        sequences = [[config["decoder_start_token_id"]]]
        scores = np.zeros(1, dtype=np.float32)
        finished = []
        beams = 4
        maximum = min(142, max(32, len(ids) // 2))
        eos = config["eos_token_id"]
        for step in range(maximum):
            batch = len(sequences)
            feed = {
                "input_ids": np.asarray([[s[-1]] for s in sequences], dtype=np.int64),
                "encoder_hidden_states": np.repeat(hidden, batch, axis=0),
                "encoder_attention_mask": np.repeat(attention, batch, axis=0),
                "use_cache_branch": np.asarray([step > 0], dtype=np.bool_), **cache,
            }
            values = self.decoder.run(None, feed)
            outputs = dict(zip(self.output_names, values))
            logits = outputs["logits"][:, -1, :].copy()
            if step == 0:
                logits[:] = -1e9
                logits[:, config["bos_token_id"]] = 0
            else:
                logits[:, config["bos_token_id"]] = -1e9
                logits[:, config["pad_token_id"]] = -1e9
                if step < 6:
                    logits[:, eos] = -1e9
                # Standard BART no-repeat-trigram constraint.
                for row, sequence in enumerate(sequences):
                    prefix = tuple(sequence[-2:])
                    for at in range(len(sequence) - 2):
                        if tuple(sequence[at:at + 2]) == prefix:
                            logits[row, sequence[at + 2]] = -1e9
            if step == maximum - 1:
                logits[:] = -1e9
                logits[:, eos] = 0
            logits -= logits.max(axis=1, keepdims=True)
            log_probs = logits - np.log(np.exp(logits).sum(axis=1, keepdims=True))
            combined = log_probs + scores[:, None]
            size = combined.shape[1]
            flat = combined.reshape(-1)
            count = min(beams * 2, len(flat))
            top = np.argpartition(flat, -count)[-count:]
            top = top[np.argsort(flat[top])[::-1]]
            next_sequences, next_scores, parents = [], [], []
            for candidate in top:
                parent, token = divmod(int(candidate), size)
                score = float(flat[candidate])
                if score < -1e8:
                    continue
                sequence = sequences[parent] + [token]
                if token == eos:
                    finished.append((score / max(1, len(sequence) - 1) ** 2, sequence))
                elif len(next_sequences) < beams:
                    next_sequences.append(sequence)
                    next_scores.append(score)
                    parents.append(parent)
            finished.sort(key=lambda x: x[0], reverse=True)
            finished = finished[:beams]
            if len(finished) >= beams or not next_sequences:
                break
            for name in self.cache_names:
                value = outputs[name.replace("past_key_values.", "present.")]
                # The merged cache branch emits empty encoder caches after the first step.
                if ".encoder." in name and value.size == 0:
                    value = cache[name]
                cache[name] = value[parents]
            sequences = next_sequences
            scores = np.asarray(next_scores, dtype=np.float32)
        if finished:
            best = finished[0][1]
        else:
            best = sequences[int(np.argmax(scores))]
        return self.tokenizer.decode(best, skip_special_tokens=True).strip()

    def summarize(self, text):
        chunks = self.chunks(text)
        parts = [self.summarize_ids(ids) for ids in chunks]
        while len(parts) > 1:
            reduced = self.chunks(" ".join(parts))
            if len(reduced) >= len(parts):
                raise RuntimeError("BART could not reduce the long transcript. Section summaries were retained in memory; the transcript is saved.")
            parts = [self.summarize_ids(ids) for ids in reduced]
        return parts[0] if parts else ""


FIRST_PERSON = re.compile(r"\b(?:I(?:'ll| will| shall)|I'll|I am going to|I can take|I agree to|I promise to|ich werde|je vais|je m'engage|voy a|me comprometo|ik zal|zrobi[eę]|far[oò]|mi impegno)\b", re.IGNORECASE)
REQUEST = re.compile(r"\b(?:please|can you|could you|would you|action item|to-do|todo|we need to|we will|we'll|wir m[uü]ssen|kannst du|bitte|pouvez-vous|por favor|puedes|per favore|kun je|prosz[eę])\b", re.IGNORECASE)
NEGATED = re.compile(r"\b(?:will not|won't|cannot|can't|not going to|never|if I|if we)\b", re.IGNORECASE)
ISO_DATE = re.compile(r"\b\d{4}-\d{2}-\d{2}\b")


def extract_actions(turns):
    """Conservative, deterministic extraction. Every item is a literal source sentence."""
    actions, seen = [], set()
    for turn in turns:
        for sentence in sentences(turn["text"]):
            if NEGATED.search(sentence) or not (FIRST_PERSON.search(sentence) or REQUEST.search(sentence)):
                continue
            key = sentence.casefold()
            if key in seen:
                continue
            seen.add(key)
            assignee = turn["speaker"] if FIRST_PERSON.search(sentence) else ""
            due = ISO_DATE.search(sentence)
            actions.append(dict(text=sentence, assignee=assignee, due=due.group(0) if due else "", evidence=sentence))
    return actions


def summarize_notes(config, home, turns, progress):
    if not turns:
        raise ValueError("This transcript has no speech to summarize.")
    progress("Loading quantized BART summarizer locally...")
    bart = BartSummary(config["bartDirectory"])
    title = SupraTitle(config, home)
    text = "\n".join(t["text"] for t in turns)
    chunks = bart.chunks(text)
    section_notes, topics = [], []
    for index, ids in enumerate(chunks):
        progress(f"Summarizing transcript section {index + 1}/{len(chunks)} with BART...")
        summary = bart.summarize_ids(ids)
        section_notes.append(summary)
        topics.append(dict(title=title.heading(summary), points=sentences(summary)))
    summary = section_notes[0] if len(section_notes) == 1 else bart.summarize(" ".join(section_notes))
    return dict(summary=summary, keyPoints=sentences(summary), topics=topics, actionItems=extract_actions(turns))
