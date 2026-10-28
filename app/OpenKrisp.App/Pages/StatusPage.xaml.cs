using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKrisp.Core.Notes;

namespace OpenKrisp.App.Pages;

public sealed partial class StatusPage : Page
{
    public TractRuntimeService Runtime => App.Runtime;
    private readonly DispatcherTimer _contextTimer = new() { Interval=TimeSpan.FromSeconds(2) };
    public StatusPage()
    {
        InitializeComponent();
        _contextTimer.Tick += (_,_) => Runtime.RefreshContext();
        Loaded += (_,_) => { Runtime.RefreshContext(); _contextTimer.Start(); };
        Unloaded += (_,_) => _contextTimer.Stop();
    }
    private async void Load_Click(object sender, RoutedEventArgs e) => await Runtime.LoadAsync();
    private async void Unload_Click(object sender, RoutedEventArgs e) => await Runtime.UnloadAsync();
    private async void Redownload_Click(object sender, RoutedEventArgs e) => await Runtime.RedownloadAsync();
    private async void Restart_Click(object sender, RoutedEventArgs e) => await Runtime.RestartAsync();
}
