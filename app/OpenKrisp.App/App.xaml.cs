using Microsoft.UI.Xaml;
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
    public static ActionItemsViewModel ActionVm { get; private set; } = null!;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _engine = TryStartEngine(out var reason);
        NoiseVm = new NoiseCancellationViewModel(_engine, reason, new JsonSettingsStore());
        RoutingVm = new AudioRoutingViewModel(_engine, new JsonSettingsStore());
        ActionVm = new ActionItemsViewModel(new ActionItemStore());

        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            RoutingVm.Stop();
            _engine?.Dispose();
        };
        _window.Activate();
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
