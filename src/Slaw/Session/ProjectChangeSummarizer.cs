using System.Globalization;
using System.Text.Json;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Describes, in plain words, what changed between two versions of a project —
/// so that when the autopilot rewrites everything, someone can at least read
/// the minutes. Agents parse these lines: facts first, always.
/// </summary>
internal static class ProjectChangeSummarizer
{
    private const int MaximumListedArrangementClips = 24;

    public static List<string> Summarize(SlawProject previousProject, SlawProject revisedProject)
    {
        var changeSummary = new List<string>();
        if (previousProject.Name != revisedProject.Name)
        {
            changeSummary.Add($"renamed the project to \"{revisedProject.Name}\"");
        }
        if (Math.Abs(previousProject.Bpm - revisedProject.Bpm) > 0.01)
        {
            changeSummary.Add(string.Create(CultureInfo.InvariantCulture, $"tempo {previousProject.Bpm:0} → {revisedProject.Bpm:0} BPM"));
        }
        if (!previousProject.Scenes.SequenceEqual(revisedProject.Scenes))
        {
            changeSummary.Add($"session scenes are now ({revisedProject.Scenes.Count}): {string.Join(", ", revisedProject.Scenes.Select((sceneName, sceneIndex) => $"{sceneIndex}=\"{sceneName}\""))}");
        }

        var previousTracksById = previousProject.Tracks.ToDictionary(track => track.Id);
        foreach (var revisedTrack in revisedProject.Tracks)
        {
            if (!previousTracksById.TryGetValue(revisedTrack.Id, out var previousTrack))
            {
                changeSummary.Add($"added track id \"{revisedTrack.Id}\" \"{revisedTrack.Name}\" ({DescribeInstrument(revisedTrack)})");
                continue;
            }
            var trackChanges = new List<string>();
            if (previousTrack.Name != revisedTrack.Name)
            {
                trackChanges.Add($"renamed to \"{revisedTrack.Name}\"");
            }
            if (previousTrack.Instrument != revisedTrack.Instrument || previousTrack.Plugin?.ClassId != revisedTrack.Plugin?.ClassId ||
                previousTrack.SoundFont?.Fingerprint != revisedTrack.SoundFont?.Fingerprint)
            {
                trackChanges.Add($"instrument {DescribeInstrument(previousTrack)} → {DescribeInstrument(revisedTrack)}");
            }
            if (DescribeValueChanges(previousTrack.InstrumentParameters, revisedTrack.InstrumentParameters) is { } instrumentChanges)
            {
                trackChanges.Add($"instrument parameters {instrumentChanges}");
            }
            var previousEffects = string.Join(" → ", previousTrack.Effects.Select(DescribeEffect));
            var revisedEffects = string.Join(" → ", revisedTrack.Effects.Select(DescribeEffect));
            if (previousEffects != revisedEffects)
            {
                trackChanges.Add($"effects are now: {(revisedEffects.Length == 0 ? "none" : revisedEffects)}");
            }
            else
            {
                for (var effectIndex = 0; effectIndex < revisedTrack.Effects.Count; effectIndex++)
                {
                    var previousEffect = previousTrack.Effects[effectIndex];
                    var revisedEffect = revisedTrack.Effects[effectIndex];
                    if (previousEffect.Enabled != revisedEffect.Enabled)
                    {
                        trackChanges.Add($"effect {effectIndex} ({DescribeEffect(revisedEffect)}) {(revisedEffect.Enabled ? "enabled" : "bypassed")}");
                    }
                    if (DescribeValueChanges(previousEffect.Parameters, revisedEffect.Parameters) is { } effectChanges)
                    {
                        trackChanges.Add($"effect {effectIndex} ({DescribeEffect(revisedEffect)}) parameters {effectChanges}");
                    }
                }
            }
            if (DescribeSessionClipChanges(previousTrack, revisedTrack) is { } clipChanges)
            {
                trackChanges.Add(clipChanges);
            }
            var mixerChanges = new List<string>();
            if (Math.Abs(previousTrack.Gain - revisedTrack.Gain) > 0.005)
            {
                mixerChanges.Add(string.Create(CultureInfo.InvariantCulture, $"gain {previousTrack.Gain:0.00} → {revisedTrack.Gain:0.00}"));
            }
            if (Math.Abs(previousTrack.Pan - revisedTrack.Pan) > 0.005)
            {
                mixerChanges.Add(string.Create(CultureInfo.InvariantCulture, $"pan {previousTrack.Pan:0.00} → {revisedTrack.Pan:0.00}"));
            }
            if (previousTrack.Muted != revisedTrack.Muted)
            {
                mixerChanges.Add(revisedTrack.Muted ? "muted" : "unmuted");
            }
            if (previousTrack.Solo != revisedTrack.Solo)
            {
                mixerChanges.Add(revisedTrack.Solo ? "soloed" : "unsoloed");
            }
            if (mixerChanges.Count > 0)
            {
                trackChanges.Add(string.Join(", ", mixerChanges));
            }
            if (trackChanges.Count > 0)
            {
                changeSummary.Add($"{previousTrack.Name}: {string.Join("; ", trackChanges)}");
            }
        }
        var revisedIdentifiers = revisedProject.Tracks.Select(track => track.Id).ToHashSet();
        foreach (var removedTrack in previousProject.Tracks.Where(track => !revisedIdentifiers.Contains(track.Id)))
        {
            changeSummary.Add($"removed track \"{removedTrack.Name}\"");
        }

        var previousMaster = previousProject.Master;
        var revisedMaster = revisedProject.Master;
        if (Math.Abs(previousMaster.Gain - revisedMaster.Gain) > 0.005 || Math.Abs(previousMaster.Drive - revisedMaster.Drive) > 0.005 ||
            Math.Abs(previousMaster.LowpassHz - revisedMaster.LowpassHz) > 1 || Math.Abs(previousMaster.DelayMix - revisedMaster.DelayMix) > 0.005)
        {
            changeSummary.Add(string.Create(CultureInfo.InvariantCulture,
                $"master bus now: gain {revisedMaster.Gain:0.00}, drive {revisedMaster.Drive:0.0}, lowpassHz {revisedMaster.LowpassHz:0}, delayMix {revisedMaster.DelayMix:0.00}"));
        }

        changeSummary.AddRange(DescribeArrangementChanges(previousProject, revisedProject));
        if (changeSummary.Count == 0)
        {
            changeSummary.Add("no changes: the project already matched the patch (a bold creative choice)");
        }
        return changeSummary;
    }

    private static string DescribeInstrument(SlawTrack track) => track switch
    {
        { Instrument: "vst3", Plugin: { } plugin } => $"vst3 {plugin.Name}",
        { Instrument: "soundfont", SoundFont: { } soundFont } => $"soundfont {SoundFontDeviceOperations.Describe(soundFont)}",
        _ => track.Instrument,
    };

    private static string DescribeEffect(EffectDevice effect) => effect.Plugin is { } plugin ? $"vst3 {plugin.Name}" : effect.Type;

    /// <summary>"cutoff 900 → 1200, attack (default) → 5"; null when nothing changed. Lists at most six keys.</summary>
    private static string? DescribeValueChanges(IReadOnlyDictionary<string, double> previousValues, IReadOnlyDictionary<string, double> revisedValues)
    {
        var changedKeys = previousValues.Keys.Union(revisedValues.Keys)
            .Where(parameterKey => !(previousValues.TryGetValue(parameterKey, out var previousValue) && revisedValues.TryGetValue(parameterKey, out var revisedValue) && Math.Abs(previousValue - revisedValue) < 1e-9))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (changedKeys.Count == 0)
        {
            return null;
        }
        string Format(IReadOnlyDictionary<string, double> values, string parameterKey) =>
            values.TryGetValue(parameterKey, out var parameterValue) ? parameterValue.ToString("0.###", CultureInfo.InvariantCulture) : "(default)";
        var listedChanges = changedKeys.Take(6).Select(parameterKey => $"{parameterKey} {Format(previousValues, parameterKey)} → {Format(revisedValues, parameterKey)}");
        return string.Join(", ", listedChanges) + (changedKeys.Count > 6 ? $" and {changedKeys.Count - 6} more" : string.Empty);
    }

    /// <summary>"session clips: scene 1 written, scene 2 changed, scene 3 cleared"; null when no slot changed.</summary>
    private static string? DescribeSessionClipChanges(SlawTrack previousTrack, SlawTrack revisedTrack)
    {
        var slotChanges = new List<string>();
        for (var sceneIndex = 0; sceneIndex < Math.Max(previousTrack.Clips.Count, revisedTrack.Clips.Count); sceneIndex++)
        {
            var previousClip = previousTrack.ClipAt(sceneIndex);
            var revisedClip = revisedTrack.ClipAt(sceneIndex);
            if (previousClip is null && revisedClip is not null)
            {
                slotChanges.Add($"scene {sceneIndex} written");
            }
            else if (previousClip is not null && revisedClip is null && sceneIndex < revisedTrack.Clips.Count)
            {
                slotChanges.Add($"scene {sceneIndex} cleared");
            }
            else if (previousClip is not null && revisedClip is not null &&
                     (!previousClip.Pattern.SequenceEqual(revisedClip.Pattern) || EnvelopeFingerprint(previousClip) != EnvelopeFingerprint(revisedClip)))
            {
                slotChanges.Add($"scene {sceneIndex} changed");
            }
        }
        return slotChanges.Count == 0 ? null : $"session clips: {string.Join(", ", slotChanges)}";
    }

    private static string EnvelopeFingerprint(SlawClip clip) =>
        clip.Automation is null ? string.Empty : JsonSerializer.Serialize(clip.Automation, ProjectSerializer.LenientSerializationOptions);

    /// <summary>One line per placed arrangement clip (with its id, so agents can address it), then counts of the rest.</summary>
    private static List<string> DescribeArrangementChanges(SlawProject previousProject, SlawProject revisedProject)
    {
        string Serialize(TimelineClip timelineClip) => JsonSerializer.Serialize(timelineClip, ProjectSerializer.LenientSerializationOptions);
        var previousClips = previousProject.Timeline.ToDictionary(timelineClip => timelineClip.Id, Serialize);
        var placedClips = revisedProject.Timeline.Where(timelineClip => !previousClips.ContainsKey(timelineClip.Id)).ToList();
        var changedClips = revisedProject.Timeline.Where(timelineClip =>
            previousClips.TryGetValue(timelineClip.Id, out var previousClip) && previousClip != Serialize(timelineClip)).ToList();
        var revisedIdentifiers = revisedProject.Timeline.Select(timelineClip => timelineClip.Id).ToHashSet();
        var removedCount = previousProject.Timeline.Count(timelineClip => !revisedIdentifiers.Contains(timelineClip.Id));

        var arrangementChanges = new List<string>();
        string Locate(TimelineClip timelineClip) =>
            $"id \"{timelineClip.Id}\" on \"{revisedProject.Tracks.FirstOrDefault(track => track.Id == timelineClip.TrackId)?.Name ?? "?"}\" {FreeTimeline.DescribeSpan(timelineClip)}";
        foreach (var placedClip in placedClips.Take(MaximumListedArrangementClips))
        {
            arrangementChanges.Add($"arrangement: placed clip {Locate(placedClip)}");
        }
        foreach (var changedClip in changedClips.Take(MaximumListedArrangementClips))
        {
            arrangementChanges.Add($"arrangement: changed clip {Locate(changedClip)}");
        }
        var unlistedCount = Math.Max(0, placedClips.Count - MaximumListedArrangementClips) + Math.Max(0, changedClips.Count - MaximumListedArrangementClips);
        if (unlistedCount > 0)
        {
            arrangementChanges.Add($"arrangement: {unlistedCount} more clip(s) placed or changed (see get_project)");
        }
        if (removedCount > 0)
        {
            arrangementChanges.Add($"arrangement: removed {removedCount} clip(s)");
        }
        return arrangementChanges;
    }
}
