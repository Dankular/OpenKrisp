using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenKrisp.Core.Notes;
using Windows.System;

namespace OpenKrisp.App.Pages;

public sealed partial class NoteTakerPage : Page
{
    public NoteTakerPage() => InitializeComponent();

    public ActionItemsViewModel Vm => App.ActionVm;

    // Function bindings used by the XAML.
    public static bool Not(bool value) => !value;

    public static Visibility HiddenWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Brush DueBackground(bool overdue) => overdue
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xE8, 0x11, 0x23))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x80, 0x80, 0x80));

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        => Vm.Filter = ReferenceEquals(sender.SelectedItem, AllItem) ? ActionItemFilter.All : ActionItemFilter.AssignedToMe;

    private void AddMeeting_Click(object sender, RoutedEventArgs e) => AddMeeting();

    private void NewMeetingBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) AddMeeting();
    }

    private void AddMeeting()
    {
        var title = NewMeetingBox.Text.Trim();
        if (title.Length == 0) return;
        Vm.AddMeeting(title);
        NewMeetingBox.Text = "";
    }

    private void AddItemBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox { Tag: Guid meetingId } box) return;
        var text = box.Text.Trim();
        if (text.Length == 0) return;
        Vm.AddItem(meetingId, text); // rebuilds the list, so the box is recreated empty
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id }) Vm.RemoveItem(id);
    }
}
