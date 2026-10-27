"""Offline Parakeet Ultra transcription, ONNX diarization, Supra titles, and Qwen notes."""
import argparse
import gc
import json
import os
import queue
import threading
import struct
from pathlib import Path
import sys
import time


def progress(message):
    print(message, file=sys.stderr, flush=True)


def decode_audio(path):
    import av
    import numpy as np
    pieces = []
    with av.open(str(path)) as container:
        resampler = av.AudioResampler(format="fltp", layout="mono", rate=16000)
        for frame in container.decode(audio=0):
            pieces.extend(f.to_ndarray().reshape(-1) for f in resampler.resample(frame))
        pieces.extend(f.to_ndarray().reshape(-1) for f in resampler.resample(None))
    return np.concatenate(pieces).astype(np.float32) if pieces else np.zeros(0, np.float32)


def label_words(words, turns, offset=0, group=True):
    """Split ASR text at actual speaker changes using word/turn time overlap."""
    result = []
    for word in words:
        start, end = float(word["start"]) + offset, float(word["end"]) + offset
        overlaps = [(max(0, min(end, t["end"]) - max(start, t["start"])), t["speaker"]) for t in turns]
        overlap, speaker = max(overlaps, default=(0, "Unknown"))
        if overlap <= 0:
            continue  # No speech detected: do not turn background sounds into meeting text.
        text = word.get("word", word.get("text", "")).strip()
        if not text:
            continue
        if group and result and result[-1]["speaker"] == speaker and start - result[-1]["end"] < 1.5:
            result[-1]["text"] += " " + text
            result[-1]["end"] = end
        else:
            result.append(dict(start=start, end=end, speaker=speaker, text=text))
    return result


def make_diarizer(config, speakers=0):
    import sherpa_onnx as so
    return so.OfflineSpeakerDiarization(so.OfflineSpeakerDiarizationConfig(
        segmentation=so.OfflineSpeakerSegmentationModelConfig(
            pyannote=so.OfflineSpeakerSegmentationPyannoteModelConfig(model=config["segmentation"])),
        embedding=so.SpeakerEmbeddingExtractorConfig(model=config["embedding"], num_threads=2),
        clustering=so.FastClusteringConfig(num_clusters=speakers if speakers > 0 else -1, threshold=0.5),
        min_duration_on=0.3, min_duration_off=0.5))


def model_request(command, audio, **options):
    import base64
    import urllib.request
    import numpy as np
    url=os.environ.get("OPENKRISP_ASR_URL")
    token=os.environ.get("OPENKRISP_ASR_TOKEN")
    if not url or not token:
        raise RuntimeError("GPU model host is not loaded. Open Status and load ASR models.")
    payload=dict(audio=base64.b64encode(np.asarray(audio,dtype="<f4").tobytes()).decode("ascii"), **options)
    request=urllib.request.Request(url+"/"+command, data=json.dumps(payload).encode(), headers={"Content-Type":"application/json","Authorization":"Bearer "+token})
    with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request,timeout=180) as response:
        result=json.load(response)
    if "error" in result: raise RuntimeError(result["error"])
    return result


def decode_asr(audio):
    return model_request("infer",audio)


