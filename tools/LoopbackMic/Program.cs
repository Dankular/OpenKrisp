using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Text.Json;

using var devices = new MMDeviceEnumerator();
if (args.Contains("--list"))
{
    var inputs = devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => new { d.ID, d.FriendlyName }).ToArray();
    var outputs = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => new { d.ID, d.FriendlyName }).ToArray();
    using var main = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    Console.WriteLine(JsonSerializer.Serialize(new { DefaultOutput = main.FriendlyName, Inputs = inputs, Outputs = outputs }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}
if (args.Contains("--route-only"))
{
    using var passThrough = new VoicemeeterRoutes(loopback: false);
    Console.WriteLine("Voicemeeter playback is mirrored to Voicemeeter Out B1. Existing speaker playback is preserved.");
    var stop = Path.Combine(AppContext.BaseDirectory, "stop.txt");
    while (!File.Exists(stop)) Thread.Sleep(500);
    File.Delete(stop);
    return;
}
var to = Array.IndexOf(args, "--to");
if (to < 0 || to + 1 == args.Length) throw new ArgumentException("Use --list, or --to <virtual playback device name>.");
var matches = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Where(d => d.FriendlyName.Equals(args[to + 1], StringComparison.OrdinalIgnoreCase)).ToArray();
if (matches.Length != 1) throw new ArgumentException("Choose exactly one playback endpoint from --list.");
using var target = matches[0];
using var source = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
if (target.ID == source.ID) throw new InvalidOperationException("The loopback target cannot be the main speakers (feedback).");
using var routes = args.Contains("--voicemeeter") ? new VoicemeeterRoutes() : null;
using var capture = new WasapiLoopbackCapture(source);
var buffer = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(3), DiscardOnBufferOverflow = true, ReadFully = true };
using var output = new WasapiOut(target, AudioClientShareMode.Shared, true, 100);
output.Init(buffer);
capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
using var quit = new ManualResetEventSlim();
Exception? failure = null;
capture.RecordingStopped += (_, e) => { failure = e.Exception; quit.Set(); };
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
output.Play();
capture.StartRecording();
Console.WriteLine($"Mirroring {source.FriendlyName} -> {target.FriendlyName}");
Console.WriteLine("Select Voicemeeter Output as the microphone in OpenKrisp. Main speakers stay unchanged.");
Console.WriteLine("Create a stop.txt file next to this executable, or press Ctrl+C, to stop and restore Voicemeeter routing.");
var stopFile = Path.Combine(AppContext.BaseDirectory, "stop.txt");
while (!quit.Wait(500))
{
    if (File.Exists(stopFile)) { File.Delete(stopFile); break; }
}
capture.StopRecording();
output.Stop();
if (failure is not null) throw failure;

sealed class VoicemeeterRoutes : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Login();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetTypeFn(out int type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFloat([MarshalAs(UnmanagedType.LPStr)] string name, out float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetFloat([MarshalAs(UnmanagedType.LPStr)] string name, float value);
    private readonly nint _library;
    private readonly Login _logout;
    private readonly SetFloat _set;
    private readonly Dictionary<string, float> _before = new();
    public VoicemeeterRoutes(bool loopback = true)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VB", "Voicemeeter", "VoicemeeterRemote64.dll");
        _library = NativeLibrary.Load(path);
        T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
        var login = Bind<Login>("VBVMR_Login");
        _logout = Bind<Login>("VBVMR_Logout");
        _set = Bind<SetFloat>("VBVMR_SetParameterFloat");
        var get = Bind<GetFloat>("VBVMR_GetParameterFloat");
        var typeFn = Bind<GetTypeFn>("VBVMR_GetVoicemeeterType");
        if (login() != 0) throw new InvalidOperationException("Voicemeeter must already be running.");
        typeFn(out var type);
        if (type != 1) { _logout(); NativeLibrary.Free(_library); throw new NotSupportedException("This routing preset is for Voicemeeter Standard."); }
        Thread.Sleep(100);
        var desired = new Dictionary<string, float> { ["Strip[0].B1"] = 0, ["Strip[1].B1"] = 0,
            ["Strip[2].A1"] = 0, ["Strip[2].B1"] = 1, ["Strip[2].Mute"] = 0, ["Strip[2].Gain"] = 0,
            ["Bus[1].Mute"] = 0, ["Bus[1].Gain"] = 0 };
        if (!loopback) desired.Remove("Strip[2].A1");
        foreach (var (key, value) in desired)
        {
            if (get(key, out var previous) != 0) { Dispose(); throw new IOException("Cannot read Voicemeeter parameter " + key); }
            _before[key] = previous;
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "routing-before.json"), JsonSerializer.Serialize(_before, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var (key, value) in desired)
            if (_set(key, value) != 0) { Dispose(); throw new IOException("Cannot set Voicemeeter parameter " + key); }
    }
    public void Dispose()
    {
        foreach (var (key, value) in _before) _set(key, value);
        _logout();
        NativeLibrary.Free(_library);
    }
}
