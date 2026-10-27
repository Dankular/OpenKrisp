using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKrisp.Core.NoiseCancellation;
using OpenKrisp.App.Audio;

namespace OpenKrisp.App.Pages;

public sealed partial class NoiseCancellationPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public NoiseCancellationPage()
    {
        InitializeComponent();
        IncomingHelp.Text = AudioRoutingViewModel.IncomingHelp;
        _timer.Tick += (_, _) =>
        {
            Vm.Refresh();
            Routing.Refresh();
        };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public NoiseCancellationViewModel Vm => App.NoiseVm;

    public AudioRoutingViewModel Routing => App.RoutingVm;
    public VoiceMicrophoneService Voice => App.VoiceMicrophone;
    private async void StartVoice_Click(object sender, RoutedEventArgs e) => await Voice.StartAsync();
    private async void StopVoice_Click(object sender, RoutedEventArgs e) => await Voice.StopAsync();
    private void RefreshVoice_Click(object sender, RoutedEventArgs e) => Voice.RefreshDevices();

    public static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Voice.IsRunning || Voice.IsBusy)
        {
            await new ContentDialog { Title = "Voice microphone is active", Content = "Stop the voice microphone before starting the additional audio route.", CloseButtonText = "OK", XamlRoot = XamlRoot }.ShowAsync();
            return;
        }
        await Routing.StartAsync();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Routing.Stop();

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => Routing.RefreshDevices();
}
