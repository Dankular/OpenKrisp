namespace OpenKrisp.Core.Notes;

public sealed class Meeting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
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
}

public sealed record ExtractedActionItem(string Text, string? Assignee, DateOnly? Due);

/// <summary>Turns a transcript into action items. No implementation exists yet: a local language model has not been chosen.</summary>
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
