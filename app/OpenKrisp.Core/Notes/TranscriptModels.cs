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
public sealed record TranscriptTranslation(double Start, double End, string Text, string Translation, string Language);
public sealed record LiveNotesUpdate(string Summary, IReadOnlyList<string> KeyPoints, IReadOnlyList<TranscriptTranslation> Translations, double ThroughSeconds)
{
    public string Error { get; init; } = "";
}
public interface ILiveNotesSession : IDisposable
{
    void Submit(IReadOnlyList<TranscriptTurn> turns);
}

public sealed record GeneratedAction(string Text, string Assignee, string Due, string Evidence)
{
    public string Kind { get; init; } = "Task";
    public string DueText { get; init; } = "";
}
public sealed record MeetingSummary(string Summary, IReadOnlyList<string> KeyPoints, IReadOnlyList<GeneratedAction> ActionItems)
{
    public string Title { get; init; } = "";
    public List<NoteTopic> Topics { get; init; } = new();
    public List<string> Decisions { get; init; } = new();
    public List<string> OpenQuestions { get; init; } = new();
    public List<string> Risks { get; init; } = new();
    public string Model { get; init; } = "";
    public int WorkingContext { get; init; }
    public int SourceSections { get; init; }
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
    ILiveNotesSession? StartLiveNotes(IProgress<LiveNotesUpdate> updates, IProgress<string> progress) => null;
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
    public IReadOnlyList<string> Decisions => Meeting.Decisions.Concat(ActionItems.Where(i => i.Kind == "Decision").Select(i => i.Text)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<string> OpenQuestions => Meeting.OpenQuestions;
    public IReadOnlyList<string> Risks => Meeting.Risks;
    public bool HasDecisions => Decisions.Count > 0;
    public bool HasOpenQuestions => OpenQuestions.Count > 0;
    public bool HasRisks => Risks.Count > 0;
    public IReadOnlyList<ActionItemView> Tasks => ActionItems.Where(i => i.Kind != "Decision").ToList();
    public string ActionStatus => Meeting.SummarizedAt is null ? "Live-captured items are refined when you summarize." : Tasks.Count == 0 ? "No explicit tasks or follow-ups were agreed in this recording." : "Tasks and follow-ups grounded in the transcript. Unspecified owners and dates remain unassigned.";
    public IReadOnlyList<TranscriptTurn> Turns => TranscriptParagraphs.Combine(Meeting.Transcript);
    public IReadOnlyList<TranscriptTranslation> Translations => Meeting.Translations;
    public string Summary => Meeting.Summary.Length > 0 ? Meeting.Summary : Meeting.RunningSummary;
    public IReadOnlyList<string> KeyPoints => Meeting.Summary.Length > 0 ? Meeting.KeyPoints : Meeting.RunningKeyPoints;
    public string SummaryStatus => Meeting.SummarizedAt is { } at ? $"Generated locally {at.ToLocalTime():g}" + (Meeting.SummaryModel.Length > 0 ? " · " + Meeting.SummaryModel : "")
        : Meeting.RunningSummary.Length > 0 ? $"Provisional running summary through {TimeSpan.FromSeconds(Meeting.RunningSummaryThrough):hh\\:mm\\:ss}. Click Summarize for complete notes."
        : "Click Summarize for detailed notes, decisions and actionable follow-ups.";
}
