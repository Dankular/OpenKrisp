namespace OpenKrisp.Core.Notes;

/// <summary>ASR window boundaries and sentence punctuation are not speaker changes.</summary>
public static class TranscriptParagraphs
{
    public static IReadOnlyList<TranscriptTurn> Combine(IReadOnlyList<TranscriptTurn> turns)
    {
        var paragraphs = new List<TranscriptTurn>();
        foreach (var turn in turns)
        {
            if (paragraphs.Count > 0 && paragraphs[^1].Speaker == turn.Speaker && !turn.ParagraphStart)
            {
                var previous = paragraphs[^1];
                paragraphs[^1] = previous with { End = Math.Max(previous.End, turn.End), Text = previous.Text.TrimEnd() + " " + turn.Text.TrimStart() };
            }
            else paragraphs.Add(turn);
        }
        return paragraphs;
    }
}
