using System.Globalization;
using SlopstackLabs.Slaw.SoundFonts;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// The outcome of a MIDI import: a project, plus a ledger itemizing everything the
/// import had to quietly give up on along the way. Transparency is a core value.
/// </summary>
internal sealed record MidiImportResult(SlawProject Project, IReadOnlyList<string> ImportLedger);

/// <summary>A SoundFont to voice an imported MIDI file with: the file, parsed.</summary>
internal sealed record MidiSoundFontChoice(string Path, SoundFontBank Bank);

/// <summary>
/// Converts Standard MIDI Files into slaw projects and patterns. Slaw's notation is
/// monophonic per step, polyphonic per chord, and lossy by design: overlapping
/// notes are approximated and velocities are synergized away. Pitch bends become
/// clip pitch envelopes. With a SoundFont, every channel plays its General MIDI
/// program from the font and the drum channel plays the font's kit, note for note.
/// </summary>
internal static class MidiFileImporter
{
    private const int StepsPerBar = 16;
    private const int StepsPerSection = 64;
    private const int DrumChannelIndex = 9;
    private const int MaximumSupportedSongSteps = 4096 * StepsPerBar;

    private sealed record ParsedNote(int MidiTrackIndex, int Channel, int MidiNote, long StartTick, long EndTick, int? Program);

    /// <summary>A pitch bend: where, on which channel, and how far in semitones (bend range applied).</summary>
    private sealed record ParsedPitchBend(int MidiTrackIndex, int Channel, long Tick, double Semitones);

    private sealed record ParsedMidiFile(
        int Format,
        int Division,
        double? TempoBpm,
        IReadOnlyList<ParsedNote> Notes,
        IReadOnlyDictionary<int, string> TrackNames,
        int MidiTrackCount,
        IReadOnlyList<ParsedPitchBend> PitchBends);

    private sealed record StepNote(int StartStep, int LengthInSteps, int MidiNote);

    private sealed class SlawTrackCandidate
    {
        public string Name { get; init; } = "";
        public string Instrument { get; init; } = "triangle";
        public double Gain { get; init; } = 0.7;
        public bool Percussive { get; init; }
        public SoundFontReference? SoundFont { get; init; }
        public List<StepNote> Notes { get; } = new();



        /// <summary>Pitch bends in song steps (fractional) and semitones, in time order.</summary>
        public List<(double Step, double Semitones)> Bends { get; } = new();

        public SlawTrackCandidate CopyWith(string name, double gain)
        {
            var copy = new SlawTrackCandidate { Name = name, Instrument = Instrument, Gain = gain, Percussive = Percussive, SoundFont = SoundFont };
            copy.Notes.AddRange(Notes);
            copy.Bends.AddRange(Bends);
            return copy;
        }
    }

    /// <summary>True for .mid, .midi and .smf files, case-insensitively.</summary>
    public static bool IsMidiFile(string path)
    {
        var extension = Path.GetExtension(path ?? string.Empty);
        return extension.Equals(".mid", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".midi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".smf", StringComparison.OrdinalIgnoreCase);
    }

    public static MidiImportResult Import(string path, MidiSoundFontChoice? soundFont = null) =>
        Import(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), soundFont);

    /// <summary>
    /// The SoundFont that came with a MIDI file: a .sf2 with the same name in the
    /// same folder, else the only .sf2 there. Null when there is none or several.
    /// </summary>
    public static string? FindCompanionSoundFont(string midiFilePath)
    {
        var midiFolder = Path.GetDirectoryName(Path.GetFullPath(midiFilePath));
        if (midiFolder is null || !Directory.Exists(midiFolder))
        {
            return null;
        }
        var fontsInFolder = Directory.EnumerateFiles(midiFolder).Where(SoundFontBank.IsSoundFontFile).OrderBy(fontPath => fontPath, StringComparer.Ordinal).ToList();
        return fontsInFolder.FirstOrDefault(fontPath => string.Equals(Path.GetFileNameWithoutExtension(fontPath), Path.GetFileNameWithoutExtension(midiFilePath), StringComparison.OrdinalIgnoreCase))
               ?? (fontsInFolder.Count == 1 ? fontsInFolder[0] : null);
    }

