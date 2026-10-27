using System.Diagnostics;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using OpenKrisp.Core.Notes;

namespace OpenKrisp.App.Notes;

/// <summary>Records real microphone audio and optional WASAPI loopback to a local WAV.</summary>
public sealed class MeetingRecorder : IMeetingRecorder
{
    private readonly List<Track> _tracks = new();
    private string _output = "";
    private System.Threading.Timer? _previewTimer;
    private System.Threading.Timer? _meterTimer;
    public event Action<float[]>? SamplesAvailable;
    public event Action<AudioLevel>? LevelAvailable;

    public IReadOnlyList<RecordingInput> Inputs
    {
        get
        {
            try
            {
                using var devices = new MMDeviceEnumerator();
                return devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .Select(d => { var input = new RecordingInput(d.ID, d.FriendlyName); d.Dispose(); return input; }).ToList();
            }
            catch (System.Runtime.InteropServices.COMException) { return Array.Empty<RecordingInput>(); }
        }
    }

    public Task StartAsync(string outputPath, int source, string? inputId = null) => Task.Run(() =>
    {
        if (_tracks.Count != 0) throw new InvalidOperationException("A recording is already running.");
        _output = outputPath;
        var clock = Stopwatch.StartNew();
        try
        {
            if (source != 2)
            {
                using var devices = new MMDeviceEnumerator();
                var device = string.IsNullOrEmpty(inputId) ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : devices.GetDevice(inputId);
                _tracks.Add(new Track(new WasapiCapture(device), outputPath + ".mic.wav", clock, device));
            }
            if (source != 0) _tracks.Add(new Track(new WasapiLoopbackCapture(), outputPath + ".system.wav", clock));
            foreach (var track in _tracks) track.Capture.StartRecording();
            var preview = new MixingSampleProvider(_tracks.Select(t => t.Preview)) { ReadFully = true };
            var meter = new MixingSampleProvider(_tracks.Select(t => t.Meter)) { ReadFully = true };
            var meterSamples = new float[1600];
            var metering = 0;
            _meterTimer = new System.Threading.Timer(_ => {
                if (Interlocked.Exchange(ref metering, 1) != 0) return;
                try
                {
                    meter.Read(meterSamples, 0, meterSamples.Length);
                    if (_tracks.Count > 1) for (var i = 0; i < meterSamples.Length; i++) meterSamples[i] *= 0.5f;
                    LevelAvailable?.Invoke(AudioLevel.Measure(meterSamples));
                }
                catch { /* Meter failure must never interrupt recording. */ }
                finally { Volatile.Write(ref metering, 0); }
            }, null, 100, 100);
            _previewTimer = new System.Threading.Timer(_ => {
                try
                {
                    var samples = new float[32000];
                    preview.Read(samples, 0, samples.Length);
                    if (_tracks.Count > 1) for (var i = 0; i < samples.Length; i++) samples[i] *= 0.5f;
                    SamplesAvailable?.Invoke(samples);
                }
                catch { /* Live preview is best effort; the WAV capture is independent. */ }
            }, null, 2000, 2000);
        }
        catch
        {
            _meterTimer?.Dispose(); _meterTimer = null;
            _previewTimer?.Dispose(); _previewTimer = null;
            foreach (var track in _tracks) track.Dispose();
            _tracks.Clear();
            throw;
        }
    });

    public async Task StopAsync()
    {
        if (_tracks.Count == 0) return;
        if (_meterTimer is not null) { await _meterTimer.DisposeAsync(); _meterTimer = null; }
        LevelAvailable?.Invoke(AudioLevel.Silence);
        if (_previewTimer is not null) { await _previewTimer.DisposeAsync(); _previewTimer = null; }
        var tracks = _tracks.ToArray();
        try
        {
            await Task.WhenAll(tracks.Select(t => t.StopAsync()));
            await Task.Run(() =>
            {
                var readers = new List<AudioFileReader>();
                try
                {
                    var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
                    foreach (var track in tracks.Where(t => t.HasData))
                    {
                        var reader = new AudioFileReader(track.Path);
                        readers.Add(reader);
                        ISampleProvider samples = reader;
                        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
                        if (samples.WaveFormat.Channels != 1) throw new NotSupportedException("Recording requires mono or stereo input.");
                        if (samples.WaveFormat.SampleRate != 16000) samples = new WdlResamplingSampleProvider(samples, 16000);
                        samples = new VolumeSampleProvider(samples) { Volume = tracks.Length > 1 ? 0.5f : 1f };
                        mixer.AddMixerInput(new OffsetSampleProvider(samples) { DelayBy = track.Offset });
                    }
                    if (readers.Count == 0) throw new InvalidOperationException("No audio was captured. Check the selected microphone.");
                    WaveFileWriter.CreateWaveFile16(_output, mixer);
                }
                finally { foreach (var reader in readers) reader.Dispose(); }
            });
            foreach (var track in tracks) File.Delete(track.Path);
        }
        finally
        {
            foreach (var track in tracks) track.Dispose();
            _tracks.Clear();
        }
    }

