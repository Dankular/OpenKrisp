using OpenKrisp.Core.Common;
using OpenKrisp.Core.Engine;

namespace OpenKrisp.Core.NoiseCancellation;

public sealed record DeviceChoice(string? Name, string Display);

/// <summary>
/// Live audio routing choices on the Noise cancellation tab. Mic lane: a capture device, cleaned, sent to the OpenKrisp
/// virtual microphone (MicyWMD driver), to a playback device, or nowhere. Incoming lane: a playback device to capture
/// (it should be a virtual speaker that applications play into), cleaned, played on another device.
/// </summary>
public sealed class AudioRoutingViewModel : ObservableObject
{
    public static readonly DeviceChoice SystemDefault = new(null, "System default");
    public static readonly DeviceChoice Off = new(null, "Off");

    public const string IncomingHelp =
        "To clean what you hear on calls, set Windows' sound output to a virtual speaker device, choose that device as the " +
        "source here, and choose your real headphones or speakers as the output. Capturing and playing on the same device would loop.";

    private readonly IAudioController? _audio;
    private readonly ISettingsStore _settings;
    private IReadOnlyList<DeviceChoice> _inputs = new[] { SystemDefault };
    private IReadOnlyList<DeviceChoice> _outputs = new[] { SystemDefault };
    private IReadOnlyList<DeviceChoice> _sources = new[] { Off };
    private DeviceChoice _micInput = SystemDefault, _micSinkDevice = SystemDefault, _incomingSource = Off, _incomingOutput = SystemDefault;
    private int _micSinkIndex = 1, _micyFormatIndex;
    private bool _running, _busy;
    private string _status = "", _error = "";

    public AudioRoutingViewModel(IAudioController? audio, ISettingsStore settings)
    {
        _audio = audio;
        _settings = settings;
        RefreshDevices();
    }

    public bool IsAvailable => _audio is not null;

    public IReadOnlyList<DeviceChoice> Inputs { get => _inputs; private set => SetField(ref _inputs, value); }
    public IReadOnlyList<DeviceChoice> Outputs { get => _outputs; private set => SetField(ref _outputs, value); }
    /// <summary>"Off" followed by every playback device (the incoming lane captures one of them).</summary>
    public IReadOnlyList<DeviceChoice> IncomingSources { get => _sources; private set => SetField(ref _sources, value); }

    public DeviceChoice MicInput { get => _micInput; set => SetField(ref _micInput, value ?? SystemDefault); }
    public DeviceChoice MicSinkDevice { get => _micSinkDevice; set => SetField(ref _micSinkDevice, value ?? SystemDefault); }
    public DeviceChoice IncomingSource { get => _incomingSource; set => SetField(ref _incomingSource, value ?? Off); }
    public DeviceChoice IncomingOutput { get => _incomingOutput; set => SetField(ref _incomingOutput, value ?? SystemDefault); }

    /// <summary>0 = discard, 1 = OpenKrisp virtual microphone, 2 = a playback device.</summary>
    public int MicSinkIndex
    {
        get => _micSinkIndex;
        set { if (SetField(ref _micSinkIndex, value)) OnPropertyChanged(nameof(ShowMicSinkDevice)); }
    }

    public bool ShowMicSinkDevice => _micSinkIndex == 2;

    /// <summary>Advanced: which PCM format to write to the virtual microphone driver (0 = 32-bit int, 1 = 32-bit float, 2 = 16-bit).</summary>
    public int MicyFormatIndex { get => _micyFormatIndex; set => SetField(ref _micyFormatIndex, value); }

