using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenKrisp.Core.Common;
using OpenKrisp.Core.Engine;
using OpenKrisp.Core.Notes;

namespace OpenKrisp.App.Audio;

/// <summary>Physical microphone -> Windows AEC -> existing Rust DPDFNet -> selected virtual cable.</summary>
public sealed class VoiceMicrophoneService : ObservableObject, IDisposable
{
    private readonly JsonSettingsStore _settings = new();
    private readonly EngineHost? _engine;
    private readonly Func<bool> _otherRoutingActive;
    private readonly Action _enableNoiseReduction;
    private readonly IProgress<(bool Running, string Message, double Level)> _progress;
    private CancellationTokenSource? _stop;
    private Task? _task;
    private bool _running, _busy;
    private string _status = "Select your microphone, speaker reference and virtual-cable output.";
    private double _level = -60;
    private IReadOnlyList<RecordingInput> _inputs = Array.Empty<RecordingInput>(), _speakers = Array.Empty<RecordingInput>(), _outputs = Array.Empty<RecordingInput>();
    private RecordingInput? _microphone, _speaker, _output;
    public IReadOnlyList<RecordingInput> Inputs { get => _inputs; private set => SetField(ref _inputs, value); }
    public IReadOnlyList<RecordingInput> Speakers { get => _speakers; private set => SetField(ref _speakers, value); }
    public IReadOnlyList<RecordingInput> Outputs { get => _outputs; private set => SetField(ref _outputs, value); }
    public RecordingInput? Microphone { get => _microphone; set => SetField(ref _microphone, value); }
    public RecordingInput? SpeakerReference { get => _speaker; set => SetField(ref _speaker, value); }
    public RecordingInput? VirtualOutput { get => _output; set { if (SetField(ref _output, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool IsRunning { get => _running; private set { if (SetField(ref _running, value)) RefreshControls(); } }
    public bool IsBusy { get => _busy; private set { if (SetField(ref _busy, value)) RefreshControls(); } }
    public bool CanConfigure => !IsRunning && !IsBusy;
    public bool CanStart => CanConfigure && _engine is not null && !_otherRoutingActive() && VirtualOutput is { Id.Length: > 0 };
    public bool CanStop => IsRunning && !IsBusy;
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string OutputLevel => $"Cleaned microphone output: {_level:F1} dBFS";

    public VoiceMicrophoneService(EngineHost? engine, Func<bool> otherRoutingActive, Action enableNoiseReduction)
    {
        _engine = engine; _otherRoutingActive = otherRoutingActive; _enableNoiseReduction = enableNoiseReduction;
        _progress = new Progress<(bool, string, double)>(update => {
            IsRunning = update.Item1; Status = update.Item2; _level = update.Item3; OnPropertyChanged(nameof(OutputLevel));
        });
        RefreshDevices();
        if (_engine is null) Status = "Load the DPDFNet noise-removal model before starting the voice microphone.";
    }

    public void RefreshDevices()
    {
        if (!CanConfigure) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            static List<RecordingInput> List(MMDeviceCollection collection) => collection.Select(device => {
                using (device) return new RecordingInput(device.ID, device.FriendlyName);
            }).ToList();
            var inputs = List(enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                .Where(d => !d.Name.StartsWith("Voicemeeter Out", StringComparison.OrdinalIgnoreCase) && !d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
            var render = List(enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active));
            var saved = _settings.Load();
            var micId = Microphone?.Id ?? saved.VoiceMicrophoneId;
            var speakerId = SpeakerReference?.Id ?? saved.VoiceSpeakerReferenceId;
            var outputId = VirtualOutput?.Id ?? saved.VoiceVirtualOutputId;
            Inputs = new[] { new RecordingInput("", "Windows default microphone") }.Concat(inputs).ToList();
            Speakers = new[] { new RecordingInput("", "Windows default speakers / playback") }.Concat(render).ToList();
            Outputs = new[] { new RecordingInput("", "Select a dedicated virtual-cable playback input") }.Concat(render.Where(d =>
                d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || d.Name.StartsWith("Voicemeeter", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))).ToList();
            Microphone = Inputs.FirstOrDefault(d => d.Id == micId) ?? Inputs[0];
            SpeakerReference = Speakers.FirstOrDefault(d => d.Id == speakerId) ?? Speakers[0];
            VirtualOutput = Outputs.FirstOrDefault(d => d.Id == outputId && d.Id.Length > 0)
                ?? Outputs.FirstOrDefault(d => d.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase)) ?? Outputs[0];
        }
        catch (Exception error) { Status = "Could not enumerate audio devices: " + error.Message; }
    }

    public async Task StartAsync()
    {
        if (!CanStart || Microphone is null || SpeakerReference is null || VirtualOutput is null) return;
        IsBusy = true;
        try
        {
            var settings = _settings.Load();
            settings.VoiceMicrophoneId = Microphone.Id; settings.VoiceSpeakerReferenceId = SpeakerReference.Id; settings.VoiceVirtualOutputId = VirtualOutput.Id;
            _settings.Save(settings);
            _stop?.Dispose(); _stop = new CancellationTokenSource();
            _enableNoiseReduction();
            Status = "Opening microphone and speaker echo reference…";
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var input = Microphone; var speaker = SpeakerReference; var output = VirtualOutput; var cancellation = _stop.Token;
            _task = Task.Factory.StartNew(() => Run(input, speaker, output, ready, cancellation), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await ready.Task;
            IsRunning = _task is { IsCompleted: false };
        }
        catch (Exception error) { Status = "Voice microphone could not start: " + error.Message; IsRunning = false; }
        finally { IsBusy = false; }
    }

    public async Task StopAsync()
    {
        if (!CanStop) return;
        IsBusy = true;
        try { _stop?.Cancel(); if (_task is not null) await _task; IsRunning = false; Status = "Voice microphone stopped."; }
        finally { _stop?.Dispose(); _stop = null; _task = null; IsBusy = false; }
    }

    private void Run(RecordingInput input, RecordingInput speaker, RecordingInput output, TaskCompletionSource ready, CancellationToken cancellation)
    {
        var finalMessage = "Voice microphone stopped.";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var microphone = input.Id.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : enumerator.GetDevice(input.Id);
            using var reference = speaker.Id.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(speaker.Id);
            using var target = enumerator.GetDevice(output.Id);
            if (target.ID == reference.ID) throw new InvalidOperationException("The virtual output must be separate from your speaker reference to prevent feedback.");
            if (microphone.FriendlyName.StartsWith("Voicemeeter Out", StringComparison.OrdinalIgnoreCase) || microphone.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select your physical microphone. The current default is a virtual playback feed.");
            var engine = _engine ?? throw new InvalidOperationException("The noise-removal engine is unavailable.");
            // AEC source mode needs an active reference stream, including while playback is silent.
            using var referenceClock = new WasapiOut(reference, AudioClientShareMode.Shared, true, 100);
            referenceClock.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
            referenceClock.Play();
            using var capture = new WindowsAecCapture(microphone.ID, reference.ID);
            var inputBuffer = new BufferedWaveProvider(capture.WaveFormat) { ReadFully = false, BufferDuration = TimeSpan.FromSeconds(2) };
            var resampler = new WdlResamplingSampleProvider(inputBuffer.ToSampleProvider(), 48000);
            var outputBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)) { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true };
            using var destination = new WasapiOut(target, AudioClientShareMode.Shared, true, 100);
            destination.Init(outputBuffer); destination.Play();
            var bytes = new byte[32000]; var samples = new float[4800]; var clock = Stopwatch.StartNew();
            var lastAudio = TimeSpan.Zero;
            var lastReport = TimeSpan.Zero;
            while (!cancellation.IsCancellationRequested)
            {
                var count = capture.Read(bytes);
                if (count == 0)
                {
                    if (clock.Elapsed - lastAudio > TimeSpan.FromSeconds(5)) throw new IOException("No synchronized microphone audio is arriving. Check the selected microphone and playback reference.");
                    Thread.Sleep(5); continue;
                }
                lastAudio = clock.Elapsed;
                inputBuffer.AddSamples(bytes, 0, count);
                while (inputBuffer.BufferedBytes > 0)
                {
                    var read = resampler.Read(samples, 0, samples.Length);
                    if (read == 0) break;
                    var processed = engine.Process(Lane.Mic, samples.AsSpan(0, read).ToArray());
                    var pcm = new byte[processed.Length * 4]; Buffer.BlockCopy(processed, 0, pcm, 0, pcm.Length);
                    outputBuffer.AddSamples(pcm, 0, pcm.Length);
                    if (pcm.Length > 0) { engine.SetExternalEchoCancellationActive(true); ready.TrySetResult(); }
                    if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200))
                    {
                        lastReport = clock.Elapsed;
                        _progress.Report((true, "Echo cancellation" + (engine.GetStatus().MicNoiseCancel ? " + DPDFNet" : "") + " → " + target.FriendlyName, AudioLevel.Measure(processed).PeakDb));
                    }
                }
            }
        }
        catch (Exception error) { ready.TrySetException(error); finalMessage = "Voice microphone stopped: " + error.Message; }
        finally { _engine?.SetExternalEchoCancellationActive(false); if (!ready.Task.IsCompleted) ready.TrySetCanceled(); _progress.Report((false, finalMessage, -60)); }
    }

    private void RefreshControls() { OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(CanStop)); }
    public void NotifyRoutingChanged() => RefreshControls();
    public bool HasStopped => _task is null || _task.IsCompleted;
    public void Dispose() { _stop?.Cancel(); _task?.Wait(TimeSpan.FromSeconds(2)); }
}
