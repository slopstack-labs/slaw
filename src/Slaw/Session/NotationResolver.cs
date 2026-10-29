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
/// lets notes struck together end at different times.
/// </summary>
internal readonly record struct StepEvent(StepEventKind Kind, IReadOnlyList<int> MidiNotes, IReadOnlyList<int> NoteLengths)
{
    private static readonly int[] NoNotes = Array.Empty<int>();

    public StepEvent(StepEventKind kind, IReadOnlyList<int> midiNotes)
        : this(kind, midiNotes, new int[midiNotes.Count])
    {
    }

    public static StepEvent Rest => new(StepEventKind.Rest, NoNotes, NoNotes);
    public static StepEvent Sustain => new(StepEventKind.Sustain, NoNotes, NoNotes);

    public static StepEvent Trigger(params int[] midiNotes) => new(StepEventKind.Trigger, midiNotes, new int[midiNotes.Length]);

    /// <summary>True when any note in the chord carries its own length.</summary>
    public bool HasExplicitLengths => NoteLengths.Any(noteLength => noteLength > 0);

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

        var chordTokens = normalizedStepNotation.Split(ChordTokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (chordTokens.Length == 0)
        {
            return null;
        }
        // "C4:8" holds C4 for eight steps regardless of what follows.
        var resolvedNoteLengths = new SortedDictionary<int, int>();
        foreach (var chordToken in chordTokens)
        {
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
        }
        return new StepEvent(StepEventKind.Trigger, resolvedNoteLengths.Keys.ToArray(), resolvedNoteLengths.Values.ToArray());
    }

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
    public static string FormatChord(IEnumerable<(int MidiNote, int Length)> notesWithLengths)
    {
        var orderedNotes = notesWithLengths
            .GroupBy(note => note.MidiNote)
            .Select(noteGroup => (MidiNote: noteGroup.Key, Length: noteGroup.Max(note => note.Length)))
            .OrderBy(note => note.MidiNote)
            .ToArray();
        return orderedNotes.Length == 0
            ? RestSymbol
            : string.Join(ChordSeparator, orderedNotes.Select(note =>
                note.Length > 1 ? $"{FormatMidiNote(note.MidiNote)}:{note.Length.ToString(CultureInfo.InvariantCulture)}" : FormatMidiNote(note.MidiNote)));
    }

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
            _ when percussiveInstrument => TriggerSymbol,
            _ => FormatChord(resolvedStepEvent.MidiNotes.Zip(resolvedStepEvent.NoteLengths)),
        };
    }
}