    public bool IsRunning { get => _running; private set { if (SetField(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => IsAvailable && !_running && !_busy;
    public string StatusText { get => _status; private set => SetField(ref _status, value); }
    public string Error { get => _error; private set => SetField(ref _error, value); }

    /// <summary>Re-reads the device lists and restores the saved choices that are still present.</summary>
    public void RefreshDevices()
    {
        if (_audio is null) return;
        try
        {
            var saved = _settings.Load();
            var ins = _audio.ListDevices(capture: true);
            var outs = _audio.ListDevices(capture: false);
            Inputs = new[] { SystemDefault }.Concat(ins.Select(n => new DeviceChoice(n, n))).ToList();
            Outputs = new[] { SystemDefault }.Concat(outs.Select(n => new DeviceChoice(n, n))).ToList();
            IncomingSources = new[] { Off }.Concat(outs.Select(n => new DeviceChoice(n, n))).ToList();
            MicInput = Pick(Inputs, saved.MicInput, SystemDefault);
            MicSinkDevice = Pick(Outputs, saved.MicSinkDevice, SystemDefault);
            IncomingOutput = Pick(Outputs, saved.IncomingOutput, SystemDefault);
            IncomingSource = Pick(IncomingSources, saved.IncomingSource, Off);
            MicSinkIndex = saved.MicSink is >= 0 and <= 2 ? saved.MicSink : 1;
            MicyFormatIndex = saved.MicyFormat is >= 0 and <= 2 ? saved.MicyFormat : 0;
            Error = "";
        }
        catch (EngineException e) { Error = e.Message; }
    }

    private static DeviceChoice Pick(IReadOnlyList<DeviceChoice> list, string? name, DeviceChoice fallback)
        => name is null ? fallback : list.FirstOrDefault(c => c.Name == name) ?? fallback;

    public AudioConfig BuildConfig() => new(
        _micInput.Name,
        (MicSinkKind)_micSinkIndex,
        _micSinkDevice.Name,
        (MicyFormat)_micyFormatIndex,
        _incomingSource.Name,
        _incomingOutput.Name);

    /// <summary>Starts live audio with the current choices. Opening devices can take a while, so the engine call runs off the caller's thread.</summary>
    public async Task StartAsync()
    {
        if (_audio is null || _running || _busy) return;
        var problem = Validate();
        if (problem is not null) { Error = problem; return; }

        _busy = true;
        OnPropertyChanged(nameof(CanStart));
        try
        {
            var config = BuildConfig();
            await Task.Run(() => _audio.StartAudio(config));
            IsRunning = true;
            Error = "";
            Save();
            Refresh();
        }
        catch (EngineException e) { Error = e.Message; StatusText = "Audio is not running."; }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(CanStart));
        }
    }

    public void Stop()
    {
        if (_audio is null) return;
        try
        {
            _audio.StopAudio();
            IsRunning = false;
            StatusText = "Audio stopped.";
            Error = "";
        }
        catch (EngineException e) { Error = e.Message; }
    }

    /// <summary>Updates the status line from the engine's counters. Call from a UI timer.</summary>
    public void Refresh()
    {
        if (_audio is null || !_running) return;
        try
        {
            var s = _audio.GetAudioStats();
            if (!s.Running) { IsRunning = false; StatusText = "Audio stopped."; return; }
            var parts = new List<string>();
            if (_micSinkIndex != 0)
                parts.Add($"microphone: {Ms(s.MicWorkerMaxBlockUs)} worst block, {s.MicInputDropped} samples dropped, {s.MicOutputUnderrunFrames} gap frames");
            if (_incomingSource.Name is not null)
                parts.Add($"incoming: {Ms(s.SpeakerWorkerMaxBlockUs)} worst block, {s.SpeakerInputDropped} samples dropped, {s.SpeakerOutputUnderrunFrames} gap frames");
            StatusText = "Running. " + string.Join("; ", parts) + ".";
        }
        catch (EngineException e) { Error = e.Message; }
    }

    private static string Ms(ulong micros) => $"{micros / 1000.0:0.0} ms";

    private string? Validate()
    {
        if (_incomingSource.Name is not null && _incomingSource.Name == _incomingOutput.Name)
            return "The incoming-audio source and its output are the same device: the cleaned audio would loop. Choose different devices.";
        if (_micSinkIndex == 0 && _incomingSource.Name is null)
            return "Nothing to run: send the microphone somewhere, or choose a source for incoming audio.";
        return null;
    }

    private void Save()
    {
        var s = _settings.Load();
        s.MicInput = _micInput.Name;
        s.MicSink = _micSinkIndex;
        s.MicSinkDevice = _micSinkDevice.Name;
        s.MicyFormat = _micyFormatIndex;
        s.IncomingSource = _incomingSource.Name;
        s.IncomingOutput = _incomingOutput.Name;
        _settings.Save(s);
    }
}
