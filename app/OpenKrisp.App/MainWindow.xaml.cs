using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKrisp.App.Pages;

namespace OpenKrisp.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            ContentFrame.Navigate(tag == "notes" ? typeof(NoteTakerPage) : typeof(NoiseCancellationPage));
        }
    }
}
