using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenKrisp.Core.Common;

namespace OpenKrisp.Core.Notes;

/// <summary>Owns the local Tract process and model lifecycle for the app.</summary>
public sealed class TractRuntimeService : ObservableObject, IDisposable
{
    private readonly LocalNotesWorker _worker;
    private readonly Func<bool> _inUse;
    private readonly IProgress<string> _messages;
    private readonly IProgress<(string State, string Message)> _updates;
    private Process? _process;
    private TaskCompletionSource _loaded = NewSignal();
    private string _state = "stopped", _message = "Waiting to start", _token = "", _url = "";
    private bool _busy;
    private bool _disposed;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string State { get => _state; private set { if (SetField(ref _state, value)) { OnPropertyChanged(nameof(IsLoaded)); Refresh(); } } }
    public string Message { get => _message; private set => SetField(ref _message, value); }
    public bool IsLoaded => State == "loaded";
    public bool IsBusy { get => _busy; private set { if (SetField(ref _busy, value)) Refresh(); } }
    public bool CanManage => !IsBusy && !_inUse();
    public string Backend => "Parakeet Ultra FP16 ASR: native CUDA. Smart Turn: Tract CUDA. Speech segmentation: Tract CPU. No PyTorch.";
    public string NotesBackend => "Qwen2.5-7B-Instruct-1M Q4_K_M: live notes and inline translations share GPU memory with ASR using a 4K working context. Full summaries use 16K section processing and temporarily release ASR memory. Inference stays local.";
    public string ModelDirectory => Path.Combine(_worker.Home, "models");
    public string DownloadStatus => Directory.Exists(ModelDirectory) ? "Model files: " + ModelDirectory : "Models will download automatically";
    public string ContextStatus
    {
        get
        {
            try
            {
                using var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(_worker.Home,"context-status.json")));
                var root=data.RootElement;
                int Number(string name) => root.TryGetProperty(name,out var value) ? value.GetInt32() : 0;
                return $"Local RAG: {Number("indexedParagraphs")} indexed paragraphs · {Number("semanticSentences")} contextual sentence windows · {Number("retainedNotes")} retained notes · {Number("semanticReranks")} semantic reranks · {Number("cachedResponses")} cached responses · {Number("modelCalls")} notes model calls. Multilingual E5 on Tract CPU reranks meaning plus exact terms and passage diversity. ASR stays on CUDA; Qwen writes notes.";
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { return "Local RAG and context cache initialize with the next recording. Confirmed speech is indexed before summarization."; }
        }
    }
    public void RefreshContext() => OnPropertyChanged(nameof(ContextStatus));

    public TractRuntimeService(LocalNotesWorker worker, Func<bool> inUse)
    {
        _worker = worker; _inUse = inUse;
        _messages = new Progress<string>(message => Message = message);
        _updates = new Progress<(string, string)>(update => { State = update.Item1; Message = update.Item2; });
    }

    public void Refresh() { OnPropertyChanged(nameof(CanManage)); OnPropertyChanged(nameof(DownloadStatus)); }
    public void Configure(ProcessStartInfo start)
    {
        if (!IsLoaded || _url.Length == 0) throw new InvalidOperationException("Parakeet Ultra GPU models are not loaded. Open Status to load them.");
        start.Environment["OPENKRISP_ASR_URL"] = _url;
        start.Environment["OPENKRISP_ASR_TOKEN"] = _token;
    }

    public Task InitializeAsync() => RunAsync(async () => {
        if (!_worker.ModelsInstalled) { State = "downloading"; await _worker.InstallAsync(_messages, CancellationToken.None); }
        if (_process is not null && !_process.HasExited) { if (!IsLoaded) await CommandAsync("load"); }
        else await StartHostAsync();
        try { await _worker.IndexLibraryAsync(_messages); }
        catch (Exception error) { Message = "ASR loaded; local context indexing will retry: " + error.Message; }
    });
    public Task LoadAsync() => RunAsync(async () => {
        if (_process is null || _process.HasExited) await StartHostAsync();
        else await CommandAsync("load");
    });
    public Task UnloadAsync() => RunAsync(async () => {
        await StopHostAsync(); State = "unloaded"; Message = "Models unloaded; Tract process stopped and GPU allocations released.";
    });
    public Task RestartAsync() => RunAsync(async () => { await StopHostAsync(); await StartHostAsync(); });
    public Task RedownloadAsync() => RunAsync(async () => {
        await StopHostAsync(); State = "downloading";
        await _worker.InstallAsync(_messages, CancellationToken.None, redownloadAsr: true);
        await StartHostAsync();
    });

    // Notes and recording are mutually exclusive. Release ASR's CUDA allocation
    // while Qwen summarizes so both large models do not compete for an 8 GB GPU.
    public async Task<T> WithModelsReleasedAsync<T>(Func<Task<T>> generate, IProgress<string> progress)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for model setup to finish before summarizing.");
        if (!IsLoaded) return await generate();
        IsBusy = true;
        try
        {
            progress.Report("Releasing ASR GPU memory for Qwen notes…");
            await StopHostAsync();
            return await generate();
        }
        finally
        {
            if (!_disposed)
            {
                progress.Report("Restoring Parakeet Ultra and Smart Turn on GPU…");
                try { await StartHostAsync(); }
                catch (Exception error) { State = "error"; Message = "Notes processing ended; ASR could not reload: " + error.Message; }
            }
            IsBusy = false;
        }
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (!CanManage) return;
        IsBusy = true;
        try { await operation(); }
        catch (Exception error) { State = "error"; Message = error.Message; }
        finally { IsBusy = false; }
    }

    private async Task StartHostAsync()
    {
        State = "loading"; _loaded = NewSignal();
        var start = new ProcessStartInfo(_worker.PythonExecutable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(Path.Combine(_worker.Scripts, "tract_host.py")); start.ArgumentList.Add(_worker.Home);
        start.Environment["PYTHONUTF8"] = "1"; start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["DO_NOT_TRACK"] = "1";
        var process = Process.Start(start) ?? throw new IOException("Could not start Tract");
        _process = process;
        _ = ObserveAsync(process);
        await _loaded.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }

    private async Task ObserveAsync(Process process)
    {
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("port", out var port)) { _url = "http://127.0.0.1:" + port.GetInt32(); _token = root.GetProperty("token").GetString()!; }
                var state = root.GetProperty("state").GetString() ?? "error";
                var message = root.GetProperty("message").GetString() ?? "";
                if (!ReferenceEquals(_process, process)) continue;
                _updates.Report((state, message));
                if (state == "loaded") _loaded.TrySetResult();
                if (state == "error") _loaded.TrySetException(new InvalidOperationException(message));
            }
            await process.WaitForExitAsync();
            var error = await errors;
            if (ReferenceEquals(_process, process))
            {
                var message = "Tract stopped. " + error[^Math.Min(error.Length, 1000)..];
                _updates.Report(("stopped", message)); _loaded.TrySetException(new IOException(message));
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ObjectDisposedException)
        { if (ReferenceEquals(_process, process)) { _updates.Report(("error", error.Message)); _loaded.TrySetException(error); } }
    }

    private async Task CommandAsync(string command)
    {
        if (_url.Length == 0) throw new InvalidOperationException("Tract is not running. Use Restart Tract.");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await client.PostAsync(_url + "/" + command, new StringContent("{}", Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(json.RootElement.GetProperty("error").GetString());
        State = json.RootElement.GetProperty("state").GetString()!;
        Message = json.RootElement.GetProperty("message").GetString()!;
    }

    private async Task StopHostAsync()
    {
        var process = _process; _process = null; _url = _token = "";
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("quit");
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); }
            }
        }
        finally { process.Dispose(); }
        State = "stopped";
    }

    public void Dispose()
    {
        _disposed = true;
        var process = _process; _process = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        process.Dispose();
    }
}
