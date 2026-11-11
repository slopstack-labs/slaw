using System.Globalization;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Whole-pattern moves a groovebox keeps one button away: shift the pattern around
/// its loop, spread hits evenly (Euclidean), roll the dice, wipe it. Like
/// <see cref="PatternEditingOperations"/>, each returns a description of what
/// changed, or null when the pattern stayed exactly as it was. Clip envelopes are
/// left where they are: they describe motion over the loop, not a single step.
/// </summary>
internal static class GrooveboxPatternOperations
{
    /// <summary>Steps on one groovebox page: one bar of sixteenths.</summary>
    public const int PageLength = 16;

    /// <summary>The lengths a groovebox row offers, the same ones the clip editor does.</summary>
    public static readonly IReadOnlyList<int> LengthChoices = new[] { 4, 8, 12, 16, 24, 32, 48, 64 };

    /// <summary>How many groovebox pages a pattern of <paramref name="stepCount"/> steps needs (at least one).</summary>
    public static int PageCount(int stepCount) => Math.Max(1, (stepCount + PageLength - 1) / PageLength);

    /// <summary>
    /// Moves every step <paramref name="stepOffset"/> steps later (negative: earlier), wrapping
    /// around the loop. A held note that wraps past the start becomes a rest there, because a
    /// loop's first step has nothing earlier to hold.
    /// </summary>
    public static string? Rotate(SlawTrack track, SlawClip clip, int stepOffset)
    {
        var stepCount = clip.Pattern.Count;
        if (stepCount < 2 || IsSilent(clip))
        {
            return null;
        }
        var normalizedOffset = ((stepOffset % stepCount) + stepCount) % stepCount;
        if (normalizedOffset == 0)
        {
            return null;
        }
        var originalPattern = clip.Pattern.ToList();
        for (var stepIndex = 0; stepIndex < stepCount; stepIndex++)
        {
            clip.Pattern[(stepIndex + normalizedOffset) % stepCount] = originalPattern[stepIndex];
        }
        RestLeadingSustains(clip.Pattern);
        var stepWord = Math.Abs(stepOffset) == 1 ? "step" : "steps";
        return string.Create(CultureInfo.InvariantCulture,
            $"shifted the {track.Name} pattern {(stepOffset > 0 ? "later" : "earlier")} by {Math.Abs(stepOffset)} {stepWord}");
    }

    /// <summary>
    /// Spreads <paramref name="hitCount"/> hits as evenly as the clip's length allows, the first
    /// on step 1 (three over eight is the tresillo). A step that already had a hit keeps its pitch
    /// and velocity; new hits play the clip's first note (a drum hit on drum tracks).
    /// </summary>
    public static string? EuclideanFill(SlawTrack track, SlawClip clip, int hitCount)
    {
        var stepCount = clip.Pattern.Count;
        if (stepCount == 0)
        {
            return null;
        }
        var clampedHits = Math.Clamp(hitCount, 0, stepCount);
        var newHitNotation = HitNotationFor(track, clip);
        var originalPattern = clip.Pattern.ToList();
        for (var stepIndex = 0; stepIndex < stepCount; stepIndex++)
        {
            var isHit = IsEuclideanHit(stepIndex, clampedHits, stepCount);
            var existingStep = NotationResolver.ResolveOrRest(originalPattern[stepIndex]);
            clip.Pattern[stepIndex] = !isHit ? NotationResolver.RestSymbol
                : existingStep.Kind == StepEventKind.Trigger ? originalPattern[stepIndex]
                : newHitNotation;
        }
        if (clip.Pattern.SequenceEqual(originalPattern))
        {
            return null;
        }
        return string.Create(CultureInfo.InvariantCulture, $"spread {clampedHits} hits evenly over {stepCount} steps on {track.Name}");
    }

    /// <summary>Whether step <paramref name="stepIndex"/> carries a hit in the Euclidean rhythm of <paramref name="hitCount"/> over <paramref name="stepCount"/>.</summary>
    public static bool IsEuclideanHit(int stepIndex, int hitCount, int stepCount) =>
        hitCount > 0 && stepCount > 0 && (int)((long)stepIndex * hitCount % stepCount) < hitCount;

