using System.Globalization;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// One clip placed freely on the timeline: a track, a start, a length, and the
/// clip itself (pattern and envelopes). The pattern loops inside the length, so
/// a one-bar groove stretched over eight bars plays eight times.
/// </summary>
internal sealed class TimelineClip
{
    public string Id { get; set; } = SlawTrack.NewIdentifier();
    public string TrackId { get; set; } = string.Empty;

    /// <summary>Where the clip starts, in sixteenth-note steps from the top of the timeline.</summary>
    public int StartStep { get; set; }

    /// <summary>How long the clip lasts, in steps; the pattern loops to fill it.</summary>
    public int LengthSteps { get; set; } = 16;

    public SlawClip Clip { get; set; } = new();

    public int EndStep => StartStep + LengthSteps;
}

/// <summary>The arrangement's loop brace: playback wraps from <see cref="EndStep"/> back to <see cref="StartStep"/>.</summary>
internal sealed class TimelineLoopRegion
{
    public int StartStep { get; set; }
    public int EndStep { get; set; } = 64;

    public bool IsValid => StartStep >= 0 && EndStep > StartStep;
}

/// <summary>
/// The arrangement: one linear timeline where clips sit at any step on any
/// track, independent of scenes. Scenes live in the session and can be placed
/// here (or launched over it) whenever they are useful.
/// Overlapping clips on one track are allowed and resolved at playback: the one
/// that started later wins, the way the clip on top wins in every arranger
/// since tape. Nothing is trimmed, so moving a clip away reveals what it covered.
/// </summary>
internal static class FreeTimeline
{
    public const int StepsPerBar = 16;
    public const int MaximumClipCount = 1024;
    public const int MaximumStep = 1024 * StepsPerBar;
    public const int MaximumClipLength = 256 * StepsPerBar;

    /// <summary>Where the last clip ends; 0 for an empty timeline.</summary>
    public static int LengthInSteps(SlawProject project) =>
        project.Timeline.Count == 0 ? 0 : project.Timeline.Max(timelineClip => timelineClip.EndStep);

    /// <summary>A track's clips in start order (ties: later in the list last), ready for <see cref="ClipAt"/>.</summary>
    public static List<TimelineClip> ClipsForTrack(SlawProject project, string trackId) =>
        project.Timeline
            .Select((timelineClip, listIndex) => (TimelineClip: timelineClip, ListIndex: listIndex))
            .Where(entry => entry.TimelineClip.TrackId == trackId)
            .OrderBy(entry => entry.TimelineClip.StartStep)
            .ThenBy(entry => entry.ListIndex)
            .Select(entry => entry.TimelineClip)
            .ToList();

    /// <summary>The clip that sounds at <paramref name="timelineStep"/>: of those covering it, the one that started last.</summary>
    public static TimelineClip? ClipAt(IReadOnlyList<TimelineClip> trackClipsInStartOrder, long timelineStep)
    {
        for (var clipIndex = trackClipsInStartOrder.Count - 1; clipIndex >= 0; clipIndex--)
        {
            var candidateClip = trackClipsInStartOrder[clipIndex];
            if (candidateClip.StartStep <= timelineStep && timelineStep < candidateClip.EndStep)
            {
                return candidateClip;
            }
        }
        return null;
    }

    /// <summary>Which step of the clip's pattern plays at <paramref name="timelineStep"/> (the pattern loops inside the clip).</summary>
    public static int PatternStepAt(TimelineClip timelineClip, long timelineStep) =>
        timelineClip.Clip.Pattern.Count == 0 ? 0 : (int)((timelineStep - timelineClip.StartStep) % timelineClip.Clip.Pattern.Count);

    /// <summary>An empty clip, one pattern of <paramref name="patternSteps"/>, placed at <paramref name="startStep"/>.</summary>
    public static TimelineClip CreateEmpty(SlawTrack track, int startStep, int patternSteps) => new()
    {
        TrackId = track.Id,
        StartStep = Math.Max(0, startStep),
        LengthSteps = patternSteps,
        Clip = SlawClip.Empty(track.Name, patternSteps),
    };

    /// <summary>A deep copy (pattern, envelopes and all) with a fresh identity at <paramref name="startStep"/>.</summary>
    public static TimelineClip Copy(TimelineClip sourceClip, int startStep, string? trackId = null) => new()
    {
        TrackId = trackId ?? sourceClip.TrackId,
        StartStep = Math.Max(0, startStep),
        LengthSteps = sourceClip.LengthSteps,
        Clip = CopyClip(sourceClip.Clip),
    };

    /// <summary>Deep-copies a clip through the serializer, so the copy carries every field a clip has now or grows later.</summary>
    public static SlawClip CopyClip(SlawClip sourceClip) =>
        System.Text.Json.JsonSerializer.Deserialize<SlawClip>(
            System.Text.Json.JsonSerializer.Serialize(sourceClip, ProjectSerializer.LenientSerializationOptions),
            ProjectSerializer.LenientSerializationOptions)!;

