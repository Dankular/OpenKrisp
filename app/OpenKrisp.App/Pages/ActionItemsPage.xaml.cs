using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenKrisp.Core.Notes;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OpenKrisp.App.Pages;

public sealed partial class ActionItemsPage : Page
{
    public ActionItemsViewModel Vm => App.ActionVm;
    public ActionItemsPage()
    {
        Vm.Filter = ActionItemFilter.All;
        InitializeComponent();
    }

    private void Views_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        => Vm.Filter = ReferenceEquals(sender.SelectedItem, Mine) ? ActionItemFilter.AssignedToMe : ActionItemFilter.All;

    private async void AddItem_Click(object sender, RoutedEventArgs e) => await EditAsync(null);
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id }) await EditAsync(Vm.Items.FirstOrDefault(i => i.Id == id));
    }

    private async Task EditAsync(ActionItemView? view)
    {
        try
        {
            var text = new TextBox { Header = "Item", Text = view?.Text ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 360 };
            var kind = new ComboBox { Header = "Type", ItemsSource = new[] { "Task", "Decision", "Follow-up" }, SelectedItem = view?.Kind ?? "Task", HorizontalAlignment = HorizontalAlignment.Stretch };
            var assignee = new TextBox { Header = "Assigned to (Me, a speaker label, or a name)", Text = view is null ? (Vm.Filter == ActionItemFilter.AssignedToMe ? "Me" : "") : view.AssigneeText == "Unassigned" ? "" : view.AssigneeText };
            var due = new CalendarDatePicker { Header = "Due date (optional)", PlaceholderText = "No due date" };
            if (view?.Item.Due is { } date) due.Date = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue));
            var clearDue = new Button { Content = "Clear due date" };
            var dueText = new TextBox { Header = "Deadline as stated (optional)", Text = view?.Item.DueTextSource ?? "", PlaceholderText = "e.g. by Friday; an exact date can be set above" };
            clearDue.Click += (_, _) => { due.Date = null; dueText.Text = ""; };
            var meetings = new ComboBox { Header = "Meeting", ItemsSource = Vm.Meetings, DisplayMemberPath = "Title", HorizontalAlignment = HorizontalAlignment.Stretch };
            meetings.SelectedItem = Vm.Meetings.FirstOrDefault(m => m.Id == view?.Item.MeetingId) ?? Vm.Meetings.FirstOrDefault();
            meetings.IsEnabled = view is null;
            var newMeeting = new TextBox { Header = "Or create a meeting for this item", PlaceholderText = "Meeting title", Visibility = view is null ? Visibility.Visible : Visibility.Collapsed };
            var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 12 };
            foreach (var element in new FrameworkElement[] { text, kind, assignee, due, dueText, clearDue, meetings, newMeeting, validation }) panel.Children.Add(element);
            var dialog = new ContentDialog { Title = view is null ? "Add item" : "Edit item", Content = new ScrollViewer { Content = panel, MaxHeight = 560 }, PrimaryButtonText = "Save", CloseButtonText = "Cancel", XamlRoot = XamlRoot };
            dialog.PrimaryButtonClick += (_, args) => {
                if (string.IsNullOrWhiteSpace(text.Text) || (view is null && meetings.SelectedItem is null && string.IsNullOrWhiteSpace(newMeeting.Text)))
                { validation.Text = "Enter an item and choose or create a meeting."; args.Cancel = true; }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var owner = assignee.Text.Trim();
            if (owner.Equals("me", StringComparison.OrdinalIgnoreCase)) owner = ActionItem.Me;
            DateOnly? deadline = due.Date is { } value ? DateOnly.FromDateTime(value.DateTime) : null;
            if (view is null)
            {
                var meeting = !string.IsNullOrWhiteSpace(newMeeting.Text) ? Vm.AddMeeting(newMeeting.Text) : meetings.SelectedItem as Meeting ?? throw new InvalidOperationException("Choose a meeting.");
                var item = Vm.AddItem(meeting.Id, text.Text);
                Vm.EditItem(item.Id, text.Text, owner, deadline, (string)kind.SelectedItem, dueText.Text);
            }
            else Vm.EditItem(view.Id, text.Text, owner, deadline, (string)kind.SelectedItem, dueText.Text);
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        try { if (sender is Button { Tag: Guid id }) Vm.RemoveItem(id); }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var markdown = Vm.ExportMarkdown();
            var csv = Vm.ExportCsv();
            var picker = new FileSavePicker { SuggestedFileName = "Meeting action items" };
            picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            if (await picker.PickSaveFileAsync() is { } file) await FileIO.WriteTextAsync(file, file.FileType.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? csv : markdown);
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async Task ShowErrorAsync(string message) => await new ContentDialog { Title = "Action items", Content = message, CloseButtonText = "OK", XamlRoot = XamlRoot }.ShowAsync();
}
