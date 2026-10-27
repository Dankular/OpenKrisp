using Microsoft.UI.Xaml;
using OpenKrisp.App.Notes;
using OpenKrisp.Core.Common;
using OpenKrisp.Core.Engine;
using OpenKrisp.Core.NoiseCancellation;
using OpenKrisp.Core.Notes;

namespace OpenKrisp.App;

public partial class App : Application
{
    private Window? _window;
    private EngineHost? _engine;

    /// <summary>Shared view models; the pages read these.</summary>
    public static NoiseCancellationViewModel NoiseVm { get; private set; } = null!;
    public static AudioRoutingViewModel RoutingVm { get; private set; } = null!;
    public static NoteTakerViewModel NotesVm { get; private set; } = null!;
    public static Window MainWindow => ((App)Current)._window!;
    public static ActionItemsViewModel ActionVm { get; private set; } = null!;
    public static TractRuntimeService Runtime { get; private set; } = null!;

    public App()
    {
        UnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(Path.Combine(AppPaths.DataDir, "startup-error.log"), e.Message + Environment.NewLine + e.Exception);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _engine = TryStartEngine(out var reason);
        NoiseVm = new NoiseCancellationViewModel(_engine, reason, new JsonSettingsStore());
        RoutingVm = new AudioRoutingViewModel(_engine, new JsonSettingsStore());
        var actionStore = new ActionItemStore();
        actionStore.InitializeAutomaticExtraction();
        ActionVm = new ActionItemsViewModel(actionStore);
        var worker = new LocalNotesWorker();
        Runtime = new TractRuntimeService(worker, () => NotesVm is not null && (NotesVm.IsBusy || NotesVm.IsRecording));
        worker.AttachRuntime(Runtime);
        NotesVm = new NoteTakerViewModel(actionStore, worker, new MeetingRecorder());
        Runtime.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(TractRuntimeService.IsLoaded) or nameof(TractRuntimeService.IsBusy) or nameof(TractRuntimeService.Message)) NotesVm.RefreshModelStatus();
        };
        NotesVm.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(NoteTakerViewModel.IsBusy) or nameof(NoteTakerViewModel.IsRecording)) Runtime.Refresh();
        };

        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            NotesVm.Dispose();
            Runtime.Dispose();
            RoutingVm.Stop();
            _engine?.Dispose();
        };
        _window.Activate();
        _ = Runtime.InitializeAsync();
    }

    /// <summary>The app still opens when the engine cannot start; the Noise cancellation tab then explains why.</summary>
    private static EngineHost? TryStartEngine(out string? reason)
    {
        var model = AppPaths.ResolveModelPath();
        if (!File.Exists(model))
        {
            reason = $"The noise-removal model was not found at {model}. Download dpdfnet2_48khz_hr.onnx (Ceva-IP/DPDFNet) and put it there, or set OPENKRISP_MODEL.";
            return null;
        }
        try
        {
            reason = null;
            return EngineHost.Create(model);
        }
        catch (EngineException e)
        {
            reason = e.Message;
            return null;
        }
    }
}