def transcribe(config, audio_path, speakers=0):
    progress("Reading audio...")
    audio = decode_audio(audio_path)
    if len(audio) == 0:
        return []
    progress("Identifying speakers locally...")
    diarizer = make_diarizer(config, speakers)
    segments = diarizer.process(audio).sort_by_start_time()
    names = {}
    turns = []
    for segment in segments:
        label = names.setdefault(segment.speaker, f"Speaker {len(names) + 1}")
        turns.append(dict(start=segment.start, end=segment.end, speaker=label))
    del diarizer
    gc.collect()
    progress("Detecting speech and semantic turn endings locally...")
    speech_spans, boundaries = [], []
    for end in range(32000, len(audio)+32000, 32000):
        end=min(end,len(audio)); start=max(0,end-128000)
        detected=model_request("analyze",audio[start:end])
        for span in detected["segments"]:
            left=max((end-32000)/16000,start/16000+span["start"])
            right=start/16000+span["end"]
            if right>left: speech_spans.append((left,right))
        if detected["segments"] and detected["turnComplete"]:
            boundary=start/16000+detected["segments"][-1]["end"]
            if not boundaries or boundary>boundaries[-1]+.1: boundaries.append(boundary)
    gated=[]
    for turn in turns:
        for first,last in speech_spans:
            start,end=max(first,turn["start"]),min(last,turn["end"])
            if end>start: gated.append(dict(start=start,end=end,speaker=turn["speaker"]))
    turns=gated
    transcript = []
    committed_end=-1.0
    for end in list(range(320000,len(audio),288000))+[len(audio)]:
        end=min(end,len(audio)); at=max(0,end-320000)
        progress(f"Transcribing with Parakeet Ultra on native CUDA: {end / len(audio):.0%}")
        if not any(t["end"]>at/16000 and t["start"]<end/16000 for t in turns):
            if end==len(audio): break
            continue
        data=decode_asr(audio[at:end])
        words=label_words(data.get("words",[]),turns,at/16000,group=False)
        fresh=[w for w in words if w["end"]>committed_end+.04 and w["start"]>=committed_end-.08]
        settled=[w for w in fresh if end==len(audio) or w["end"]<=end/16000-1]
        transcript.extend(settled)
        if settled: committed_end=settled[-1]["end"]
        if end==len(audio): break
    return compact_words(mark_paragraphs(transcript,boundaries))


# Titles remain independent of the detailed notes model.
from note_models import chunk_lines


def summarize(config, home, turns):
    from qwen_notes import summarize_notes
    return summarize_notes(config, home, turns, progress)


class SpeakerTracker:
    def __init__(self, config, max_speakers=0):
        import sherpa_onnx as so
        self.extractor = so.SpeakerEmbeddingExtractor(so.SpeakerEmbeddingExtractorConfig(model=config["embedding"], num_threads=2))
        self.centers = []
        self.max_speakers = max_speakers

    def identify(self, samples, used):
        import numpy as np
        stream = self.extractor.create_stream()
        stream.accept_waveform(sample_rate=16000, waveform=samples)
        stream.input_finished()
        if not self.extractor.is_ready(stream):
            return "Unknown"
        vector = np.asarray(self.extractor.compute(stream), dtype=np.float32)
        vector /= max(float(np.linalg.norm(vector)), 1e-8)
        candidates = [(float(np.dot(vector, c)), i) for i, c in enumerate(self.centers)]
        score, index = max(candidates, default=(-1, -1))
        may_add = self.max_speakers <= 0 or len(self.centers) < self.max_speakers
        if not self.centers or (score < 0.6 and may_add and len(samples) >= 16000):
            index = len(self.centers)
            self.centers.append(vector)
        else:
            center = self.centers[index] * 0.9 + vector * 0.1
            self.centers[index] = center / max(float(np.linalg.norm(center)), 1e-8)
        used.add(index)
        return f"Speaker {index + 1}"


def mark_paragraphs(words, boundaries):
    result=[]; index=0
    for word in words:
        word=dict(word)
        while index<len(boundaries) and boundaries[index]<=word["start"]+.04:
            if result: word["paragraphStart"]=True
            index+=1
        result.append(word)
    return result


def compact_words(words):
    rows = []
    for word in words:
        if rows and rows[-1]["speaker"] == word["speaker"] and not word.get("paragraphStart", False):
            rows[-1]["text"] += " " + word["text"]
            rows[-1]["end"] = word["end"]
        else:
            rows.append(dict(word))
    return rows


