OpenKrisp (pre-release, Windows x64)
====================================

Windows x64 unpackaged build with bundled .NET and Windows App SDK.

1. Get the model (about 10 MB, one time). In PowerShell, from this folder:
       powershell -ExecutionPolicy Bypass -File .\get-model.ps1
   It is saved to %LOCALAPPDATA%\OpenKrisp\models\.

2. Run OpenKrisp.App.exe.
   Windows SmartScreen may warn that the app is from an unknown publisher: it is not code-signed.

Features
  - The window opens with Meetings, Action items, Noise cancellation and Status.
  - Note taker: add meetings and action items by hand, tick them off. They are saved to %LOCALAPPDATA%\OpenKrisp\.
  - The noise-removal engine loads and the switches are saved.
  - Audio routing (Noise cancellation tab): pick devices and press Start. The microphone can go to a playback device,
    or to the OpenKrisp virtual microphone if you have installed the MicyWMD driver yourself (not included).
    My voice uses Windows AEC with a selectable speaker reference and separate virtual-cable output.

What does not work yet
  - Cleaning incoming audio needs a virtual speaker device (apps play into it, OpenKrisp captures and cleans it). None is included.
  - Personal voiceprint filtering of unrelated nearby speakers is not implemented.
  - Cloud/team sync and Slack/HubSpot/Salesforce delivery are not connected; use local exports.
  - No virtual microphone driver is included (see tools\ below).

tools\
  voiceclean.exe   Runs the noise removal on a WAV file:  voiceclean.exe <model.onnx> 48000 in48k.wav out.wav
                   (mono, 48 kHz, 16-bit or 32-bit float WAV).
  tone.exe         Test for the MicyWMD virtual-microphone driver. Only useful if you have built and installed that
                   driver on a test machine (see docs/virtual-mic.md in the repository).

Models download and load automatically. Status provides Load, Unload, Redownload ASR and Restart Tract.
Python 3.12+ and compatible NVIDIA CUDA 13 libraries are required; models/runtime download about 5 GB once.
Record or import audio, then use Summarize above each saved transcript for key points, topics and action items.
Whistle ASR and Smart Turn execute on Tract CUDA. Supra 50M titles and detailed Qwen2.5-7B-Instruct-1M notes run locally.
The 1M checkpoint uses a 16K working context here; longer transcripts are processed fully in sections.
Whistle supports English, German, French, Spanish, Italian, Dutch and Polish.
