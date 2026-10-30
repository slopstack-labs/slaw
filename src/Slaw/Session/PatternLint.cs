namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Patterns that parse fine but probably do not mean what their author meant.
/// Velocity (<c>@0.7</c>) and length (<c>:8</c>) bind to the single note they follow,
/// so <c>A3+C4+E4@0.7</c> softens only E4. Tools that accept patterns report these
/// as warnings rather than guessing: <c>C4:8@0.6+E4</c> (a soft held root under a
/// full third) is legitimate and must keep its meaning. A ratchet (<c>*3</c>) is the
/// opposite: it always repeats the whole step, so one written on an inner chord note,
/// or on several, reads as per-note but is not.
/// </summary>
internal static class PatternLint
{
    private const int MaximumReportedSteps = 3;

    private static readonly char[] NoteSuffixSeparators = { '@', ':' };

    /// <summary>
    /// One warning per suspicious step (at most three of each kind, then a count), prefixed with <paramref name="context"/>:
    /// chord suffixes that bind to one note only, and ratchets that are misplaced or too large.
    /// </summary>
    public static List<string> ChordSuffixWarnings(IReadOnlyList<string> pattern, string context)
    {
        var suspiciousSteps = new List<(int StepIndex, string Step, string Suggestion, string BoundNote, string Suffix)>();
        var ratchetFindings = new List<(int StepIndex, string Step, string Problem)>();
        for (var stepIndex = 0; stepIndex < pattern.Count; stepIndex++)
        {
            if (DescribeTrailingSuffix(pattern[stepIndex]) is { } finding)
            {
                suspiciousSteps.Add((stepIndex, pattern[stepIndex], finding.Suggestion, finding.BoundNote, finding.Suffix));
            }
            if (DescribeRatchetProblem(pattern[stepIndex]) is { } ratchetProblem)
            {
                ratchetFindings.Add((stepIndex, pattern[stepIndex], ratchetProblem));
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
        warnings.AddRange(ratchetFindings.Take(MaximumReportedSteps)
            .Select(finding => $"{context}step {finding.StepIndex}: \"{finding.Step}\" {finding.Problem}"));
        if (ratchetFindings.Count > MaximumReportedSteps)
        {
            warnings.Add($"{context}{ratchetFindings.Count - MaximumReportedSteps} more ratchet step(s) like that");
        }
        return warnings;
    }

    /// <summary>
    /// For a chord whose LAST note alone carries a suffix (and no other note has one),
    /// the note it binds to, the suffix, and the step with the suffix on every note; null otherwise.
    /// A step-wide ratchet ("*2") is not a note suffix: it is set aside and kept at the end of the suggestion.
    /// </summary>
    internal static (string BoundNote, string Suffix, string Suggestion)? DescribeTrailingSuffix(string step)
    {
        var chordNotes = step.Split('+');
        if (chordNotes.Length < 2 || chordNotes.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }
        var ratchetSuffix = string.Empty;
        for (var noteIndex = 0; noteIndex < chordNotes.Length; noteIndex++)
        {
            if (!NotationResolver.TryStripRatchet(chordNotes[noteIndex], out var ratchetlessNote, out var ratchetCount))
            {
                return null;
            }
            chordNotes[noteIndex] = ratchetlessNote;
            if (ratchetCount > 1)
            {
                ratchetSuffix = NotationResolver.FormatRatchetSuffix(ratchetCount);
            }
        }
        var lastNote = chordNotes[^1];
        var suffixStart = lastNote.IndexOfAny(NoteSuffixSeparators);
        if (suffixStart <= 0 || chordNotes[..^1].Any(chordNote => chordNote.IndexOfAny(NoteSuffixSeparators) >= 0))
        {
            return null;
        }
        var suffix = lastNote[suffixStart..];
        return (lastNote[..suffixStart], suffix,
            string.Join('+', chordNotes.Select(chordNote => chordNote.IndexOfAny(NoteSuffixSeparators) >= 0 ? chordNote : chordNote + suffix)) + ratchetSuffix);
    }

    /// <summary>
    /// What is misleading about a step's ratchet, or null: "*N" written on a chord note other
    /// than the last, or on several notes (it always repeats the whole step), or a count above
    /// <see cref="NotationResolver.MaximumRatchetCount"/> (it plays as the maximum).
    /// </summary>
    internal static string? DescribeRatchetProblem(string step)
    {
        if (string.IsNullOrWhiteSpace(step) || step.IndexOf(NotationResolver.RatchetSeparator, 1) < 0)
        {
            return null;
        }
        var chordNotes = step.Trim().Split('+');
        var ratchetedNoteIndices = new List<int>();
        var largestWrittenCount = 0;
        var ratchetlessNotes = new string[chordNotes.Length];
        for (var noteIndex = 0; noteIndex < chordNotes.Length; noteIndex++)
        {
            ratchetlessNotes[noteIndex] = chordNotes[noteIndex];
            var ratchetOffset = chordNotes[noteIndex].Length < 2 ? -1 : chordNotes[noteIndex].IndexOf(NotationResolver.RatchetSeparator, 1);
            if (ratchetOffset < 0)
            {
                continue;
            }
            if (!NotationResolver.TryStripRatchet(chordNotes[noteIndex], out var ratchetlessNote, out _))
            {
                // Malformed ratchets make the step unreadable; the remediator reports those.
                return null;
            }
            ratchetlessNotes[noteIndex] = ratchetlessNote;
            ratchetedNoteIndices.Add(noteIndex);
            var countText = new string(chordNotes[noteIndex][(ratchetOffset + 1)..].TakeWhile(char.IsAsciiDigit).ToArray());
            if (int.TryParse(countText, out var writtenCount))
            {
                largestWrittenCount = Math.Max(largestWrittenCount, writtenCount);
            }
        }
        if (ratchetedNoteIndices.Count == 0)
        {
            return null;
        }
        var playedCount = Math.Min(largestWrittenCount, NotationResolver.MaximumRatchetCount);
        var suggestion = string.Join('+', ratchetlessNotes) + NotationResolver.FormatRatchetSuffix(playedCount);
        var misplaced = chordNotes.Length > 1 && (ratchetedNoteIndices.Count > 1 || ratchetedNoteIndices[0] != chordNotes.Length - 1);
        if (misplaced)
        {
            return $"repeats the WHOLE step {playedCount} times, every note of the chord (\"*N\" is not per note); write it once, at the end: \"{suggestion}\"";
        }
        if (largestWrittenCount > NotationResolver.MaximumRatchetCount)
        {
            return $"plays as \"{suggestion}\": a step holds at most {NotationResolver.MaximumRatchetCount} repeats";
        }
        return null;
    }
}
