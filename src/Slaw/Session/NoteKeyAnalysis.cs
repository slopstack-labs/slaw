using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Session;

/// <summary>A musical key: a pitch class and whether it is major or minor.</summary>
internal readonly record struct MusicalKey(int Tonic, bool IsMinor)
{
    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly int[] NaturalMinorScale = { 0, 2, 3, 5, 7, 8, 10 };
    private static readonly Regex KeyNotation = new(@"^\s*([A-Ga-g])\s*([#b♯♭]?)\s*(m|min|minor|maj|major)?\s*$", RegexOptions.Compiled);

    public string Name => $"{NoteNames[Tonic]} {(IsMinor ? "minor" : "major")}";

    public static string NoteName(int pitchClass) => NoteNames[(pitchClass % 12 + 12) % 12];

    public bool Contains(int pitchClass) => (IsMinor ? NaturalMinorScale : MajorScale).Contains(((pitchClass - Tonic) % 12 + 12) % 12);

    public string ScaleNames
    {
        get
        {
            var tonic = Tonic;
            return string.Join(" ", (IsMinor ? NaturalMinorScale : MajorScale).Select(degree => NoteName(tonic + degree)));
        }
    }

    /// <summary>"A minor", "Am", "F# major", "Bb": a bare letter is major.</summary>
    public static MusicalKey? Parse(string? notation)
    {
        var parsed = KeyNotation.Match(notation ?? string.Empty);
        if (!parsed.Success)
        {
            return null;
        }
        var letter = char.ToUpperInvariant(parsed.Groups[1].Value[0]);
        var tonic = Array.IndexOf(NoteNames, letter.ToString());
        tonic += parsed.Groups[2].Value switch { "#" or "♯" => 1, "b" or "♭" => -1, _ => 0 };
        return new MusicalKey((tonic + 12) % 12, parsed.Groups[3].Value.ToLowerInvariant() is "m" or "min" or "minor");
    }
}

/// <summary>One note of the song that the chosen key does not contain.</summary>
internal sealed record OutOfKeyNote(string TrackName, string NoteName, string Position, double Beats, string? ClipId);

/// <summary>What the notes of a song say about its key.</summary>
internal sealed record NoteKeyReport(
    MusicalKey? Key,
    double Match,
    MusicalKey? RunnerUp,
    bool KeyWasGiven,
    int NoteCount,
    int CheckedTrackCount,
    IReadOnlyList<OutOfKeyNote> OutOfKeyNotes,
    IReadOnlyList<string> UncheckedTracks);

/// <summary>
/// Reads the song's written notes, so the answer is exact where audio analysis could only
/// guess: which key they fit (Krumhansl–Schmuckler over how long each pitch class sounds)
/// and which notes fall outside it. Recordings have no notes to read and are named instead.
/// </summary>
internal static class NoteKeyAnalysis
{
    private static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
    private static readonly IReadOnlySet<string> UnpitchedInstruments = new HashSet<string> { "kick", "snare", "hat", "clap", "shaker", "conga", "tom", "cymbal", "noise", "sampler" };

    private const int MaximumListedPositions = 5;

    public static NoteKeyReport Analyze(SlawProject project, MusicalKey? givenKey = null)
    {
        var durationByPitchClass = new double[12];
        var notes = new List<(SlawTrack Track, int Midi, int Step, int Length, string? ClipId)>();
        var unchecked_ = new List<string>();
        var checkedTrackCount = 0;
        foreach (var track in project.Tracks)
        {
            var trackNotes = NotesOf(project, track).ToList();
            if (trackNotes.Count == 0)
            {
                continue;
            }
            if (!HasReadablePitch(track))
            {
                unchecked_.Add(track.Name);
                continue;
            }
            checkedTrackCount++;
            var transpose = (int)Math.Round(track.InstrumentParameters.GetValueOrDefault("transpose", 0));
            foreach (var (midi, step, length, clipId) in trackNotes)
            {
                var soundingMidi = midi + transpose;
                durationByPitchClass[(soundingMidi % 12 + 12) % 12] += length;
                notes.Add((track, soundingMidi, step, length, clipId));
            }
        }

        var (bestKey, bestMatch, runnerUp) = RankKeys(durationByPitchClass);
        var key = givenKey ?? bestKey;
        var outOfKey = new List<OutOfKeyNote>();
        if (key is { } chosenKey)
        {
            outOfKey.AddRange(notes
                .Where(note => !chosenKey.Contains(note.Midi))
                .OrderBy(note => note.Step)
                .Select(note => new OutOfKeyNote(note.Track.Name, MusicalKey.NoteName(note.Midi), Position(note.Step), note.Length / 4.0, note.ClipId)));
        }
        return new NoteKeyReport(key, givenKey is null ? bestMatch : Correlate(durationByPitchClass, givenKey.Value), givenKey is null ? runnerUp : bestKey,
            givenKey is not null, notes.Count, checkedTrackCount, outOfKey, unchecked_);
    }

