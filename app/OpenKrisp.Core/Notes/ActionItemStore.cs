using OpenKrisp.Core.Common;

namespace OpenKrisp.Core.Notes;

public sealed class ActionItemData
{
    public List<Meeting> Meetings { get; set; } = new();
    public List<ActionItem> Items { get; set; } = new();
    public int ExtractionVersion { get; set; }
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

    public void InitializeAutomaticExtraction()
    {
        if (_data.ExtractionVersion >= 1) return;
        lock (_gate)
        {
            if (File.Exists(_path) && !File.Exists(_path + ".before-automatic-extraction")) File.Copy(_path, _path + ".before-automatic-extraction");
            foreach (var meeting in _data.Meetings) MergeInsights(meeting, MeetingInsightExtractor.Extract(meeting, meeting.Transcript), replace: false);
            _data.ExtractionVersion = 1;
            JsonFile.Save(_path, _data);
        }
    }

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

    private static string SentenceTitle(string title)
    {
        var line = title.ReplaceLineEndings(" ").Trim().Trim('"');
        line = new System.Text.RegularExpressions.Regex(@"(?<=[.!?])\s+(?=[A-Z])").Split(line, 2)[0];
        if (line.Length > 197) line = line[..197].TrimEnd() + "...";
        if (line.Length > 0 && !".!?".Contains(line[^1])) line += ".";
        return line;
    }

    public void SetTitle(Guid meetingId, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        Mutate(() => _data.Meetings.Single(m => m.Id == meetingId).Title = SentenceTitle(title));
    }

    public void SetOrganization(Guid meetingId, string kind, string tags)
    {
        Mutate(() =>
        {
            var meeting = _data.Meetings.Single(m => m.Id == meetingId);
            meeting.Kind = kind == "Lecture" ? "Lecture" : "Meeting";
            meeting.Tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        });
    }

    public void SaveTranscript(Guid meetingId, IReadOnlyList<TranscriptTurn> turns, string audioPath)
    {
        if (turns.Any(t => !double.IsFinite(t.Start) || !double.IsFinite(t.End) || t.Start < 0 || t.End < t.Start
            || string.IsNullOrWhiteSpace(t.Text) || string.IsNullOrWhiteSpace(t.Speaker)))
            throw new ArgumentException("Invalid transcript turns.", nameof(turns));
        Mutate(() =>
        {
            var meeting = _data.Meetings.Single(m => m.Id == meetingId);
            if (meeting.Transcript.Count > 0)
            {
                meeting.RunningSummary = ""; meeting.RunningKeyPoints.Clear(); meeting.RunningSummaryThrough = 0;
            }
            meeting.Transcript = turns.OrderBy(t => t.Start).ToList();
            var paragraphs = TranscriptParagraphs.Combine(meeting.Transcript);
            meeting.Translations = meeting.Translations.Where(a => paragraphs.Any(t => t.End >= a.Start && t.Start <= a.End
                && t.Text.Contains(a.Text, StringComparison.OrdinalIgnoreCase))).ToList();
            meeting.TranscriptRevision++;
            meeting.AudioPath = audioPath;
            meeting.Summary = "";
            meeting.KeyPoints.Clear();
            meeting.Topics.Clear();
            meeting.Decisions.Clear(); meeting.OpenQuestions.Clear(); meeting.Risks.Clear();
            meeting.SummaryModel = ""; meeting.SummaryWorkingContext = meeting.SummarySourceSections = 0;
            meeting.SummarizedAt = null;
            MergeInsights(meeting, MeetingInsightExtractor.Extract(meeting, meeting.Transcript), replace: true);
        });
    }

    public void SaveRunningNotes(Guid meetingId, LiveNotesUpdate update)
    {
        Mutate(() =>
        {
            var meeting = _data.Meetings.Single(m => m.Id == meetingId);
            meeting.RunningSummary = update.Summary;
            meeting.RunningKeyPoints = update.KeyPoints.ToList();
            meeting.RunningSummaryThrough = update.ThroughSeconds;
            meeting.Translations = update.Translations.ToList();
        });
    }

