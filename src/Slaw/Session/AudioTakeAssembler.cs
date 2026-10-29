using System.Globalization;
using System.Text;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Session;

/// <summary>Where a take lands in its clip, and how much of its head is pre-roll or latency.</summary>
internal sealed record TakePlacement(int StartStep, int TrimFrames);

/// <summary>
/// Turns a recorded take into project data: a WAV on disk, a sampler that plays
/// it back unaltered, and a clip whose single note starts the take on the step
/// it was sung on. Pure functions over the project, so the studio's recording
/// workflow stays thin and this stays testable without a microphone.
/// </summary>
internal static class AudioTakeAssembler
{
    /// <summary>The note a take is triggered with; the sampler's root, so it plays at its recorded pitch.</summary>
    public const int TakeRootNote = 60;

    public const string RecordingsFolderName = "recordings";

    /// <summary>
    /// Places a take whose first captured frame sat at <paramref name="captureStartPosition"/>
    /// (in clip steps, as the performer heard it) on the next whole step, trimming
    /// the frames before that step plus the input latency.
    /// </summary>
    /// <param name="clipWasRunning">
    /// True when the clip was already looping as capture began, so the position
    /// wraps around the clip. False when the transport started after capture: a
    /// negative position is pre-roll before step 0.
    /// </param>
    public static TakePlacement Place(double captureStartPosition, bool clipWasRunning, int clipLength, double stepSeconds, double inputLatencySeconds, int sampleRate)
    {
        clipLength = Math.Max(1, clipLength);
        var startPosition = clipWasRunning
            ? (captureStartPosition % clipLength + clipLength) % clipLength
            : captureStartPosition;
        // A hair of tolerance so a take captured exactly on a step is not pushed to the next one.
        var startStep = Math.Max(0, (int)Math.Ceiling(startPosition - 1e-6));
        var trimSeconds = (startStep - startPosition) * stepSeconds + inputLatencySeconds;
        return new TakePlacement(startStep % clipLength, (int)Math.Round(Math.Max(0, trimSeconds) * sampleRate));
    }

    public static float[] TrimHead(float[] takeSamples, int trimFrames) =>
        trimFrames <= 0 ? takeSamples
        : trimFrames >= takeSamples.Length ? Array.Empty<float>()
        : takeSamples[trimFrames..];

    /// <summary>
    /// A track can hold one sample. If its current sample is still played by
    /// clips in other scenes, a new take goes on a fresh track instead of quietly
    /// replacing those performances.
    /// </summary>
    public static bool NeedsFreshTrack(SlawTrack track, int sceneIndex) =>
        track.Sample is not null &&
        track.Clips.Where((clip, clipSceneIndex) => clipSceneIndex != sceneIndex && clip is not null)
            .Any(clip => clip!.Pattern.Any(step => NotationResolver.ResolveOrRest(step).Kind == StepEventKind.Trigger));

    /// <summary>An empty sampler track for takes, modelled on <paramref name="template"/>'s mixer and effects if given.</summary>
    public static SlawTrack CreateTakeTrack(SlawProject project, SlawTrack? template)
    {
        string takeTrackName;
        if (template is null)
        {
            takeTrackName = $"Vocal Stakeholder {project.Tracks.Count(track => track.Name.StartsWith("Vocal Stakeholder", StringComparison.Ordinal)) + 1}";
        }
        else
        {
            // Takes of takes are still takes of the original: "Lead · take 3", never "Lead · take 2 · take 2".
            var originalName = TakeSuffix.Replace(template.Name, string.Empty);
            var takeNumber = project.Tracks.Count(track => track.Name == originalName || TakeSuffix.Replace(track.Name, string.Empty) == originalName) + 1;
            takeTrackName = $"{originalName} · take {takeNumber}";
        }
        return new SlawTrack
        {
            Name = takeTrackName,
            Instrument = "sampler",
            Gain = template?.Gain ?? 0.9,
            Pan = template?.Pan ?? 0,
            Effects = template?.Effects.Select(effect => new EffectDevice
            {
                Type = effect.Type,
                Enabled = effect.Enabled,
                Parameters = new Dictionary<string, double>(effect.Parameters),
                // A plugin effect without its plugin is remediated away, taking the vocal reverb with it.
                Plugin = effect.Plugin is { } plugin
                    ? new PluginReference { Path = plugin.Path, ClassId = plugin.ClassId, Name = plugin.Name, State = plugin.State }
                    : null,
            }).ToList() ?? new List<EffectDevice>(),
            Clips = Enumerable.Repeat<SlawClip?>(null, project.Scenes.Count).ToList(),
        };
    }

