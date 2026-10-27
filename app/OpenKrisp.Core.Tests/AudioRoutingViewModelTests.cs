using OpenKrisp.Core.Common;
using OpenKrisp.Core.Engine;
using OpenKrisp.Core.NoiseCancellation;

namespace OpenKrisp.Core.Tests;

internal sealed class FakeAudio : IAudioController
{
    public string[] Inputs = { "Mic A", "Mic B" };
    public string[] Outputs = { "Speakers", "Virtual Speaker" };
    public AudioConfig? Started;
    public int StartCalls, StopCalls;
    public bool FailStart, Running;
    public AudioStats Stats = new(true, 0, 0, 0, 0, 2500, 0);

    public IReadOnlyList<string> ListDevices(bool capture) => capture ? Inputs : Outputs;

    public void StartAudio(AudioConfig config)
    {
        StartCalls++;
        if (FailStart) throw new EngineException("device busy");
        Started = config;
        Running = true;
    }

    public void StopAudio() { StopCalls++; Running = false; }
    public AudioStats GetAudioStats() => Stats with { Running = Running };
}

public class AudioRoutingViewModelTests
{
    [Fact]
    public void Device_lists_start_with_the_default_and_the_incoming_list_starts_with_off()
    {
        var vm = new AudioRoutingViewModel(new FakeAudio(), new MemorySettings());
        Assert.Equal(new[] { "System default", "Mic A", "Mic B" }, vm.Inputs.Select(d => d.Display));
        Assert.Equal(new[] { "System default", "Speakers", "Virtual Speaker" }, vm.Outputs.Select(d => d.Display));
        Assert.Equal(new[] { "Off", "Speakers", "Virtual Speaker" }, vm.IncomingSources.Select(d => d.Display));
        Assert.Null(vm.MicInput.Name);
        Assert.Equal(1, vm.MicSinkIndex); // virtual microphone by default
    }

    [Fact]
    public async Task Start_sends_the_chosen_routing_to_the_engine_and_remembers_it()
    {
        var a = new FakeAudio();
        var s = new MemorySettings();
        var vm = new AudioRoutingViewModel(a, s);
        vm.MicInput = vm.Inputs.Single(d => d.Name == "Mic B");
        vm.IncomingSource = vm.IncomingSources.Single(d => d.Name == "Virtual Speaker");
        vm.IncomingOutput = vm.Outputs.Single(d => d.Name == "Speakers");
        vm.MicyFormatIndex = 1;

        await vm.StartAsync();

        Assert.True(vm.IsRunning);
        Assert.Equal(new AudioConfig("Mic B", MicSinkKind.MicyVirtualMic, null, MicyFormat.Float32, "Virtual Speaker", "Speakers"), a.Started);
        Assert.Equal("Mic B", s.Current.MicInput);
        Assert.Equal("Virtual Speaker", s.Current.IncomingSource);
        Assert.Equal(1, s.Current.MicyFormat);
    }

    [Fact]
    public async Task Saved_choices_come_back_and_vanished_devices_fall_back_to_the_default()
    {
        var s = new MemorySettings { Current = new AppSettings { MicInput = "Mic B", IncomingSource = "Gone Speaker", IncomingOutput = "Speakers", MicSink = 2, MicSinkDevice = "Virtual Speaker" } };
        var vm = new AudioRoutingViewModel(new FakeAudio(), s);

        Assert.Equal("Mic B", vm.MicInput.Name);
        Assert.Null(vm.IncomingSource.Name);            // "Gone Speaker" is no longer present -> Off
        Assert.Equal("Speakers", vm.IncomingOutput.Name);
        Assert.Equal(2, vm.MicSinkIndex);
        Assert.True(vm.ShowMicSinkDevice);
        Assert.Equal("Virtual Speaker", vm.MicSinkDevice.Name);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Capturing_and_playing_on_the_same_device_is_refused_before_the_engine_is_called()
    {
        var a = new FakeAudio();
        var vm = new AudioRoutingViewModel(a, new MemorySettings());
        vm.IncomingSource = vm.IncomingSources.Single(d => d.Name == "Speakers");
        vm.IncomingOutput = vm.Outputs.Single(d => d.Name == "Speakers");

        await vm.StartAsync();

        Assert.Contains("would loop", vm.Error);
        Assert.Equal(0, a.StartCalls);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Nothing_to_run_is_refused()
    {
        var a = new FakeAudio();
        var vm = new AudioRoutingViewModel(a, new MemorySettings()) { MicSinkIndex = 0 };
        await vm.StartAsync();
        Assert.Contains("Nothing to run", vm.Error);
        Assert.Equal(0, a.StartCalls);
    }

    [Fact]
    public async Task An_engine_failure_is_shown_and_audio_stays_off_and_startable()
    {
        var a = new FakeAudio { FailStart = true };
        var vm = new AudioRoutingViewModel(a, new MemorySettings());

        await vm.StartAsync();

        Assert.Equal("device busy", vm.Error);
        Assert.False(vm.IsRunning);
        Assert.True(vm.CanStart);

        a.FailStart = false;
        await vm.StartAsync();
        Assert.True(vm.IsRunning);
        Assert.Equal("", vm.Error);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public async Task Status_reports_the_active_lanes_and_stop_works()
    {
        var a = new FakeAudio();
        var vm = new AudioRoutingViewModel(a, new MemorySettings());
        vm.IncomingSource = vm.IncomingSources.Single(d => d.Name == "Virtual Speaker");
        await vm.StartAsync();

        Assert.Contains("microphone: 2.5 ms worst block", vm.StatusText);
        Assert.Contains("incoming:", vm.StatusText);

        vm.Stop();
        Assert.False(vm.IsRunning);
        Assert.Equal(1, a.StopCalls);
        Assert.Equal("Audio stopped.", vm.StatusText);
    }

    [Fact]
    public async Task Status_leaves_out_a_lane_that_is_off()
    {
        var vm = new AudioRoutingViewModel(new FakeAudio(), new MemorySettings());
        await vm.StartAsync();
        Assert.Contains("microphone:", vm.StatusText);
        Assert.DoesNotContain("incoming:", vm.StatusText);
    }

    [Fact]
    public void Without_an_audio_engine_nothing_can_start()
    {
        var vm = new AudioRoutingViewModel(null, new MemorySettings());
        Assert.False(vm.IsAvailable);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void A_device_listing_failure_is_reported()
    {
        var vm = new AudioRoutingViewModel(new ThrowingAudio(), new MemorySettings());
        Assert.Equal("no audio", vm.Error);
    }

    private sealed class ThrowingAudio : IAudioController
    {
        public IReadOnlyList<string> ListDevices(bool capture) => throw new EngineException("no audio");
        public void StartAudio(AudioConfig config) { }
        public void StopAudio() { }
        public AudioStats GetAudioStats() => new(false, 0, 0, 0, 0, 0, 0);
    }
}