    public void SaveSummary(Guid meetingId, int transcriptRevision, MeetingSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.Summary)) throw new ArgumentException("The model returned an empty summary.");
        Mutate(() =>
        {
            var meeting = _data.Meetings.Single(m => m.Id == meetingId);
            if (meeting.TranscriptRevision != transcriptRevision) throw new InvalidOperationException("The transcript changed; summarize it again.");
            var actions = new List<ActionItem>();
            foreach (var action in summary.ActionItems.Where(a => !string.IsNullOrWhiteSpace(a.Text)))
            {
                // Keep only commitments supported by a literal quote from this transcript.
                if (string.IsNullOrWhiteSpace(action.Evidence) || !meeting.Transcript.Any(t => t.Text.Contains(action.Evidence, StringComparison.OrdinalIgnoreCase))) continue;
                if (actions.Any(a => string.Equals(a.Text, action.Text.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
                var previous = _data.Items.FirstOrDefault(i => i.MeetingId == meetingId && i.GeneratedFromTranscript && !actions.Any(a => a.Id == i.Id)
                    && (string.Equals(i.Text, action.Text.Trim(), StringComparison.OrdinalIgnoreCase) || EvidenceMatches(i.Evidence, action.Evidence)));
                DateOnly? due = DateOnly.TryParseExact(action.Due, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date) && action.Evidence.Contains(action.Due) ? date : null;
                var source = meeting.Transcript.First(t => t.Text.Contains(action.Evidence, StringComparison.OrdinalIgnoreCase));
                if (previous?.UserEdited == true) { actions.Add(previous); continue; }
                if (meeting.SuppressedActionEvidence.Any(q => EvidenceMatches(q, action.Evidence))) continue;
                actions.Add(new ActionItem {
                    Id = previous?.Id ?? Guid.NewGuid(), MeetingId = meetingId, Text = action.Text.Trim(),
                    Assignee = !string.IsNullOrWhiteSpace(action.Assignee) && meeting.Transcript.Any(t =>
                        string.Equals(t.Speaker, action.Assignee, StringComparison.OrdinalIgnoreCase) || t.Text.Contains(action.Assignee, StringComparison.OrdinalIgnoreCase))
                        ? action.Assignee : "", Due = due, Evidence = action.Evidence,
                    CapturedAt = meeting.StartedAt.AddSeconds(source.Start), GeneratedFromTranscript = true, Done = previous?.Done ?? false,
                    Kind = action.Kind is "Decision" or "Follow-up" ? action.Kind : "Task",
                    DueTextSource = !string.IsNullOrWhiteSpace(action.DueText) && action.Evidence.Contains(action.DueText, StringComparison.OrdinalIgnoreCase) ? action.DueText.Trim() : "",
                });
            }
            // Replace draft extraction with the model's refined task wording, preserving user edits and completion IDs.
            _data.Items.RemoveAll(i => i.MeetingId == meetingId && i.GeneratedFromTranscript && !i.UserEdited
                && (!i.Done || actions.Any(a => a.Id == i.Id)));
            foreach (var action in actions)
                if (_data.Items.All(i => i.Id != action.Id)) _data.Items.Add(action);
            meeting.Summary = summary.Summary;
            meeting.KeyPoints = summary.KeyPoints.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            meeting.Topics = summary.Topics;
            meeting.Decisions = summary.Decisions;
            meeting.OpenQuestions = summary.OpenQuestions;
            meeting.Risks = summary.Risks;
            meeting.SummaryModel = summary.Model;
            meeting.SummaryWorkingContext = summary.WorkingContext;
            meeting.SummarySourceSections = summary.SourceSections;
            meeting.SummarizedAt = DateTimeOffset.Now;
        });
    }

    public bool SetDone(Guid id, bool done) => Update(id, i => i.Done = done);
    public bool SetDue(Guid id, DateOnly? due) => Update(id, i => { i.Due = due; i.DueTextSource = ""; i.UserEdited = true; });
    public bool SetAssignee(Guid id, string assignee) => Update(id, i => { i.Assignee = assignee.Trim(); i.UserEdited = true; });

    public bool EditItem(Guid id, string text, string assignee, DateOnly? due, string kind, string dueText = "")
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("An item needs text.");
        if (kind is not ("Task" or "Decision" or "Follow-up")) throw new ArgumentException("Unknown item type.");
        return Update(id, i => { i.Text = text.Trim(); i.Assignee = assignee.Trim(); i.Due = due; i.Kind = kind; i.DueTextSource = dueText.Trim(); i.UserEdited = true; });
    }

    public void UpdateLiveInsights(Guid meetingId, IReadOnlyList<TranscriptTurn> turns)
    {
        lock (_gate)
        {
            var meeting = _data.Meetings.Single(m => m.Id == meetingId);
            var found = MeetingInsightExtractor.Extract(meeting, turns, completeSentencesOnly: true);
            if (found.All(f => _data.Items.Any(i => i.MeetingId == meetingId && SameEvidence(i, f)) || meeting.SuppressedActionEvidence.Contains(f.Evidence, StringComparer.OrdinalIgnoreCase))) return;
            MergeInsights(meeting, found, replace: false);
            JsonFile.Save(_path, _data);
        }
        Changed?.Invoke();
    }

    private static bool SameEvidence(ActionItem a, ActionItem b) => a.Evidence.Length > 0 && string.Equals(a.Evidence, b.Evidence, StringComparison.OrdinalIgnoreCase);
    private static bool EvidenceMatches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || (Math.Min(a.Length, b.Length) >= 16 && (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)));

    private void MergeInsights(Meeting meeting, IReadOnlyList<ActionItem> candidates, bool replace)
    {
        var old = _data.Items.Where(i => i.MeetingId == meeting.Id && i.GeneratedFromTranscript).ToList();
        if (replace) _data.Items.RemoveAll(i => i.MeetingId == meeting.Id && i.GeneratedFromTranscript && !i.UserEdited);
        foreach (var item in candidates)
        {
            if (meeting.SuppressedActionEvidence.Contains(item.Evidence, StringComparer.OrdinalIgnoreCase)) continue;
            var previous = old.FirstOrDefault(i => SameEvidence(i, item));
            if (_data.Items.Any(i => i.MeetingId == meeting.Id && SameEvidence(i, item))) continue;
            if (previous is not null) { item.Id = previous.Id; item.Done = previous.Done; }
            _data.Items.Add(item);
        }
    }

    public bool Remove(Guid id)
    {
        var removed = false;
        Mutate(() => {
            var item = _data.Items.FirstOrDefault(i => i.Id == id);
            if (item is { GeneratedFromTranscript: true } && item.Evidence.Length > 0)
                _data.Meetings.Single(m => m.Id == item.MeetingId).SuppressedActionEvidence.Add(item.Evidence);
            removed = _data.Items.RemoveAll(i => i.Id == id) > 0;
        });
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
