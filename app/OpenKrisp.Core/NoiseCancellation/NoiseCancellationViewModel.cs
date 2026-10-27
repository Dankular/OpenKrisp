using OpenKrisp.Core.Common;
using OpenKrisp.Core.Engine;

namespace OpenKrisp.Core.NoiseCancellation;

/// <summary>
/// State behind the "Noise cancellation" tab. Three controls: cancel my noise (microphone lane), cancel others' noise
/// (incoming-audio lane), and voice isolation Off/Auto. The engine is optional: without one (model or native library
/// missing) the controls are disabled and <see cref="UnavailableReason"/> says why.
/// </summary>
public sealed class NoiseCancellationViewModel : ObservableObject
{
    /// <summary>What the Voice isolation setting means (always shown next to the control).</summary>
    public const string IsolationDescription =
        "Off: standard noise removal only. Auto: also removes nearby voices and echo.";

    private readonly IEngineController? _engine;
    private readonly ISettingsStore _settings;
    private bool _my, _others;
    private VoiceIsolationMode _isolation;
    private string _status = "";
    private string _error = "";
    private bool _applying;
    private bool _echoActive, _nearbyActive;

    public NoiseCancellationViewModel(IEngineController? engine, string? unavailableReason, ISettingsStore settings)
    {
        _engine = engine;
        _settings = settings;
        UnavailableReason = engine is null ? (unavailableReason ?? "The audio engine is not available.") : "";

        var saved = settings.Load();
        _my = saved.MyNoiseCancel;
        _others = saved.OthersNoiseCancel;
        _isolation = saved.VoiceIsolation == 1 ? VoiceIsolationMode.Auto : VoiceIsolationMode.Off;
        if (engine is not null) ApplyAll();
        Refresh();
    }

    public bool IsAvailable => _engine is not null;
    public bool IsUnavailable => _engine is null;
    public string UnavailableReason { get; }

    public bool MyNoiseCancel
    {
        get => _my;
        set => Change(ref _my, value, v => _engine!.SetMicNoiseCancel(v));
    }

    public bool OthersNoiseCancel
    {
        get => _others;
        set => Change(ref _others, value, v => _engine!.SetSpeakerNoiseCancel(v));
    }

    public VoiceIsolationMode VoiceIsolation
    {
        get => _isolation;
        set => Change(ref _isolation, value, v => _engine!.SetVoiceIsolation(v));
    }

    /// <summary>0 = Off, 1 = Auto: lets a two-item selector bind straight to the mode.</summary>
    public int VoiceIsolationIndex
    {
        get => (int)_isolation;
        set => VoiceIsolation = value == 1 ? VoiceIsolationMode.Auto : VoiceIsolationMode.Off;
    }

    public bool HasIsolationNote => IsolationNote.Length > 0;

    /// <summary>
    /// Shown under the isolation control when Auto is selected and part of what it promises is not running. Built from the
    /// engine's own capability flags, so it never claims more than the engine reports.
    /// </summary>
    public string IsolationNote
    {
        get
        {
            if (_isolation != VoiceIsolationMode.Auto) return "";
            var missing = new List<string>();
            if (!_nearbyActive) missing.Add("nearby-voice removal");
            if (!_echoActive) missing.Add("echo removal");
            return missing.Count == 0
                ? ""
                : $"Not active yet: {string.Join(" and ", missing)}. " +
                  (missing.Count == 2 ? "Until then Auto sounds the same as Off." : "Auto currently does only the part that is active.");
        }
    }

    /// <summary>Honest one-line description of whether audio is actually flowing through the engine.</summary>
    public string StatusText { get => _status; private set => SetField(ref _status, value); }

    public string Error { get => _error; private set => SetField(ref _error, value); }

    /// <summary>Re-reads the engine counters. Call from a UI timer.</summary>
    public void Refresh()
    {
        if (_engine is null) { StatusText = UnavailableReason; return; }
        try
        {
            var s = _engine.GetStatus();
            SetCapabilities(s);
            StatusText = s.MicSamples == 0 && s.SpeakerSamples == 0
                ? "No audio is flowing through the engine yet, so these settings do not change any sound."
                : $"Processed audio: microphone {Seconds(s.MicSamples, s.SampleRate)}, incoming {Seconds(s.SpeakerSamples, s.SampleRate)}.";
        }
        catch (EngineException e) { Error = e.Message; }
    }

    private void SetCapabilities(EngineStatus s)
    {
        if (_echoActive == s.EchoCancelActive && _nearbyActive == s.NearbyVoiceRemovalActive) return;
        _echoActive = s.EchoCancelActive;
        _nearbyActive = s.NearbyVoiceRemovalActive;
        OnPropertyChanged(nameof(IsolationNote));
        OnPropertyChanged(nameof(HasIsolationNote));
    }

    private static string Seconds(ulong samples, uint rate) => $"{(rate == 0 ? 0 : samples / (double)rate):0.0} s";

    private void ApplyAll()
    {
        try
        {
            _engine!.SetMicNoiseCancel(_my);
            _engine.SetSpeakerNoiseCancel(_others);
            _engine.SetVoiceIsolation(_isolation);
        }
        catch (EngineException e) { Error = e.Message; }
    }

    private void Change<T>(ref T field, T value, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (_engine is null || EqualityComparer<T>.Default.Equals(field, value) || _applying) return;
        try
        {
            _applying = true;
            apply(value);
            field = value;
            Error = "";
            OnPropertyChanged(name);
            if (name == nameof(VoiceIsolation))
            {
                OnPropertyChanged(nameof(IsolationNote));
                OnPropertyChanged(nameof(HasIsolationNote));
                OnPropertyChanged(nameof(VoiceIsolationIndex));
            }
            var saved = _settings.Load();
            saved.MyNoiseCancel = _my; saved.OthersNoiseCancel = _others; saved.VoiceIsolation = (int)_isolation;
            _settings.Save(saved);
        }
        catch (EngineException e)
        {
            Error = e.Message;
            OnPropertyChanged(name); // let a bound control snap back to the real value
        }
        finally { _applying = false; }
    }
}
