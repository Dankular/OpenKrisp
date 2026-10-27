using System.Globalization;
using OpenKrisp.Core.Common;

namespace OpenKrisp.Core.Notes;

public enum ActionItemFilter { AssignedToMe, All }

public sealed class ActionItemView : ObservableObject
{
    private readonly ActionItemStore _store;
    private readonly Func<DateOnly> _today;

    internal ActionItemView(ActionItem item, ActionItemStore store, Func<DateOnly> today)
    {
        Item = item;
        _store = store;
        _today = today;
    }

    public ActionItem Item { get; }
    public Guid Id => Item.Id;
    public string Evidence => Item.Evidence;
    public string Kind => Item.Kind;
    public string MeetingTitle => _store.Meetings.FirstOrDefault(m => m.Id == Item.MeetingId)?.Title ?? "Meeting";
    public string Text => Item.Text;
    public string TimeText => Item.CapturedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    public string DueText => Item.Due is { } d ? "Due " + d.ToString("MMM d", CultureInfo.InvariantCulture) : Item.DueTextSource.Length > 0 ? "Due " + Item.DueTextSource : "No due date";
    public bool IsOverdue => Item.Due is { } d && !Item.Done && d < _today();

    public string AssigneeText => Item.Assignee switch
    {
        "" => "Unassigned",
        ActionItem.Me => "Me",
        var name => name,
    };

    public bool HasDue => Item.Due is not null || Item.DueTextSource.Length > 0;
    public bool HasAssignee => Item.Assignee.Length > 0;

    /// <summary>Letter shown in the assignee's avatar: "Y" for the user (as in "You"), otherwise the name's first letter, upper-cased. Empty when unassigned.</summary>
    public string AssigneeInitial => Item.Assignee switch
    {
        "" => "",
        ActionItem.Me => "Y",
        var name => string.IsNullOrWhiteSpace(name) ? "" : char.ToUpperInvariant(name.Trim()[0]).ToString(),
    };

    /// <summary>Which of the five avatar colours to use (0-4). Stable across runs: the same name always gets the same colour.</summary>
    public int AvatarColorIndex => AvatarColors.IndexFor(Item.Assignee);

    public bool Done
    {
        get => Item.Done;
        set
        {
            if (Item.Done == value) return;
            _store.SetDone(Item.Id, value); // the store raises Changed; the view model rebuilds
            OnPropertyChanged();
        }
    }
}

public static class AvatarColors
{
    public const int Count = 5;

    /// <summary>The user's own avatar always uses index 3.</summary>
    public static int IndexFor(string assignee)
    {
        if (assignee.Length == 0) return 0;
        if (assignee == ActionItem.Me) return 3;
        var sum = 0;
        foreach (var c in assignee.Trim().ToLowerInvariant()) sum += c; // not string.GetHashCode(): that changes every run
        return sum % Count;
    }
}

public sealed class MeetingGroup
{
    public MeetingGroup(Meeting meeting, IReadOnlyList<ActionItemView> items)
    {
        Meeting = meeting;
        Items = items;
    }

    public Meeting Meeting { get; }
    public string Title => Meeting.Title;
    public IReadOnlyList<ActionItemView> Items { get; }
    public int DoneCount => Items.Count(i => i.Done);
    public string CountText => $"{DoneCount} of {Items.Count} done";
}

/// <summary>State behind the "Action items" view: items grouped by meeting, filtered to "Assigned to me" or "All".</summary>
public sealed class ActionItemsViewModel : ObservableObject
{
    private readonly ActionItemStore _store;
    private readonly IActionItemExtractor _extractor;
    private readonly Func<DateOnly> _today;
    private ActionItemFilter _filter = ActionItemFilter.AssignedToMe;
    private IReadOnlyList<MeetingGroup> _groups = Array.Empty<MeetingGroup>();
    private IReadOnlyList<ActionItemView> _items = Array.Empty<ActionItemView>();
    private string _search = "";
    private int _kindIndex;
    public string Search { get => _search; set { if (SetField(ref _search, value)) Rebuild(); } }
    public int KindIndex { get => _kindIndex; set { if (SetField(ref _kindIndex, value)) Rebuild(); } }
    public IReadOnlyList<ActionItemView> Items { get => _items; private set => SetField(ref _items, value); }
    public IReadOnlyList<Meeting> Meetings => _store.Meetings.OrderByDescending(m => m.StartedAt).ToList();
    public string CountText => $"{Items.Count(i => !i.Done)} open · {Items.Count(i => i.Done)} completed";