    public static MidiImportResult Import(byte[] midiFileBytes, string projectName, MidiSoundFontChoice? soundFont = null)
    {
        var parsedMidiFile = Parse(midiFileBytes);
        var importLedger = new List<string>();
        var stepsPerTick = 4.0 / parsedMidiFile.Division;

        var allStepNotes = new List<(ParsedNote Source, StepNote Quantized)>();
        var droppedBeyondLimit = 0;
        foreach (var parsedNote in parsedMidiFile.Notes)
        {
            var quantized = Quantize(parsedNote, stepsPerTick);
            if (quantized.StartStep >= MaximumSupportedSongSteps)
            {
                droppedBeyondLimit++;
                continue;
            }
            allStepNotes.Add((parsedNote, quantized));
        }
        if (droppedBeyondLimit > 0)
            importLedger.Add($"dropped {droppedBeyondLimit} notes beyond bar {MaximumSupportedSongSteps / StepsPerBar}: even our patience has a budget");
        if (allStepNotes.Count == 0)
            throw new InvalidDataException("This MIDI file contains no notes. Silence is bold, but slaw needs at least one note to deliver value.");

        var bendsByChannel = BendsByChannel(parsedMidiFile.PitchBends, stepsPerTick);
        var candidates = BuildTrackCandidates(allStepNotes, parsedMidiFile.TrackNames, soundFont, bendsByChannel, out var foldedUnisonCount);
        var bendedTrackCount = candidates.Count(candidate => candidate.Bends.Count > 0);
        var importedBendCount = candidates.Sum(candidate => candidate.Bends.Count);
        if (foldedUnisonCount > 0)
            importLedger.Add($"folded {foldedUnisonCount} unison double(s) into their twins (louder, not duplicated)");

        if (candidates.Count > ProjectAlignmentRemediator.MaximumTrackCount)
        {
            var survivors = candidates
                .Select((candidate, originalIndex) => (candidate, originalIndex))
                .OrderByDescending(entry => entry.candidate.Notes.Count)
                .ThenBy(entry => entry.originalIndex)
                .Take(ProjectAlignmentRemediator.MaximumTrackCount)
                .OrderBy(entry => entry.originalIndex)
                .Select(entry => entry.candidate)
                .ToList();
            importLedger.Add($"dropped {candidates.Count - survivors.Count} of {candidates.Count} tracks: slaw tops out at {ProjectAlignmentRemediator.MaximumTrackCount}, keeping the busiest");
            candidates = survivors;
        }

        var project = new SlawProject
        {
            Name = SongTitle(parsedMidiFile, projectName),
            Bpm = Math.Clamp(parsedMidiFile.TempoBpm ?? 120.0, 40.0, 300.0),
            Steps = StepsPerBar,
        };

        var songEndStep = candidates.SelectMany(candidate => candidate.Notes).Max(note => note.StartStep + note.LengthInSteps);
        var firstNoteStep = candidates.SelectMany(candidate => candidate.Notes).Min(note => note.StartStep);
        var songStartStep = firstNoteStep / StepsPerBar * StepsPerBar;
        var fullPatterns = candidates
            .Select(candidate => RenderPattern(candidate.Notes, songEndStep, candidate.Percussive))
            .ToList();

        foreach (var candidate in candidates)
        {
            project.Tracks.Add(new SlawTrack { Name = candidate.Name, Instrument = candidate.Instrument, Gain = candidate.Gain, SoundFont = candidate.SoundFont?.Copy() });
        }

        var sectionCount = Math.Max(1, (int)Math.Ceiling((songEndStep - songStartStep) / (double)StepsPerSection));
        var sceneSignatures = new List<string>();
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var sectionStartStep = songStartStep + sectionIndex * StepsPerSection;
            var sectionPatterns = fullPatterns
                .Select(fullPattern => SlicePattern(fullPattern, sectionStartStep, StepsPerSection))
                .ToList();
            var sectionBends = candidates
                .Select(candidate => SliceBends(candidate.Bends, sectionStartStep, StepsPerSection))
                .ToList();
            // Bends are part of a section's identity: the same notes bent differently are a different section.
            var sectionSignature = string.Join("##", sectionPatterns.Select((pattern, trackIndex) =>
                (pattern.All(step => step == NotationResolver.RestSymbol) ? "<silent>" : string.Join(' ', pattern)) +
                (sectionBends[trackIndex] is { } bendPoints ? "~" + ClipAutomation.Describe(ClipAutomation.PitchTarget, bendPoints) : string.Empty)));

            var sceneIndex = sceneSignatures.IndexOf(sectionSignature);
            if (sceneIndex < 0)
            {
                if (sceneSignatures.Count >= ProjectAlignmentRemediator.MaximumSceneCount)
                {
                    importLedger.Add($"song truncated after bar {songStartStep / StepsPerBar + sectionIndex * 4}: slaw arrangements top out at {ProjectAlignmentRemediator.MaximumSceneCount} unique sections");
                    break;
                }

                sceneIndex = sceneSignatures.Count;
                sceneSignatures.Add(sectionSignature);
                var sceneName = $"Section {sceneIndex + 1}";
                project.Scenes.Add(sceneName);
                for (var trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
                {
                    var pattern = sectionPatterns[trackIndex];
                    project.Tracks[trackIndex].Clips.Add(pattern.All(step => step == NotationResolver.RestSymbol)
                        ? null
                        : new SlawClip
                        {
                            Name = $"{project.Tracks[trackIndex].Name} {sceneIndex + 1}",
                            Pattern = pattern,
                            Automation = sectionBends[trackIndex] is { } bendPoints
                                ? new Dictionary<string, List<AutomationPoint>> { [ClipAutomation.PitchTarget] = bendPoints }
                                : null,
                        });
                }
            }

            var lastSection = project.Arrangement.Count > 0 ? project.Arrangement[^1] : null;
            if (lastSection is not null && lastSection.Scene == sceneIndex)
                lastSection.Repeats++;
            else
                project.Arrangement.Add(new ArrangementSection { Scene = sceneIndex, Repeats = 1 });
        }

        var tempoDescription = project.Bpm.ToString("0.#", CultureInfo.InvariantCulture);
        importLedger.Insert(0, $"imported {allStepNotes.Count} notes from {parsedMidiFile.MidiTrackCount} MIDI tracks at BPM {tempoDescription}");
        if (importedBendCount > 0)
        {
            importLedger.Insert(1, $"imported {importedBendCount} pitch bend(s) as pitch envelopes on {bendedTrackCount} track(s)");
        }
        if (soundFont is not null)
        {
            var voicedTracks = candidates.Where(candidate => candidate.SoundFont is not null)
                .Select(candidate => $"{candidate.Name} → {candidate.SoundFont!.Bank}:{candidate.SoundFont.Program} {candidate.SoundFont.PresetName}");
            importLedger.Insert(1, $"voiced with SoundFont {Path.GetFileName(soundFont.Path)}: {string.Join(", ", voicedTracks)}");
        }
        importLedger.AddRange(ProjectAlignmentRemediator.Remediate(project));
        return new MidiImportResult(project, importLedger);
    }

