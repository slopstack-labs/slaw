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
        if (Math.Abs(previousProject.Swing - revisedProject.Swing) > 0.005)
        {
            changeSummary.Add($"swing {GrooveTiming.FormatSwing(previousProject.Swing)} → {GrooveTiming.FormatSwing(revisedProject.Swing)} (project; tracks with their own swing keep it)");
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
                var addedGroove = (revisedTrack.Swing is { } addedSwing ? $", swing {GrooveTiming.FormatSwing(addedSwing)}" : string.Empty) +
                                  (revisedTrack.Timing > 0 ? string.Create(CultureInfo.InvariantCulture, $", timing {revisedTrack.Timing:0.#} ms") : string.Empty);
                changeSummary.Add($"added track id \"{revisedTrack.Id}\" \"{revisedTrack.Name}\" ({DescribeInstrument(revisedTrack)}{addedGroove})");
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
            var previousEffects = string.Join(" → ", previousTrack.Effects.Select(effect => DescribeEffect(effect, previousProject)));
            var revisedEffects = string.Join(" → ", revisedTrack.Effects.Select(effect => DescribeEffect(effect, revisedProject)));
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
                        trackChanges.Add($"effect {effectIndex} ({DescribeEffect(revisedEffect, revisedProject)}) {(revisedEffect.Enabled ? "enabled" : "bypassed")}");
                    }
                    if (DescribeValueChanges(previousEffect.Parameters, revisedEffect.Parameters) is { } effectChanges)
                    {
                        trackChanges.Add($"effect {effectIndex} ({DescribeEffect(revisedEffect, revisedProject)}) parameters {effectChanges}");
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
            if (previousTrack.Swing != revisedTrack.Swing)
            {
                mixerChanges.Add($"swing {DescribeTrackSwing(previousTrack.Swing)} → {DescribeTrackSwing(revisedTrack.Swing)}");
            }
            if (Math.Abs(previousTrack.Timing - revisedTrack.Timing) > 0.005)
            {
                mixerChanges.Add(string.Create(CultureInfo.InvariantCulture, $"timing {previousTrack.Timing:0.#} → {revisedTrack.Timing:0.#} ms"));
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
            // A removed track takes its arrangement clips along; say so here, once.
            var removedClipCount = previousProject.Timeline.Count(timelineClip => timelineClip.TrackId == removedTrack.Id);
            changeSummary.Add(removedClipCount == 0
                ? $"removed track \"{removedTrack.Name}\""
                : $"removed track \"{removedTrack.Name}\" and its {removedClipCount} arrangement clip{(removedClipCount == 1 ? string.Empty : "s")}");
        }

        var previousMaster = previousProject.Master;
        var revisedMaster = revisedProject.Master;
        if (Math.Abs(previousMaster.Gain - revisedMaster.Gain) > 0.005 || Math.Abs(previousMaster.Drive - revisedMaster.Drive) > 0.005 ||
            Math.Abs(previousMaster.LowpassHz - revisedMaster.LowpassHz) > 1 || Math.Abs(previousMaster.DelayMix - revisedMaster.DelayMix) > 0.005)
        {
            changeSummary.Add(string.Create(CultureInfo.InvariantCulture,
                $"master bus now: gain {revisedMaster.Gain:0.00}, drive {revisedMaster.Drive:0.0}, lowpassHz {revisedMaster.LowpassHz:0}, delayMix {revisedMaster.DelayMix:0.00}"));
        }

        var previousMasterEffects = string.Join(" → ", previousMaster.Effects.Select(effect => DescribeEffect(effect, previousProject)));
        var revisedMasterEffects = string.Join(" → ", revisedMaster.Effects.Select(effect => DescribeEffect(effect, revisedProject)));
        if (previousMasterEffects != revisedMasterEffects)
        {
            changeSummary.Add($"master effects are now: {(revisedMasterEffects.Length == 0 ? "none" : revisedMasterEffects)}");
        }
        else
        {
            for (var effectIndex = 0; effectIndex < revisedMaster.Effects.Count; effectIndex++)
            {
                var previousEffect = previousMaster.Effects[effectIndex];
                var revisedEffect = revisedMaster.Effects[effectIndex];
                if (previousEffect.Enabled != revisedEffect.Enabled)
                {
                    changeSummary.Add($"master effect {effectIndex} ({DescribeEffect(revisedEffect, revisedProject)}) {(revisedEffect.Enabled ? "enabled" : "bypassed")}");
                }
                if (DescribeValueChanges(previousEffect.Parameters, revisedEffect.Parameters) is { } effectChanges)
                {
                    changeSummary.Add($"master effect {effectIndex} ({DescribeEffect(revisedEffect, revisedProject)}) parameters {effectChanges}");
                }
            }
        }

        changeSummary.AddRange(DescribeArrangementChanges(previousProject, revisedProject));
        if (changeSummary.Count == 0)
        {
            changeSummary.Add("no changes: the project already matched the patch (a bold creative choice)");
        }
        return changeSummary;
    }

    private static string DescribeTrackSwing(double? trackSwing) =>
        trackSwing is { } ownSwing ? GrooveTiming.FormatSwing(ownSwing) : "(project's)";

    private static string DescribeInstrument(SlawTrack track) => track switch
    {
        { Instrument: "vst3", Plugin: { } plugin } => $"vst3 {plugin.Name}",
        { Instrument: "soundfont", SoundFont: { } soundFont } => $"soundfont {SoundFontDeviceOperations.Describe(soundFont)}",
        _ => track.Instrument,
    };

    /// <summary>"duck (sidechain ← Kick)", "vst3 MVerb", "lowpass".</summary>
    private static string DescribeEffect(EffectDevice effect, SlawProject project)
    {
        var effectLabel = effect.Plugin is { } plugin ? $"vst3 {plugin.Name}" : effect.Type;
        return SidechainRouting.DescribeSource(project, effect) is { } sourceName ? $"{effectLabel} (sidechain ← {sourceName})" : effectLabel;
    }

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
                slotChanges.Add($"scene {sceneIndex} written{DescribeEnvelopes(revisedClip)}");
            }
            else if (previousClip is not null && revisedClip is null && sceneIndex < revisedTrack.Clips.Count)
            {
                slotChanges.Add($"scene {sceneIndex} cleared");
            }
            else if (previousClip is not null && revisedClip is not null &&
                     (!previousClip.Pattern.SequenceEqual(revisedClip.Pattern) || EnvelopeFingerprint(previousClip) != EnvelopeFingerprint(revisedClip)))
            {
                slotChanges.Add($"scene {sceneIndex} changed{DescribeEnvelopes(revisedClip)}");
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
        // Clips of removed tracks are already counted on the track's own "removed track" line.
        var survivingTrackIds = revisedProject.Tracks.Select(track => track.Id).ToHashSet();
        var removedCount = previousProject.Timeline.Count(timelineClip => !revisedIdentifiers.Contains(timelineClip.Id) && survivingTrackIds.Contains(timelineClip.TrackId));

        var arrangementChanges = new List<string>();
        string Locate(TimelineClip timelineClip) =>
            $"id \"{timelineClip.Id}\" on \"{revisedProject.Tracks.FirstOrDefault(track => track.Id == timelineClip.TrackId)?.Name ?? "?"}\" {FreeTimeline.DescribeSpan(timelineClip)}";
        foreach (var placedClip in placedClips.Take(MaximumListedArrangementClips))
        {
            arrangementChanges.Add($"arrangement: placed clip {Locate(placedClip)}{DescribeEnvelopes(placedClip.Clip)}");
        }
        foreach (var changedClip in changedClips.Take(MaximumListedArrangementClips))
        {
            arrangementChanges.Add($"arrangement: changed clip {Locate(changedClip)}{DescribeEnvelopes(changedClip.Clip)}");
        }
        var unlistedCount = Math.Max(0, placedClips.Count - MaximumListedArrangementClips) + Math.Max(0, changedClips.Count - MaximumListedArrangementClips);
        if (unlistedCount > 0)
        {
            arrangementChanges.Add($"arrangement: {unlistedCount} more clip(s) placed or changed (see get_project)");
        }
        if (removedCount > 0)
        {
            arrangementChanges.Add($"arrangement: removed {removedCount} clip{(removedCount == 1 ? string.Empty : "s")}");
        }
        return arrangementChanges;
    }

    /// <summary>"; envelopes: fx.0.cutoff 0:700 → 128:6000", so a result confirms what was automated without a get_project.</summary>
    internal static string DescribeEnvelopes(SlawClip clip) =>
        clip.Automation is { Count: > 0 } envelopes
            ? "; envelopes: " + string.Join("; ", envelopes.Select(envelope => ClipAutomation.Describe(envelope.Key, envelope.Value)))
            : string.Empty;
}
