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

public sealed class EngineException : Exception
{
    public EngineException(string message) : base(message) { }
}