    /// <summary>
    /// Merges every note of every channel into one pattern of exactly
    /// <paramref name="stepCount"/> steps, starting at the bar of the first note.
    /// </summary>
    public static List<string> ImportAsPattern(byte[] midiFileBytes, int stepCount, bool percussive)
    {
        var parsedMidiFile = Parse(midiFileBytes);
        var stepsPerTick = 4.0 / parsedMidiFile.Division;
        var stepNotes = parsedMidiFile.Notes
            .Select(note => Quantize(note, stepsPerTick))
            .Where(note => note.StartStep < MaximumSupportedSongSteps)
            .ToList();
        if (stepNotes.Count == 0)
            return Enumerable.Repeat(NotationResolver.RestSymbol, Math.Max(0, stepCount)).ToList();

        var songEndStep = stepNotes.Max(note => note.StartStep + note.LengthInSteps);
        var firstBarStartStep = stepNotes.Min(note => note.StartStep) / StepsPerBar * StepsPerBar;
        return SlicePattern(RenderPattern(stepNotes, songEndStep, percussive), firstBarStartStep, Math.Max(0, stepCount));
    }

    /// <summary>
    /// Pitch bends per (MIDI track, channel) in song steps, dropping channels that
    /// never leave the centre. Drums never bend, whatever the file says.
    /// </summary>
    private static Dictionary<(int MidiTrackIndex, int Channel), List<(double Step, double Semitones)>> BendsByChannel(IReadOnlyList<ParsedPitchBend> pitchBends, double stepsPerTick) =>
        pitchBends
            .Where(bend => bend.Channel != DrumChannelIndex)
            .GroupBy(bend => (bend.MidiTrackIndex, bend.Channel))
            .Select(channelBends => (channelBends.Key, Bends: channelBends.OrderBy(bend => bend.Tick)
                .Select(bend => (Step: bend.Tick * stepsPerTick, Semitones: Math.Round(bend.Semitones, 4)))
                .ToList()))
            .Where(channelBends => channelBends.Bends.Any(bend => Math.Abs(bend.Semitones) > 1e-6))
            .ToDictionary(channelBends => channelBends.Key, channelBends => channelBends.Bends);

    /// <summary>
    /// The pitch envelope for one section: the bend in force at the section's start,
    /// then a step at every bend event (two points on the same step, the later one
    /// winning). Null when the section is never bent.
    /// </summary>
    private static List<AutomationPoint>? SliceBends(IReadOnlyList<(double Step, double Semitones)> bends, int sectionStartStep, int sectionLength)
    {
        if (bends.Count == 0)
        {
            return null;
        }
        var bendAtStart = bends.LastOrDefault(bend => bend.Step <= sectionStartStep).Semitones;
        var bendsInside = bends.Where(bend => bend.Step > sectionStartStep && bend.Step < sectionStartStep + sectionLength).ToList();
        if (bendsInside.Count == 0 && Math.Abs(bendAtStart) < 1e-6)
        {
            return null;
        }
        var points = new List<AutomationPoint> { new() { Step = 0, Value = bendAtStart } };
        var previousSemitones = bendAtStart;
        foreach (var (bendStep, semitones) in bendsInside)
        {
            var relativeStep = Math.Round(bendStep - sectionStartStep, 4);
            points.Add(new AutomationPoint { Step = relativeStep, Value = previousSemitones });
            points.Add(new AutomationPoint { Step = relativeStep, Value = semitones });
            previousSemitones = semitones;
        }
        return points;
    }

