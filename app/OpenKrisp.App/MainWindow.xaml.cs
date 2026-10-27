using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKrisp.App.Pages;

namespace OpenKrisp.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 950));
        var initialPage = Environment.GetEnvironmentVariable("OPENKRISP_START_PAGE") ?? "notes";
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, initialPage)) ?? Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            ContentFrame.Navigate(tag switch { "notes" => typeof(NoteTakerPage), "actions" => typeof(ActionItemsPage), "status" => typeof(StatusPage), _ => typeof(NoiseCancellationPage) });
        }
    }
}