    public static string Describe(NoteKeyReport report)
    {
        var description = new StringBuilder();
        if (report.Key is not { } key)
        {
            description.Append("no pitched notes to check.");
        }
        else
        {
            description.Append(CultureInfo.InvariantCulture, $"key: {key.Name} ({(report.KeyWasGiven ? "as asked" : "best fit")}, match {report.Match:0.00}, where 1 means the time spent on each note fits this key's usual pattern exactly and 0 means no relation");
            if (report.RunnerUp is { } runnerUp && runnerUp != key)
            {
                description.Append(CultureInfo.InvariantCulture, $"; {(report.KeyWasGiven ? "best fit" : "next")} {runnerUp.Name}");
            }
            description.Append(CultureInfo.InvariantCulture, $"), from {report.NoteCount} notes on {report.CheckedTrackCount} track(s). Scale: {key.ScaleNames}.");
            if (report.OutOfKeyNotes.Count == 0)
            {
                description.Append("\noutside the scale: none.");
            }
            else
            {
                description.Append(CultureInfo.InvariantCulture, $"\noutside the scale: {report.OutOfKeyNotes.Count} note(s). These may be deliberate (a raised 6th or 7th is normal in minor), so decide whether each one is meant before changing it. Positions are bar.step, both counted from 1, so 1.1 is step 0 of the song:");
                foreach (var byNote in report.OutOfKeyNotes.GroupBy(note => (note.TrackName, note.NoteName)))
                {
                    var positions = byNote.Select(note => note.Position).Take(MaximumListedPositions);
                    var more = byNote.Count() > MaximumListedPositions ? $", and {byNote.Count() - MaximumListedPositions} more" : string.Empty;
                    var clipIds = byNote.Select(note => note.ClipId).OfType<string>().Distinct().ToList();
                    var inClips = clipIds.Count > 0 ? $" in clip {string.Join(", ", clipIds.Select(clipId => $"id \"{clipId}\""))}" : " in its session clips";
                    description.Append(CultureInfo.InvariantCulture, $"\n- \"{byNote.Key.TrackName}\" {byNote.Key.NoteName}: {byNote.Count()} note(s) at {string.Join(", ", positions)}{more}{inClips}");
                }
            }
        }
        if (report.UncheckedTracks.Count > 0)
        {
            description.Append("\nnot checked, because their notes do not say their pitch (drums, kits, recordings): ")
                .Append(string.Join(", ", report.UncheckedTracks.Select(trackName => $"\"{trackName}\"")))
                .Append('.');
        }
        return description.ToString();
    }

    /// <summary>A drum, a kit's slices or a recording plays what it plays whatever key its step says; a one-note-per-key drum rack too.</summary>
    private static bool HasReadablePitch(SlawTrack track)
    {
        if (UnpitchedInstruments.Contains(track.Instrument))
        {
            return false;
        }
        if (track.Instrument == "multisampler" && track.SampleZones is { Count: > 0 } zones && zones.All(zone => zone.KeyLow == zone.KeyHigh))
        {
            return false;
        }
        return !(track.Instrument == "soundfont" && track.SoundFont?.Bank == 128);
    }

    /// <summary>Every note a track plays, from the arrangement, or from its session clips while the arrangement is empty.</summary>
    private static IEnumerable<(int Midi, int Step, int Length, string? ClipId)> NotesOf(SlawProject project, SlawTrack track)
    {
        if (project.Timeline.Count == 0)
        {
            foreach (var clip in track.Clips.OfType<SlawClip>())
            {
                foreach (var (midi, step, length) in NotesOfPattern(clip.Pattern, 0, clip.Pattern.Count))
                {
                    yield return (midi, step, length, null);
                }
            }
            yield break;
        }
        var trackClips = project.Timeline.Where(timelineClip => timelineClip.TrackId == track.Id).OrderBy(timelineClip => timelineClip.StartStep).ToList();
        for (var clipIndex = 0; clipIndex < trackClips.Count; clipIndex++)
        {
            var clip = trackClips[clipIndex];
            // Where clips overlap, the later-starting clip plays.
            var audibleEnd = clipIndex + 1 < trackClips.Count ? Math.Min(clip.EndStep, trackClips[clipIndex + 1].StartStep) : clip.EndStep;
            foreach (var (midi, step, length) in NotesOfPattern(clip.Clip.Pattern, clip.StartStep, audibleEnd - clip.StartStep))
            {
                yield return (midi, step, length, clip.Id);
            }
        }
    }

    private static IEnumerable<(int Midi, int Step, int Length)> NotesOfPattern(IReadOnlyList<string> pattern, int startStep, int playedSteps)
    {
        if (pattern.Count == 0)
        {
            yield break;
        }
        for (var offset = 0; offset < playedSteps; offset++)
        {
            var stepEvent = NotationResolver.ResolveOrRest(pattern[offset % pattern.Count]);
            if (stepEvent.Kind != StepEventKind.Trigger)
            {
                continue;
            }
            var heldSteps = 1;
            while (offset + heldSteps < playedSteps && NotationResolver.ResolveOrRest(pattern[(offset + heldSteps) % pattern.Count]).Kind == StepEventKind.Sustain)
            {
                heldSteps++;
            }
            for (var noteIndex = 0; noteIndex < stepEvent.MidiNotes.Count; noteIndex++)
            {
                var ownLength = noteIndex < stepEvent.NoteLengths.Count ? stepEvent.NoteLengths[noteIndex] : 0;
                yield return (stepEvent.MidiNotes[noteIndex], startStep + offset, ownLength > 0 ? ownLength : heldSteps);
            }
        }
    }

    private static string Position(int step) =>
        string.Create(CultureInfo.InvariantCulture, $"{step / FreeTimeline.StepsPerBar + 1}.{step % FreeTimeline.StepsPerBar + 1}");

    private static (MusicalKey? Best, double Match, MusicalKey? RunnerUp) RankKeys(double[] durationByPitchClass)
    {
        if (durationByPitchClass.All(duration => duration == 0))
        {
            return (null, 0, null);
        }
        var ranked = Enumerable.Range(0, 12)
            .SelectMany(tonic => new[] { new MusicalKey(tonic, false), new MusicalKey(tonic, true) })
            .Select(key => (Key: key, Match: Correlate(durationByPitchClass, key)))
            .OrderByDescending(candidate => candidate.Match)
            .ToList();
        return (ranked[0].Key, ranked[0].Match, ranked[1].Key);
    }

    private static double Correlate(double[] durationByPitchClass, MusicalKey key)
    {
        var profile = key.IsMinor ? MinorProfile : MajorProfile;
        var durationMean = durationByPitchClass.Average();
        var profileMean = profile.Average();
        double cross = 0, durationEnergy = 0, profileEnergy = 0;
        for (var pitchClass = 0; pitchClass < 12; pitchClass++)
        {
            var durationDelta = durationByPitchClass[pitchClass] - durationMean;
            var profileDelta = profile[((pitchClass - key.Tonic) % 12 + 12) % 12] - profileMean;
            cross += durationDelta * profileDelta;
            durationEnergy += durationDelta * durationDelta;
            profileEnergy += profileDelta * profileDelta;
        }
        return durationEnergy > 0 ? cross / Math.Sqrt(durationEnergy * profileEnergy) : 0;
    }
}
