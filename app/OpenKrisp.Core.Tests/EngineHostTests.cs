using OpenKrisp.Core.Engine;

namespace OpenKrisp.Core.Tests;

/// <summary>
/// Real P/Invoke against the Rust engine. Needs OPENKRISP_MODEL (the DPDFNet 48 kHz model) and the native library on the
/// loader path (LD_LIBRARY_PATH on Linux, PATH or the app folder on Windows); otherwise these tests are reported as skipped.
/// </summary>
public class EngineHostTests
{
    private static EngineHost CreateOrSkip()
    {
        var model = Environment.GetEnvironmentVariable("OPENKRISP_MODEL");
        Skip.If(string.IsNullOrEmpty(model) || !File.Exists(model), "OPENKRISP_MODEL not set");
        try { return EngineHost.Create(model!); }
        catch (EngineException e) when (e.Message.StartsWith("Could not load the native engine"))
        {
            Skip.If(true, e.Message);
            throw;
        }
    }

    [SkippableFact]
    public void Controls_round_trip_through_the_native_status()
    {
        using var e = CreateOrSkip();
        var s0 = e.GetStatus();
        Assert.False(s0.MicNoiseCancel || s0.SpeakerNoiseCancel);
        Assert.Equal(VoiceIsolationMode.Off, s0.VoiceIsolation);
        Assert.Equal(48000u, s0.SampleRate);
        Assert.Equal(480u, s0.Block);

        e.SetMicNoiseCancel(true);
        e.SetSpeakerNoiseCancel(true);
        e.SetVoiceIsolation(VoiceIsolationMode.Auto);
        var s1 = e.GetStatus();
        Assert.True(s1.MicNoiseCancel && s1.SpeakerNoiseCancel);
        Assert.Equal(VoiceIsolationMode.Auto, s1.VoiceIsolation);
        Assert.False(s1.VoiceIsolationActive); // not implemented in the engine
        Assert.False(s1.EchoCancelActive || s1.NearbyVoiceRemovalActive);
    }

    [SkippableFact]
    public void Bypass_delays_audio_by_one_block_and_counts_samples()
    {
        using var e = CreateOrSkip();
        var x = Enumerable.Range(0, 4800).Select(i => (float)Math.Sin(i * 0.05) * 0.2f).ToArray();
        var y = e.Process(Lane.Mic, x);

        Assert.Equal(x.Length, y.Length);
        Assert.All(y.Take(480), v => Assert.Equal(0f, v));
        Assert.Equal(x.Take(x.Length - 480), y.Skip(480));
        var s = e.GetStatus();
        Assert.Equal((ulong)x.Length, s.MicSamples);
        Assert.Equal(0ul, s.SpeakerSamples);
    }

    [SkippableFact]
    public void Enabling_noise_cancel_changes_the_audio()
    {
        using var e = CreateOrSkip();
        var rng = new Random(1);
        var x = Enumerable.Range(0, 480 * 60).Select(i => (float)(0.3 * Math.Sin(i * 0.05) + 0.05 * (rng.NextDouble() - 0.5))).ToArray();
        e.SetSpeakerNoiseCancel(true);
        var y = e.Process(Lane.Speaker, x);
        Assert.Equal(x.Length, y.Length);
        Assert.NotEqual(x.Take(x.Length - 480), y.Skip(480));
    }

    [SkippableFact]
    public void Audio_calls_are_wired_through_the_native_library()
    {
        using var e = CreateOrSkip();
        // Stats before any session: zeros and not running (valid in every build).
        var s = e.GetAudioStats();
        Assert.False(s.Running);
        Assert.Equal(0ul, s.MicInputDropped);
        e.StopAudio(); // no-op when nothing runs

        // Device listing works only in a build with the `audio` feature; otherwise the engine reports "no live audio".
        try
        {
            var inputs = e.ListDevices(capture: true);
            Assert.NotNull(inputs); // a CI machine may have no devices: an empty list is fine
        }
        catch (EngineException ex)
        {
            // no `audio` feature in this build, or a machine with no usable audio host (CI runners)
            Assert.True(
                ex.Message.Contains("no live audio support") || ex.Message.Contains("Listing audio devices failed"),
                ex.Message);
        }
    }

    [Fact]
    public void A_missing_model_gives_a_readable_error()
    {
        var ex = Assert.Throws<EngineException>(() => EngineHost.Create("/no/such/model.onnx"));
        Assert.True(
            ex.Message.StartsWith("Engine failed to start") || ex.Message.StartsWith("Could not load the native engine"),
            ex.Message);
    }
}