    /// <summary>A single-track file's track name is the song title; otherwise use the file name.</summary>
    private static string SongTitle(ParsedMidiFile parsedMidiFile, string projectName)
    {
        var fallbackName = string.IsNullOrWhiteSpace(projectName) ? "imported slop" : projectName;
        if (parsedMidiFile.MidiTrackCount == 1 && parsedMidiFile.TrackNames.TryGetValue(0, out var songTitle) && !string.IsNullOrWhiteSpace(songTitle))
        {
            return string.Equals(songTitle, fallbackName, StringComparison.OrdinalIgnoreCase) ? songTitle : $"{songTitle} ({fallbackName})";
        }
        return fallbackName;
    }

    private static StepNote Quantize(ParsedNote parsedNote, double stepsPerTick)
    {
        var startStep = (int)Math.Min(int.MaxValue / 2, Math.Round(parsedNote.StartTick * stepsPerTick, MidpointRounding.AwayFromZero));
        var lengthInSteps = (int)Math.Min(int.MaxValue / 4, Math.Max(1, Math.Round((parsedNote.EndTick - parsedNote.StartTick) * stepsPerTick, MidpointRounding.AwayFromZero)));
        return new StepNote(startStep, lengthInSteps, parsedNote.MidiNote);
    }

    private static List<string> SlicePattern(IReadOnlyList<string> fullPattern, int startStep, int stepCount)
    {
        var slice = new List<string>(stepCount);
        for (var offset = 0; offset < stepCount; offset++)
        {
            var sourceIndex = startStep + offset;
            slice.Add(sourceIndex >= 0 && sourceIndex < fullPattern.Count ? fullPattern[sourceIndex] : NotationResolver.RestSymbol);
        }
        return slice;
    }

    /// <summary>
    /// Renders notes to one string per step: chords where notes start together, sustains
    /// for the longest note of a step until the next trigger, rests elsewhere.
    /// </summary>
    private static string[] RenderPattern(IEnumerable<StepNote> notes, int totalStepCount, bool percussive)
    {
        var pattern = Enumerable.Repeat(NotationResolver.RestSymbol, Math.Max(1, totalStepCount)).ToList();
        var notesByStartStep = notes
            .Where(note => note.StartStep >= 0 && note.StartStep < pattern.Count)
            .GroupBy(note => note.StartStep)
            .OrderBy(startingNotes => startingNotes.Key)
            .ToList();
        if (percussive)
        {
            foreach (var startingNotes in notesByStartStep)
            {
                pattern[startingNotes.Key] = NotationResolver.TriggerSymbol;
            }
            return pattern.ToArray();
        }

        // Place every chord with explicit per-note lengths first, so later
        // triggers never truncate earlier notes; then let WriteChord fold
        // uniform chords back into the classic "chord _ _ _" spelling.
        foreach (var startingNotes in notesByStartStep)
        {
            pattern[startingNotes.Key] = NotationResolver.FormatChord(startingNotes
                .GroupBy(note => note.MidiNote)
                .Select(sameNote => (sameNote.Key, Math.Max(1, sameNote.Max(note => note.LengthInSteps)))));
        }
        foreach (var startingNotes in notesByStartStep)
        {
            var placedNotes = ClipNoteEditor.NotesStartingAt(pattern, startingNotes.Key);
            ClipNoteEditor.WriteChord(pattern, startingNotes.Key, placedNotes.Select(note => (note.MidiNote, note.Length)).ToList());
        }
        return pattern.ToArray();
    }

