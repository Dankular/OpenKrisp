Early pre-release of OpenKrisp for Windows x64. **Not tested on a real Windows machine yet** (the build and the engine tests ran on GitHub's Windows runner; nothing here has been run with a microphone or speakers).

**Download** `OpenKrisp-win-x64.zip`, unzip, read `README-RUN.txt`, run `get-model.ps1` once, then `OpenKrisp.App.exe`.

Meant to work, but untested on real hardware: the two-tab window, the Note taker (manual action items, saved locally), the noise-removal engine, live audio routing with Start/Stop (use headphones: no echo cancellation), and the command-line file tool in `tools\`.
Does not: voice isolation (nearby voices and echo), recording/transcription, cleaning incoming audio without a virtual speaker device (none included), and the virtual microphone driver (not included; install MicyWMD yourself).

Not included on purpose: the DPDFNet model (download script provided) and the TEN VAD library (its license has extra conditions). The app is not code-signed, so SmartScreen will warn. The repository has no license file yet.

SHA-256 of the zip is in `OpenKrisp-win-x64.zip.sha256`.
