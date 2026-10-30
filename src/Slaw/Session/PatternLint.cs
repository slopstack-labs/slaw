namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Patterns that parse fine but probably do not mean what their author meant.
/// Velocity (<c>@0.7</c>) and length (<c>:8</c>) bind to the single note they follow,
/// so <c>A3+C4+E4@0.7</c> softens only E4. Tools that accept patterns report these
/// as warnings rather than guessing: <c>C4:8@0.6+E4</c> (a soft held root under a
/// full third) is legitimate and must keep its meaning.
/// </summary>
internal static class PatternLint
{
    private const int MaximumReportedSteps = 3;

    /// <summary>One warning per suspicious chord step (at most three, then a count), prefixed with <paramref name="context"/>.</summary>
    public static List<string> ChordSuffixWarnings(IReadOnlyList<string> pattern, string context)
    {
        var suspiciousSteps = new List<(int StepIndex, string Step, string Suggestion, string BoundNote, string Suffix)>();
        for (var stepIndex = 0; stepIndex < pattern.Count; stepIndex++)
        {
            if (DescribeTrailingSuffix(pattern[stepIndex]) is { } finding)
            {
                suspiciousSteps.Add((stepIndex, pattern[stepIndex], finding.Suggestion, finding.BoundNote, finding.Suffix));
            }
        }
        var warnings = suspiciousSteps.Take(MaximumReportedSteps)
            .Select(finding =>
                $"{context}step {finding.StepIndex}: \"{finding.Step}\" applies \"{finding.Suffix}\" to {finding.BoundNote} only (velocity @ and length : bind to the note they follow); " +
                $"for the whole chord write \"{finding.Suggestion}\"")
            .ToList();
        if (suspiciousSteps.Count > MaximumReportedSteps)
        {
            warnings.Add($"{context}{suspiciousSteps.Count - MaximumReportedSteps} more chord step(s) like that");
        }
        return warnings;
    }

    /// <summary>
    /// For a chord whose LAST note alone carries a suffix (and no other note has one),
    /// the note it binds to, the suffix, and the step with the suffix on every note; null otherwise.
    /// </summary>
    internal static (string BoundNote, string Suffix, string Suggestion)? DescribeTrailingSuffix(string step)
    {
        var chordNotes = step.Split('+');
        if (chordNotes.Length < 2 || chordNotes.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }
        var lastNote = chordNotes[^1];
        var suffixStart = lastNote.IndexOfAny(new[] { '@', ':' });
        if (suffixStart <= 0 || chordNotes[..^1].Any(chordNote => chordNote.IndexOfAny(new[] { '@', ':' }) >= 0))
        {
            return null;
        }
        var suffix = lastNote[suffixStart..];
        return (lastNote[..suffixStart], suffix, string.Join('+', chordNotes.Select(chordNote => chordNote.Contains('@') || chordNote.Contains(':') ? chordNote : chordNote + suffix)));
    }
}
