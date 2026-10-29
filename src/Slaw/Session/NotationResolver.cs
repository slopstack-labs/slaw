using System.Globalization;
using System.Text.RegularExpressions;

namespace SlopstackLabs.Slaw.Session;

internal enum StepEventKind
{
    Rest,
    Sustain,
    Trigger,
}

/// <summary>
/// A single resolved pattern step. A trigger carries one or more MIDI notes —
/// more than one makes it a chord. <see cref="NoteLengths"/> is aligned with
/// <see cref="MidiNotes"/>: 0 means "held for this step plus any following
/// sustains", a positive value is an explicit length in steps ("C4:8"), which
/// lets notes struck together end at different times. <see cref="NoteVelocities"/>
/// is aligned too: 0..1, where 1 ("C4") is full strength and "C4@0.5" is not.
/// </summary>
internal readonly record struct StepEvent(StepEventKind Kind, IReadOnlyList<int> MidiNotes, IReadOnlyList<int> NoteLengths, IReadOnlyList<double> NoteVelocities)
{
    private static readonly int[] NoNotes = Array.Empty<int>();
    private static readonly double[] NoVelocities = Array.Empty<double>();

    public StepEvent(StepEventKind kind, IReadOnlyList<int> midiNotes)
        : this(kind, midiNotes, new int[midiNotes.Count])
    {
    }

    public StepEvent(StepEventKind kind, IReadOnlyList<int> midiNotes, IReadOnlyList<int> noteLengths)
        : this(kind, midiNotes, noteLengths, FullVelocities(midiNotes.Count))
    {
    }

    public static StepEvent Rest => new(StepEventKind.Rest, NoNotes, NoNotes, NoVelocities);
    public static StepEvent Sustain => new(StepEventKind.Sustain, NoNotes, NoNotes, NoVelocities);

    public static StepEvent Trigger(params int[] midiNotes) => new(StepEventKind.Trigger, midiNotes, new int[midiNotes.Length]);

    /// <summary>True when any note in the chord carries its own length.</summary>
    public bool HasExplicitLengths => NoteLengths.Any(noteLength => noteLength > 0);

    /// <summary>The loudest note of the step: what a drum hit, which has only one, plays at.</summary>
    public double PeakVelocity => NoteVelocities.Count > 0 ? NoteVelocities.Max() : 1;

    private static double[] FullVelocities(int noteCount) => Enumerable.Repeat(1.0, noteCount).ToArray();

    /// <summary>The lowest note of the step, or 0 for rests and sustains.</summary>
    public int MidiNote => MidiNotes.Count > 0 ? MidiNotes[0] : 0;
}

/// <summary>
/// Translates the pattern step vocabulary into MIDI note numbers and back.
/// Lenient by design: language models are creative about notation, and every
/// unparseable step is a learning opportunity that renders as silence.
/// </summary>
internal static partial class NotationResolver
{
    public const string RestSymbol = ".";
    public const string SustainSymbol = "_";
    public const string TriggerSymbol = "x";

    /// <summary>Separator used when writing chords, e.g. "C4+E4+G4".</summary>
    public const char ChordSeparator = '+';

    /// <summary>The pitch a bare trigger ("x") plays on a melodic instrument.</summary>
    public const int DefaultTriggerMidiNote = 60;

    /// <summary>Separator before a note's velocity, e.g. "C4@0.6" or "x@0.6".</summary>
    public const char VelocitySeparator = '@';

    /// <summary>The softest a note can be struck; 0 would be a rest with extra steps.</summary>
    public const double MinimumVelocity = 0.01;

    private static readonly char[] ChordTokenSeparators = { '+', ',', ' ', '/', '&' };

    private static readonly string[] ChromaticPitchClassNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static readonly Dictionary<char, int> NaturalPitchClassOffsets = new()
    {
        ['C'] = 0,
        ['D'] = 2,
        ['E'] = 4,
        ['F'] = 5,
        ['G'] = 7,
        ['A'] = 9,
        ['B'] = 11,
    };

    [GeneratedRegex(@"^(?<letter>[A-Ga-g])(?<accidental>#|b|s)?(?<octave>-?\d)?$")]
    private static partial Regex ScientificPitchNotationPattern();

