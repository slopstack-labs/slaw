namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Who keys whom. An effect with a <see cref="EffectDevice.SidechainSource"/> listens to
/// another track, so that track has to be processed first in every block: the edges run
/// source → keyed track, and the engine walks tracks in <see cref="ProcessingOrder"/>.
/// Master effects may key off any track; the master always runs after every track, so
/// they can never close a loop. Track-to-track loops are refused (<see cref="WouldCreateCycle"/>)
/// and remediated away; if one sneaks into the engine anyway, its keys hear silence.
/// </summary>
internal static class SidechainRouting
{
    /// <summary>Built-in effects that listen to a sidechain. VST3 effects listen if the plugin has an aux input.</summary>
    public static bool AcceptsSidechain(EffectDevice effectDevice) =>
        effectDevice.Type is DuckEffectType or Plugins.DeviceTypes.Vst3;

    /// <summary>The built-in ducker's effect type.</summary>
    public const string DuckEffectType = "duck";

    /// <summary>
    /// Indices into <see cref="SlawProject.Tracks"/>, sources before the tracks they key.
    /// Ties keep project order, so a project without sidechains plays in project order.
    /// Tracks caught in a loop come last, in project order.
    /// </summary>
    public static int[] ProcessingOrder(SlawProject project)
    {
        var trackCount = project.Tracks.Count;
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            indexById.TryAdd(project.Tracks[trackIndex].Id, trackIndex);
        }
        var sourcesByTrack = new List<int>[trackCount];
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            sourcesByTrack[trackIndex] = project.Tracks[trackIndex].Effects
                .Select(effectDevice => effectDevice.SidechainSource is { } sourceId && indexById.TryGetValue(sourceId, out var sourceIndex) ? sourceIndex : -1)
                .Where(sourceIndex => sourceIndex >= 0 && sourceIndex != trackIndex)
                .Distinct()
                .ToList();
        }

        var processingOrder = new List<int>(trackCount);
        var placed = new bool[trackCount];
        while (processingOrder.Count < trackCount)
        {
            var nextTrack = -1;
            for (var trackIndex = 0; trackIndex < trackCount && nextTrack < 0; trackIndex++)
            {
                if (!placed[trackIndex] && sourcesByTrack[trackIndex].All(sourceIndex => placed[sourceIndex]))
                {
                    nextTrack = trackIndex;
                }
            }
            if (nextTrack < 0)
            {
                // A loop: whatever is left goes in project order; its keys arrive too late and are silent.
                processingOrder.AddRange(Enumerable.Range(0, trackCount).Where(trackIndex => !placed[trackIndex]));
                break;
            }
            placed[nextTrack] = true;
            processingOrder.Add(nextTrack);
        }
        return processingOrder.ToArray();
    }

    /// <summary>Track ids that key at least one effect (on a track or on the master).</summary>
    public static HashSet<string> Sources(SlawProject project) =>
        AllEffects(project)
            .Select(effectDevice => effectDevice.SidechainSource)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    public static IEnumerable<EffectDevice> AllEffects(SlawProject project) =>
        project.Tracks.SelectMany(track => track.Effects).Concat(project.Master.Effects);

    /// <summary>
    /// Whether keying an effect on <paramref name="targetTrackId"/> from <paramref name="sourceTrackId"/>
    /// would make a loop: true for the track itself, or when the source already depends on the target.
    /// A null target is the master, which never loops.
    /// </summary>
    public static bool WouldCreateCycle(SlawProject project, string? targetTrackId, string sourceTrackId)
    {
        if (targetTrackId is null)
        {
            return false;
        }
        if (targetTrackId == sourceTrackId)
        {
            return true;
        }
        // Does the source (transitively) listen to the target? Then the target cannot listen to the source.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(sourceTrackId);
        while (pending.TryPop(out var trackId))
        {
            if (!visited.Add(trackId) || project.Tracks.FirstOrDefault(track => track.Id == trackId) is not { } track)
            {
                continue;
            }
            foreach (var upstreamId in track.Effects.Select(effectDevice => effectDevice.SidechainSource).OfType<string>())
            {
                if (upstreamId == targetTrackId)
                {
                    return true;
                }
                pending.Push(upstreamId);
            }
        }
        return false;
    }

    /// <summary>Clears every sidechain keyed from a track (it was removed). Returns how many were cleared.</summary>
    public static int ForgetSource(SlawProject project, string removedTrackId)
    {
        var clearedCount = 0;
        foreach (var effectDevice in AllEffects(project).Where(effectDevice => effectDevice.SidechainSource == removedTrackId))
        {
            effectDevice.SidechainSource = null;
            clearedCount++;
        }
        return clearedCount;
    }

    /// <summary>A track by id, exact name, or name ignoring case; null if none matches.</summary>
    public static SlawTrack? ResolveTrack(SlawProject project, string? trackReference)
    {
        if (string.IsNullOrWhiteSpace(trackReference))
        {
            return null;
        }
        return project.Tracks.FirstOrDefault(track => track.Id == trackReference)
               ?? project.Tracks.FirstOrDefault(track => track.Name == trackReference)
               ?? project.Tracks.FirstOrDefault(track => string.Equals(track.Name, trackReference, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Kick" for an effect keyed by the Kick track, for listings and device headers.</summary>
    public static string? DescribeSource(SlawProject project, EffectDevice effectDevice) =>
        effectDevice.SidechainSource is { } sourceId
            ? project.Tracks.FirstOrDefault(track => track.Id == sourceId)?.Name ?? sourceId
            : null;
}