    private static List<SlawTrackCandidate> BuildTrackCandidates(
        IReadOnlyList<(ParsedNote Source, StepNote Quantized)> allStepNotes,
        IReadOnlyDictionary<int, string> midiTrackNames,
        MidiSoundFontChoice? soundFont,
        IReadOnlyDictionary<(int MidiTrackIndex, int Channel), List<(double Step, double Semitones)>> bendsByChannel,
        out int foldedUnisonCount)
    {
        var candidates = new List<SlawTrackCandidate>();
        var groups = allStepNotes
            .GroupBy(entry => (entry.Source.MidiTrackIndex, entry.Source.Channel))
            .OrderBy(group => group.Key.MidiTrackIndex).ThenBy(group => group.Key.Channel);

        // A MIDI track name only identifies an instrument when it is not shared:
        // in single-track files it is usually the song title.
        var channelCountPerMidiTrack = allStepNotes
            .GroupBy(entry => entry.Source.MidiTrackIndex)
            .ToDictionary(trackGroup => trackGroup.Key, trackGroup => trackGroup.Select(entry => entry.Source.Channel).Distinct().Count());

        foreach (var group in groups)
        {
            midiTrackNames.TryGetValue(group.Key.MidiTrackIndex, out var midiTrackName);
            if (channelCountPerMidiTrack.GetValueOrDefault(group.Key.MidiTrackIndex) > 1)
            {
                midiTrackName = null;
            }
            if (group.Key.Channel == DrumChannelIndex && soundFont?.Bank.DrumKit() is { } drumKit)
            {
                // The font's kit plays every drum note itself, so the channel stays one pitched track.
                var kitCandidate = new SlawTrackCandidate
                {
                    Name = string.IsNullOrWhiteSpace(midiTrackName) ? "Drums" : $"{midiTrackName}: Drums",
                    Instrument = "soundfont",
                    Gain = 0.6,
                    SoundFont = new SoundFontReference { Path = soundFont.Path, Bank = drumKit.Bank, Program = drumKit.Program, PresetName = drumKit.Name },
                };
                kitCandidate.Notes.AddRange(group.Select(entry => entry.Quantized));
                candidates.Add(kitCandidate);
                continue;
            }
            if (group.Key.Channel == DrumChannelIndex)
            {
                foreach (var drumGroup in group.GroupBy(entry => ClassifyDrum(entry.Source.MidiNote)).OrderBy(drumGroup => drumGroup.Key))
                {
                    var dominantDrumNote = drumGroup.GroupBy(entry => entry.Source.MidiNote).OrderByDescending(noteGroup => noteGroup.Count()).First().Key;
                    var drumName = GeneralMidiPercussionNames.TryGetValue(dominantDrumNote, out var percussionName)
                        ? percussionName
                        : char.ToUpperInvariant(drumGroup.Key[0]) + drumGroup.Key[1..];
                    var candidate = new SlawTrackCandidate
                    {
                        Name = string.IsNullOrWhiteSpace(midiTrackName) ? $"Drums: {drumName}" : $"{midiTrackName}: {drumName}",
                        Instrument = drumGroup.Key,
                        Gain = drumGroup.Key is "hat" or "shaker" ? 0.4 : 0.85,
                        Percussive = true,
                    };
                    candidate.Notes.AddRange(drumGroup.Select(entry => entry.Quantized));
                    candidates.Add(candidate);
                }
                continue;
            }

            var program = group.OrderBy(entry => entry.Source.StartTick).First().Source.Program;
            var fontPreset = soundFont?.Bank.FindPreset(0, program ?? 0);
            var melodic = new SlawTrackCandidate
            {
                Name = string.IsNullOrWhiteSpace(midiTrackName) ? DescribeProgram(program) : midiTrackName,
                Instrument = fontPreset is null ? InstrumentForProgram(program) : "soundfont",
                SoundFont = fontPreset is null
                    ? null
                    : new SoundFontReference { Path = soundFont!.Path, Bank = fontPreset.Bank, Program = fontPreset.Program, PresetName = fontPreset.Name },
            };
            melodic.Notes.AddRange(group.Select(entry => entry.Quantized));
            if (bendsByChannel.TryGetValue(group.Key, out var channelBends))
            {
                melodic.Bends.AddRange(channelBends);
            }
            candidates.Add(melodic);
        }

        candidates = FoldUnisonDoubles(candidates, out foldedUnisonCount);

        // Duplicate names get numbered, because "Piano" twice is a naming collision and "Piano 1" is a taxonomy.
        var renamed = new List<SlawTrackCandidate>();
        foreach (var candidate in candidates)
        {
            var duplicates = candidates.Where(other => other.Name == candidate.Name).ToList();
            if (duplicates.Count <= 1)
            {
                renamed.Add(candidate);
                continue;
            }
            renamed.Add(candidate.CopyWith($"{candidate.Name} {duplicates.IndexOf(candidate) + 1}", candidate.Gain));
        }
        return renamed;
    }

    /// <summary>
    /// Arrangers often double a part on a second channel to thicken it (strings
    /// in unison are a classic). slaw would render two identical voices anyway,
    /// so identical tracks fold into one, a little louder.
    /// </summary>
    private static List<SlawTrackCandidate> FoldUnisonDoubles(List<SlawTrackCandidate> candidates, out int foldedUnisonCount)
    {
        // Two channels only fold when they would also sound the same: same notes, same bends and, with a font, the same preset.
        static string NoteSignature(SlawTrackCandidate candidate) =>
            candidate.SoundFont?.Fingerprint + "|" +
            string.Join(';', candidate.Bends.Select(bend => FormattableString.Invariant($"{bend.Step:R}:{bend.Semitones:R}"))) + "|" +
            string.Join(';', candidate.Notes.OrderBy(note => note.StartStep).ThenBy(note => note.MidiNote).Select(note => $"{note.StartStep}:{note.MidiNote}:{note.LengthInSteps}"));

        var folded = new List<SlawTrackCandidate>();
        var foldedBySignature = new Dictionary<string, (SlawTrackCandidate Candidate, int Copies)>();
        foldedUnisonCount = 0;
        foreach (var candidate in candidates)
        {
            var signature = $"{candidate.Percussive}|{NoteSignature(candidate)}";
            if (foldedBySignature.TryGetValue(signature, out var existing))
            {
                foldedBySignature[signature] = (existing.Candidate, existing.Copies + 1);
                foldedUnisonCount++;
                continue;
            }
            foldedBySignature[signature] = (candidate, 1);
            folded.Add(candidate);
        }
        return folded.Select(candidate =>
        {
            var copies = foldedBySignature[$"{candidate.Percussive}|{NoteSignature(candidate)}"].Copies;
            if (copies == 1)
            {
                return candidate;
            }
            return candidate.CopyWith($"{candidate.Name} ×{copies}", Math.Min(1.0, candidate.Gain * (1 + 0.25 * (copies - 1))));
        }).ToList();
    }

