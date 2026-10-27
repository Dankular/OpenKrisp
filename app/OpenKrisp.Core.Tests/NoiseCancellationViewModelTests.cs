using OpenKrisp.Core.Common;
using OpenKrisp.Core.Engine;
using OpenKrisp.Core.NoiseCancellation;

namespace OpenKrisp.Core.Tests;

internal sealed class FakeEngine : IEngineController
{
    public bool Mic, Speaker;
    public VoiceIsolationMode Isolation;
    public ulong MicSamples, SpeakerSamples;
    public bool EchoActive, NearbyActive;
    public bool FailNext;
    public List<string> Calls = new();

    public void SetMicNoiseCancel(bool on) { Fail(); Mic = on; Calls.Add($"mic={on}"); }
    public void SetSpeakerNoiseCancel(bool on) { Fail(); Speaker = on; Calls.Add($"speaker={on}"); }
    public void SetVoiceIsolation(VoiceIsolationMode mode) { Fail(); Isolation = mode; Calls.Add($"iso={mode}"); }
    public EngineStatus GetStatus() => new(Mic, Speaker, Isolation, EchoActive || NearbyActive, EchoActive, NearbyActive, 48000, 480, MicSamples, SpeakerSamples);

    private void Fail()
    {
        if (!FailNext) return;
        FailNext = false;
        throw new EngineException("boom");
    }
}

internal sealed class MemorySettings : ISettingsStore
{
    public AppSettings Current = new();
    public int Saves;
    public AppSettings Load() => Current;
    public void Save(AppSettings s) { Current = s; Saves++; }
}

public class NoiseCancellationViewModelTests
{
    [Fact]
    public void Toggles_reach_the_engine_and_are_saved()
    {
        var e = new FakeEngine();
        var s = new MemorySettings();
        var vm = new NoiseCancellationViewModel(e, null, s);

        vm.MyNoiseCancel = true;
        vm.OthersNoiseCancel = true;
        vm.VoiceIsolation = VoiceIsolationMode.Auto;

        Assert.True(e.Mic && e.Speaker);
        Assert.Equal(VoiceIsolationMode.Auto, e.Isolation);
        Assert.Equal(new AppSettings { MyNoiseCancel = true, OthersNoiseCancel = true, VoiceIsolation = 1 }.VoiceIsolation, s.Current.VoiceIsolation);
        Assert.True(s.Current.MyNoiseCancel && s.Current.OthersNoiseCancel);
    }

    [Fact]
    public void Saved_settings_are_applied_to_the_engine_at_startup()
    {
        var e = new FakeEngine();
        var s = new MemorySettings { Current = new AppSettings { MyNoiseCancel = true, VoiceIsolation = 1 } };
        var vm = new NoiseCancellationViewModel(e, null, s);

        Assert.True(vm.MyNoiseCancel);
        Assert.False(vm.OthersNoiseCancel);
        Assert.True(e.Mic);
        Assert.Equal(VoiceIsolationMode.Auto, e.Isolation);
    }

    [Fact]
    public void A_failing_engine_call_keeps_the_old_value_and_reports_the_error()
    {
        var e = new FakeEngine();
        var s = new MemorySettings();
        var vm = new NoiseCancellationViewModel(e, null, s);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, a) => raised.Add(a.PropertyName);

        e.FailNext = true;
        vm.MyNoiseCancel = true;

        Assert.False(vm.MyNoiseCancel);
        Assert.Equal("boom", vm.Error);
        Assert.Equal(0, s.Saves);
        Assert.Contains(nameof(vm.MyNoiseCancel), raised); // so a bound toggle snaps back
    }

    [Fact]
    public void Voice_isolation_auto_describes_what_it_does_and_says_what_is_not_active()
    {
        var e = new FakeEngine();
        var vm = new NoiseCancellationViewModel(e, null, new MemorySettings());
        Assert.Contains("nearby voices and echo", NoiseCancellationViewModel.IsolationDescription);
        Assert.Equal("", vm.IsolationNote); // Off: nothing to warn about

        vm.VoiceIsolation = VoiceIsolationMode.Auto;
        Assert.Equal("Not active yet: nearby-voice removal and echo removal. Until then Auto sounds the same as Off.", vm.IsolationNote);

        e.EchoActive = true; // echo removal comes online
        vm.Refresh();
        Assert.Equal("Not active yet: nearby-voice removal. Auto currently does only the part that is active.", vm.IsolationNote);

        e.NearbyActive = true;
        vm.Refresh();
        Assert.Equal("", vm.IsolationNote); // everything Auto promises is running
    }

    [Fact]
    public void Isolation_index_binds_to_the_mode_and_raises_the_dependent_notifications()
    {
        var e = new FakeEngine();
        var vm = new NoiseCancellationViewModel(e, null, new MemorySettings());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, a) => raised.Add(a.PropertyName);

        vm.VoiceIsolationIndex = 1;

        Assert.Equal(VoiceIsolationMode.Auto, e.Isolation);
        Assert.True(vm.HasIsolationNote);
        Assert.Contains(nameof(vm.VoiceIsolationIndex), raised);
        Assert.Contains(nameof(vm.HasIsolationNote), raised);
        Assert.Contains(nameof(vm.IsolationNote), raised);
        vm.VoiceIsolationIndex = 0;
        Assert.False(vm.HasIsolationNote);
    }

    [Fact]
    public void Status_text_says_so_when_no_audio_flows_and_reports_seconds_when_it_does()
    {
        var e = new FakeEngine();
        var vm = new NoiseCancellationViewModel(e, null, new MemorySettings());
        Assert.Contains("No audio is flowing", vm.StatusText);

        e.MicSamples = 96000;
        vm.Refresh();
        Assert.Contains("microphone 2.0 s", vm.StatusText);
        Assert.Contains("incoming 0.0 s", vm.StatusText);
    }

    [Fact]
    public void Without_an_engine_the_controls_are_unavailable_and_changes_are_ignored()
    {
        var s = new MemorySettings();
        var vm = new NoiseCancellationViewModel(null, "model file not found", s);
        Assert.False(vm.IsAvailable);
        Assert.Equal("model file not found", vm.StatusText);

        vm.MyNoiseCancel = true;
        Assert.False(vm.MyNoiseCancel);
        Assert.Equal(0, s.Saves);
    }
}
