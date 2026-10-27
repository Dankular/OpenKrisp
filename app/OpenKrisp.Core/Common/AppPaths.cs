namespace OpenKrisp.Core.Common;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\OpenKrisp on Windows (the platform's local app data folder elsewhere).</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenKrisp");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ActionItemsFile => Path.Combine(DataDir, "action-items.json");

    /// <summary>Where the app looks for the DPDFNet model unless OPENKRISP_MODEL is set.</summary>
    public static string DefaultModelPath => Path.Combine(DataDir, "models", "dpdfnet2_48khz_hr.onnx");

    public static string ResolveModelPath()
        => Environment.GetEnvironmentVariable("OPENKRISP_MODEL") is { Length: > 0 } p ? p : DefaultModelPath;
}
