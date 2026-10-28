using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using OpenKrisp.Core.Notes;
using Windows.System;

namespace OpenKrisp.App.Pages;

public sealed partial class TranscriptPrompter : UserControl
{
    public static readonly DependencyProperty TurnsProperty = DependencyProperty.Register(nameof(Turns),
        typeof(IReadOnlyList<TranscriptTurn>), typeof(TranscriptPrompter), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(nameof(IsLive),
        typeof(bool), typeof(TranscriptPrompter), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty TranslationsProperty = DependencyProperty.Register(nameof(Translations),
        typeof(IReadOnlyList<TranscriptTranslation>), typeof(TranscriptPrompter), new PropertyMetadata(null, Changed));
    public IReadOnlyList<TranscriptTranslation>? Translations { get => (IReadOnlyList<TranscriptTranslation>?)GetValue(TranslationsProperty); set => SetValue(TranslationsProperty, value); }
    public IReadOnlyList<TranscriptTurn>? Turns { get => (IReadOnlyList<TranscriptTurn>?)GetValue(TurnsProperty); set => SetValue(TurnsProperty, value); }
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly List<(TextBlock Label, RichTextBlock Body)> _paragraphs = new();
    private readonly Stopwatch _clock = new();
    private double _fontSize = 26;
    private bool _wasLive;

    public TranscriptPrompter()
    {
        InitializeComponent();
        _timer.Tick += ScrollTick;
        Loaded += (_, _) => { Render(); _clock.Restart(); _timer.Start(); };
        Unloaded += (_, _) => { _timer.Stop(); _clock.Stop(); };
        // Observe handled events too: wheel, touch, scrollbar and keyboard reading all pause following.
        Reader.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(PauseForPointer), true);
        Reader.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PauseForPointer), true);
        Reader.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Reader_KeyDown), true);
    }

    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((TranscriptPrompter)sender).Render();

    private void Render()
    {
        if (Paragraphs is null) return;
        if (_wasLive != IsLive)
        {
            _wasLive = IsLive;
            ScrollToggle.IsChecked = IsLive;
        }
        ScrollToggle.Content = IsLive ? "Follow live" : "Auto-scroll";
        var turns = Turns ?? Array.Empty<TranscriptTurn>();
        if (IsLive && turns.Count == 0 && _paragraphs.Count > 0)
        {
            ScrollToggle.IsChecked = true;
            Reader.ChangeView(null, 0, null, true);
        }
        EmptyMessage.Visibility = turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Text = IsLive ? "Listening… Your transcript will appear here." : "No transcript yet.";
        // Update existing paragraphs in place so each incoming ASR window preserves the reading position.
        while (_paragraphs.Count > turns.Count)
        {
            _paragraphs.RemoveAt(_paragraphs.Count - 1);
            Paragraphs.Children.RemoveAt(Paragraphs.Children.Count - 1);
        }
        for (var i = 0; i < turns.Count; i++)
        {
            if (i == _paragraphs.Count)
            {
                var label = new TextBlock { FontSize = 12, Foreground = Brush(0x64, 0x74, 0x8B) };
                var body = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                var section = new StackPanel { Spacing = 8 };
                section.Children.Add(label); section.Children.Add(body);
                Paragraphs.Children.Add(section); _paragraphs.Add((label, body));
            }
            var (heading, text) = _paragraphs[i];
            heading.Text = turns[i].Speaker + "  ·  " + turns[i].TimeText;
            RenderText(text, turns[i]);
            text.FontSize = _fontSize;
            text.LineHeight = Math.Round(_fontSize * 1.6);
            text.Foreground = IsLive && i == turns.Count - 1 ? Brush(0x43, 0x38, 0xCA) : Brush(0x1E, 0x29, 0x3B);
        }
        SetReadingSpace(); UpdateHint();
    }

    private void RenderText(RichTextBlock body, TranscriptTurn turn)
    {
        var matches = new List<(int Start, TranscriptTranslation Translation)>();
        foreach (var annotation in Translations ?? Array.Empty<TranscriptTranslation>())
        {
            if (annotation.Text.Length == 0 || annotation.Start > turn.End || annotation.End < turn.Start) continue;
            var at = 0;
            while ((at = turn.Text.IndexOf(annotation.Text, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                matches.Add((at, annotation)); at += annotation.Text.Length;
            }
        }
        var signature = turn.Text + "\0" + _fontSize + "\0" + string.Join("\0", matches.Select(m => m.Start + m.Translation.Translation + m.Translation.Text));
        if (body.Tag is string previous && previous == signature) return;
        body.Tag = signature;
        body.Blocks.Clear(); body.TextHighlighters.Clear();
        var paragraph = new Paragraph();
        var highlights = new TextHighlighter { Background = Brush(0xED, 0xE9, 0xFE), Foreground = Brush(0x5B, 0x21, 0xB6) };
        int sourceAt = 0, displayAt = 0;
        void Append(string value) { paragraph.Inlines.Add(new Run { Text = value }); displayAt += value.Length; }
        foreach (var (at, annotation) in matches.OrderBy(m => m.Start).ThenByDescending(m => m.Translation.Text.Length))
        {
            if (at < sourceAt) continue;
            Append(turn.Text[sourceAt..at]);
            highlights.Ranges.Add(new TextRange { StartIndex = displayAt, Length = annotation.Text.Length });
            paragraph.Inlines.Add(new Run { Text = turn.Text.Substring(at, annotation.Text.Length), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            displayAt += annotation.Text.Length;
            var gloss = " (" + annotation.Translation + ")";
            paragraph.Inlines.Add(new Run { Text = gloss, FontSize = Math.Max(14, _fontSize * .68), Foreground = Brush(0x64, 0x74, 0x8B) });
            displayAt += gloss.Length;
            sourceAt = at + annotation.Text.Length;
        }
        Append(turn.Text[sourceAt..]);
        body.Blocks.Add(paragraph);
        if (highlights.Ranges.Count > 0) body.TextHighlighters.Add(highlights);
    }

    private void Reader_Loaded(object sender, RoutedEventArgs e) => SetReadingSpace();
    private void SetReadingSpace()
    {
        // Keep the newest words near the reading line, rather than against the bottom edge.
        Paragraphs.Padding = new Thickness(0, 0, 0, Math.Max(60, Reader.ViewportHeight * .28));
    }

    private void ScrollTick(object? sender, object e)
    {
        var elapsed = Math.Min(.1, _clock.Elapsed.TotalSeconds);
        _clock.Restart();
        if (ScrollToggle.IsChecked != true || Reader.ViewportHeight <= 0) return;
        SetReadingSpace();
        var target = Reader.ScrollableHeight;
        var remaining = target - Reader.VerticalOffset;
        if (remaining <= .5)
        {
            if (!IsLive) { ScrollToggle.IsChecked = false; UpdateHint(); }
            return;
        }
        // Live following eases toward incoming speech; saved transcripts move at a steady reading pace.
        var step = IsLive ? Math.Max(18, remaining * 3) * elapsed : 22 * elapsed;
        Reader.ChangeView(null, Math.Min(target, Reader.VerticalOffset + step), null, true);
    }

    private void ScrollToggle_Click(object sender, RoutedEventArgs e) { _clock.Restart(); UpdateHint(); }
    private void PauseForPointer(object sender, PointerRoutedEventArgs e) => Pause();
    private void Reader_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End or VirtualKey.Space) Pause();
    }
    private void Pause() { ScrollToggle.IsChecked = false; UpdateHint(); }
    private void UpdateHint() => ScrollHint.Text = ScrollToggle.IsChecked == true
        ? (IsLive ? "Following new speech · scroll to pause" : "Scrolling · scroll to pause")
        : (IsLive ? "Paused · Follow live to catch up" : "Scroll at your own pace");
    private void Smaller_Click(object sender, RoutedEventArgs e) { _fontSize = Math.Max(20, _fontSize - 2); Render(); }
    private void Larger_Click(object sender, RoutedEventArgs e) { _fontSize = Math.Min(38, _fontSize + 2); Render(); }
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
}
