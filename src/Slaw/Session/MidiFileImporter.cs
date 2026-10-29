using System.Globalization;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// The outcome of a MIDI import: a project, plus a ledger itemizing everything the
/// import had to quietly give up on along the way. Transparency is a core value.
/// </summary>
internal sealed record MidiImportResult(SlawProject Project, IReadOnlyList<string> ImportLedger);

/// <summary>
/// Converts Standard MIDI Files into slaw projects and patterns. Slaw's notation is
/// monophonic per step, polyphonic per chord, and lossy by design: overlapping
/// notes are approximated, velocities are synergized away, and pitch bend is
/// left as an exercise for a future quarter.
/// </summary>
internal static class MidiFileImporter
{
    private const int StepsPerBar = 16;
    private const int StepsPerSection = 64;
    private const int DrumChannelIndex = 9;
    private const int MaximumSupportedSongSteps = 4096 * StepsPerBar;

    private sealed record ParsedNote(int MidiTrackIndex, int Channel, int MidiNote, long StartTick, long EndTick, int? Program);

    private sealed record ParsedMidiFile(
        int Format,
        int Division,
        double? TempoBpm,
        IReadOnlyList<ParsedNote> Notes,
        IReadOnlyDictionary<int, string> TrackNames,
        int MidiTrackCount);

    private sealed record StepNote(int StartStep, int LengthInSteps, int MidiNote);

    private sealed class SlawTrackCandidate
    {
        public string Name { get; init; } = "";
        public string Instrument { get; init; } = "triangle";
        public double Gain { get; init; } = 0.7;
        public bool Percussive { get; init; }
        public List<StepNote> Notes { get; } = new();
    }

    /// <summary>True for .mid, .midi and .smf files, case-insensitively.</summary>
    public static bool IsMidiFile(string path)
    {
        var extension = Path.GetExtension(path ?? string.Empty);
        return extension.Equals(".mid", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".midi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".smf", StringComparison.OrdinalIgnoreCase);
    }

    public static MidiImportResult Import(string path) =>
        Import(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    public static MidiImportResult Import(byte[] midiFileBytes, string projectName)
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

        var candidates = BuildTrackCandidates(allStepNotes, parsedMidiFile.TrackNames);

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
            Name = string.IsNullOrWhiteSpace(projectName) ? "imported slop" : projectName,
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
            project.Tracks.Add(new SlawTrack { Name = candidate.Name, Instrument = candidate.Instrument, Gain = candidate.Gain });
        }

        var sectionCount = Math.Max(1, (int)Math.Ceiling((songEndStep - songStartStep) / (double)StepsPerSection));
        var sceneSignatures = new List<string>();
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var sectionStartStep = songStartStep + sectionIndex * StepsPerSection;
            var sectionPatterns = fullPatterns
                .Select(fullPattern => SlicePattern(fullPattern, sectionStartStep, StepsPerSection))
                .ToList();
            var sectionSignature = string.Join("##", sectionPatterns.Select(pattern =>
                pattern.All(step => step == NotationResolver.RestSymbol) ? "<silent>" : string.Join(' ', pattern)));

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
                        : new SlawClip { Name = $"{project.Tracks[trackIndex].Name} {sceneIndex + 1}", Pattern = pattern });
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
        var pattern = Enumerable.Repeat(NotationResolver.RestSymbol, Math.Max(1, totalStepCount)).ToArray();
        var notesByStartStep = notes.Where(note => note.StartStep >= 0 && note.StartStep < pattern.Length).GroupBy(note => note.StartStep).ToList();
        foreach (var startingNotes in notesByStartStep)
        {
            pattern[startingNotes.Key] = percussive
                ? NotationResolver.TriggerSymbol
                : NotationResolver.FormatChord(startingNotes.Select(note => note.MidiNote));
        }