    /// <summary>
    /// Resolves one step. Returns null when the step is not valid notation, so the
    /// caller can decide whether that counts as a rest or as a remediation event.
    /// </summary>
    public static StepEvent? TryResolve(string? rawStepNotation)
    {
        var normalizedStepNotation = rawStepNotation?.Trim() ?? string.Empty;
        switch (normalizedStepNotation)
        {
            case "" or "." or "-" or "0" or "rest" or "~~":
                return StepEvent.Rest;
            case "_" or "~" or "hold" or "=":
                return StepEvent.Sustain;
            case "x" or "X" or "*" or "o" or "1" or "hit":
                return StepEvent.Trigger(DefaultTriggerMidiNote);
        }
        // "x@0.6": a drum hit with a velocity.
        var velocitySeparatorOffset = normalizedStepNotation.IndexOf(VelocitySeparator);
        if (velocitySeparatorOffset > 0 &&
            normalizedStepNotation[..velocitySeparatorOffset] is "x" or "X" or "*" or "o" or "hit")
        {
            return new StepEvent(StepEventKind.Trigger, new[] { DefaultTriggerMidiNote }, new[] { 0 },
                new[] { ParseVelocity(normalizedStepNotation[(velocitySeparatorOffset + 1)..]) });
        }

        var chordTokens = normalizedStepNotation.Split(ChordTokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (chordTokens.Length == 0)
        {
            return null;
        }
        // "C4:8" holds C4 for eight steps regardless of what follows; "C4:8@0.6" also plays it softly.
        var resolvedNoteLengths = new SortedDictionary<int, int>();
        var resolvedNoteVelocities = new Dictionary<int, double>();
        foreach (var rawChordToken in chordTokens)
        {
            var chordToken = rawChordToken;
            var noteVelocity = 1.0;
            var noteVelocityOffset = chordToken.IndexOf(VelocitySeparator);
            if (noteVelocityOffset >= 0)
            {
                // The velocity may come before or after the length: "C4@0.6:8" and "C4:8@0.6" agree.
                var velocityEnd = chordToken.IndexOf(':', noteVelocityOffset);
                velocityEnd = velocityEnd < 0 ? chordToken.Length : velocityEnd;
                noteVelocity = ParseVelocity(chordToken[(noteVelocityOffset + 1)..velocityEnd]);
                chordToken = chordToken[..noteVelocityOffset] + chordToken[velocityEnd..];
            }
            var lengthSeparatorOffset = chordToken.IndexOf(':');
            var pitchToken = lengthSeparatorOffset >= 0 ? chordToken[..lengthSeparatorOffset] : chordToken;
            var explicitLength = 0;
            if (lengthSeparatorOffset >= 0 &&
                (!int.TryParse(chordToken[(lengthSeparatorOffset + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out explicitLength) ||
                 explicitLength is < 1 or > 256))
            {
                return null;
            }
            if (TryResolveSinglePitch(pitchToken) is not { } resolvedMidiNote)
            {
                return null;
            }
            resolvedNoteLengths[resolvedMidiNote] = Math.Max(resolvedNoteLengths.GetValueOrDefault(resolvedMidiNote), explicitLength);
            resolvedNoteVelocities[resolvedMidiNote] = Math.Max(resolvedNoteVelocities.GetValueOrDefault(resolvedMidiNote), noteVelocity);
        }
        return new StepEvent(StepEventKind.Trigger, resolvedNoteLengths.Keys.ToArray(), resolvedNoteLengths.Values.ToArray(),
            resolvedNoteLengths.Keys.Select(midiNote => resolvedNoteVelocities[midiNote]).ToArray());
    }

    /// <summary>
    /// Reads a velocity: 0..1, or a MIDI-style 2..127 (models and humans both think in those).
    /// Anything unreadable plays at full strength rather than vanishing; the note was clearly meant.
    /// </summary>
    private static double ParseVelocity(string velocityText)
    {
        if (!double.TryParse(velocityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedVelocity) || !double.IsFinite(parsedVelocity))
        {
            return 1;
        }
        return ClampVelocity(parsedVelocity > 1 ? parsedVelocity / 127 : parsedVelocity);
    }

    public static double ClampVelocity(double velocity) => double.IsFinite(velocity) ? Math.Clamp(velocity, MinimumVelocity, 1) : 1;

    /// <summary>
    /// How loud a velocity plays. Amplitude follows velocity^1.5: linear leaves half
    /// velocity only 6 dB down, which reads as "barely softer"; squared buries ghost
    /// notes under the mix. 1.5 puts 0.5 at about -9 dB, where ghost notes live.
    /// </summary>
    public static double VelocityToGain(double velocity) => Math.Pow(ClampVelocity(velocity), 1.5);

    /// <summary>Writes "@0.6" for a velocity below full strength, nothing otherwise. Two decimals is plenty of nuance.</summary>
    public static string FormatVelocitySuffix(double velocity)
    {
        var roundedVelocity = Math.Round(ClampVelocity(velocity), 2);
        return roundedVelocity >= 1 ? string.Empty : VelocitySeparator + roundedVelocity.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>A drum hit at a velocity: "x" or "x@0.6".</summary>
    public static string FormatTrigger(double velocity) => TriggerSymbol + FormatVelocitySuffix(velocity);

    private static int? TryResolveSinglePitch(string pitchToken)
    {
        if (int.TryParse(pitchToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var literalMidiNote))
        {
            return literalMidiNote is >= 12 and <= 127 ? literalMidiNote : null;
        }

        var notationMatch = ScientificPitchNotationPattern().Match(pitchToken);
        if (!notationMatch.Success)
        {
            return null;
        }
        var pitchClassOffset = NaturalPitchClassOffsets[char.ToUpperInvariant(notationMatch.Groups["letter"].Value[0])];
        pitchClassOffset += notationMatch.Groups["accidental"].Value switch
        {
            "#" or "s" => 1,
            "b" => -1,
            _ => 0,
        };
        var octaveNumber = notationMatch.Groups["octave"].Success
            ? int.Parse(notationMatch.Groups["octave"].Value, CultureInfo.InvariantCulture)
            : 4;
        var resolvedMidiNote = (octaveNumber + 1) * 12 + pitchClassOffset;
        return resolvedMidiNote is >= 0 and <= 127 ? resolvedMidiNote : null;
    }

    public static StepEvent ResolveOrRest(string? rawStepNotation) => TryResolve(rawStepNotation) ?? StepEvent.Rest;

    /// <summary>Formats a MIDI note in sharps-only scientific pitch notation, e.g. 61 → "C#4".</summary>
    public static string FormatMidiNote(int midiNote)
    {
        var clampedMidiNote = Math.Clamp(midiNote, 0, 127);
        return ChromaticPitchClassNames[clampedMidiNote % 12] + (clampedMidiNote / 12 - 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Formats one or more notes as a step, e.g. [60, 64, 67] → "C4+E4+G4".</summary>
    public static string FormatChord(IEnumerable<int> midiNotes)
    {
        var orderedMidiNotes = midiNotes.Distinct().Order().ToArray();
        return orderedMidiNotes.Length == 0
            ? RestSymbol
            : string.Join(ChordSeparator, orderedMidiNotes.Select(FormatMidiNote));
    }

    /// <summary>
    /// Formats notes with lengths, e.g. [(60, 8), (64, 2)] → "C4:8+E4:2". A
    /// length of 0 (implicit) or 1 is written bare.
    /// </summary>
    public static string FormatChord(IEnumerable<(int MidiNote, int Length)> notesWithLengths) =>
        FormatChord(notesWithLengths.Select(note => (note.MidiNote, note.Length, Velocity: 1.0)));

    /// <summary>
    /// Formats notes with lengths and velocities, e.g. [(60, 8, 0.5), (64, 1, 1)] → "C4:8@0.5+E4".
    /// Full-strength velocities are not written.
    /// </summary>
    public static string FormatChord(IEnumerable<(int MidiNote, int Length, double Velocity)> notesWithLengthsAndVelocities)
    {
        var orderedNotes = notesWithLengthsAndVelocities
            .GroupBy(note => note.MidiNote)
            .Select(noteGroup => (MidiNote: noteGroup.Key, Length: noteGroup.Max(note => note.Length), Velocity: noteGroup.Max(note => note.Velocity)))
            .OrderBy(note => note.MidiNote)
            .ToArray();
        return orderedNotes.Length == 0
            ? RestSymbol
            : string.Join(ChordSeparator, orderedNotes.Select(note =>
                FormatMidiNote(note.MidiNote) +
                (note.Length > 1 ? ":" + note.Length.ToString(CultureInfo.InvariantCulture) : string.Empty) +
                FormatVelocitySuffix(note.Velocity)));
    }

    /// <summary>A chord step without explicit lengths, keeping each note's velocity: "C4@0.5+E4+G4".</summary>
    public static string FormatChord(IEnumerable<(int MidiNote, double Velocity)> notesWithVelocities) =>
        FormatChord(notesWithVelocities.Select(note => (note.MidiNote, Length: 0, note.Velocity)));

    public static double MidiNoteToFrequency(double midiNote) => 440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);

    /// <summary>
    /// Rewrites a step into canonical notation: "." for rests, "_" for sustains,
    /// "x" for drum triggers, and sharps-only pitch names (joined with "+" for
    /// chords) for everything else. Unparseable steps canonicalize to a rest.
    /// </summary>
    public static string Canonicalize(string? rawStepNotation, bool percussiveInstrument)
    {
        var resolvedStepEvent = ResolveOrRest(rawStepNotation);
        return resolvedStepEvent.Kind switch
        {
            StepEventKind.Rest => RestSymbol,
            StepEventKind.Sustain => percussiveInstrument ? RestSymbol : SustainSymbol,
            _ when percussiveInstrument => FormatTrigger(resolvedStepEvent.PeakVelocity),
            _ => FormatChord(resolvedStepEvent.MidiNotes.Select((midiNote, noteIndex) =>
                (midiNote, resolvedStepEvent.NoteLengths[noteIndex], resolvedStepEvent.NoteVelocities[noteIndex]))),
        };
    }
}
