# Virtual microphone: driver choice

We need a Windows microphone endpoint that other apps (Teams, Zoom, Discord...) can select, fed with our cleaned audio.
Four candidates were read from source (shallow clones, 2026-10-27). Nothing here has been built or run: there is no Windows machine in the dev sandbox.

| project | what it is (from its code) | fits? |
|---|---|---|
| [MicyWMD](https://github.com/Mithronn/MicyWMD) (MIT, last commit 2025-11-18) | One capture endpoint "MicyAudio", fed by a user-space IOCTL into a global ring buffer | **Chosen** |
| [Virtual-Audio-Driver](https://github.com/VirtualDrivers/Virtual-Audio-Driver) (MIT, last commit 2026-03-03) | Virtual speaker + virtual mic. The mic's capture stream writes a generated tone (or silence); there is no user-space input path in the open-source code (no IOCTL, pipe or shared memory). The README offers named pipes / shared memory only as paid custom builds. | No |
| [AudioMirror](https://github.com/JannesP/AudioMirror) (MIT, single commit 2022-10-15) | A real virtual cable: audio played to its speaker lands in its mic (ring buffer between the paired streams). The author says it is unfinished with quirks, tested only on Windows 10 x64. | Possible fallback, not chosen |
| [Scream](https://github.com/duncanthrax/scream) (MS-PL, active) | A virtual *speaker* that multicasts audio over the network. No microphone endpoint. | No |

## Why MicyWMD

It is the only candidate where our code can push audio in directly: open `\\.\MicyAudio` and call `DeviceIoControl` with
`IOCTL_MICYAUDIO_SET_AUDIO_DATA` (`0x001D2408`). It advertises 48 kHz, matching the pipeline, and it is MIT licensed, so we may build and sign our own copy.
(An AudioMirror-style cable would instead need us to open the virtual speaker and play into it, a second audio device in the loop.)

## What is known and unknown

Read from the source (not run):
- The driver appends the whole IOCTL input buffer to the ring; it does not parse the `StreamId/DataSize` header that the project's own test sender prepends. We send raw PCM with no header.
- The endpoint's only advertised format is 48 kHz, 2 channels, 32-bit, subtype PCM. A comment next to it says "float sample", which contradicts the GUID. **Unknown which is right**; `virtualmic` can write I32, F32 or I16.
- The project's test sender generates 16-bit samples, which does not match the advertised 32-bit format.
- Underrun gives silence; overflow drops the oldest data. The ring is 1 MB by default (about 2.6 s at 48 kHz stereo 32-bit). There is no call to read its fill level, so latency must be controlled by pacing writes to real time.
- I did not find access restrictions on the control device in the code, so who may open it is unknown. There is one global ring, so any process that can open it can inject audio.
- Shipping needs a signed driver (the project's README says EV certificate plus Microsoft attestation signing; test signing is for development only).

## Windows spike (needs a test VM with test signing on)

1. Build and install MicyWMD (`msbuild MicyAudio.sln /p:Configuration=Release /p:Platform=x64`, then `pnputil /add-driver MicyAudio.inf /install`).
2. `cargo run --example tone -p virtualmic -- i32 10`, with "MicyAudio" chosen as the microphone in Sound settings (watch the input level meter) or in a recording app.
3. If you hear a clean 440 Hz tone: the format is right. If noise or the wrong pitch: repeat with `f32`, then `i16`. Record which one works in this file.
4. Watch for glitches over several minutes; if audio drifts late, the ring is filling and the pacing in the sender needs a correction loop.

## The incoming-audio lane needs a virtual *speaker*

MicyWMD provides only a microphone. To clean what you hear, applications must play into a virtual speaker that OpenKrisp captures (loopback) and then plays, cleaned,
on the real speakers. Capturing and playing on the same device would loop, and the engine refuses that. Of the projects read, Virtual-Audio-Driver ships a virtual speaker
(beta, needs test signing); whether its speaker works for this purpose has not been tested. See `crates/engine/src/session.rs`.