    /// <summary>
    /// Maps General MIDI percussion onto slaw's drum voices. Low drums of
    /// any tradition (toms, congas, bongos, timbales — a darbuka's doum) become
    /// the kick; high ones (the tek) the snare; wood and hand percussion the
    /// clap; cabasa and maracas the shaker; metal the hat.
    /// </summary>
    private static string ClassifyDrum(int midiNote) => midiNote switch
    {
        35 or 36 or 41 or 43 or 45 or 61 or 64 or 66 => "kick",
        37 or 38 or 40 or 47 or 48 or 50 or 60 or 62 or 63 or 65 => "snare",
        39 or 54 or 75 or 76 or 77 or 78 or 79 => "clap",
        69 or 70 => "shaker",
        _ => "hat",
    };

    private static readonly Dictionary<int, string> GeneralMidiPercussionNames = new()
    {
        [35] = "Acoustic Kick",
        [36] = "Kick",
        [37] = "Side Stick",
        [38] = "Snare",
        [39] = "Hand Clap",
        [40] = "Electric Snare",
        [41] = "Low Floor Tom",
        [42] = "Closed Hat",
        [43] = "High Floor Tom",
        [44] = "Pedal Hat",
        [45] = "Low Tom",
        [46] = "Open Hat",
        [47] = "Low-Mid Tom",
        [48] = "High-Mid Tom",
        [49] = "Crash",
        [50] = "High Tom",
        [51] = "Ride",
        [52] = "China",
        [53] = "Ride Bell",
        [54] = "Tambourine",
        [55] = "Splash",
        [56] = "Cowbell",
        [57] = "Crash 2",
        [58] = "Vibraslap",
        [59] = "Ride 2",
        [60] = "High Bongo",
        [61] = "Low Bongo",
        [62] = "Muted Conga",
        [63] = "Open Conga",
        [64] = "Low Conga",
        [65] = "High Timbale",
        [66] = "Low Timbale",
        [67] = "High Agogo",
        [68] = "Low Agogo",
        [69] = "Cabasa",
        [70] = "Maracas",
        [73] = "Short Guiro",
        [74] = "Long Guiro",
        [75] = "Claves",
        [76] = "High Wood Block",
        [77] = "Low Wood Block",
        [78] = "Muted Cuica",
        [79] = "Open Cuica",
        [80] = "Muted Triangle",
        [81] = "Open Triangle",
    };

    private static readonly string[] GeneralMidiProgramNames =
    {
        "Grand Piano", "Bright Piano", "Electric Grand", "Honky-Tonk", "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavinet",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ", "Reed Organ", "Accordion", "Harmonica", "Bandoneon",
        "Nylon Guitar", "Steel Guitar", "Jazz Guitar", "Clean Guitar", "Muted Guitar", "Overdrive Guitar", "Distortion Guitar", "Guitar Harmonics",
        "Acoustic Bass", "Fingered Bass", "Picked Bass", "Fretless Bass", "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings", "Harp", "Timpani",
        "String Ensemble", "Slow Strings", "Synth Strings 1", "Synth Strings 2", "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit",
        "Trumpet", "Trombone", "Tuba", "Muted Trumpet", "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2",
        "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe", "English Horn", "Bassoon", "Clarinet",
        "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Square Lead", "Saw Lead", "Calliope Lead", "Chiff Lead", "Charang Lead", "Voice Lead", "Fifths Lead", "Bass + Lead",
        "New Age Pad", "Warm Pad", "Polysynth Pad", "Choir Pad", "Bowed Pad", "Metallic Pad", "Halo Pad", "Sweep Pad",
        "Rain FX", "Soundtrack FX", "Crystal FX", "Atmosphere FX", "Brightness FX", "Goblins FX", "Echoes FX", "Sci-Fi FX",
        "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bagpipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause", "Gunshot",
    };

    private static string DescribeProgram(int? program) =>
        program is { } knownProgram && knownProgram >= 0 && knownProgram < GeneralMidiProgramNames.Length
            ? GeneralMidiProgramNames[knownProgram]
            : DescribeProgramFamily(program);

    private static string InstrumentForProgram(int? program) => program switch
    {
        null => "triangle",
        <= 7 => "epiano",
        <= 15 => "mallet",
        <= 23 => "organ",
        <= 31 => "guitar",
        <= 47 => "saw",
        <= 55 => "triangle",
        <= 63 => "square",
        <= 71 => "triangle",
        <= 79 => "flute",
        <= 87 => "square",
        <= 95 => "triangle",
        <= 103 => "noise",
        105 => "guitar",
        108 or 112 or 114 => "mallet",
        _ => "pluck",
    };

    private static string DescribeProgramFamily(int? program) => program switch
    {
        null => "Synth",
        <= 7 => "Piano",
        <= 15 => "Chromatic Percussion",
        <= 23 => "Organ",
        <= 31 => "Guitar",
        <= 39 => "Bass",
        <= 47 => "Strings",
        <= 55 => "Ensemble",
        <= 63 => "Brass",
        <= 71 => "Reed",
        <= 79 => "Pipe",
        <= 87 => "Lead",
        <= 95 => "Pad",
        <= 103 => "FX",
        _ => "Synth",
    };

