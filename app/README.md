# OpenKrisp for Windows

Local meeting and lecture recording, Whistle transcription, speaker labels, searchable notes, summaries and action items in a WinUI 3 workspace.

## Workspace

- **Meetings:** one audio-source selector, a prominent fixed Record / Stop header, a searchable meeting list, and separate Notes and Recording & Transcript tabs. Summarize sits above the selected transcript.
- **Action items:** Assigned to me and All action items views, search and type filters, tasks/decisions/follow-ups, editable text/owners/due dates, manual additions, deletion, and Markdown/CSV export. Completed items sort to the bottom.
- **Status:** model download and load progress, Tract state, Load, Unload, Redownload ASR and Restart Tract. Models install and load automatically. Lifecycle controls are disabled during recording and processing. Unload stops the model host to release GPU allocations.
- **Noise cancellation:** the existing Rust DPDFNet engine and audio routing controls.

## Local models

Whistle remains the ASR model in English, German, French, Spanish, Italian, Dutch and Polish. The app uses an [ONNX export of the same Whistle weights](https://huggingface.co/mrfakename/whistle-ONNX), pinned to revision `14fc92cb09ad5c271fe9a4ec4be70274790050d2`, on **Sonos Tract CUDA**. There is no silent CPU ASR fallback. Tract 0.23.9 handles Gather/index operations on CPU to avoid its empty initial KV-cache CUDA Gather limitation; the ASR neural network executes on CUDA.

[Smart Turn v3 ONNX](https://huggingface.co/onnx-community/smart-turn-v3-ONNX), revision `dbd03130e87854011fa8c2f203077f2cbfc3adad`, runs on Tract CUDA and predicts semantic end-of-turn probability from audio. Its Whisper-derived encoder is used only for turn classification; it does not transcribe audio or replace Whistle. Model turn endings and speaker changes determine paragraph boundaries. Fixed character-count and pause-based paragraph splitting are removed.

Speech segmentation uses the existing pyannote model on Tract CPU. The live speech threshold controls its actual speech posterior. The separate Smart Turn threshold controls turn completion. Raw recording peaks/RMS remain visible on a green-to-red meter, independently of both model thresholds. Speaker embeddings and final clustering use sherpa-onnx with NeMo TitaNet; the expected speaker count defaults to two (zero selects automatic clustering).

Titles use [Supra Title 50M](https://huggingface.co/SupraLabs/supra-title-50m-pre) Q8 weights and a minimal CPU llama.cpp completion runtime. Summaries, key points and topic sections use quantized BART-large-CNN ONNX. These purpose-built note models replace Qwen. No PyTorch or TensorFlow dependency is installed.

Automatic action extraction runs on live and saved transcripts without waiting for Summarize. It conservatively recognizes explicit English commitments, requests, decisions and follow-ups, retaining exact source quotes. It does not infer speaker names or unstated obligations. Due dates are automatically filled only for explicit ISO dates; other dates can be entered manually. User edits, completion state and deleted-item suppression survive extraction and summarization. Generated speaker labels must be reassigned to **Me** when appropriate; the app does not guess which speaker is the user.

Summaries are generated only on click, using chunking and hierarchical reduction for long transcripts. BART and the automatic extraction rules currently target English; Whistle still supports all seven ASR languages. Speaker identities and semantic turn boundaries are model estimates, particularly with overlap, short spans, music and noise.

## Setup and privacy

Python 3.12+ and a compatible NVIDIA CUDA 13 installation/driver are required for this CUDA build. Status reports missing runtime libraries or model errors. Model weights/runtime downloads are roughly 800 MB once, plus Python dependencies. To prepare manually:

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

## Build

```powershell
cargo build --release -p openkrisp-engine --features audio
dotnet publish app/OpenKrisp.App -c Release -p:Platform=x64 -r win-x64 --self-contained true -o dist/OpenKrisp
```

The publish includes .NET, Windows App SDK, compiled XAML and PRI resources. No separate .NET runtime installation is required. The noise-removal model is resolved from `OPENKRISP_MODEL` or `%LOCALAPPDATA%\OpenKrisp\models\dpdfnet2_48khz_hr.onnx`. Voice isolation, echo cancellation and nearby-voice removal remain unimplemented.