        if (!percussive)
        {
            foreach (var startingNotes in notesByStartStep)
            {
                var sustainEnd = Math.Min(pattern.Length, startingNotes.Key + startingNotes.Max(note => note.LengthInSteps));
                for (var step = startingNotes.Key + 1; step < sustainEnd; step++)
                {
                    if (pattern[step] != NotationResolver.RestSymbol && pattern[step] != NotationResolver.SustainSymbol)
                        break;
                    pattern[step] = NotationResolver.SustainSymbol;
                }
            }
        }
        return pattern;
    }

    private static List<SlawTrackCandidate> BuildTrackCandidates(
        IReadOnlyList<(ParsedNote Source, StepNote Quantized)> allStepNotes,
        IReadOnlyDictionary<int, string> midiTrackNames)
    {
        var candidates = new List<SlawTrackCandidate>();
        var groups = allStepNotes
            .GroupBy(entry => (entry.Source.MidiTrackIndex, entry.Source.Channel))
            .OrderBy(group => group.Key.MidiTrackIndex).ThenBy(group => group.Key.Channel);

        foreach (var group in groups)
        {
            midiTrackNames.TryGetValue(group.Key.MidiTrackIndex, out var midiTrackName);
            if (group.Key.Channel == DrumChannelIndex)
            {
                foreach (var drumGroup in group.GroupBy(entry => ClassifyDrum(entry.Source.MidiNote)).OrderBy(drumGroup => drumGroup.Key))
                {
                    var drumName = char.ToUpperInvariant(drumGroup.Key[0]) + drumGroup.Key[1..];
                    var candidate = new SlawTrackCandidate
                    {
                        Name = string.IsNullOrWhiteSpace(midiTrackName) ? $"Drums: {drumName}" : $"{midiTrackName}: {drumName}",
                        Instrument = drumGroup.Key,
                        Gain = drumGroup.Key == "hat" ? 0.4 : 0.85,
                        Percussive = true,
                    };
                    candidate.Notes.AddRange(drumGroup.Select(entry => entry.Quantized));
                    candidates.Add(candidate);
                }
                continue;
            }

            var program = group.OrderBy(entry => entry.Source.StartTick).First().Source.Program;
            var melodic = new SlawTrackCandidate
            {
                Name = string.IsNullOrWhiteSpace(midiTrackName) ? DescribeProgramFamily(program) : midiTrackName,
                Instrument = InstrumentForProgram(program),
            };
            melodic.Notes.AddRange(group.Select(entry => entry.Quantized));
            candidates.Add(melodic);
        }

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
            var numbered = new SlawTrackCandidate
            {
                Name = $"{candidate.Name} {duplicates.IndexOf(candidate) + 1}",
                Instrument = candidate.Instrument,
                Gain = candidate.Gain,
                Percussive = candidate.Percussive,
            };
            numbered.Notes.AddRange(candidate.Notes);
            renamed.Add(numbered);
        }
        return renamed;
    }

    private static string ClassifyDrum(int midiNote) => midiNote switch
    {
        35 or 36 => "kick",
        37 or 38 or 40 => "snare",
        39 => "clap",
        _ => "hat",
    };

    private static string InstrumentForProgram(int? program) => program switch
    {
        null => "triangle",
        <= 15 => "pluck",
        <= 23 => "square",
        <= 31 => "pluck",
        <= 47 => "saw",
        <= 55 => "triangle",
        <= 63 => "square",
        <= 79 => "triangle",
        <= 87 => "square",
        <= 95 => "triangle",
        <= 103 => "noise",
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
                ParseTrackChunk(bytes, chunkStart, chunkEnd, midiTrackCount, notes, trackNames, ref tempoBpm);
                midiTrackCount++;
            }
            position = chunkStart + chunkLength;
        }

        if (midiTrackCount == 0)
            throw new InvalidDataException("This MIDI file has no tracks. It is a header with aspirations.");
        return new ParsedMidiFile(format, rawDivision, tempoBpm, notes, trackNames, midiTrackCount);
    }

    private static void ParseTrackChunk(
        byte[] bytes, int start, int end, int midiTrackIndex,
        List<ParsedNote> notes, Dictionary<int, string> trackNames, ref double? tempoBpm)
    {
        var cursor = start;
        long tick = 0;
        var runningStatus = 0;
        var currentPrograms = new int?[16];
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
                        var name = System.Text.Encoding.Latin1.GetString(bytes, cursor, metaLength).Trim('\0', ' ');
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
