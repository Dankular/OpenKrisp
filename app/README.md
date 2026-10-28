# OpenKrisp for Windows

Local meeting and lecture recording, Parakeet Ultra transcription, speaker labels, searchable notes, summaries and action items in a WinUI 3 workspace.

## Workspace

- **Meetings:** one audio-source selector, a prominent fixed Record / Stop header, a searchable meeting list, and separate Notes and Transcript tabs. Summarize sits above the selected transcript.
- **Action items:** Assigned to me and All action items views, search and type filters, tasks/decisions/follow-ups, editable text/owners/due dates, manual additions, deletion, and Markdown/CSV export. Completed items sort to the bottom.
- **Status:** model download and load progress, Tract state, Load, Unload, Redownload ASR and Restart Tract. Models install and load automatically. Lifecycle controls are disabled during recording and processing. Unload stops the model host to release GPU allocations.
- **Noise cancellation:** the existing Rust DPDFNet engine and audio routing controls.

## Local models

[Parakeet Ultra](https://huggingface.co/moondream/parakeet-ultra) is the ASR model, supporting 25 European languages including English, German, French, Spanish, Italian, Dutch and Polish. The [FP16 GGUF conversion](https://huggingface.co/handy-computer/parakeet-ultra-gguf), pinned to `a1539be1b849ea7463b88343e5277b373d0c4ea9`, runs on **native transcribe.cpp CUDA v0.4.1**. The runtime selects an exact CUDA device and refuses CPU fallback. No PyTorch, TensorFlow or standalone ONNX Runtime is required. The model is 1.26 GB, plus the native CUDA runtime. Native TDT word timestamps feed the existing speaker-label pipeline. Live preview uses overlapping short windows; final transcription uses overlapping 20-second windows. Ultra is a non-streaming model used for windowed live preview. Smart Turn remains on Tract CUDA. Tract cannot directly load Ultra's variable-length strided-slice ONNX export, so the ASR path uses the dedicated native runtime.

[Smart Turn v3 ONNX](https://huggingface.co/onnx-community/smart-turn-v3-ONNX), revision `dbd03130e87854011fa8c2f203077f2cbfc3adad`, runs on Tract CUDA and predicts semantic end-of-turn probability from audio. Its Whisper-derived encoder is used only for turn classification; it does not transcribe audio or replace Parakeet Ultra. Model turn endings and speaker changes determine paragraph boundaries. Fixed character-count and pause-based paragraph splitting are removed.

Speech segmentation uses the existing pyannote model on Tract CPU. Voice activity follows the model’s native powerset classification automatically. Smart Turn uses its trained binary classification to mark completed thoughts. Both confidence displays update from incoming audio and are read-only; there are no manual sensitivity thresholds. Raw recording peaks/RMS remain visible on a green-to-red meter. Speaker embeddings and final clustering use sherpa-onnx with NeMo TitaNet; the expected speaker count defaults to two (zero selects automatic clustering).

Titles use [Supra Title 50M](https://huggingface.co/SupraLabs/supra-title-50m-pre) Q8 weights and a minimal CPU completion runtime. Detailed notes use **[Qwen2.5-7B-Instruct-1M](https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-1M)** in Q4_K_M GGUF form, pinned to revision `381ac2097f05bff2f77e7e3864239c11e521c3d3` of [bartowski's conversion](https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-1M-GGUF). The native llama.cpp CUDA runtime loads it only while summarizing, fits GPU offload to available memory, and releases it when the operation ends. The app stops the ASR/Smart Turn host before Qwen notes generation to free GPU memory, then restores it after generation or cancellation; recording and summarization cannot run concurrently. No PyTorch or TensorFlow is installed. BART and its standalone ONNX Runtime/tokenizer dependencies are removed from the notes pipeline.

The model is the **1M-context checkpoint**, but this 8 GB GPU configuration uses a **16,384-token working context**, quantized KV cache and hierarchical section processing. It does not allocate or claim a million-token context. Every source section is read; detailed topic notes and grounded tasks are retained from all sections, while their overviews are consolidated. Qwen's published full-1M deployment requires substantially more VRAM than this machine.

Summarize generates a connected multi-paragraph overview, substantive key points, detailed topic sections, decisions, open questions, risks/blockers, and executable tasks/follow-ups. Tasks have a specific requested outcome and a separate verbatim source quote. A focused Qwen review rejects proposed tasks unsupported by an actual assignment, commitment or requested follow-up. Historical proposals, observations and rhetorical questions are not obligations. Names and dates are not guessed; literal relative deadlines such as “by Friday” remain visible and editable. Automatically resolved dates require an explicit ISO date in the evidence.

Live draft extraction still recognizes explicit commitments while recording; Summarize refines those drafts using Qwen. User edits, assignments, deadlines, completion state and deleted-item suppression survive regeneration. Speaker labels must be reassigned to **Me** when appropriate; the app does not guess which speaker is the user. Empty task lists are correct for recordings with no actual commitments.

Full detailed summaries run on click. During recording, the right-hand context panel adds supported note paragraphs after three new stable paragraphs or about 180 new words, at most once every 30 seconds. Earlier paragraphs persist. Qwen can mark a genuine duplicate, fuse redundant paragraphs while retaining unique details, or supersede a fact explicitly corrected later. Each entry retains verbatim source evidence; replaced entries remain in the context checkpoint for provenance. There is no fixed five-point list or whole-meeting two-paragraph rewrite that discards earlier topics. A batched, response-cached consolidation pass runs when duplicate headings appear or eight further memory entries accumulate; it operates on cited source passages and retains ungrouped points. Fusion is model judgment and can still be imperfect.

German and other detected foreign speech trigger a separate English-translation request after at least two confirmed new words, throttled to eight seconds, without waiting for a summary batch. A small `langid` CPU model gates those requests; English-only passages do not require a generative language-detection call. The same Qwen process handles translation and context fusion. Pending requests coalesce while preserving a pending summary request. Foreign words/phrases remain highlighted with an inline English gloss, and the original transcript is unchanged. Translations are batched and may lag speech; mixed-language detection can miss short phrases.

Confirmed speech is written directly to `context-engine.sqlite3` in the model home before summary generation. The local RAG index stores meeting IDs, speaker labels, source times, original text and available English translations. Existing saved transcripts are indexed at startup. SQLite FTS5/BM25 retrieves relevant earlier source excerpts for subsequent notes. This is lexical RAG, not an embedding-based vector search service. Use `python notes/worker.py context-search --home .notes-runtime --session MEETING_GUID --query "budget approval"` to inspect timestamped source results.

The context engine follows Iris's recall/serve/record pattern without requiring Redis or a network service. Persistent meeting-scoped checkpoints resume the ledger and processed-source watermark after pauses or worker restarts. Exact response caching includes model/runtime identity, prompts/schema, full source context and generation settings. Changed facts, numbers, dates, negation or source revisions cause misses; semantic similarity alone never licenses reusing an old summary. Completed model results are cached for 30 days, with LRU limits of 1,500 responses and 32 MiB of payload. Source transcripts and context ledgers are durable data, not expiring response-cache entries. Cache hits can bypass model loading, and repeated tokenizer requests reuse an in-process token-count cache. The Status tab shows indexed paragraphs, cache hits, cached responses and actual model-call counts; no fixed savings percentage is claimed.

A live Qwen session uses a 4K context, small GPU batches, two CPU threads and CUDA memory fitting alongside Ultra; it never unloads ASR. Notes pause temporarily if transcription falls more than 12 seconds behind. Pausing notes releases the live model, while its checkpoint remains available. Stopping recording or closing the app terminates its process tree. Full detailed summaries use the separate 16K path and temporarily release ASR GPU memory.

Output-budget exhaustion retries once with concise formatting, then live notes split the source batch recursively. Each completed live batch is checkpointed so a subsequent worker restart can skip it. Truncated JSON is never accepted as notes. Duplicate consolidation retries smaller groups and otherwise retains the existing ledger. Detailed summaries likewise split oversized source sections; if final synthesis cannot fit, complete section notes are retained instead of discarded. Recovery retries are bounded.

After Stop and transcribe, the progress panel reports actual stages and completed/total chunks: reading audio, speaker identification, speech detection, ASR, indexing/saving and title generation. Unknown-length stages are indeterminate; the remaining steps are listed explicitly. There is no timer-based progress estimate.

Draft context and supported translations are saved with the meeting, searchable and exported to Markdown. Final transcription removes translations no longer supported by its text. Retranscribing an existing transcript clears the old display draft. Source-scoped context checkpoints independently validate source revisions before reusing evidence. Review source passages before treating a generated point as an obligation.

## Setup and privacy

Python 3.12+ and a compatible NVIDIA CUDA 13 installation/driver are required for this CUDA build. Status reports missing runtime libraries or model errors. Model weights/runtime downloads are roughly 6.5 GB once, plus Python dependencies. Qwen Q4_K_M weights are 4.68 GB; installed CUDA libraries are reused where available. To prepare manually:

```powershell
python notes/bootstrap.py --home .notes-runtime
$env:OPENKRISP_NOTES_HOME = "$PWD\.notes-runtime"
```

The unpackaged development publish also discovers `.notes-runtime` beside the repository's `dist` folder. Normal installations store models under `%LOCALAPPDATA%\OpenKrisp\local-notes`.

Inference uses installed local files. An app-owned model host binds to loopback on a random port and uses a per-process bearer token; audio is never submitted to a cloud service. The host is terminated when the app closes. There is no meeting participant bot, online account, cloud/team synchronization, or Slack/HubSpot/Salesforce delivery integration. Markdown and CSV exports can be shared manually.

Meetings and action items live in `%LOCALAPPDATA%\OpenKrisp\action-items.json`; recordings live in its `recordings` folder. Automatic extraction's initial migration creates a backup. Capture is independent of live preview: if preview falls behind, all audio remains in the saved WAV and the final transcript is generated after stopping.

## Virtual microphone

For the configured Voicemeeter Standard setup, the playback virtual microphone is **Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)**. It is preferred in the single Audio source selector when available. The existing helper mirrors playback to B1:

```powershell
dotnet run --project tools/LoopbackMic -- --route-only
```

The helper preserves the hardware speaker route and saves/restores its previous routing. Create `stop.txt` beside the helper executable to stop it. Alternatively, select **Computer audio (speakers, YouTube, calls)** to capture playback directly through WASAPI.

## My voice virtual microphone

The Noise cancellation page provides selectable physical microphone, speaker echo reference and virtual-cable playback output. Windows Voice Capture DSP performs adaptive acoustic echo cancellation with synchronized source-mode capture; the existing Rust/Tract DPDFNet engine then suppresses background noise. No new ML framework or model download is needed for AEC. Confidence/level decisions are automatic.

Choose the playback device carrying the audio you hear as the echo reference. The cleaned output must use a separate virtual cable (for example CABLE Input → CABLE Output); select its recording side in the calling app. A source/output identity check blocks direct feedback. B1 playback transcription remains a separate route. Voicemeeter’s extra In 1 / In 2 channels require a licensed VAIO extension; their presence in Windows does not guarantee an active route.

The voice path reports running only after processed microphone samples reach the output. It keeps a silent reference stream open so capture continues while speaker playback is silent. This Windows DSP path currently captures mono 16 kHz audio, then resamples to 48 kHz for DPDFNet/output. It reuses the app’s existing noise engine; additional native audio routing must be stopped before starting this path. The microphone, reference and output selections persist.

AEC targets the selected loudspeaker signal, including other people’s voices played through those speakers. It does not provide voiceprint isolation from unrelated people physically speaking nearby. Acoustic attenuation has not been measured on the user’s selected microphone/speaker setup.

## Build

```powershell
cargo build --release -p openkrisp-engine --features audio
dotnet publish app/OpenKrisp.App -c Release -p:Platform=x64 -r win-x64 --self-contained true -o dist/OpenKrisp
```

The publish includes .NET, Windows App SDK, compiled XAML and PRI resources. No separate .NET runtime installation is required. The noise-removal model is resolved from `OPENKRISP_MODEL` or `%LOCALAPPDATA%\OpenKrisp\models\dpdfnet2_48khz_hr.onnx`. The My voice virtual-microphone path uses Windows acoustic echo cancellation followed by the same Rust/Tract DPDFNet engine. Personal voiceprint filtering of unrelated nearby voices remains unsupported.
