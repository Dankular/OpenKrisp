using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenKrisp.Core.Notes;

/// <summary>One local model process per recording; pending snapshots coalesce rather than accumulate.</summary>
internal sealed class LiveNotesSession : ILiveNotesSession
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private sealed record Request(IReadOnlyList<TranscriptTurn> Turns, bool Summarize);
    private readonly object _gate = new();
    private readonly Channel<Request> _requests = Channel.CreateBounded<Request>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private bool _disposed;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public LiveNotesSession(string python, string script, string home, string sessionId, IProgress<LiveNotesUpdate> updates, IProgress<string> progress)
    {
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { script, "live-notes", "--home", home }) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--session"); start.ArgumentList.Add(sessionId);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        _process = Process.Start(start) ?? throw new IOException("Could not start local running notes.");
        _ = RunAsync(updates, progress);
    }

    public void Submit(IReadOnlyList<TranscriptTurn> turns, bool summarize = true)
    {
        lock (_gate)
        {
            if (_requests.Reader.TryRead(out var pending)) summarize |= pending.Summarize;
            _requests.Writer.TryWrite(new Request(turns, summarize));
        }
    }

    private async Task RunAsync(IProgress<LiveNotesUpdate> updates, IProgress<string> progress)
    {
        var tail = new Queue<string>();
        async Task ReadProgressAsync()
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                tail.Enqueue(line); if (tail.Count > 5) tail.Dequeue();
                if (!_disposed) progress.Report(line.StartsWith("Loading Qwen", StringComparison.Ordinal) ? "Preparing local running notes…"
                    : line.StartsWith("Qwen ready", StringComparison.Ordinal) ? "Reading recent paragraphs…"
                    : line.StartsWith("Writing lecture notes", StringComparison.Ordinal) ? "Writing notes from retrieved transcript context…"
                    : line.StartsWith("Translating the latest", StringComparison.Ordinal) ? "Adding English translations beside the original speech…" : line);
            }
        }
        var errors = ReadProgressAsync();
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_stop.Token))
            {
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json));
                await _process.StandardInput.FlushAsync(_stop.Token);
                var line = await _process.StandardOutput.ReadLineAsync(_stop.Token);
                if (line is null) { await errors; throw new IOException("Running notes stopped. " + string.Join(" ", tail)); }
                var result = JsonSerializer.Deserialize<LiveNotesUpdate>(line, Json) ?? throw new IOException("Empty running notes response.");
                if (!_disposed) updates.Report(result);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            if (!_disposed) updates.Report(new LiveNotesUpdate("", Array.Empty<string>(), Array.Empty<TranscriptTranslation>(), 0) { Error = error.Message });
        }
        finally
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await errors; } catch (IOException) { }
            _process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel(); _requests.Writer.TryComplete();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
