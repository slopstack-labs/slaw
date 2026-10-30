using System.Globalization;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Describes, in plain words, what changed between two versions of a project —
/// so that when the autopilot rewrites everything, someone can at least read
/// the minutes.
/// </summary>
internal static class ProjectChangeSummarizer
{
    public static List<string> Summarize(SlawProject previousProject, SlawProject revisedProject)
    {
        var changeSummary = new List<string>();
        if (previousProject.Name != revisedProject.Name)
        {
            changeSummary.Add($"retitled the project \"{revisedProject.Name}\"");
        }
        if (Math.Abs(previousProject.Bpm - revisedProject.Bpm) > 0.01)
        {
            changeSummary.Add(string.Create(CultureInfo.InvariantCulture, $"tempo {previousProject.Bpm:0} → {revisedProject.Bpm:0} BPM"));
        }
        if (!previousProject.Scenes.SequenceEqual(revisedProject.Scenes))
        {
            changeSummary.Add($"scenes are now: {string.Join(", ", revisedProject.Scenes)}");
        }

        var previousTracksById = previousProject.Tracks.ToDictionary(track => track.Id);
        foreach (var revisedTrack in revisedProject.Tracks)
        {
            if (!previousTracksById.TryGetValue(revisedTrack.Id, out var previousTrack))
            {
                changeSummary.Add($"added track \"{revisedTrack.Name}\" ({revisedTrack.Instrument})");
                continue;
            }
            var trackChanges = new List<string>();
            if (previousTrack.Name != revisedTrack.Name)
            {
                trackChanges.Add($"renamed to \"{revisedTrack.Name}\"");
            }
            if (previousTrack.Instrument != revisedTrack.Instrument)
            {
                trackChanges.Add($"instrument → {revisedTrack.Instrument}");
            }
            if (!DictionariesEqual(previousTrack.InstrumentParameters, revisedTrack.InstrumentParameters))
            {
                trackChanges.Add("instrument retuned");
            }
            var previousEffects = string.Join(",", previousTrack.Effects.Select(effect => effect.Type));
            var revisedEffects = string.Join(",", revisedTrack.Effects.Select(effect => effect.Type));
            if (previousEffects != revisedEffects)
            {
                trackChanges.Add($"device chain → {(revisedEffects.Length == 0 ? "empty" : revisedEffects.Replace(",", " → "))}");
            }
            else if (previousTrack.Effects.Zip(revisedTrack.Effects).Any(pair => !DictionariesEqual(pair.First.Parameters, pair.Second.Parameters) || pair.First.Enabled != pair.Second.Enabled))
            {
                trackChanges.Add("effects retuned");
            }
            var changedClipCount = previousTrack.Clips.Zip(revisedTrack.Clips)
                .Count(pair => string.Join(' ', pair.First?.Pattern ?? new List<string>()) != string.Join(' ', pair.Second?.Pattern ?? new List<string>()));
            changedClipCount += Math.Abs(previousTrack.Clips.Count - revisedTrack.Clips.Count);
            if (changedClipCount > 0)
            {
                trackChanges.Add($"{changedClipCount} clip(s) rewritten");
            }
            if (Math.Abs(previousTrack.Gain - revisedTrack.Gain) > 0.005 || Math.Abs(previousTrack.Pan - revisedTrack.Pan) > 0.005 ||
                previousTrack.Muted != revisedTrack.Muted || previousTrack.Solo != revisedTrack.Solo)
            {
                trackChanges.Add("remixed");
            }
            if (trackChanges.Count > 0)
            {
                changeSummary.Add($"{previousTrack.Name}: {string.Join(", ", trackChanges)}");
            }
        }
        var revisedIdentifiers = revisedProject.Tracks.Select(track => track.Id).ToHashSet();
        foreach (var removedTrack in previousProject.Tracks.Where(track => !revisedIdentifiers.Contains(track.Id)))
        {
            changeSummary.Add($"made \"{removedTrack.Name}\" redundant");
        }

        var previousMaster = previousProject.Master;
        var revisedMaster = revisedProject.Master;
        if (Math.Abs(previousMaster.Gain - revisedMaster.Gain) > 0.005 || Math.Abs(previousMaster.Drive - revisedMaster.Drive) > 0.005 ||
            Math.Abs(previousMaster.LowpassHz - revisedMaster.LowpassHz) > 1 || Math.Abs(previousMaster.DelayMix - revisedMaster.DelayMix) > 0.005)
        {
            changeSummary.Add("remastered the master bus");
        }

        var previousTimelineClips = previousProject.Timeline.ToDictionary(timelineClip => timelineClip.Id, timelineClip => System.Text.Json.JsonSerializer.Serialize(timelineClip, ProjectSerializer.LenientSerializationOptions));
        var revisedTimelineClips = revisedProject.Timeline.ToDictionary(timelineClip => timelineClip.Id, timelineClip => System.Text.Json.JsonSerializer.Serialize(timelineClip, ProjectSerializer.LenientSerializationOptions));
        var addedTimelineClips = revisedTimelineClips.Keys.Count(clipId => !previousTimelineClips.ContainsKey(clipId));
        var removedTimelineClips = previousTimelineClips.Keys.Count(clipId => !revisedTimelineClips.ContainsKey(clipId));
        var editedTimelineClips = revisedTimelineClips.Count(revisedClip =>
            previousTimelineClips.TryGetValue(revisedClip.Key, out var previousClip) && previousClip != revisedClip.Value);
        if (addedTimelineClips + removedTimelineClips + editedTimelineClips > 0)
        {
            changeSummary.Add($"arrangement: {addedTimelineClips} clip(s) placed, {editedTimelineClips} revised, {removedTimelineClips} offboarded");
        }
        if (changeSummary.Count == 0)
        {
            changeSummary.Add("changed nothing, and described this as a bold creative choice");
        }
        return changeSummary;
    }

    private static bool DictionariesEqual(IReadOnlyDictionary<string, double> previousValues, IReadOnlyDictionary<string, double> revisedValues) =>
        previousValues.Count == revisedValues.Count &&
        previousValues.All(pair => revisedValues.TryGetValue(pair.Key, out var revisedValue) && Math.Abs(revisedValue - pair.Value) < 1e-9);
}
