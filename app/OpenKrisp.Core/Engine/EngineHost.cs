using System.Text;

namespace OpenKrisp.Core.Engine;

/// <summary>Owns one native engine instance. Thread-safe for the control calls (the native side uses atomics).</summary>
public sealed class EngineHost : IEngineController, IDisposable
{
    private IntPtr _handle;

    private EngineHost(IntPtr handle) => _handle = handle;

    /// <exception cref="EngineException">The native library or model could not be loaded.</exception>
    public static EngineHost Create(string modelPath, uint sampleRate = 48000)
    {
        var err = new byte[512];
        IntPtr h;
        try
        {
            h = NativeMethods.vc_engine_create(modelPath, sampleRate, err, (nuint)err.Length);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new EngineException($"Could not load the native engine (openkrisp_engine): {e.Message}");
        }
        if (h == IntPtr.Zero)
        {
            var n = Array.IndexOf(err, (byte)0);
            throw new EngineException("Engine failed to start: " + Encoding.UTF8.GetString(err, 0, n < 0 ? err.Length : n));
        }
        return new EngineHost(h);
    }

    public void SetMicNoiseCancel(bool on) => Check(NativeMethods.vc_set_mic_nc(Handle, on ? 1 : 0), nameof(SetMicNoiseCancel));

    public void SetSpeakerNoiseCancel(bool on) => Check(NativeMethods.vc_set_speaker_nc(Handle, on ? 1 : 0), nameof(SetSpeakerNoiseCancel));

    public void SetVoiceIsolation(VoiceIsolationMode mode)
        => Check(NativeMethods.vc_set_voice_isolation(Handle, (int)mode), nameof(SetVoiceIsolation));

    public EngineStatus GetStatus()
    {
        Check(NativeMethods.vc_get_status(Handle, out var s), nameof(GetStatus));
        return new EngineStatus(
            s.MicNc != 0, s.SpeakerNc != 0, s.VoiceIsolation == 1 ? VoiceIsolationMode.Auto : VoiceIsolationMode.Off,
            s.VoiceIsolationActive != 0, s.EchoCancelActive != 0, s.NearbyVoiceRemovalActive != 0, s.SampleRate, s.Block, s.MicSamples, s.SpeakerSamples);
    }

    /// <summary>Feeds mono samples (any count) to a lane and returns the processed samples that are ready. Output lags input by one block.</summary>
    public float[] Process(Lane lane, float[] input)
    {
        var buf = new float[input.Length + (int)GetStatus().Block];
        Check(NativeMethods.vc_process(Handle, (int)lane, input, (nuint)input.Length, buf, (nuint)buf.Length, out var written), nameof(Process));
        return buf.AsSpan(0, (int)written).ToArray();
    }

    private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(EngineHost));

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new EngineException($"{what} failed (engine error {rc})");
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) NativeMethods.vc_engine_destroy(h);
        GC.SuppressFinalize(this);
    }

    ~EngineHost() => Dispose();
}
