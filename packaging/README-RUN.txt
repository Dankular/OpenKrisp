OpenKrisp (pre-release, Windows x64)
====================================

This is an early build. It has not been tested on a real Windows machine by its authors yet.
Please report what happens.

1. Get the model (about 10 MB, one time). In PowerShell, from this folder:
       powershell -ExecutionPolicy Bypass -File .\get-model.ps1
   It is saved to %LOCALAPPDATA%\OpenKrisp\models\.

2. Run OpenKrisp.App.exe.
   Windows SmartScreen may warn that the app is from an unknown publisher: it is not code-signed.

What should work (untested on real hardware)
  - The window opens with two tabs: Noise cancellation and Note taker.
  - Note taker: add meetings and action items by hand, tick them off. They are saved to %LOCALAPPDATA%\OpenKrisp\.
  - The noise-removal engine loads and the switches are saved.
  - Audio routing (Noise cancellation tab): pick devices and press Start. The microphone can go to a playback device,
    or to the OpenKrisp virtual microphone if you have installed the MicyWMD driver yourself (not included).
    Use headphones while testing: there is no echo cancellation.

What does not work yet
  - Cleaning incoming audio needs a virtual speaker device (apps play into it, OpenKrisp captures and cleans it). None is included.
  - Voice isolation (Auto: remove nearby voices and echo) is not implemented. The tab says which parts are not active.
  - No recording, transcription or automatic action items.
  - No virtual microphone driver is included (see tools\ below).

tools\
  voiceclean.exe   Runs the noise removal on a WAV file:  voiceclean.exe <model.onnx> 48000 in48k.wav out.wav
                   (mono, 48 kHz, 16-bit or 32-bit float WAV).
  tone.exe         Test for the MicyWMD virtual-microphone driver. Only useful if you have built and installed that
                   driver on a test machine (see docs/virtual-mic.md in the repository).
