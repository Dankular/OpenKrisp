using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenKrisp.Core.Notes;

/// <summary>Conservative English commitment extraction; every result retains a literal source quote.</summary>
public static class MeetingInsightExtractor
{
    private static readonly Regex Split = new(@"(?<=[.!?])\s+|[\r\n]+", RegexOptions.Compiled);
    private static readonly Regex Negative = new(@"\b(?:won't|will not|would be|would have|could potentially|might|maybe|hypothetically|don't need to|do not need to|not going to)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Commitment = new(@"\b(?:I(?:'ll| will| am going to| need to)|we(?:'ll| will| need to)|let's|please|(?:can|could|will|would) you)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Decision = new(@"\b(?:we (?:have )?(?:decided|agreed|approved)|(?:the )?decision is|we are going (?:with|ahead with)|let's go with)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FollowUp = new(@"\b(?:follow[ -]?up|check (?:back|in)|circle back|touch base|report back|get back to|revisit)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FirstPerson = new(@"\bI(?:'ll| will| am going to| need to)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Date = new(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);

    public static IReadOnlyList<ActionItem> Extract(Meeting meeting, IReadOnlyList<TranscriptTurn> turns, bool completeSentencesOnly = false)
    {
        var found = new List<ActionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var turn in turns)
        foreach (var part in Split.Split(turn.Text.Replace('’', '\'')))
        {
            var text = part.Trim();
            if (text.Length < 10 || (completeSentencesOnly && !".!?".Contains(text[^1])) || Negative.IsMatch(text)) continue;
            var kind = Decision.IsMatch(text) ? "Decision" : FollowUp.IsMatch(text) && Commitment.IsMatch(text) ? "Follow-up" : Commitment.IsMatch(text) ? "Task" : "";
            if (kind.Length == 0 || !seen.Add(text)) continue;
            // Keep the exact original spelling for evidence validation, including curly apostrophes.
            var evidence = turn.Text.Contains(text, StringComparison.Ordinal) ? text : text.Replace('\'', '’');
            if (!turn.Text.Contains(evidence, StringComparison.Ordinal)) continue;
            var date = Date.Match(text).Value;
            found.Add(new ActionItem {
                MeetingId = meeting.Id, Text = evidence, Evidence = evidence, Kind = kind,
                Assignee = kind != "Decision" && FirstPerson.IsMatch(text) ? turn.Speaker : "",
                Due = DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due) ? due : null,
                CapturedAt = meeting.StartedAt.AddSeconds(turn.Start), GeneratedFromTranscript = true,
            });
        }
        return found;
    }
}
