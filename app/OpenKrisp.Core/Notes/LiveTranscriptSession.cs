using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenKrisp.Core.Notes;

/// <summary>Bounded PCM pipe to Parakeet Ultra; final recording never depends on preview speed.</summary>
internal sealed class LiveTranscriptSession : ILiveTranscript
{
    private readonly Process _process;
    private readonly Channel<PcmPacket> _audio = Channel.CreateBounded<PcmPacket>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _run;
    private bool _disposed;
    private double _sentSeconds;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private sealed record PcmPacket(double Start, float[] Samples);

    public LiveTranscriptSession(string python, string script, string home, IProgress<LiveTranscriptUpdate> preview, int speakers, Action<ProcessStartInfo>? configure = null)
    {
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { script, "live", "--home", home, "--speakers", speakers.ToString() }) start.ArgumentList.Add(arg);
        start.Environment["PYTHONUTF8"] = "1";
        configure?.Invoke(start);
        _process = Process.Start(start) ?? throw new IOException("Could not start Parakeet Ultra live preview.");
        _run = RunAsync(preview);
    }

    public void AddSamples(float[] samples)
    {
        lock (_audio)
        {
            var packet = new PcmPacket(_sentSeconds, samples);
            _sentSeconds += (double)samples.Length / 16000;
            _audio.Writer.TryWrite(packet);
        }
    }

    private async Task RunAsync(IProgress<LiveTranscriptUpdate> preview)
    {
        async Task WriteAsync()
        {
            try
            {
                await foreach (var packet in _audio.Reader.ReadAllAsync())
                {
                    var samples = packet.Samples;
                    var header = new byte[12];
                    BinaryPrimitives.WriteDoubleLittleEndian(header.AsSpan(0, 8), packet.Start);
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), samples.Length);
                    await _process.StandardInput.BaseStream.WriteAsync(header);
                    var bytes = new byte[samples.Length * sizeof(float)];
                    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                    await _process.StandardInput.BaseStream.WriteAsync(bytes);
                    await _process.StandardInput.BaseStream.FlushAsync();
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { try { _process.StandardInput.Close(); } catch (InvalidOperationException) { } }
        }
        async Task ReadAsync()
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                try
                {
                    var update = JsonSerializer.Deserialize<LiveTranscriptUpdate>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (update is not null) preview.Report(update with { LatencySeconds = Math.Max(0, _clock.Elapsed.TotalSeconds - update.AudioEnd) });
                }
                catch (JsonException) { }
            }
        }
        try
        {
            var errors = _process.StandardError.ReadToEndAsync();
            await Task.WhenAll(WriteAsync(), ReadAsync(), _process.WaitForExitAsync());
            var error = await errors;
            if (_process.ExitCode != 0 && !_disposed) preview.Report(new LiveTranscriptUpdate(Array.Empty<TranscriptTurn>(), 0, 0, 0) { Error = "Live preview unavailable. The recording is still being saved. " + error[^Math.Min(error.Length, 300)..] });
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!_disposed) preview.Report(new LiveTranscriptUpdate(Array.Empty<TranscriptTurn>(), 0, 0, 0) { Error = "Live preview stopped. The recording is still being saved." }); }
        finally { _process.Dispose(); }
    }

    public async Task CompleteAsync()
    {
        _audio.Writer.TryComplete();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audio.Writer.TryComplete();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        // RunAsync owns the redirected streams until the process exits.
    }
}
