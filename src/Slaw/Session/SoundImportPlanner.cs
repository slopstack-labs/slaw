using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Session;

/// <summary>How imported sounds join the project.</summary>
internal enum SoundImportMode
{
    /// <summary>All of them on one track, one sound per key.</summary>
    DrumKit,

    /// <summary>One sampler track per sound, pitched across the keyboard.</summary>
    SeparateTracks,

    /// <summary>Nothing in the project: the folders join Browser → Samples.</summary>
    SampleLibrary,

    /// <summary>Ableton presets (.adg, .adv): one multisampler track per preset.</summary>
    AbletonPreset,
}

/// <summary>What a file name suggests a sound is. <see cref="Order"/> is its place in a kit.</summary>
internal sealed record SoundKind(string Label, int Order);

/// <summary>A kit's audio laid end to end, and the slice lines between its sounds.</summary>
internal sealed record AssembledKit(float[] Samples, int SampleRate, List<double> SlicePointsSeconds);

/// <summary>
/// The thinking behind the Import window, with no window attached. A drum kit needs
/// nothing new from the project format or the engine: the sounds are joined into one
/// WAV and played by a sampler in Slice mode, which already gives every note its own
/// slice at its original pitch, in bounces too.
/// </summary>
internal static class SoundImportPlanner
{
    public const string ImportsFolderName = "imports";

    /// <summary>A kit starts on C4 and may not walk off the top of the keyboard, or out of anyone's reach.</summary>
    public const int MaximumKitSounds = 64;

    private static readonly SoundKind Unsorted = new("Sound", 99);

    // First match wins, so "open hat" is tested before "hat" and "kick" before the catch-all "bd".
    private static readonly (string[] Words, SoundKind Kind)[] KindsByWord =
    {
        (new[] { "kick", "bassdrum", "bass drum", "bd" }, new SoundKind("Kick", 0)),
        (new[] { "snare", "sd", "rimshot", "rim" }, new SoundKind("Snare", 1)),
        (new[] { "clap", "snap" }, new SoundKind("Clap", 2)),
        (new[] { "closed hat", "closed hi", "closedhat", "chh", "ch" }, new SoundKind("Closed hat", 3)),
        (new[] { "open hat", "open hi", "openhat", "ohh", "oh" }, new SoundKind("Open hat", 4)),
        (new[] { "hihat", "hi-hat", "hi hat", "hat", "hh" }, new SoundKind("Hat", 5)),
        (new[] { "tom" }, new SoundKind("Tom", 6)),
        (new[] { "conga", "bongo", "shaker", "tamb", "cowbell", "clave", "perc" }, new SoundKind("Percussion", 7)),
        (new[] { "crash", "ride", "cymbal" }, new SoundKind("Cymbal", 8)),
        (new[] { "bass", "sub" }, new SoundKind("Bass", 9)),
        (new[] { "vocal", "vox" }, new SoundKind("Vocal", 10)),
        (new[] { "loop", "break" }, new SoundKind("Loop", 11)),
        (new[] { "fx", "riser", "impact", "sweep" }, new SoundKind("FX", 12)),
    };

    public static SoundKind Classify(string filePath)
    {
        var normalizedName = " " + Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant().Replace('_', ' ').Replace('.', ' ') + " ";
        foreach (var (words, kind) in KindsByWord)
        {
            foreach (var word in words)
            {
                // Short abbreviations only count as whole words: "ch" is not in "choir", nor "bd" in "abdicate".
                var searchedWord = word.Length <= 3 ? $" {word} " : word;
                if (normalizedName.Contains(searchedWord, StringComparison.Ordinal))
                {
                    return kind;
                }
            }
        }
        return Unsorted;
    }

