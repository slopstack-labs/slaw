namespace SlopstackLabs.Slaw.Session;

/// <summary>A note as it actually sounds: where it starts, for how many steps, and how hard (0..1).</summary>
internal readonly record struct PlacedNote(int StartStep, int MidiNote, int Length, double Velocity = 1);

/// <summary>
/// Per-note editing of step patterns. Steps can say "this chord, held by the
/// following sustains" (the classic form) or "C4:8+E4:2" (each note its own
/// length). Every write goes through <see cref="WriteChord"/>, which picks the
/// classic form whenever it can express the chord, so patterns stay readable
/// for humans and small language models alike.
/// </summary>
internal static class ClipNoteEditor
{
    private const int MaximumLookBehindSteps = 256;

    /// <summary>Length of the sustain run after a trigger: the step itself plus every directly following "_".</summary>
    public static int ImplicitLength(IReadOnlyList<string> pattern, int startStep)
    {
        var endStep = startStep + 1;
        while (endStep < pattern.Count && NotationResolver.ResolveOrRest(pattern[endStep]).Kind == StepEventKind.Sustain)
        {
            endStep++;
        }
        return endStep - startStep;
    }

    /// <summary>The notes that start at <paramref name="startStep"/>, with the lengths they actually play for.</summary>
    public static List<PlacedNote> NotesStartingAt(IReadOnlyList<string> pattern, int startStep)
    {
        var stepEvent = NotationResolver.ResolveOrRest(pattern[startStep]);
        if (stepEvent.Kind != StepEventKind.Trigger)
        {
            return new List<PlacedNote>();
        }
        var implicitLength = ImplicitLength(pattern, startStep);
        return stepEvent.MidiNotes
            .Select((midiNote, noteIndex) => new PlacedNote(startStep, midiNote,
                stepEvent.NoteLengths[noteIndex] > 0 ? stepEvent.NoteLengths[noteIndex] : implicitLength,
                stepEvent.NoteVelocities[noteIndex]))
            .ToList();
    }

    /// <summary>Every note in the pattern, with its real length.</summary>
    public static IEnumerable<PlacedNote> AllNotes(IReadOnlyList<string> pattern) =>
        Enumerable.Range(0, pattern.Count).SelectMany(stepIndex => NotesStartingAt(pattern, stepIndex));

    /// <summary>The note of this pitch sounding at <paramref name="step"/>, if any (it may have started earlier).</summary>
    public static PlacedNote? FindNoteCovering(IReadOnlyList<string> pattern, int step, int midiNote)
    {
        for (var candidateStart = step; candidateStart >= Math.Max(0, step - MaximumLookBehindSteps); candidateStart--)
        {
            foreach (var placedNote in NotesStartingAt(pattern, candidateStart))
            {
                if (placedNote.MidiNote == midiNote && placedNote.StartStep + placedNote.Length > step)
                {
                    return placedNote;
                }
            }
        }
        return null;
    }

    /// <summary>Rewrites one step's chord at full velocity. See the velocity-aware overload.</summary>
    public static void WriteChord(List<string> pattern, int startStep, IReadOnlyCollection<(int MidiNote, int Length)> notes) =>
        WriteChord(pattern, startStep, notes.Select(note => (note.MidiNote, note.Length, Velocity: 1.0)).ToList());

