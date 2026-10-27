using System.Text.Json;

namespace OpenKrisp.Core.Common;

/// <summary>Atomic JSON persistence: write to a temp file, then replace. A corrupt file is moved aside, never silently deleted.</summary>
internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var s = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(s, Options);
        }
        catch (JsonException)
        {
            var aside = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(path, aside);
            return null;
        }
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
