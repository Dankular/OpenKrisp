namespace OpenKrisp.Core.Notes;

public sealed class Meeting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public List<TranscriptTurn> Transcript { get; set; } = new();
    public int TranscriptRevision { get; set; }
    public string Summary { get; set; } = "";
    public List<string> KeyPoints { get; set; } = new();
    public DateTimeOffset? SummarizedAt { get; set; }
    public string AudioPath { get; set; } = "";
    public string Kind { get; set; } = "Meeting";
    public List<string> Tags { get; set; } = new();
    public List<NoteTopic> Topics { get; set; } = new();
    public List<string> SuppressedActionEvidence { get; set; } = new();
    public List<string> Decisions { get; set; } = new();
    public List<string> OpenQuestions { get; set; } = new();
    public List<string> Risks { get; set; } = new();
    public string SummaryModel { get; set; } = "";
    public int SummaryWorkingContext { get; set; }
    public int SummarySourceSections { get; set; }
    public string RunningSummary { get; set; } = "";
    public List<string> RunningKeyPoints { get; set; } = new();
    public double RunningSummaryThrough { get; set; }
    public List<TranscriptTranslation> Translations { get; set; } = new();
}

public sealed class ActionItem
{
    /// <summary>Assignee value meaning "the app's user".</summary>
    public const string Me = "me";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MeetingId { get; set; }
    public string Text { get; set; } = "";
    /// <summary>When it was said/captured (shown as a clock time on the item).</summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public DateOnly? Due { get; set; }
    /// <summary>Empty = unassigned, <see cref="Me"/> = the user, otherwise a display name.</summary>
    public string Assignee { get; set; } = "";
    public bool Done { get; set; }
    public bool GeneratedFromTranscript { get; set; }
    public string Evidence { get; set; } = "";
    public string Kind { get; set; } = "Task";
    public bool UserEdited { get; set; }
    public string DueTextSource { get; set; } = "";
}

public sealed record ExtractedActionItem(string Text, string? Assignee, DateOnly? Due);

/// <summary>Legacy text-only extractor. The note-taking workflow uses ILocalNotesWorker for structured local summaries.</summary>
public interface IActionItemExtractor
{
    bool IsAvailable { get; }
    string UnavailableReason { get; }
    Task<IReadOnlyList<ExtractedActionItem>> ExtractAsync(string transcript, CancellationToken ct);
}

public sealed class UnavailableExtractor : IActionItemExtractor
{
    public bool IsAvailable => false;
    public string UnavailableReason =>
        "Automatic extraction needs a local language model, which is not set up yet. You can add and track action items by hand.";

    public Task<IReadOnlyList<ExtractedActionItem>> ExtractAsync(string transcript, CancellationToken ct)
        => throw new NotSupportedException(UnavailableReason);
}
