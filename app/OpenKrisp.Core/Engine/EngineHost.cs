using System.Text;

namespace OpenKrisp.Core.Engine;

/// <summary>Owns one native engine instance. Thread-safe for the control calls (the native side uses atomics).</summary>
public sealed class EngineHost : IEngineController, IAudioController, IDisposable
{
    private IntPtr _handle;
    private int _externalEchoCancellation;
    public void SetExternalEchoCancellationActive(bool active) => Volatile.Write(ref _externalEchoCancellation, active ? 1 : 0);

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
        var externalEcho = Volatile.Read(ref _externalEchoCancellation) != 0;
        return new EngineStatus(
            s.MicNc != 0, s.SpeakerNc != 0, s.VoiceIsolation == 1 ? VoiceIsolationMode.Auto : VoiceIsolationMode.Off,
            s.VoiceIsolationActive != 0 || externalEcho, s.EchoCancelActive != 0 || externalEcho, s.NearbyVoiceRemovalActive != 0, s.SampleRate, s.Block, s.MicSamples, s.SpeakerSamples);
    }

    /// <summary>Feeds mono samples (any count) to a lane and returns the processed samples that are ready. Output lags input by one block.</summary>
    public float[] Process(Lane lane, float[] input)
    {
        var buf = new float[input.Length + (int)GetStatus().Block];
        Check(NativeMethods.vc_process(Handle, (int)lane, input, (nuint)input.Length, buf, (nuint)buf.Length, out var written), nameof(Process));
        return buf.AsSpan(0, (int)written).ToArray();
    }

    public IReadOnlyList<string> ListDevices(bool capture)
    {
        var buf = new byte[64 * 1024];
        var n = NativeMethods.vc_audio_list_devices(capture ? 0 : 1, buf, (nuint)buf.Length);
        if (n == -4) throw new EngineException("This build of the engine has no live audio support.");
        if (n < 0) throw new EngineException($"Listing audio devices failed (engine error {n})");
        var text = Encoding.UTF8.GetString(buf, 0, n);
        return text.Length == 0 ? Array.Empty<string>() : text.Split('\n');
    }

    public void StartAudio(AudioConfig config)
    {
        IntPtr micIn = IntPtr.Zero, micSink = IntPtr.Zero, loop = IntPtr.Zero, spkOut = IntPtr.Zero;
        try
        {
            micIn = Utf8(config.MicInput);
            micSink = Utf8(config.MicSinkDevice);
            loop = Utf8(config.SpeakerLoopback);
            spkOut = Utf8(config.SpeakerOutput);
            var native = new NativeMethods.VcAudioConfig
            {
                MicInput = micIn, MicSink = (int)config.MicSink, MicSinkDevice = micSink,
                MicyFormat = (int)config.MicyFormat, SpeakerLoopback = loop, SpeakerOutput = spkOut,
            };
            var err = new byte[1024];
            var rc = NativeMethods.vc_audio_start(Handle, ref native, err, (nuint)err.Length);
            if (rc != 0)
            {
                var n = Array.IndexOf(err, (byte)0);
                var msg = Encoding.UTF8.GetString(err, 0, n < 0 ? err.Length : n);
                throw new EngineException(msg.Length > 0 ? msg : $"Starting audio failed (engine error {rc})");
            }
        }
        finally
        {
            foreach (var p in new[] { micIn, micSink, loop, spkOut })
                if (p != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
        }
    }

    public void StopAudio() => Check(NativeMethods.vc_audio_stop(Handle), nameof(StopAudio));

    public AudioStats GetAudioStats()
    {
        Check(NativeMethods.vc_audio_get_stats(Handle, out var s), nameof(GetAudioStats));
        return new AudioStats(s.Running != 0, s.MicInputDropped, s.MicOutputUnderrunFrames, s.SpeakerInputDropped,
            s.SpeakerOutputUnderrunFrames, s.MicWorkerMaxBlockUs, s.SpeakerWorkerMaxBlockUs);
    }

    private static IntPtr Utf8(string? s) => s is null ? IntPtr.Zero : System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(s);

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