    /// <summary>Kick first and FX last, then by name with numbers in counting order: "Kick 2" before "Kick 10".</summary>
    public static IReadOnlyList<string> SortForKit(IEnumerable<string> filePaths) =>
        filePaths
            .OrderBy(filePath => Classify(filePath).Order)
            .ThenBy(filePath => NaturalSortKey(Path.GetFileNameWithoutExtension(filePath)), StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NaturalSortKey(string fileStem) =>
        System.Text.RegularExpressions.Regex.Replace(fileStem, @"\d+", digits => digits.Value.PadLeft(10, '0'));

    /// <summary>
    /// Joins the sounds end to end at the rate most of them already use, with the
    /// others resampled to it. Mixed rates play at the right pitch, not the wrong one.
    /// </summary>
    public static AssembledKit AssembleKit(IReadOnlyList<SampleData> sounds)
    {
        if (sounds.Count == 0)
        {
            throw new InvalidDataException("A drum kit needs at least one sound.");
        }
        var kitRate = sounds.GroupBy(sound => sound.SampleRate).OrderByDescending(rateGroup => rateGroup.Count()).ThenByDescending(rateGroup => rateGroup.Key).First().Key;
        var parts = sounds.Select(sound => SampleLibrary.ResampleLinearly(sound.Samples, sound.SampleRate, kitRate)).ToList();
        var totalFrames = parts.Sum(part => (long)part.Length);
        if (totalFrames > (long)SampleLibrary.MaximumDecodedSeconds * kitRate)
        {
            throw new InvalidDataException($"These sounds add up to more than {SampleLibrary.MaximumDecodedSeconds / 60} minutes, which is too long for one kit. Import the long ones as separate tracks.");
        }

        var kitSamples = new float[totalFrames];
        var slicePoints = new List<double>();
        var writePosition = 0;
        foreach (var part in parts)
        {
            if (writePosition > 0)
            {
                // Half a frame past the boundary, so the sampler's truncation lands exactly on it.
                slicePoints.Add((writePosition + 0.5) / kitRate);
            }
            part.CopyTo(kitSamples, writePosition);
            writePosition += part.Length;
        }
        return new AssembledKit(kitSamples, kitRate, slicePoints);
    }

    /// <summary>The kit's track: an ordinary sampler in Slice mode, with an empty clip to write the beat in.</summary>
    public static SlawTrack CreateKitTrack(SlawProject project, string kitName, string kitWavePath, AssembledKit kit, int sceneIndex)
    {
        var kitTrack = AudioTakeAssembler.CreateTakeTrack(project, template: null);
        kitTrack.Name = kitName;
        kitTrack.Sample = Path.GetFullPath(kitWavePath);
        kitTrack.Gain = 0.8;
        kitTrack.SampleEditing = new SampleEditSettings { PlaybackMode = SampleEditSettings.SliceMode, SlicePointsSeconds = kit.SlicePointsSeconds };
        kitTrack.Clips[sceneIndex] = SlawClip.Empty(project.Scenes[sceneIndex], project.SceneLengthInSteps(sceneIndex));
        return kitTrack;
    }

    /// <summary>One sampler per sound, pointing at the original file.</summary>
    public static SlawTrack CreateSoundTrack(SlawProject project, string soundPath, int sceneIndex)
    {
        var soundStem = Path.GetFileNameWithoutExtension(soundPath);
        var soundTrack = AudioTakeAssembler.CreateTakeTrack(project, template: null);
        soundTrack.Name = soundStem.Length > 24 ? soundStem[..24] : soundStem;
        soundTrack.Sample = Path.GetFullPath(soundPath);
        soundTrack.Gain = 0.8;
        soundTrack.Clips[sceneIndex] = SlawClip.Empty(project.Scenes[sceneIndex], project.SceneLengthInSteps(sceneIndex));
        return soundTrack;
    }

    /// <summary>A preset's track: every recording as a zone, with an empty clip to write in.</summary>
    public static SlawTrack CreateRackTrack(SlawProject project, ImportedRack rack, int sceneIndex)
    {
        var rackTrack = AudioTakeAssembler.CreateTakeTrack(project, template: null);
        rackTrack.Name = rack.Name;
        rackTrack.Instrument = rack.Instrument;
        rackTrack.Gain = 0.8;
        rackTrack.SampleZones = rack.Zones.Count > 0 ? rack.Zones : null;
        rackTrack.InstrumentParameters = new Dictionary<string, double>(rack.InstrumentParameters);
        rackTrack.Effects = rack.Effects;
        rackTrack.Clips[sceneIndex] = SlawClip.Empty(project.Scenes[sceneIndex], project.SceneLengthInSteps(sceneIndex));
        return rackTrack;
    }

    /// <summary>Which key plays the sound at this place in a kit.</summary>
    public static string KeyForKitPosition(int kitPosition) =>
        NotationResolver.FormatMidiNote(NotationResolver.DefaultTriggerMidiNote + kitPosition);
}
