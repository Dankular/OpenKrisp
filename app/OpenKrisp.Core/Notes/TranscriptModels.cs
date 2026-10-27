using System.Text.Json.Serialization;

namespace OpenKrisp.Core.Notes;

public sealed record TranscriptTurn(double Start, double End, string Speaker, string Text)
{
    public bool ParagraphStart { get; init; }
    [JsonIgnore] public string TimeText => TimeSpan.FromSeconds(Start).ToString(@"hh\:mm\:ss");
}

public sealed record LiveTranscriptUpdate(IReadOnlyList<TranscriptTurn> Turns, double AudioEnd, double ProcessingSeconds, double SkippedSeconds)
{
    public double LatencySeconds { get; init; }
    public string Error { get; init; } = "";
    public bool SpeechDetected { get; init; }
    public double SpeechProbability { get; init; }
    public double TurnProbability { get; init; }
    public bool TurnComplete { get; init; }
    public IReadOnlyList<double> TurnBoundaries { get; init; } = Array.Empty<double>();
}

public sealed record NoteTopic(string Title, IReadOnlyList<string> Points);

public sealed record GeneratedAction(string Text, string Assignee, string Due, string Evidence);
public sealed record MeetingSummary(string Summary, IReadOnlyList<string> KeyPoints, IReadOnlyList<GeneratedAction> ActionItems)
{
    public string Title { get; init; } = "";
    public List<NoteTopic> Topics { get; init; } = new();
}

public interface ILiveTranscript : IDisposable
{
    void AddSamples(float[] samples);
    Task CompleteAsync();
}

public interface ILocalNotesWorker
{
    bool IsReady { get; }
    bool IsPreparing => false;
    string RuntimeStatus => "";
    ILiveTranscript? StartLive(IProgress<LiveTranscriptUpdate> preview, int speakers = 0) => null;
    Task<string> CreateTitleAsync(IReadOnlyList<TranscriptTurn> transcript, IProgress<string> progress, CancellationToken ct) => Task.FromResult("");
    Task InstallAsync(IProgress<string> progress, CancellationToken ct);
    Task<IReadOnlyList<TranscriptTurn>> TranscribeAsync(string audioPath, int speakers, IProgress<string> progress, CancellationToken ct);
    Task<MeetingSummary> SummarizeAsync(IReadOnlyList<TranscriptTurn> transcript, IProgress<string> progress, CancellationToken ct);
}

public sealed record RecordingInput(string Id, string Name);
public sealed record RecordingSource(int Mode, string InputId, string Name);

public interface IMeetingRecorder : IDisposable
{
    event Action<float[]>? SamplesAvailable;
    event Action<AudioLevel>? LevelAvailable { add { } remove { } }
    IReadOnlyList<RecordingInput> Inputs => Array.Empty<RecordingInput>();
    Task StartAsync(string outputPath, int source, string? inputId = null);
    Task StopAsync();
}

public sealed class TranscriptCard
{
    public required Meeting Meeting { get; init; }
    public required bool CanSummarize { get; init; }
    public required bool CanTranscribe { get; init; }
    public required IReadOnlyList<ActionItemView> ActionItems { get; init; }
    public Guid Id => Meeting.Id;
    public string Title => Meeting.Title;
    public string Kind => Meeting.Kind;
    public string StartedText => Meeting.StartedAt.ToLocalTime().ToString("ddd, MMM d · h:mm tt");
    public string Tags => string.Join(", ", Meeting.Tags);
    public string AudioPath => Meeting.AudioPath;
    public IReadOnlyList<NoteTopic> Topics => Meeting.Topics;
    public IReadOnlyList<TranscriptTurn> Turns => TranscriptParagraphs.Combine(Meeting.Transcript);
    public string Summary => Meeting.Summary;
    public IReadOnlyList<string> KeyPoints => Meeting.KeyPoints;
    public string SummaryStatus => Meeting.SummarizedAt is { } at ? $"Generated locally {at.ToLocalTime():g}" : "Not summarized";
}
