namespace OpenKrisp.Core.Common;

public sealed class AppSettings
{
    public bool MyNoiseCancel { get; set; }
    public bool OthersNoiseCancel { get; set; }
    public int VoiceIsolation { get; set; } // 0 off, 1 auto

    // Audio routing (null device = system default)
    public string? MicInput { get; set; }
    public int MicSink { get; set; } = 1;          // MicSinkKind: 0 discard, 1 virtual mic, 2 render device
    public string? MicSinkDevice { get; set; }
    public int MicyFormat { get; set; }            // 0 i32, 1 f32, 2 i16
    public string? IncomingSource { get; set; }    // null = incoming-audio lane off
    public string? IncomingOutput { get; set; }
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    public JsonSettingsStore(string? path = null) => _path = path ?? AppPaths.SettingsFile;
    public AppSettings Load() => JsonFile.Load<AppSettings>(_path) ?? new AppSettings();
    public void Save(AppSettings settings) => JsonFile.Save(_path, settings);
}