    /// <summary>
    /// Rewrites one step's chord. The step's old sustain run is cleared first;
    /// then, if every note shares one length and the steps it needs are free,
    /// the classic "chord + _ _ _" form is used, otherwise each note gets an
    /// explicit ":length". Velocities below 1 ride along as "@0.6" either way.
    /// </summary>
    public static void WriteChord(List<string> pattern, int startStep, IReadOnlyCollection<(int MidiNote, int Length, double Velocity)> notes)
    {
        var previousImplicitLength = NotationResolver.ResolveOrRest(pattern[startStep]).Kind == StepEventKind.Trigger
            ? ImplicitLength(pattern, startStep)
            : 1;
        for (var sustainStep = startStep + 1; sustainStep < startStep + previousImplicitLength; sustainStep++)
        {
            pattern[sustainStep] = NotationResolver.RestSymbol;
        }

        var clampedNotes = notes
            .Select(note => (note.MidiNote, Length: Math.Clamp(note.Length, 1, pattern.Count - startStep), note.Velocity))
            .ToList();
        if (clampedNotes.Count == 0)
        {
            pattern[startStep] = NotationResolver.RestSymbol;
            return;
        }

        var sharedLength = clampedNotes.Select(note => note.Length).Distinct().Count() == 1 ? clampedNotes[0].Length : (int?)null;
        var classicFormFits = sharedLength is { } length &&
                              Enumerable.Range(startStep + 1, length - 1)
                                  .All(step => NotationResolver.ResolveOrRest(pattern[step]).Kind == StepEventKind.Rest);
        if (classicFormFits)
        {
            pattern[startStep] = NotationResolver.FormatChord(clampedNotes.Select(note => (note.MidiNote, note.Velocity)));
            for (var sustainStep = startStep + 1; sustainStep < startStep + sharedLength!.Value; sustainStep++)
            {
                pattern[sustainStep] = NotationResolver.SustainSymbol;
            }
            return;
        }
        pattern[startStep] = NotationResolver.FormatChord(clampedNotes);
    }

    /// <summary>Adds a note (or replaces the same pitch) at a step, keeping the other chord notes' lengths and velocities.</summary>
    public static void AddNote(List<string> pattern, int startStep, int midiNote, int length = 1, double velocity = 1)
    {
        var chordNotes = NotesStartingAt(pattern, startStep)
            .Where(placedNote => placedNote.MidiNote != midiNote)
            .Select(placedNote => (placedNote.MidiNote, placedNote.Length, placedNote.Velocity))
            .Append((midiNote, length, NotationResolver.ClampVelocity(velocity)))
            .ToList();
        WriteChord(pattern, startStep, chordNotes);
    }

    public static void RemoveNote(List<string> pattern, int startStep, int midiNote)
    {
        var chordNotes = NotesStartingAt(pattern, startStep)
            .Where(placedNote => placedNote.MidiNote != midiNote)
            .Select(placedNote => (placedNote.MidiNote, placedNote.Length, placedNote.Velocity))
            .ToList();
        WriteChord(pattern, startStep, chordNotes);
    }

    /// <summary>Changes one note's length, leaving the rest of its chord alone.</summary>
    public static void SetNoteLength(List<string> pattern, int startStep, int midiNote, int length)
    {
        var chordNotes = NotesStartingAt(pattern, startStep)
            .Select(placedNote => (placedNote.MidiNote, placedNote.MidiNote == midiNote ? length : placedNote.Length, placedNote.Velocity))
            .ToList();
        WriteChord(pattern, startStep, chordNotes);
    }

    /// <summary>Changes how hard one note is struck, leaving the rest of its chord alone.</summary>
    public static void SetNoteVelocity(List<string> pattern, int startStep, int midiNote, double velocity)
    {
        var chordNotes = NotesStartingAt(pattern, startStep)
            .Select(placedNote => (placedNote.MidiNote, placedNote.Length, placedNote.MidiNote == midiNote ? NotationResolver.ClampVelocity(velocity) : placedNote.Velocity))
            .ToList();
        WriteChord(pattern, startStep, chordNotes);
    }

    /// <summary>Re-strikes a drum hit at a velocity. Does nothing on a step without a hit.</summary>
    public static void SetDrumHitVelocity(List<string> pattern, int step, double velocity)
    {
        if (NotationResolver.ResolveOrRest(pattern[step]).Kind == StepEventKind.Trigger)
        {
            pattern[step] = NotationResolver.FormatTrigger(velocity);
        }
    }
}