    public void Dispose()
    {
        _meterTimer?.Dispose();
        _meterTimer = null;
        _previewTimer?.Dispose();
        _previewTimer = null;
        foreach (var track in _tracks) track.Dispose();
        _tracks.Clear();
    }

    private sealed class Track : IDisposable
    {
        public WasapiCapture Capture { get; }
        public string Path { get; }
        public TimeSpan Offset { get; private set; }
        public bool HasData { get; private set; }
        private readonly BufferedWaveProvider _previewBuffer;
        private readonly BufferedWaveProvider _meterBuffer;
        public ISampleProvider Preview { get; }
        public ISampleProvider Meter { get; }
        private readonly WaveFileWriter _writer;
        private readonly MMDevice? _device;
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch _clock;
        private Exception? _error;
        private bool _disposed;

        public Track(WasapiCapture capture, string path, Stopwatch clock, MMDevice? device = null)
        {
            Capture = capture; Path = path; _clock = clock; _device = device;
            _writer = new WaveFileWriter(path, capture.WaveFormat);
            _previewBuffer = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(15), DiscardOnBufferOverflow = true, ReadFully = true };
            ISampleProvider samples = _previewBuffer.ToSampleProvider();
            if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
            if (samples.WaveFormat.Channels != 1) throw new NotSupportedException("Recording requires mono or stereo input.");
            Preview = samples.WaveFormat.SampleRate == 16000 ? samples : new WdlResamplingSampleProvider(samples, 16000);
            _meterBuffer = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true, ReadFully = true };
            ISampleProvider meterSamples = _meterBuffer.ToSampleProvider();
            if (meterSamples.WaveFormat.Channels == 2) meterSamples = new StereoToMonoSampleProvider(meterSamples);
            Meter = meterSamples.WaveFormat.SampleRate == 16000 ? meterSamples : new WdlResamplingSampleProvider(meterSamples, 16000);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, e) => { _error ??= e.Exception; _stopped.TrySetResult(); };
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            try
            {
                var bytesPerSecond = Capture.WaveFormat.AverageBytesPerSecond;
                if (!HasData) { Offset = TimeSpan.FromSeconds(Math.Max(0, _clock.Elapsed.TotalSeconds - (double)e.BytesRecorded / bytesPerSecond)); HasData = true; }
                // WASAPI loopback can stop delivering buffers during silence. Preserve wall-clock gaps.
                var expected = (long)((_clock.Elapsed - Offset).TotalSeconds * bytesPerSecond) - e.BytesRecorded;
                var gap = expected - _writer.Length;
                if (gap > bytesPerSecond / 10)
                {
                    var zeros = new byte[Capture.WaveFormat.BlockAlign * 1024];
                    gap -= gap % Capture.WaveFormat.BlockAlign;
                    while (gap > 0) { var count = (int)Math.Min(zeros.Length, gap); _writer.Write(zeros, 0, count); gap -= count; }
                }
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                _previewBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                _meterBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }
            catch (Exception error) { _error = error; Capture.StopRecording(); }
        }

        public async Task StopAsync()
        {
            if (!_stopped.Task.IsCompleted) Capture.StopRecording();
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            _writer.Flush();
            Dispose();
            if (_error is not null) throw new IOException("Audio recording failed.", _error);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Capture.Dispose();
            _writer.Dispose();
            _device?.Dispose();
        }
    }
}
