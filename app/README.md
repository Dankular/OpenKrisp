# OpenKrisp app (Windows)

| project | what | status |
|---|---|---|
| `OpenKrisp.Core` | Engine bindings (P/Invoke to the Rust `openkrisp_engine`), the two tab view models, the action-item store. Plain `net8.0`, no UI framework. | Built and tested on Linux (24 tests) |
| `OpenKrisp.Core.Tests` | xunit tests, including real calls into the Rust library | Passing on Linux |
| `OpenKrisp.App` | WinUI 3 shell: "Noise cancellation" and "Note taker" tabs | **Not built or run.** Windows only; the dev sandbox has no Windows. XAML is checked for well-formedness only |

## What the app does today

**Noise cancellation tab:** switches for "Cancel my noise" (microphone lane) and "Cancel others' noise" (incoming-audio lane), and "Voice isolation" Off / Auto.
Auto is meant to remove nearby voices and echo on top of standard noise removal. Neither is implemented, and the tab says which parts are not active yet
(the text comes from the engine's own capability flags). The tab also shows whether any audio is actually flowing through the engine.

**Audio routing (same tab):** choose the microphone, where the cleaned microphone goes (the OpenKrisp virtual microphone, a playback device, or nowhere),
and optionally a virtual speaker to capture for incoming audio plus the real device to play the cleaned result on. Start/Stop begins and ends live audio; the status
line shows the worst processing time per block and any dropped samples or gaps. Until you press Start nothing feeds audio into the engine, so the switches change settings but not sound.
Capturing and playing on the same device is refused, because the cleaned audio would loop. None of this has been run on a real device: it is compile-checked and its logic is tested with a fake engine.

**Note taker tab:** action items grouped by meeting, with "Assigned to me" and "All action items" views, time, due date and assignee per item, and "Add action item…".
Items are added by hand and saved to `%LOCALAPPDATA%\OpenKrisp\action-items.json`. Recording, transcription and automatic extraction are not implemented
(extraction needs a local language model that has not been chosen).

## Build and test

On any OS with the .NET 8 SDK (the Core library and tests):

```
cargo build --release -p openkrisp-engine          # from the repo root (add --features audio for live audio; needs libasound2-dev on Linux)
export OPENKRISP_MODEL=/path/to/dpdfnet2_48khz_hr.onnx
export LD_LIBRARY_PATH=$PWD/target/release          # Linux; on Windows put openkrisp_engine.dll next to the test binaries or on PATH
dotnet test app/OpenKrisp.Core.Tests
```

Without `OPENKRISP_MODEL` the native-engine tests are reported as skipped, not passed.

On Windows (the app; needs the .NET 8 SDK and Visual Studio build tools for WinUI 3):

```
cargo build --release -p openkrisp-engine
dotnet build app/OpenKrisp.App -c Release -p:Platform=x64
```

The app looks for the model at `%LOCALAPPDATA%\OpenKrisp\models\dpdfnet2_48khz_hr.onnx` (or `OPENKRISP_MODEL`). If the model or the engine DLL is missing the
app still opens and the Noise cancellation tab explains what is missing. The project pins Windows App SDK 1.8.260921001; that combination has not been compiled yet.