def live(config, expected_speakers=0):
    import numpy as np
    packets = queue.Queue(maxsize=1)
    latest = [0.0]
    def exact(size):
        data = bytearray()
        while len(data) < size:
            part = sys.stdin.buffer.read(size - len(data))
            if not part:
                return None
            data.extend(part)
        return bytes(data)
    def read_packets():
        try:
            while True:
                header = exact(12)
                if header is None:
                    break
                offset, count = struct.unpack("<dI", header)
                if count > 16000 * 10:
                    break
                raw = exact(count * 4)
                if raw is None:
                    break
                latest[0] = offset + count / 16000
                if packets.full():
                    try: packets.get_nowait()
                    except queue.Empty: pass
                packets.put((offset, np.frombuffer(raw, dtype="<f4").copy()))
        finally:
            packets.put(None)
    threading.Thread(target=read_packets, daemon=True).start()
    identities = SpeakerTracker(config, expected_speakers)
    tail = np.zeros(0, np.float32)
    expected_end = 0.0
    stable, pending = [], []
    boundaries = []
    committed_end = -1.0
    skipped = 0.0
    while True:
        packet = packets.get()
        if packet is None:
            if pending:
                print(json.dumps(dict(turns=compact_words(mark_paragraphs(stable + pending, boundaries)), audioEnd=expected_end, processingSeconds=0, skippedSeconds=skipped)), flush=True)
            return
        offset, new_audio = packet
        if offset - expected_end > 0.1:
            skipped += offset - expected_end
            tail = np.zeros(0, np.float32)
        window = np.concatenate([tail, new_audio])
        window_start = offset - len(tail) / 16000
        end = offset + len(new_audio) / 16000
        started = time.monotonic()
        detection = model_request("analyze", window)
        segments = detection["segments"]
        # Embed short speech spans separately: one mixed-voice window must not
        # gradually drag a person's identity centroid toward the next speaker.
        turns = []
        for segment in segments:
            first, last = max(0,int(segment["start"]*16000)), min(len(window),int(segment["end"]*16000))
            length = last - first
            count = max(1, length // 32000)
            for indices in np.array_split(np.arange(first,last), count):
                if len(indices) == 0:
                    continue
                first_sample, last_sample = int(indices[0]), int(indices[-1])+1
                label = identities.identify(window[first_sample:last_sample], set())
                turns.append(dict(start=window_start+first_sample/16000,end=window_start+last_sample/16000,speaker=label))
        data = decode_asr(window) if turns else {"words": []}
        words = label_words(data.get("words", []), turns, window_start, group=False)
        fresh = [w for w in words if w["end"] > committed_end + 0.04 and w["start"] >= committed_end - 0.08]
        pending = [w for w in fresh if w["end"] > end - 1.0]
        settled = [w for w in fresh if w["end"] <= end - 1.0]
        stable.extend(settled)
        if settled:
            committed_end = settled[-1]["end"]
        expected_end = end
        tail = window[-6*16000:]
        if segments and detection["turnComplete"]:
            boundary = window_start + segments[-1]["end"]
            if not boundaries or boundary > boundaries[-1] + .1: boundaries.append(boundary)
        print(json.dumps(dict(turns=compact_words(mark_paragraphs(stable + pending, boundaries)), audioEnd=end,
            processingSeconds=time.monotonic()-started, skippedSeconds=skipped,
            speechDetected=detection.get("speechDetected", False), speechProbability=detection["speechProbability"],
            turnProbability=detection["turnProbability"], turnComplete=detection["turnComplete"], turnBoundaries=boundaries), ensure_ascii=False), flush=True)


def create_title(config, home, turns):
    from note_models import SupraTitle
    progress("Generating a title with Supra 50M locally...")
    return {"title": SupraTitle(config, home).transcript_title(turns)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["transcribe", "summarize", "live", "title"])
    parser.add_argument("--home", required=True)
    parser.add_argument("--input")
    parser.add_argument("--output")
    parser.add_argument("--speakers", type=int, default=0)
    parser.add_argument("--config", default="config.json")
    args = parser.parse_args()
    home = Path(args.home).resolve()
    os.environ.update(HF_HOME=str(home / "cache"), HF_HUB_OFFLINE="1", NEEDLE_TELEMETRY="0", DO_NOT_TRACK="1", HF_HUB_DISABLE_TELEMETRY="1")
    config = json.loads((home / args.config).read_text(encoding="utf-8-sig"))
    if args.command == "live":
        live(config, args.speakers)
        return
    if not args.input or not args.output:
        parser.error("--input and --output are required")
    if args.command == "transcribe":
        result = transcribe(config, args.input, args.speakers)
    else:
        turns = json.loads(Path(args.input).read_text(encoding="utf-8-sig"))
        result = create_title(config, home, turns) if args.command == "title" else summarize(config, home, turns)
    output = Path(args.output)
    temp = output.with_suffix(".tmp")
    temp.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(output)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        progress(str(error))
        sys.exit(1)
