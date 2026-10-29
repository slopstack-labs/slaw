using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Step-level edits shared by every surface that lets a human touch a pattern —
/// a practice slaw tolerates for now. Each method returns a description of what
/// happened for the co-pilot to have opinions about, or null if nothing changed.
/// </summary>
internal static class PatternEditingOperations
{
    public static string? Toggle(SlawTrack track, SlawClip clip, int stepIndex)
    {
        var currentStepEvent = NotationResolver.ResolveOrRest(clip.Pattern[stepIndex]);
        if (currentStepEvent.Kind == StepEventKind.Trigger)
        {
            clip.Pattern[stepIndex] = NotationResolver.RestSymbol;
            return $"removed a note from {track.Name}";
        }
        if (InstrumentVoiceSynthesizer.IsPercussive(track.Instrument))
        {
            clip.Pattern[stepIndex] = NotationResolver.TriggerSymbol;
            return $"added a hit to {track.Name}";
        }
        // Re-use the nearest preceding pitch so melodic entry stays in register.
        var precedingPitch = clip.Pattern
            .Take(stepIndex)
            .Reverse()
            .Select(NotationResolver.ResolveOrRest)
            .FirstOrDefault(stepEvent => stepEvent.Kind == StepEventKind.Trigger);
        clip.Pattern[stepIndex] = precedingPitch.Kind == StepEventKind.Trigger
            ? NotationResolver.FormatChord(precedingPitch.MidiNotes)
            : NotationResolver.FormatMidiNote(NotationResolver.DefaultTriggerMidiNote);
        return $"added a note to {track.Name}";
    }

    public static string? Clear(SlawTrack track, SlawClip clip, int stepIndex)
    {
        if (NotationResolver.ResolveOrRest(clip.Pattern[stepIndex]).Kind == StepEventKind.Rest)
        {
            return null;
        }
        clip.Pattern[stepIndex] = NotationResolver.RestSymbol;
        return $"cleared a step on {track.Name}";
    }

    public static string? Sustain(SlawTrack track, SlawClip clip, int stepIndex)
    {
        if (InstrumentVoiceSynthesizer.IsPercussive(track.Instrument) || stepIndex == 0)
        {
            return null;
        }
        clip.Pattern[stepIndex] = NotationResolver.SustainSymbol;
        return $"sustained a note on {track.Name}";
    }

    public static string? Transpose(SlawTrack track, SlawClip clip, int stepIndex, int semitoneDelta)
    {
        if (InstrumentVoiceSynthesizer.IsPercussive(track.Instrument))
        {
            return null;
        }
        var currentStepEvent = NotationResolver.ResolveOrRest(clip.Pattern[stepIndex]);
        if (currentStepEvent.Kind != StepEventKind.Trigger)
        {
            return null;
        }
        clip.Pattern[stepIndex] = NotationResolver.FormatChord(
            currentStepEvent.MidiNotes.Select(midiNote => Math.Clamp(midiNote + semitoneDelta, 12, 120)));
        return $"transposed a note on {track.Name} by {semitoneDelta:+0;-0} semitones";
    }

    /// <summary>Switches instrument and re-canonicalizes the pattern for drum/melodic semantics.</summary>
    public static void ChangeInstrument(SlawTrack track, string instrumentName)
    {
        track.Instrument = ProjectAlignmentRemediator.ResolveInstrument(instrumentName);
        var percussiveInstrument = InstrumentVoiceSynthesizer.IsPercussive(track.Instrument);
        foreach (var clip in track.Clips.OfType<SlawClip>())
        {
            for (var stepIndex = 0; stepIndex < clip.Pattern.Count; stepIndex++)
            {
                clip.Pattern[stepIndex] = NotationResolver.Canonicalize(clip.Pattern[stepIndex], percussiveInstrument);
            }
        }
    }

    /// <summary>Resizes a clip, repeating its content when it grows so the groove survives.</summary>
    public static string ResizeClip(SlawClip clip, int requestedStepCount)
    {
        var originalPattern = clip.Pattern.ToList();
        clip.Pattern = Enumerable.Range(0, requestedStepCount)
            .Select(stepIndex => originalPattern.Count == 0 ? NotationResolver.RestSymbol : originalPattern[stepIndex % originalPattern.Count])
            .ToList();
        return $"resized \"{clip.Name}\" to {requestedStepCount} steps";
    }
}
