using OpenKrisp.Core.Common;

namespace OpenKrisp.Core.Notes;

public sealed class ActionItemData
{
    public List<Meeting> Meetings { get; set; } = new();
    public List<ActionItem> Items { get; set; } = new();
}

/// <summary>JSON-file backed store of meetings and their action items. Every mutation is saved immediately.</summary>
public sealed class ActionItemStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ActionItemData _data;

    public ActionItemStore(string? path = null)
    {
        _path = path ?? AppPaths.ActionItemsFile;
        _data = JsonFile.Load<ActionItemData>(_path) ?? new ActionItemData();
    }

    public event Action? Changed;

    public IReadOnlyList<Meeting> Meetings { get { lock (_gate) return _data.Meetings.ToList(); } }
    public IReadOnlyList<ActionItem> Items { get { lock (_gate) return _data.Items.ToList(); } }

    public Meeting AddMeeting(string title, DateTimeOffset? startedAt = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A meeting needs a title.", nameof(title));
        var m = new Meeting { Title = title.Trim(), StartedAt = startedAt ?? DateTimeOffset.Now };
        Mutate(() => _data.Meetings.Add(m));
        return m;
    }

    public ActionItem AddItem(Guid meetingId, string text, string assignee = "", DateOnly? due = null, DateTimeOffset? capturedAt = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("An action item needs text.", nameof(text));
        var item = new ActionItem
        {
            MeetingId = meetingId, Text = text.Trim(), Assignee = assignee, Due = due, CapturedAt = capturedAt ?? DateTimeOffset.Now,
        };
        Mutate(() =>
        {
            if (_data.Meetings.All(m => m.Id != meetingId)) throw new ArgumentException("Unknown meeting.", nameof(meetingId));
            _data.Items.Add(item);
        });
        return item;
    }

    public bool SetDone(Guid id, bool done) => Update(id, i => i.Done = done);
    public bool SetDue(Guid id, DateOnly? due) => Update(id, i => i.Due = due);
    public bool SetAssignee(Guid id, string assignee) => Update(id, i => i.Assignee = assignee);

    public bool Remove(Guid id)
    {
        var removed = false;
        Mutate(() => removed = _data.Items.RemoveAll(i => i.Id == id) > 0);
        return removed;
    }

    private bool Update(Guid id, Action<ActionItem> change)
    {
        var found = false;
        Mutate(() =>
        {
            var item = _data.Items.FirstOrDefault(i => i.Id == id);
            if (item is null) return;
            change(item);
            found = true;
        });
        return found;
    }

    private void Mutate(Action change)
    {
        lock (_gate)
        {
            change();
            JsonFile.Save(_path, _data);
        }
        Changed?.Invoke();
    }
}
