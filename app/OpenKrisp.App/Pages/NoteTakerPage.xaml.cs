using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenKrisp.Core.Notes;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.Storage;
using System.Text;

namespace OpenKrisp.App.Pages;

public sealed partial class NoteTakerPage : Page
{
    public NoteTakerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { NotesVm.PropertyChanged += NotesChanged; RenderMeter(); };
        Unloaded += (_, _) => NotesVm.PropertyChanged -= NotesChanged;
    }

    private void NotesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteTakerViewModel.PeakDb))
            DispatcherQueue.TryEnqueue(RenderMeter);
        if (e.PropertyName == nameof(NoteTakerViewModel.LiveTurns)) DispatcherQueue.TryEnqueue(() => {
            if (LiveTranscriptList.Items.Count > 0) LiveTranscriptList.ScrollIntoView(LiveTranscriptList.Items[^1]);
        });
    }

    private void LevelTrack_SizeChanged(object sender, SizeChangedEventArgs e) => RenderMeter();

    private void RenderMeter()
    {
        if (LevelTrack is null || LevelFill is null) return;
        var width = LevelTrack.ActualWidth;
        double Position(double db) => width * Math.Clamp((db + 60) / 60, 0, 1);
        LevelFill.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Position(NotesVm.PeakDb), LevelTrack.ActualHeight) };
        RmsFill.Width = Position(NotesVm.RmsDb);
        HeldPeakMarker.Margin = new Thickness(Math.Clamp(Position(NotesVm.HeldPeakDb), 0, Math.Max(0, width - 2)), 0, 0, 0);
    }

    public ActionItemsViewModel Vm => App.ActionVm;
    public NoteTakerViewModel NotesVm => App.NotesVm;

    private async void SetUpModels_Click(object sender, RoutedEventArgs e) => await App.Runtime.InitializeAsync();
    private async void Record_Click(object sender, RoutedEventArgs e) => await NotesVm.StartRecordingAsync(RecordingTitleBox.Text);
    private async void StopRecording_Click(object sender, RoutedEventArgs e) => await NotesVm.StopRecordingAsync();
    private void CancelProcessing_Click(object sender, RoutedEventArgs e) => NotesVm.Cancel();
    private void SpeakerCount_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => NotesVm.ExpectedSpeakers = double.IsFinite(args.NewValue) ? (int)Math.Clamp(args.NewValue, 0, 20) : 0;

    private async void ImportAudio_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".mp4", ".wma" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            if (await picker.PickSingleFileAsync() is { } file) await NotesVm.ImportAsync(file.Path, RecordingTitleBox.Text);
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id }) await NotesVm.SummarizeAsync(id);
    }

    private async void TranscribeAgain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id }) await NotesVm.TranscribeMeetingAsync(id);
    }

    private void Tags_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox { Tag: Guid id } box) NotesVm.SaveTags(id, box.Text);
    }

    private async void PlayRecording_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: string path } && File.Exists(path))
                await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async void ExportNotes_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        try
        {
            var card = NotesVm.Transcripts.Single(t => t.Id == id);
            var picker = new FileSavePicker { SuggestedFileName = card.Title };
            picker.FileTypeChoices.Add("Markdown notes", new List<string> { ".md" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            if (await picker.PickSaveFileAsync() is not { } file) return;
            var text = new StringBuilder().AppendLine("# " + card.Title).AppendLine()
                .AppendLine("Tags: " + card.Tags).AppendLine().AppendLine(card.Summary).AppendLine().AppendLine("## Key points");
            foreach (var point in card.KeyPoints) text.AppendLine("- " + point);
            foreach (var topic in card.Topics)
            {
                text.AppendLine().AppendLine("## " + topic.Title);
                foreach (var point in topic.Points) text.AppendLine("- " + point);
            }
            foreach (var section in new[] { ("Decisions", card.Decisions), ("Open questions", card.OpenQuestions), ("Risks and blockers", card.Risks) })
            {
                if (section.Item2.Count == 0) continue;
                text.AppendLine().AppendLine("## " + section.Item1);
                foreach (var point in section.Item2) text.AppendLine("- " + point);
            }
            text.AppendLine().AppendLine("## Action items");
            foreach (var item in card.Tasks)
            {
                text.AppendLine($"- [{(item.Done ? "x" : " ")}] {item.Text} ({item.AssigneeText}" + (item.HasDue ? "; " + item.DueText : "") + ")");
                if (item.Evidence.Length > 0) text.AppendLine("  - Source: " + item.Evidence);
            }
            text.AppendLine().AppendLine("## Transcript");
            foreach (var turn in card.Turns) text.AppendLine($"[{turn.TimeText}] {turn.Speaker}: {turn.Text}").AppendLine();
            await FileIO.WriteTextAsync(file, text.ToString());
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async Task ShowErrorAsync(string message)
        => await new ContentDialog { Title = "Note taker", Content = message, CloseButtonText = "OK", XamlRoot = XamlRoot }.ShowAsync();

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

}