    /// <summary>
    /// Rolls a new pattern: each step is a hit with probability <paramref name="density"/>. Drum
    /// hits get an accent or a ghost note; melodic hits only use pitches the clip already
    /// plays (the default note when it plays none), so the result stays in the song's key.
    /// </summary>
    public static string? Randomize(SlawTrack track, SlawClip clip, double density, Random random)
    {
        var stepCount = clip.Pattern.Count;
        if (stepCount == 0)
        {
            return null;
        }
        var percussiveInstrument = InstrumentVoiceSynthesizer.IsPercussive(track.Instrument);
        var pitchPalette = clip.Pattern
            .Select(NotationResolver.ResolveOrRest)
            .Where(stepEvent => stepEvent.Kind == StepEventKind.Trigger)
            .Select(stepEvent => NotationResolver.FormatChord(stepEvent.MidiNotes))
            .Distinct()
            .ToList();
        if (pitchPalette.Count == 0)
        {
            pitchPalette.Add(NotationResolver.FormatMidiNote(NotationResolver.DefaultTriggerMidiNote));
        }
        var clampedDensity = Math.Clamp(density, 0, 1);
        for (var stepIndex = 0; stepIndex < stepCount; stepIndex++)
        {
            if (random.NextDouble() >= clampedDensity)
            {
                clip.Pattern[stepIndex] = NotationResolver.RestSymbol;
                continue;
            }
            clip.Pattern[stepIndex] = percussiveInstrument
                ? NotationResolver.FormatTrigger(random.NextDouble() < 0.25 ? 0.6 : 1)
                : pitchPalette[random.Next(pitchPalette.Count)];
        }
        var hitCount = clip.Pattern.Count(stepNotation => NotationResolver.ResolveOrRest(stepNotation).Kind == StepEventKind.Trigger);
        return string.Create(CultureInfo.InvariantCulture, $"rolled a random pattern on {track.Name}: {hitCount} hits over {stepCount} steps");
    }

    /// <summary>Turns every step into a rest; the clip and its length stay.</summary>
    public static string? Clear(SlawTrack track, SlawClip clip)
    {
        if (clip.Pattern.All(stepNotation => stepNotation == NotationResolver.RestSymbol))
        {
            return null;
        }
        for (var stepIndex = 0; stepIndex < clip.Pattern.Count; stepIndex++)
        {
            clip.Pattern[stepIndex] = NotationResolver.RestSymbol;
        }
        return $"cleared the {track.Name} pattern";
    }

    /// <summary>
    /// Sets the clip's length in steps, repeating its content when it grows (a groovebox's
    /// per-track length, for polymeters). Null when it already has that length. A session clip's
    /// envelopes live on its pattern, so <paramref name="sessionClip"/> drops points past the new
    /// end; an arrangement clip's envelopes span the clip, not the pattern, and stay.
    /// </summary>
    public static string? SetLength(SlawTrack track, SlawClip clip, int requestedStepCount, bool sessionClip = true)
    {
        var clampedStepCount = Math.Clamp(requestedStepCount, 1, ProjectAlignmentRemediator.MaximumStepCount);
        if (clampedStepCount == clip.Pattern.Count)
        {
            return null;
        }
        PatternEditingOperations.ResizeClip(clip, clampedStepCount);
        if (sessionClip)
        {
            RemoveEnvelopePointsPast(clip, clampedStepCount);
        }
        return string.Create(CultureInfo.InvariantCulture, $"set the {track.Name} pattern to {clampedStepCount} steps");
    }

    private static bool IsSilent(SlawClip clip) =>
        clip.Pattern.All(stepNotation => NotationResolver.ResolveOrRest(stepNotation).Kind == StepEventKind.Rest);

    /// <summary>What a new hit on this track writes: a drum trigger, or the clip's first note.</summary>
    private static string HitNotationFor(SlawTrack track, SlawClip clip)
    {
        if (InstrumentVoiceSynthesizer.IsPercussive(track.Instrument))
        {
            return NotationResolver.TriggerSymbol;
        }
        var firstTrigger = clip.Pattern
            .Select(NotationResolver.ResolveOrRest)
            .FirstOrDefault(stepEvent => stepEvent.Kind == StepEventKind.Trigger);
        return firstTrigger.Kind == StepEventKind.Trigger
            ? NotationResolver.FormatChord(firstTrigger.MidiNotes)
            : NotationResolver.FormatMidiNote(NotationResolver.DefaultTriggerMidiNote);
    }

    private static void RestLeadingSustains(List<string> pattern)
    {
        for (var stepIndex = 0; stepIndex < pattern.Count; stepIndex++)
        {
            if (NotationResolver.ResolveOrRest(pattern[stepIndex]).Kind != StepEventKind.Sustain)
            {
                return;
            }
            pattern[stepIndex] = NotationResolver.RestSymbol;
        }
    }

    /// <summary>A session clip's envelope lives on 0..its length; a shorter clip drops what fell off the end.</summary>
    private static void RemoveEnvelopePointsPast(SlawClip clip, int stepCount)
    {
        if (clip.Automation is null)
        {
            return;
        }
        foreach (var targetKey in clip.Automation.Keys.ToList())
        {
            var keptPoints = clip.Automation[targetKey].Where(point => point.Step <= stepCount).ToList();
            if (keptPoints.Count == 0)
            {
                clip.Automation.Remove(targetKey);
            }
            else
            {
                clip.Automation[targetKey] = keptPoints;
            }
        }
        if (clip.Automation.Count == 0)
        {
            clip.Automation = null;
        }
    }
}
