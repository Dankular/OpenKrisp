using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKrisp.Core.NoiseCancellation;

namespace OpenKrisp.App.Pages;

public sealed partial class NoiseCancellationPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public NoiseCancellationPage()
    {
        InitializeComponent();
        IsolationDescription.Text = NoiseCancellationViewModel.IsolationDescription;
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

    public static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void Start_Click(object sender, RoutedEventArgs e) => await Routing.StartAsync();

    private void Stop_Click(object sender, RoutedEventArgs e) => Routing.Stop();

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => Routing.RefreshDevices();
}
