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

    public static Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Done items are greyed out and struck through, as in the reference design.</summary>
    public static Brush TextBrush(bool done) => (Brush)Application.Current.Resources[done ? "DoneTextBrush" : "BodyBrush"];

    public static Windows.UI.Text.TextDecorations Decorations(bool done)
        => done ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;

    public static Brush DueBackground(bool overdue) => (Brush)Application.Current.Resources[overdue ? "ChipOverdueBrush" : "ChipBrush"];

    // Five pastel avatar colours (fill, then the letter's colour), picked by AvatarColors.IndexFor in the Core library.
    private static readonly (byte R, byte G, byte B)[] AvatarFills =
        { (0xCF, 0xE8, 0xFA), (0xCD, 0xEF, 0xE4), (0xFB, 0xEF, 0xC4), (0xE2, 0xDC, 0xFB), (0xFA, 0xDD, 0xE7) };

    private static readonly (byte R, byte G, byte B)[] AvatarInks =
        { (0x1F, 0x6F, 0x8B), (0x1E, 0x7A, 0x62), (0x8A, 0x6D, 0x12), (0x5B, 0x4F, 0xE8), (0xA2, 0x3B, 0x62) };

    public static Brush AvatarFill(int index) => Solid(AvatarFills[index % AvatarFills.Length]);

    public static Brush AvatarInk(int index) => Solid(AvatarInks[index % AvatarInks.Length]);

    private static SolidColorBrush Solid((byte R, byte G, byte B) c) => new(Windows.UI.Color.FromArgb(0xFF, c.R, c.G, c.B));

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
