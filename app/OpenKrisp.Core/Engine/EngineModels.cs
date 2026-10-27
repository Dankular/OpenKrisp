namespace OpenKrisp.Core.Engine;

public enum VoiceIsolationMode { Off = 0, Auto = 1 }

public enum Lane { Mic = 0, Speaker = 1 }

/// <summary>Snapshot of the engine state (mirrors <c>VcStatus</c> in openkrisp_engine.h).</summary>
public sealed record EngineStatus(
    bool MicNoiseCancel,
    bool SpeakerNoiseCancel,
    VoiceIsolationMode VoiceIsolation,
    bool VoiceIsolationActive,
    bool EchoCancelActive,
    bool NearbyVoiceRemovalActive,
    uint SampleRate,
    uint Block,
    ulong MicSamples,
    ulong SpeakerSamples);

public interface IEngineController
{
    void SetMicNoiseCancel(bool on);
    void SetSpeakerNoiseCancel(bool on);
    void SetVoiceIsolation(VoiceIsolationMode mode);
    EngineStatus GetStatus();
}

public enum MicSinkKind { Discard = 0, MicyVirtualMic = 1, RenderDevice = 2 }

/// <summary>PCM format written to the MicyWMD driver. Which one the driver really expects is not yet known (see docs/virtual-mic.md).</summary>
public enum MicyFormat { Int32 = 0, Float32 = 1, Int16 = 2 }

/// <summary>Live audio routing. A null device name means the system default (or, for <see cref="SpeakerLoopback"/>, "lane off").</summary>
public sealed record AudioConfig(
    string? MicInput,
    MicSinkKind MicSink,
    string? MicSinkDevice,
    MicyFormat MicyFormat,
    string? SpeakerLoopback,
    string? SpeakerOutput);

public sealed record AudioStats(
    bool Running,
    ulong MicInputDropped,
    ulong MicOutputUnderrunFrames,
    ulong SpeakerInputDropped,
    ulong SpeakerOutputUnderrunFrames,
    ulong MicWorkerMaxBlockUs,
    ulong SpeakerWorkerMaxBlockUs);

public interface IAudioController
{
    /// <summary>Capture devices (<paramref name="capture"/> = true) or render devices.</summary>
    IReadOnlyList<string> ListDevices(bool capture);
    void StartAudio(AudioConfig config);
    void StopAudio();
    AudioStats GetAudioStats();
}

public sealed class EngineException : Exception
{
    public EngineException(string message) : base(message) { }
}