    private static ParsedMidiFile Parse(byte[] bytes)
    {
        if (bytes is null || bytes.Length < 14 || bytes[0] != 'M' || bytes[1] != 'T' || bytes[2] != 'h' || bytes[3] != 'd')
            throw new InvalidDataException("This does not look like a Standard MIDI File (missing the MThd header). We checked twice.");

        var headerLength = ReadBigEndian(bytes, 4, 4);
        if (headerLength < 6 || 8 + headerLength > bytes.Length)
            throw new InvalidDataException("This MIDI file has a corrupt header. A fresh export is the recommended remediation.");

        var format = (int)ReadBigEndian(bytes, 8, 2);
        var declaredTrackCount = (int)ReadBigEndian(bytes, 10, 2);
        var rawDivision = (int)ReadBigEndian(bytes, 12, 2);
        if (format > 2)
            throw new InvalidDataException($"MIDI format {format} is not one we have heard of.");
        if ((rawDivision & 0x8000) != 0)
            throw new InvalidDataException("SMPTE-timed MIDI files are not supported: slaw counts in musical time, not wall-clock time.");
        if (rawDivision == 0)
            throw new InvalidDataException("This MIDI file declares zero ticks per quarter note, which is a bold interpretation of time.");

        var notes = new List<ParsedNote>();
        var pitchBends = new List<ParsedPitchBend>();
        var trackNames = new Dictionary<int, string>();
        double? tempoBpm = null;
        var midiTrackCount = 0;
        long position = 8 + headerLength;

        while (position + 8 <= bytes.Length && midiTrackCount < Math.Max(declaredTrackCount, 4096))
        {
            var isTrackChunk = bytes[position] == 'M' && bytes[position + 1] == 'T' && bytes[position + 2] == 'r' && bytes[position + 3] == 'k';
            var chunkLength = ReadBigEndian(bytes, (int)position + 4, 4);
            var chunkStart = (int)position + 8;
            var chunkEnd = (int)Math.Min(bytes.Length, chunkStart + chunkLength);
            if (isTrackChunk)
            {
                ParseTrackChunk(bytes, chunkStart, chunkEnd, midiTrackCount, notes, pitchBends, trackNames, ref tempoBpm);
                midiTrackCount++;
            }
            position = chunkStart + chunkLength;
        }

        if (midiTrackCount == 0)
            throw new InvalidDataException("This MIDI file has no tracks. It is a header with aspirations.");
        return new ParsedMidiFile(format, rawDivision, tempoBpm, notes, trackNames, midiTrackCount, pitchBends);
    }