    public ActionItemsViewModel(ActionItemStore store, IActionItemExtractor? extractor = null, Func<DateOnly>? today = null)
    {
        _store = store;
        _extractor = extractor ?? new UnavailableExtractor();
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        _store.Changed += Rebuild;
        Rebuild();
    }

    public ActionItemFilter Filter
    {
        get => _filter;
        set { if (SetField(ref _filter, value)) Rebuild(); }
    }

    public IReadOnlyList<MeetingGroup> Groups { get => _groups; private set => SetField(ref _groups, value); }

    public bool HasMeetings => _store.Meetings.Count > 0;
    public bool ExtractionAvailable => _extractor.IsAvailable;
    public string ExtractionStatus => _extractor.IsAvailable ? "" : _extractor.UnavailableReason;

    public Meeting AddMeeting(string title) => _store.AddMeeting(title);

    /// <summary>Adds an item typed by the user. On the "Assigned to me" view it is assigned to the user so it stays visible; on "All" it is unassigned.</summary>
    public ActionItem AddItem(Guid meetingId, string text)
        => _store.AddItem(meetingId, text, _filter == ActionItemFilter.AssignedToMe ? ActionItem.Me : "");

    public void RemoveItem(Guid id) => _store.Remove(id);
    public void EditItem(Guid id, string text, string assignee, DateOnly? due, string kind, string dueText = "") => _store.EditItem(id, text, assignee, due, kind, dueText);

    public string ExportMarkdown()
    {
        var text = new System.Text.StringBuilder("# " + (Filter == ActionItemFilter.AssignedToMe ? "Assigned to me" : "All action items") + "\n\n");
        foreach (var item in Items)
        {
            text.AppendLine($"- [{(item.Done ? "x" : " ")}] **{item.Kind}:** {item.Text}");
            text.AppendLine($"  - Meeting: {item.MeetingTitle} · Assigned to: {item.AssigneeText}" + (item.HasDue ? " · " + item.DueText : ""));
            if (item.Evidence.Length > 0) text.AppendLine("  - Source: " + item.Evidence);
        }
        return text.ToString();
    }

    public string ExportCsv()
    {
        static string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "Type,Text,Meeting,Assignee,Due,Completed,Source" };
        lines.AddRange(Items.Select(i => string.Join(",", new[] { i.Kind, i.Text, i.MeetingTitle, i.AssigneeText, i.Item.Due?.ToString("yyyy-MM-dd") ?? i.Item.DueTextSource, i.Done ? "Yes" : "No", i.Evidence }.Select(Cell))));
        return string.Join("\r\n", lines);
    }

    /// <summary>Extracts items from a transcript into a meeting. Throws <see cref="NotSupportedException"/> when no extractor is configured.</summary>
    public async Task<int> ExtractAsync(Guid meetingId, string transcript, CancellationToken ct = default)
    {
        if (!_extractor.IsAvailable) throw new NotSupportedException(_extractor.UnavailableReason);
        var found = await _extractor.ExtractAsync(transcript, ct);
        foreach (var f in found) _store.AddItem(meetingId, f.Text, f.Assignee ?? "", f.Due);
        return found.Count;
    }

    private void Rebuild()
    {
        var items = _store.Items;
        var groups = new List<MeetingGroup>();
        foreach (var m in _store.Meetings.OrderByDescending(m => m.StartedAt))
        {
            var mine = items.Where(i => i.MeetingId == m.Id)
                .Where(i => _filter == ActionItemFilter.All || i.Assignee == ActionItem.Me)
                .OrderBy(i => i.Done).ThenBy(i => i.CapturedAt)
                .Select(i => new ActionItemView(i, _store, _today))
                .ToList();
            // An empty meeting is still listed on "All" so items can be added to it.
            if (mine.Count > 0 || _filter == ActionItemFilter.All) groups.Add(new MeetingGroup(m, mine));
        }
        Groups = groups;
        var kinds = new[] { "", "Task", "Decision", "Follow-up" };
        var selectedKind = kinds[Math.Clamp(KindIndex, 0, 3)];
        Items = items.Where(i => Filter == ActionItemFilter.All || i.Assignee == ActionItem.Me)
            .Where(i => selectedKind.Length == 0 || i.Kind == selectedKind)
            .Select(i => new ActionItemView(i, _store, _today))
            .Where(i => string.IsNullOrWhiteSpace(Search) || (i.Text + " " + i.MeetingTitle + " " + i.AssigneeText + " " + i.Evidence).Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Done).ThenBy(i => i.Item.Due ?? DateOnly.MaxValue).ThenByDescending(i => i.Item.CapturedAt).ToList();
        OnPropertyChanged(nameof(HasMeetings));
        OnPropertyChanged(nameof(Meetings));
        OnPropertyChanged(nameof(CountText));
    }
}