    private static readonly System.Text.RegularExpressions.Regex TakeSuffix = new(@"( · take \d+)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Loads a take into a track and writes its clip: one note at <paramref name="startStep"/>
    /// held to the clip's end, with the clip grown (in whole bars, up to the
    /// step limit) so the whole take fits before the loop comes around. The
    /// sampler follows note length, so a take longer than that is cut at the loop
    /// rather than piling up on itself.
    /// </summary>
    public static SlawClip ApplyTake(SlawProject project, SlawTrack track, int sceneIndex, string samplePath, int startStep, int takeFrameCount, int sampleRate)
    {
        while (track.Clips.Count <= sceneIndex)
        {
            track.Clips.Add(null);
        }
        var sceneName = sceneIndex < project.Scenes.Count ? project.Scenes[sceneIndex] : $"Scene {sceneIndex + 1}";
        var takeClip = track.Clips[sceneIndex] ??= SlawClip.Empty(sceneName, project.SceneLengthInSteps(sceneIndex));

        var takeSteps = (int)Math.Ceiling(takeFrameCount / (double)sampleRate / project.StepDurationSeconds);
        var requiredSteps = startStep + Math.Max(1, takeSteps);
        var clipLength = Math.Clamp((requiredSteps + 15) / 16 * 16, takeClip.Pattern.Count, ProjectAlignmentRemediator.MaximumStepCount);
        startStep = Math.Clamp(startStep, 0, clipLength - 1);

        // The take replaces whatever this clip played before: the sample it pointed at is gone.
        var takePattern = Enumerable.Repeat(NotationResolver.RestSymbol, clipLength).ToList();
        ClipNoteEditor.AddNote(takePattern, startStep, TakeRootNote, clipLength - startStep);
        takeClip.Pattern = takePattern;

        track.Instrument = "sampler";
        track.Sample = Path.GetFullPath(samplePath);
        track.SampleEditing = null;
        foreach (var (parameterKey, parameterValue) in UnalteredPlaybackParameters)
        {
            track.InstrumentParameters[parameterKey] = parameterValue;
        }
        return takeClip;
    }

    /// <summary>Longest timeline clip one slice of a take fills: the longest pattern a clip may have.</summary>
    public const int TimelineTakeSliceSteps = ProjectAlignmentRemediator.MaximumStepCount;

    /// <summary>A sliced take's first note: low, so a whole song's worth of slices still has notes to spare.</summary>
    public const int SlicedTakeRootNote = 24;

    /// <summary>
    /// A track can hold one sample, so a new timeline take goes on a fresh track
    /// whenever this one already plays a take somewhere (on the timeline or in a scene).
    /// </summary>
    public static bool NeedsFreshTimelineTrack(SlawProject project, SlawTrack track) =>
        track.Sample is not null &&
        (project.Timeline.Any(timelineClip => timelineClip.TrackId == track.Id) ||
         track.Clips.OfType<SlawClip>().Any(clip => clip.Pattern.Any(step => NotationResolver.ResolveOrRest(step).Kind == StepEventKind.Trigger)));

    /// <summary>
    /// Puts a take on the free timeline at <paramref name="startStep"/>. A take
    /// longer than one clip's pattern is sliced every <see cref="TimelineTakeSliceSteps"/>
    /// steps, one slice per consecutive timeline clip, so a whole song can be sung
    /// in one pass. Slices play one-shot: each runs to its end, with no gate
    /// cutting it short and nothing looping back over it. Returns the new clips.
    /// </summary>
    public static List<TimelineClip> ApplyTimelineTake(SlawProject project, SlawTrack track, string samplePath, int startStep, int takeFrameCount, int sampleRate)
    {
        var takeSteps = Math.Max(1, (int)Math.Ceiling(takeFrameCount / (double)sampleRate / project.StepDurationSeconds));
        var sliceCount = Math.Min((takeSteps + TimelineTakeSliceSteps - 1) / TimelineTakeSliceSteps, 127 - SlicedTakeRootNote + 1);
        var rootNote = sliceCount > 1 ? SlicedTakeRootNote : TakeRootNote;

        track.Instrument = "sampler";
        track.Sample = Path.GetFullPath(samplePath);
        foreach (var (parameterKey, parameterValue) in UnalteredPlaybackParameters)
        {
            track.InstrumentParameters[parameterKey] = parameterValue;
        }
        track.InstrumentParameters["root"] = rootNote;
        track.InstrumentParameters["hold"] = 0;
        track.SampleEditing = sliceCount > 1
            ? new SampleEditSettings
            {
                PlaybackMode = SampleEditSettings.SliceMode,
                SlicePointsSeconds = Enumerable.Range(1, sliceCount - 1)
                    .Select(sliceIndex => sliceIndex * TimelineTakeSliceSteps * project.StepDurationSeconds)
                    .ToList(),
            }
            : null;

        var takeClips = new List<TimelineClip>();
        for (var sliceIndex = 0; sliceIndex < sliceCount; sliceIndex++)
        {
            var sliceSteps = Math.Min(TimelineTakeSliceSteps, takeSteps - sliceIndex * TimelineTakeSliceSteps);
            var slicePattern = Enumerable.Repeat(NotationResolver.RestSymbol, sliceSteps).ToList();
            ClipNoteEditor.AddNote(slicePattern, 0, rootNote + sliceIndex, sliceSteps);
            takeClips.Add(new TimelineClip
            {
                TrackId = track.Id,
                StartStep = Math.Max(0, startStep) + sliceIndex * TimelineTakeSliceSteps,
                LengthSteps = sliceSteps,
                Clip = new SlawClip { Name = sliceCount > 1 ? $"{track.Name} {sliceIndex + 1}/{sliceCount}" : track.Name, Pattern = slicePattern },
            });
        }
        project.Timeline.AddRange(takeClips);
        return takeClips;
    }

    /// <summary>Sampler settings that play a take exactly as it was recorded, starting the moment its note does.</summary>
    public static IReadOnlyDictionary<string, double> UnalteredPlaybackParameters { get; } = new Dictionary<string, double>
    {
        ["root"] = TakeRootNote,
        ["transpose"] = 0,
        ["start"] = 0,
        ["end"] = 1,
        ["hold"] = 1,
        ["reverse"] = 0,
        ["attack"] = 0.5,
        ["release"] = 20,
        ["gain"] = 1,
        ["cutoff"] = 20000,
        ["humanize"] = 0,
    };

    /// <summary>"Vocal Stakeholder 1" recorded at 14:03:07 → "vocal-stakeholder-1-20261030-140307.wav".</summary>
    public static string TakeFileName(string trackName, DateTime recordedAt)
    {
        var fileStem = new StringBuilder();
        foreach (var nameCharacter in trackName.ToLowerInvariant())
        {
            fileStem.Append(char.IsAsciiLetterOrDigit(nameCharacter) ? nameCharacter : '-');
        }
        var collapsedStem = string.Join('-', fileStem.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return $"{(collapsedStem.Length == 0 ? "take" : collapsedStem)}-{recordedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.wav";
    }

    /// <summary>Writes a mono take as a 24-bit WAV (both channels identical, which the sampler sums back to mono).</summary>
    public static void WriteTakeWave(string outputPath, float[] takeSamples, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var takeBuffer = new StereoBuffer(takeSamples.Length, sampleRate);
        takeSamples.CopyTo(takeBuffer.Left, 0);
        takeSamples.CopyTo(takeBuffer.Right, 0);
        WaveFileMaterializer.Write(outputPath, takeBuffer, bitsPerSample: 24);
    }
}