    /// <summary>
    /// Flattens a legacy scene sequence into timeline clips starting at <paramref name="startStep"/>:
    /// one per track per section that has a clip, spanning the whole section, so the
    /// song sounds the same and every section can then be edited on its own.
    /// </summary>
    public static List<TimelineClip> FlattenSections(SlawProject project, IEnumerable<ArrangementSection> sections, int startStep = 0)
    {
        var flattenedClips = new List<TimelineClip>();
        var sectionStartStep = startStep;
        foreach (var section in sections)
        {
            if (section.Scene < 0 || section.Scene >= project.Scenes.Count)
            {
                continue;
            }
            var sectionLength = project.SceneLengthInSteps(section.Scene) * Math.Max(1, section.Repeats);
            foreach (var track in project.Tracks)
            {
                if (track.ClipAt(section.Scene) is { Pattern.Count: > 0 } sectionClip)
                {
                    flattenedClips.Add(new TimelineClip
                    {
                        TrackId = track.Id,
                        StartStep = sectionStartStep,
                        LengthSteps = sectionLength,
                        Clip = CopyClip(sectionClip),
                    });
                }
            }
            sectionStartStep += sectionLength;
        }
        return flattenedClips;
    }

    /// <summary>The legacy scene sequence (<see cref="SlawProject.Arrangement"/>) as timeline clips.</summary>
    public static List<TimelineClip> FlattenArrangement(SlawProject project) => FlattenSections(project, project.Arrangement);

    /// <summary>A scene's clips as timeline clips at <paramref name="startStep"/>, each as long as the scene.</summary>
    public static List<TimelineClip> PlaceScene(SlawProject project, int sceneIndex, int startStep, int repeats = 1) =>
        FlattenSections(project, new[] { new ArrangementSection { Scene = sceneIndex, Repeats = repeats } }, startStep);

    /// <summary>
    /// Migrates a legacy scene sequence: the arrangement is the timeline now. A project with
    /// no timeline clips gets its sections flattened onto it; either way the old sequence is
    /// dropped, so it is never written back. Returns true if anything changed.
    /// </summary>
    public static bool MigrateLegacyArrangement(SlawProject project)
    {
        if (project.Arrangement.Count == 0)
        {
            return false;
        }
        if (project.Timeline.Count == 0)
        {
            project.Timeline.AddRange(FlattenArrangement(project));
        }
        project.Arrangement.Clear();
        return true;
    }

    /// <summary>"bar.step", one-based, the way a producer reads a position: step 20 is "2.5".</summary>
    public static string FormatPosition(int timelineStep) =>
        string.Create(CultureInfo.InvariantCulture, $"{timelineStep / StepsPerBar + 1}.{timelineStep % StepsPerBar + 1}");

    /// <summary>Squares up the timeline after an AI rewrite or a hand-edited file. Returns what it had to fix.</summary>
    public static List<string> Remediate(SlawProject project)
    {
        var remediationLedger = new List<string>();
        var knownTrackIds = project.Tracks.Select(track => track.Id).ToHashSet();
        var orphanedCount = project.Timeline.RemoveAll(timelineClip => timelineClip is null || !knownTrackIds.Contains(timelineClip.TrackId));
        if (orphanedCount > 0)
        {
            remediationLedger.Add($"timeline: {orphanedCount} clip(s) on tracks that no longer exist removed");
        }
        var seenIds = new HashSet<string>();
        foreach (var timelineClip in project.Timeline)
        {
            if (string.IsNullOrWhiteSpace(timelineClip.Id) || !seenIds.Add(timelineClip.Id))
            {
                timelineClip.Id = SlawTrack.NewIdentifier();
                seenIds.Add(timelineClip.Id);
            }
            timelineClip.Clip ??= new SlawClip();
            timelineClip.Clip.Pattern ??= new List<string>();
            if (timelineClip.Clip.Pattern.Count == 0)
            {
                timelineClip.Clip.Pattern = Enumerable.Repeat(NotationResolver.RestSymbol, StepsPerBar).ToList();
            }
            if (timelineClip.Clip.Automation is not null)
            {
                var owningTrack = project.Tracks.First(track => track.Id == timelineClip.TrackId);
                timelineClip.Clip.Automation = timelineClip.Clip.Automation
                    .Where(envelope => envelope.Value is { Count: > 0 })
                    .Select(envelope => (envelope.Key, Target: ClipAutomation.ResolveTarget(owningTrack, envelope.Key), Points: envelope.Value))
                    .Where(envelope => envelope.Target is not null)
                    .ToDictionary(envelope => envelope.Key, envelope => ClipAutomation.Tidy(envelope.Points, timelineClip.Clip.Pattern.Count, envelope.Target!.Specification));
                if (timelineClip.Clip.Automation.Count == 0)
                {
                    timelineClip.Clip.Automation = null;
                }
            }
            timelineClip.StartStep = Math.Clamp(timelineClip.StartStep, 0, MaximumStep);
            timelineClip.LengthSteps = Math.Clamp(timelineClip.LengthSteps <= 0 ? timelineClip.Clip.Pattern.Count : timelineClip.LengthSteps, 1, MaximumClipLength);
        }
        if (project.Timeline.Count > MaximumClipCount)
        {
            remediationLedger.Add($"timeline: trimmed to {MaximumClipCount} clips");
            project.Timeline.RemoveRange(MaximumClipCount, project.Timeline.Count - MaximumClipCount);
        }
        if (project.TimelineLoop is { IsValid: false })
        {
            project.TimelineLoop = null;
        }
        return remediationLedger;
    }
}
