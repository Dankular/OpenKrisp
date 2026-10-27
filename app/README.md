# OpenKrisp for Windows

Local meeting and lecture recording, Parakeet Ultra transcription, speaker labels, searchable notes, summaries and action items in a WinUI 3 workspace.

## Workspace

- **Meetings:** one audio-source selector, a prominent fixed Record / Stop header, a searchable meeting list, and separate Notes and Recording & Transcript tabs. Summarize sits above the selected transcript.
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

Summaries run only on click. Model notes are estimates, and speaker identities/turn boundaries can be wrong with overlap, short spans, music and noise. Review source quotes before treating an item as an obligation.

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
