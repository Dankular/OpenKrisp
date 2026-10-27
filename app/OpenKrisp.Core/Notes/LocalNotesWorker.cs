using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenKrisp.Core.Common;

namespace OpenKrisp.Core.Notes;

/// <summary>Runs the bundled worker without a shell. Inference uses only installed local files.</summary>
public sealed class LocalNotesWorker : ILocalNotesWorker
{
    private readonly string _home;
    private readonly string _scripts;
    private TractRuntimeService? _runtime;
    public string Home => _home;
    public string Scripts => _scripts;
    public string PythonExecutable => Python;
    public void AttachRuntime(TractRuntimeService runtime) => _runtime = runtime;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string Python => Path.Combine(_home, "venv", "Scripts", "python.exe");
    public LocalNotesWorker(string? home = null, string? scripts = null)
    {
        var bundledDevelopmentRuntime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".notes-runtime"));
        _home = home ?? Environment.GetEnvironmentVariable("OPENKRISP_NOTES_HOME")
            ?? (File.Exists(Path.Combine(bundledDevelopmentRuntime, "config.json")) ? bundledDevelopmentRuntime : Path.Combine(AppPaths.DataDir, "local-notes"));
        _scripts = scripts ?? Path.Combine(AppContext.BaseDirectory, "notes");
    }
    public bool IsReady => ModelsInstalled && (_runtime?.IsLoaded ?? true);
    public bool IsPreparing => _runtime?.IsBusy ?? false;
    public string RuntimeStatus => _runtime?.Message ?? "";
    public bool ModelsInstalled
    {
        get
        {
            if (!File.Exists(Python)) return false;
            try
            {
                using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "config.json")));
                var root = config.RootElement;
                return root.TryGetProperty("notesBackend", out var backend) && backend.GetString() == "qwen2.5-1m"
                    && File.Exists(root.GetProperty("titleModel").GetString()) && File.Exists(root.GetProperty("titleCli").GetString())
                    && root.TryGetProperty("asrModel", out var asrModel) && asrModel.GetString() == "parakeet-ultra"
                    && root.TryGetProperty("parakeetUltraModel", out var asr) && File.Exists(asr.GetString())
                    && root.TryGetProperty("smartTurnDirectory", out var turn) && File.Exists(Path.Combine(turn.GetString()!, "onnx", "model.onnx"))
                    && File.Exists(root.GetProperty("notesModel").GetString()) && File.Exists(root.GetProperty("notesServer").GetString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return false; }
        }
    }

    public ILiveTranscript StartLive(IProgress<LiveTranscriptUpdate> preview, int speakers = 0)
    {
        var live = new LiveTranscriptSession(Python, Path.Combine(_scripts, "worker.py"), _home, preview, speakers, start => _runtime?.Configure(start));
        return live;
    }

    public Task InstallAsync(IProgress<string> progress, CancellationToken ct) => InstallAsync(progress, ct, false);
    public async Task InstallAsync(IProgress<string> progress, CancellationToken ct, bool redownloadAsr)
    {
        await RunAsync(Environment.GetEnvironmentVariable("OPENKRISP_SETUP_PYTHON") ?? "python",
            new[] { Path.Combine(_scripts, "bootstrap.py"), "--home", _home }.Concat(redownloadAsr ? new[] { "--redownload-asr" } : Array.Empty<string>()).ToArray(), progress, ct);
        if (!ModelsInstalled) throw new InvalidOperationException("Local model setup did not finish.");
    }

    public async Task<IReadOnlyList<TranscriptTurn>> TranscribeAsync(string audioPath, int speakers, IProgress<string> progress, CancellationToken ct)
        => await ExecuteAsync<List<TranscriptTurn>>("transcribe", audioPath, speakers, progress, ct);

    public async Task<string> CreateTitleAsync(IReadOnlyList<TranscriptTurn> transcript, IProgress<string> progress, CancellationToken ct)
        => (await GenerateAsync<TranscriptTitle>("title", transcript, progress, ct)).Title;

    private sealed record TranscriptTitle(string Title);

    public Task<MeetingSummary> SummarizeAsync(IReadOnlyList<TranscriptTurn> transcript, IProgress<string> progress, CancellationToken ct)
        => _runtime is null ? GenerateAsync<MeetingSummary>("summarize", transcript, progress, ct)
            : _runtime.WithModelsReleasedAsync(() => GenerateAsync<MeetingSummary>("summarize", transcript, progress, ct), progress);

    private async Task<T> GenerateAsync<T>(string command, IReadOnlyList<TranscriptTurn> transcript, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_home);
        var input = Path.Combine(_home, Guid.NewGuid() + ".transcript.json");
        try
        {
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(transcript, Json), ct);
            return await ExecuteAsync<T>(command, input, 0, progress, ct);
        }
        finally { if (File.Exists(input)) File.Delete(input); }
    }

    private async Task<T> ExecuteAsync<T>(string command, string input, int speakers, IProgress<string> progress, CancellationToken ct)
    {
        if (!ModelsInstalled || (command == "transcribe" && !IsReady)) throw new InvalidOperationException("Set up and load local models first.");
        var output = Path.Combine(_home, Guid.NewGuid() + ".result.json");
        try
        {
            await RunAsync(Python, [Path.Combine(_scripts, "worker.py"), command, "--home", _home,
                "--input", input, "--output", output, "--speakers", speakers.ToString()], progress, ct, useModels: command == "transcribe");
            var result = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(output, ct), Json);
            return result ?? throw new InvalidOperationException("The local model returned no result.");
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private async Task RunAsync(string executable, string[] arguments, IProgress<string> progress, CancellationToken ct, bool useModels = false)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["NEEDLE_TELEMETRY"] = "0";
        start.Environment["DO_NOT_TRACK"] = "1";
        if (useModels) _runtime?.Configure(start);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the local notes worker.");
        using var cancellation = ct.Register(() => {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var tail = new Queue<string>();
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (tail) { tail.Enqueue(line); if (tail.Count > 12) tail.Dequeue(); }
                progress.Report(line);
            }
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError), process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException("Local processing failed: " + string.Join(Environment.NewLine, tail));
    }
}