    private static void ParseTrackChunk(
        byte[] bytes, int start, int end, int midiTrackIndex,
        List<ParsedNote> notes, List<ParsedPitchBend> pitchBends, Dictionary<int, string> trackNames, ref double? tempoBpm)
    {
        var cursor = start;
        long tick = 0;
        var runningStatus = 0;
        var currentPrograms = new int?[16];
        // Pitch-bend sensitivity per channel (RPN 0): ±2 semitones unless the file says otherwise.
        var bendRangeSemitones = Enumerable.Repeat(2.0, 16).ToArray();
        var selectedParameterMsb = Enumerable.Repeat(127, 16).ToArray();
        var selectedParameterLsb = Enumerable.Repeat(127, 16).ToArray();
        var openNotes = new Dictionary<(int Channel, int Note), Queue<(long StartTick, int? Program)>>();

        try
        {
            while (cursor < end)
            {
                tick += ReadVariableLength(bytes, ref cursor, end);
                var statusByte = bytes[cursor];
                if (statusByte >= 0x80)
                {
                    cursor++;
                    if (statusByte < 0xF0)
                        runningStatus = statusByte;
                }
                else
                {
                    if (runningStatus == 0)
                        break;
                    statusByte = (byte)runningStatus;
                }

                if (statusByte == 0xFF)
                {
                    runningStatus = 0;
                    var metaType = bytes[cursor++];
                    var metaLength = (int)ReadVariableLength(bytes, ref cursor, end);
                    if (cursor + metaLength > end)
                        break;
                    if (metaType == 0x51 && metaLength >= 3 && tempoBpm is null)
                    {
                        var microsecondsPerQuarter = (bytes[cursor] << 16) | (bytes[cursor + 1] << 8) | bytes[cursor + 2];
                        if (microsecondsPerQuarter > 0)
                            tempoBpm = Math.Round(60_000_000.0 / microsecondsPerQuarter, 1);
                    }
                    else if (metaType == 0x03 && !trackNames.ContainsKey(midiTrackIndex))
                    {
                        var name = DecodeMidiText(bytes.AsSpan(cursor, metaLength)).Trim('\0', ' ');
                        if (name.Length > 0)
                            trackNames[midiTrackIndex] = name;
                    }
                    cursor += metaLength;
                    if (metaType == 0x2F)
                        break;
                    continue;
                }

                if (statusByte == 0xF0 || statusByte == 0xF7)
                {
                    runningStatus = 0;
                    var sysexLength = (int)ReadVariableLength(bytes, ref cursor, end);
                    cursor += sysexLength;
                    continue;
                }

                var messageType = statusByte & 0xF0;
                var channel = statusByte & 0x0F;
                if (messageType == 0xC0 || messageType == 0xD0)
                {
                    var dataByte = bytes[cursor++];
                    if (messageType == 0xC0)
                        currentPrograms[channel] = dataByte & 0x7F;
                    continue;
                }
                if (messageType < 0x80 || messageType > 0xE0)
                    break;

                var firstData = bytes[cursor++] & 0x7F;
                var secondData = bytes[cursor++] & 0x7F;
                if (messageType == 0xB0)
                {
                    switch (firstData)
                    {
                        case 101:
                            selectedParameterMsb[channel] = secondData;
                            break;
                        case 100:
                            selectedParameterLsb[channel] = secondData;
                            break;
                        case 6 when selectedParameterMsb[channel] == 0 && selectedParameterLsb[channel] == 0:
                            bendRangeSemitones[channel] = secondData + bendRangeSemitones[channel] % 1;
                            break;
                        case 38 when selectedParameterMsb[channel] == 0 && selectedParameterLsb[channel] == 0:
                            bendRangeSemitones[channel] = Math.Floor(bendRangeSemitones[channel]) + secondData / 100.0;
                            break;
                    }
                    continue;
                }
                if (messageType == 0xE0)
                {
                    var bendAmount = ((secondData << 7) | firstData) - 8192;
                    pitchBends.Add(new ParsedPitchBend(midiTrackIndex, channel, tick, bendAmount / 8192.0 * bendRangeSemitones[channel]));
                    continue;
                }
                var isNoteOn = messageType == 0x90 && secondData > 0;
                var isNoteOff = messageType == 0x80 || (messageType == 0x90 && secondData == 0);
                var noteKey = (channel, firstData);
                if (isNoteOn)
                {
                    if (!openNotes.TryGetValue(noteKey, out var startQueue))
                        openNotes[noteKey] = startQueue = new Queue<(long, int?)>();
                    startQueue.Enqueue((tick, currentPrograms[channel]));
                }
                else if (isNoteOff && openNotes.TryGetValue(noteKey, out var pendingStarts) && pendingStarts.Count > 0)
                {
                    var (noteStartTick, noteProgram) = pendingStarts.Dequeue();
                    notes.Add(new ParsedNote(midiTrackIndex, channel, firstData, noteStartTick, tick, noteProgram));
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            // Truncated track: keep whatever we managed to read and move on with our lives.
        }
        catch (InvalidDataException)
        {
            // Malformed variable-length quantity: same remediation.
        }

        foreach (var ((channel, note), pendingStarts) in openNotes)
        {
            foreach (var (noteStartTick, noteProgram) in pendingStarts)
                notes.Add(new ParsedNote(midiTrackIndex, channel, note, noteStartTick, Math.Max(tick, noteStartTick), noteProgram));
        }
    }

    private static readonly System.Text.Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// MIDI text has no declared encoding. Valid UTF-8 wins; plain ASCII is
    /// ASCII; text that is mostly high bytes is assumed to come from a legacy
    /// non-Latin code page (Windows-1256 Arabic, then 1251 Cyrillic, 1255
    /// Hebrew); anything else is Windows-1252. A heuristic, but a better guess
    /// than rendering a song title as "íÇ ØÇÑÔí".
    /// </summary>
    internal static string DecodeMidiText(ReadOnlySpan<byte> textBytes)
    {
        try
        {
            return StrictUtf8.GetString(textBytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            // Not UTF-8; consult the legacy code pages.
        }
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var highByteCount = 0;
        var letterishByteCount = 0;
        foreach (var textByte in textBytes)
        {
            if (textByte >= 0x80)
            {
                highByteCount++;
            }
            if (textByte >= 0x80 || char.IsAsciiLetter((char)textByte))
            {
                letterishByteCount++;
            }
        }
        var codePage = letterishByteCount > 0 && highByteCount * 2 >= letterishByteCount ? 1256 : 1252;
        return System.Text.Encoding.GetEncoding(codePage).GetString(textBytes);
    }

    private static long ReadBigEndian(byte[] bytes, int offset, int byteCount)
    {
        long value = 0;
        for (var index = 0; index < byteCount; index++)
            value = (value << 8) | bytes[offset + index];
        return value;
    }

    private static long ReadVariableLength(byte[] bytes, ref int cursor, int end)
    {
        long value = 0;
        for (var byteIndex = 0; byteIndex < 4; byteIndex++)
        {
            if (cursor >= end)
                throw new InvalidDataException("truncated variable-length quantity");
            var current = bytes[cursor++];
            value = (value << 7) | (uint)(current & 0x7F);
            if ((current & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("overlong variable-length quantity");
    }
}
