using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Applies an autopilot patch — a JSON object describing only what should
/// change — to a project. Writing a patch costs a model a few hundred tokens
/// where re-typing the whole project costs thousands, which on a CPU-bound
/// local model is the difference between "thinking" and "abandoned".
///
/// Tolerant by design: a model that ignores the patch format and returns the
/// whole project produces a patch that simply touches everything.
/// </summary>
internal static class ProjectPatchApplier
{
    /// <summary>What applying a patch did, besides change the project. <paramref name="Warnings"/> lists what was ignored or adjusted.</summary>
    internal sealed record PatchOutcome(SlawProject Project, string? ModelSummary, int LockedTrackEditsIgnored, IReadOnlyList<string> Warnings);

    /// <summary>Every top-level key a patch may carry; anything else is reported as ignored.</summary>
    internal static readonly HashSet<string> KnownPatchKeys = new(StringComparer.Ordinal)
    {
        "summary", "reset", "name", "bpm", "steps", "scenes", "removeScenes", "arrangement", "tracks", "timeline", "master", "format", "aiProvenance", "loops",
    };

    public static PatchOutcome Apply(SlawProject baseProject, JsonObject projectPatch)
    {
        // "reset": true starts from an empty project (one empty scene, default master), then applies the rest of the patch.
        var patchedProject = ReadBoolean(projectPatch["reset"]) == true
            ? ProjectSerializer.CreateBlankProject(ReadString(projectPatch["name"]) ?? "untitled slop", ReadDouble(projectPatch["bpm"]) ?? 120)
            : baseProject.DeepClone();
        var lockedTrackEditsIgnored = 0;
        var patchWarnings = new List<string>();
        var pendingSidechains = new List<(EffectDevice Effect, string? SourceReference, string EntryLabel)>();
        foreach (var (patchKey, _) in projectPatch)
        {
            if (!KnownPatchKeys.Contains(patchKey))
            {
                patchWarnings.Add($"ignored unknown key \"{patchKey}\" (known: {string.Join(", ", KnownPatchKeys.Where(key => key is not ("format" or "aiProvenance" or "loops" or "steps")))})");
            }
        }

        if (ReadString(projectPatch["name"]) is { Length: > 0 } patchedName)
        {
            patchedProject.Name = patchedName;
        }
        if (ReadDouble(projectPatch["bpm"]) is { } patchedTempo)
        {
            patchedProject.Bpm = patchedTempo;
        }
        if (projectPatch["removeScenes"] is JsonArray removedSceneArray)
        {
            // Highest index first, so earlier removals do not shift the later ones.
            foreach (var removedSceneIndex in removedSceneArray.Select(sceneNode => ReadDouble(sceneNode)).OfType<double>().Select(sceneNumber => (int)sceneNumber).Distinct().OrderByDescending(sceneIndex => sceneIndex))
            {
                if (!SceneOperations.RemoveScene(patchedProject, removedSceneIndex))
                {
                    patchWarnings.Add(patchedProject.Scenes.Count <= 1
                        ? $"removeScenes: kept scene {removedSceneIndex}; the last scene cannot be removed"
                        : $"removeScenes: there is no scene {removedSceneIndex} (scenes are 0..{patchedProject.Scenes.Count - 1})");
                }
            }
        }
        else if (projectPatch.ContainsKey("removeScenes"))
        {
            patchWarnings.Add("removeScenes must be an array of 0-based scene indices; ignored");
        }
        if (projectPatch["scenes"] is JsonArray sceneArray)
        {
            var patchedScenes = sceneArray
                .Select(sceneNode => ReadString(sceneNode) ?? ReadString(sceneNode?["name"]))
                .Select((sceneName, sceneIndex) => string.IsNullOrWhiteSpace(sceneName) ? $"Scene {sceneIndex + 1}" : sceneName!)
                .ToList();
            if (patchedScenes.Count == 0)
            {
                patchWarnings.Add("scenes: [] ignored; \"scenes\" only renames scenes. Delete scenes with removeScenes [indices] (or the remove_scene tool)");
            }
            else
            {
                if (patchedScenes.Count < patchedProject.Scenes.Count)
                {
                    patchWarnings.Add($"scenes: renamed the first {patchedScenes.Count} of {patchedProject.Scenes.Count} scenes; the rest keep their names. \"scenes\" never deletes: use removeScenes [indices]");
                    patchedScenes.AddRange(patchedProject.Scenes.Skip(patchedScenes.Count));
                }
                patchedProject.Scenes = patchedScenes;
            }
        }
        else if (projectPatch.ContainsKey("scenes"))
        {
            patchWarnings.Add("scenes must be an array of scene names; ignored");
        }
        // Legacy: a scene sequence rebuilds the arrangement timeline from those scenes,
        // once the patch's tracks and clips are in (see below).
        List<ArrangementSection>? legacyArrangement = null;
        if (projectPatch["arrangement"] is JsonArray arrangementArray)
        {
            var patchedArrangement = arrangementArray
                .Select(sectionNode => sectionNode is JsonObject sectionObject
                    ? new ArrangementSection
                    {
                        Scene = (int)(ReadDouble(sectionObject["scene"]) ?? 0),
                        Repeats = (int)(ReadDouble(sectionObject["repeats"]) ?? 1),
                    }
                    : new ArrangementSection { Scene = (int)(ReadDouble(sectionNode) ?? 0), Repeats = 1 })
                .ToList();
            if (patchedArrangement.Count > 0)
            {
                legacyArrangement = patchedArrangement;
            }
            else
            {
                patchWarnings.Add("arrangement: [] ignored. The arrangement is the timeline: remove its clips with timeline [{\"id\": ..., \"remove\": true}] or the arrangement_remove_clips tool");
            }
        }
        else if (projectPatch.ContainsKey("arrangement"))
        {
            patchWarnings.Add("arrangement must be an array of {scene, repeats}; ignored");
        }

        if (projectPatch["tracks"] is JsonArray trackPatchArray)
        {
            var trackEntryIndex = -1;
            foreach (var trackPatch in trackPatchArray.OfType<JsonObject>())
            {
                trackEntryIndex++;
                var patchedIdentifier = ReadString(trackPatch["id"]);
                var addsTrack = patchedIdentifier is null or "" || string.Equals(patchedIdentifier, "new", StringComparison.OrdinalIgnoreCase);
                // Agents address tracks the way the focused tools allow: id, exact name, or index.
                var existingTrack = addsTrack ? null : ResolvePatchTrack(patchedProject, trackPatch["id"]);
                if (existingTrack is { AiLocked: true })
                {
                    lockedTrackEditsIgnored++;
                    continue;
                }
                if (ReadBoolean(trackPatch["remove"]) == true)
                {
                    if (existingTrack is not null)
                    {
                        // The track's arrangement clips go with it (reported once, as part of the removal).
                        patchedProject.Tracks.Remove(existingTrack);
                        patchedProject.Timeline.RemoveAll(timelineClip => timelineClip.TrackId == existingTrack.Id);
                        SidechainRouting.ForgetSource(patchedProject, existingTrack.Id);
                    }
                    else
                    {
                        patchWarnings.Add($"tracks[{trackEntryIndex}]: no track \"{patchedIdentifier}\" to remove; {DescribeTrackReferences(patchedProject)}");
                    }
                    continue;
                }
                if (existingTrack is null && !addsTrack && trackPatch["instrument"] is null)
                {
                    // An edit aimed at a track that does not exist must not quietly become a new track.
                    patchWarnings.Add($"tracks[{trackEntryIndex}]: no track \"{patchedIdentifier}\"; entry ignored. Use \"id\": \"new\" (with an instrument) to add one; {DescribeTrackReferences(patchedProject)}");
                    continue;
                }
                var targetTrack = existingTrack;
                if (targetTrack is null)
                {
                    if (!addsTrack)
                    {
                        patchWarnings.Add($"tracks[{trackEntryIndex}]: no track \"{patchedIdentifier}\", so a new track was added (use \"id\": \"new\" to add tracks on purpose)");
                    }
                    targetTrack = new SlawTrack
                    {
                        Id = SlawTrack.NewIdentifier(),
                        Name = $"AI Track {patchedProject.Tracks.Count + 1}",
                        Gain = 0.7,
                    };
                    patchedProject.Tracks.Add(targetTrack);
                }
                ApplyTrackPatch(targetTrack, trackPatch, patchedProject.Steps, patchWarnings, pendingSidechains, $"tracks[{trackEntryIndex}]");
            }
        }

        if (legacyArrangement is not null)
        {
            // It describes the whole song, as it always did: the timeline is replaced.
            patchedProject.Timeline = FreeTimeline.FlattenSections(patchedProject, legacyArrangement);
            patchedProject.Arrangement.Clear();
        }

        if (projectPatch["timeline"] is JsonArray timelinePatchArray)
        {
            lockedTrackEditsIgnored += ApplyTimelinePatch(patchedProject, timelinePatchArray, patchWarnings);
        }

        if (projectPatch["master"] is JsonObject masterPatch)
        {
            var masterChain = patchedProject.Master;
            masterChain.Gain = ReadDouble(masterPatch["gain"]) ?? masterChain.Gain;
            masterChain.Drive = ReadDouble(masterPatch["drive"]) ?? masterChain.Drive;
            masterChain.LowpassHz = ReadDouble(masterPatch["lowpassHz"]) ?? masterChain.LowpassHz;
            masterChain.DelaySteps = (int)(ReadDouble(masterPatch["delaySteps"]) ?? masterChain.DelaySteps);
            masterChain.DelayFeedback = ReadDouble(masterPatch["delayFeedback"]) ?? masterChain.DelayFeedback;
            masterChain.DelayMix = ReadDouble(masterPatch["delayMix"]) ?? masterChain.DelayMix;
            masterChain.Rationale = ReadString(masterPatch["rationale"]) ?? masterChain.Rationale;
            if (masterPatch["effects"] is JsonArray masterEffectArray)
            {
                masterChain.Effects = ParseEffectChain(masterEffectArray, masterChain.Effects, pendingSidechains, "master.effects");
            }
        }

        // Sidechains name tracks, which may have been added by this same patch: resolved last.
        foreach (var (keyedEffect, sourceReference, entryLabel) in pendingSidechains)
        {
            if (sourceReference is null)
            {
                keyedEffect.SidechainSource = null;
            }
            else if (SidechainRouting.ResolveTrack(patchedProject, sourceReference) is { } sourceTrack)
            {
                keyedEffect.SidechainSource = sourceTrack.Id;
            }
            else
            {
                keyedEffect.SidechainSource = null;
                patchWarnings.Add($"{entryLabel}: sidechain \"{sourceReference}\" matches no track; left unset. {DescribeTrackReferences(patchedProject)}");
            }
        }

        return new PatchOutcome(patchedProject, ReadString(projectPatch["summary"]), lockedTrackEditsIgnored, patchWarnings);
    }

    private static void ApplyTrackPatch(SlawTrack targetTrack, JsonObject trackPatch, int defaultStepCount, List<string> patchWarnings,
        List<(EffectDevice Effect, string? SourceReference, string EntryLabel)> pendingSidechains, string entryLabel)
    {
        targetTrack.Name = ReadString(trackPatch["name"]) is { Length: > 0 } patchedName ? patchedName : targetTrack.Name;
        if (ReadString(trackPatch["instrument"]) is { Length: > 0 } patchedInstrument)
        {
            targetTrack.Instrument = ProjectAlignmentRemediator.ResolveInstrument(patchedInstrument);
        }
        if (PluginDeviceOperations.ParseReference(trackPatch["plugin"]) is { } patchedPlugin)
        {
            PluginDeviceOperations.LoadInstrument(targetTrack, patchedPlugin);
        }
        if (trackPatch["soundfont"] is { } soundFontNode)
        {
            try
            {
                SoundFontDeviceOperations.LoadIntoTrack(targetTrack, SoundFontDeviceOperations.ParseReference(soundFontNode));
            }
            catch (InvalidDataException soundFontRemediationOpportunity)
            {
                patchWarnings.Add($"{targetTrack.Name}: soundfont ignored: {soundFontRemediationOpportunity.Message}");
            }
        }
        targetTrack.Sample = ReadString(trackPatch["sample"]) ?? targetTrack.Sample;
        targetTrack.Gain = ReadDouble(trackPatch["gain"]) ?? targetTrack.Gain;
        targetTrack.Pan = ReadDouble(trackPatch["pan"]) ?? targetTrack.Pan;
        targetTrack.Muted = ReadBoolean(trackPatch["muted"]) ?? targetTrack.Muted;
        targetTrack.Solo = ReadBoolean(trackPatch["solo"]) ?? targetTrack.Solo;

        var parameterPatch = trackPatch["instrumentParameters"] as JsonObject ?? trackPatch["params"] as JsonObject;
        if (parameterPatch is not null)
        {
            foreach (var (parameterKey, parameterNode) in parameterPatch)
            {
                if (ReadDouble(parameterNode) is { } parameterValue)
                {
                    targetTrack.InstrumentParameters[parameterKey] = parameterValue;
                }
            }
        }

        if (trackPatch["effects"] is JsonArray effectArray)
        {
            targetTrack.Effects = ParseEffectChain(effectArray, targetTrack.Effects, pendingSidechains, $"{entryLabel}.effects");
        }

        switch (trackPatch["clips"])
        {
            case JsonObject clipsBySceneIndex:
                foreach (var (sceneKey, clipNode) in clipsBySceneIndex)
                {
                    if (int.TryParse(sceneKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sceneIndex) && sceneIndex is >= 0 and < 64)
                    {
                        SetClip(targetTrack, sceneIndex, clipNode, defaultStepCount, patchWarnings);
                    }
                }
                break;
            case JsonArray clipsInSceneOrder:
                for (var sceneIndex = 0; sceneIndex < clipsInSceneOrder.Count && sceneIndex < 64; sceneIndex++)
                {
                    SetClip(targetTrack, sceneIndex, clipsInSceneOrder[sceneIndex], defaultStepCount, patchWarnings);
                }
                break;
        }

        if (trackPatch["automation"] is JsonObject automationBySceneIndex)
        {
            foreach (var (sceneKey, sceneEnvelopes) in automationBySceneIndex)
            {
                if (int.TryParse(sceneKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sceneIndex) &&
                    targetTrack.ClipAt(sceneIndex) is { } automatedClip)
                {
                    ApplyClipAutomation(targetTrack, automatedClip, sceneEnvelopes, automatedClip.Pattern.Count, patchWarnings);
                }
                else
                {
                    patchWarnings.Add($"{targetTrack.Name}: automation for scene \"{sceneKey}\" ignored; that track has no session clip there");
                }
            }
        }

        // Drum steps on a melodic instrument (and vice versa) are re-spelled to match.
        PatternEditingOperations.ChangeInstrument(targetTrack, targetTrack.Instrument);
    }

    /// <summary>
    /// Merges envelopes into a clip: <c>{"fx.0.cutoff": [[0, 200], [16, 8000]], "pan": null}</c>.
    /// Points may also be <c>{"step": 0, "value": 200}</c>; null removes a lane; null for the whole object removes them all.
    /// </summary>
    internal static void ApplyClipAutomation(SlawTrack track, SlawClip clip, JsonNode? envelopesNode) =>
        ApplyClipAutomation(track, clip, envelopesNode, clip.Pattern.Count, warnings: null);

    /// <param name="envelopeLength">How many steps the envelopes span: a session clip's pattern length, an arrangement clip's length.</param>
    /// <param name="warnings">Collects unknown targets and points that had to be moved or clamped.</param>
    internal static void ApplyClipAutomation(SlawTrack track, SlawClip clip, JsonNode? envelopesNode, int envelopeLength, List<string>? warnings)
    {
        if (envelopesNode is not JsonObject envelopesByTarget)
        {
            clip.Automation = null;
            return;
        }
        var mergedEnvelopes = clip.Automation is null
            ? new Dictionary<string, List<AutomationPoint>>()
            : new Dictionary<string, List<AutomationPoint>>(clip.Automation);
        foreach (var (targetKey, pointsNode) in envelopesByTarget)
        {
            var parsedPoints = ReadAutomationPoints(pointsNode);
            var automationTarget = ClipAutomation.ResolveTarget(track, targetKey);
            if (automationTarget is null && parsedPoints is { Count: > 0 })
            {
                warnings?.Add($"{track.Name}: unknown envelope target \"{targetKey}\" ignored; valid targets: {string.Join(", ", ClipAutomation.TargetsFor(track).Select(target => target.Key))}");
            }
            if (parsedPoints is not { Count: > 0 } || automationTarget is null)
            {
                mergedEnvelopes.Remove(targetKey);
                continue;
            }
            mergedEnvelopes[targetKey] = ClipAutomation.Tidy(parsedPoints, envelopeLength, automationTarget.Specification, warnings, targetKey);
        }
        clip.Automation = mergedEnvelopes.Count > 0 ? mergedEnvelopes : null;
    }

    private static List<AutomationPoint>? ReadAutomationPoints(JsonNode? pointsNode) => pointsNode is JsonArray pointArray
        ? pointArray.Select(pointNode => pointNode switch
            {
                JsonArray { Count: >= 2 } pair when ReadDouble(pair[0]) is { } pairStep && ReadDouble(pair[1]) is { } pairValue =>
                    new AutomationPoint { Step = pairStep, Value = pairValue },
                JsonObject pointObject when ReadDouble(pointObject["step"]) is { } objectStep && ReadDouble(pointObject["value"]) is { } objectValue =>
                    new AutomationPoint { Step = objectStep, Value = objectValue },
                _ => null,
            })
            .OfType<AutomationPoint>()
            .ToList()
        : null;

    /// <summary>
    /// An effect list (a track's or the master's) replacing <paramref name="previousEffects"/>.
    /// A slot rewritten as {"type": "vst3"} keeps its plugin, and one that does not mention
    /// "sidechain" keeps its source; "sidechain": "Kick" (or a track id, or null) is resolved
    /// once every track of the patch exists.
    /// </summary>
    private static List<EffectDevice> ParseEffectChain(JsonArray effectArray, List<EffectDevice> previousEffects,
        List<(EffectDevice Effect, string? SourceReference, string EntryLabel)> pendingSidechains, string entryLabel)
    {
        var parsedEffects = effectArray.Select((effectNode, effectIndex) => effectNode switch
        {
            JsonObject effectObject => new EffectDevice
            {
                Type = ReadString(effectObject["type"]) ?? (effectObject["plugin"] is null ? "lowpass" : Plugins.DeviceTypes.Vst3),
                Enabled = ReadBoolean(effectObject["enabled"]) ?? true,
                Parameters = (effectObject["parameters"] as JsonObject ?? effectObject["params"] as JsonObject)?
                    .Where(parameterPair => ReadDouble(parameterPair.Value) is not null)
                    .ToDictionary(parameterPair => parameterPair.Key, parameterPair => ReadDouble(parameterPair.Value)!.Value)
                    ?? new Dictionary<string, double>(),
                Plugin = PluginDeviceOperations.ParseReference(effectObject["plugin"]) ?? PluginInSlot(previousEffects, effectIndex),
                SidechainSource = SidechainInSlot(previousEffects, effectIndex),
            },
            _ => new EffectDevice { Type = ReadString(effectNode) ?? "lowpass", Plugin = PluginInSlot(previousEffects, effectIndex), SidechainSource = SidechainInSlot(previousEffects, effectIndex) },
        }).ToList();
        for (var effectIndex = 0; effectIndex < effectArray.Count; effectIndex++)
        {
            if (effectArray[effectIndex] is JsonObject effectObject && effectObject.TryGetPropertyValue("sidechain", out var sidechainNode))
            {
                pendingSidechains.Add((parsedEffects[effectIndex], ReadString(sidechainNode), $"{entryLabel}[{effectIndex}]"));
            }
        }
        return parsedEffects;
    }

    private static string? SidechainInSlot(List<EffectDevice> previousEffects, int effectIndex) =>
        effectIndex < previousEffects.Count ? previousEffects[effectIndex].SidechainSource : null;

    /// <summary>A model rewriting the chain as [{"type": "vst3"}] keeps the plugin that was already in that slot.</summary>
    private static PluginReference? PluginInSlot(List<EffectDevice> previousEffects, int effectIndex) =>
        effectIndex < previousEffects.Count ? previousEffects[effectIndex].Plugin : null;

    private static void SetClip(SlawTrack targetTrack, int sceneIndex, JsonNode? clipNode, int defaultStepCount, List<string> patchWarnings)
    {
        while (targetTrack.Clips.Count <= sceneIndex)
        {
            targetTrack.Clips.Add(null);
        }
        List<string>? patchedPattern = clipNode switch
        {
            null => null,
            JsonValue => CompactPatternJsonConverter.SplitStepString(ReadString(clipNode)),
            JsonArray stepArray => stepArray.Select(stepNode => ReadString(stepNode) ?? NotationResolver.RestSymbol).ToList(),
            JsonObject clipObject => clipObject["pattern"] switch
            {
                JsonArray stepArray => stepArray.Select(stepNode => ReadString(stepNode) ?? NotationResolver.RestSymbol).ToList(),
                var patternNode => CompactPatternJsonConverter.SplitStepString(ReadString(patternNode)),
            },
            _ => null,
        };
        if (patchedPattern is null || patchedPattern.Count == 0)
        {
            targetTrack.Clips[sceneIndex] = null;
            return;
        }
        patchWarnings.AddRange(PatternLint.ChordSuffixWarnings(patchedPattern, $"\"{targetTrack.Name}\" session clip {sceneIndex}, "));
        var clipName = ReadString((clipNode as JsonObject)?["name"]) ?? targetTrack.Clips[sceneIndex]?.Name ?? targetTrack.Name;
        // A rewritten pattern keeps its envelopes: models rewrite notes far more often than they mean to delete a filter sweep.
        var patchedClip = new SlawClip { Name = clipName, Pattern = patchedPattern, Automation = targetTrack.Clips[sceneIndex]?.Automation };
        targetTrack.Clips[sceneIndex] = patchedClip;
        if (clipNode is JsonObject automatedClipObject && automatedClipObject.ContainsKey("automation"))
        {
            ApplyClipAutomation(targetTrack, patchedClip, automatedClipObject["automation"]);
        }
    }

    /// <summary>
    /// Arrangement (timeline) edits: <c>[{"id": "new", "track": "Bass", "start": 16, "length": 32, "pattern": "A1 . . .", "automation": {...}}]</c>.
    /// An existing id updates only the fields given; <c>"remove": true</c> deletes. Start and length are in steps (16 per bar).
    /// Returns how many edits were refused because they touched a locked track.
    /// </summary>
    private static int ApplyTimelinePatch(SlawProject patchedProject, JsonArray timelinePatchArray, List<string> patchWarnings)
    {
        var lockedEditsIgnored = 0;
        var entryIndex = -1;
        foreach (var clipPatch in timelinePatchArray.OfType<JsonObject>())
        {
            entryIndex++;
            var patchedIdentifier = ReadString(clipPatch["id"]);
            var existingClip = patchedProject.Timeline.FirstOrDefault(timelineClip => timelineClip.Id == patchedIdentifier);
            if (existingClip is null && patchedIdentifier is { Length: > 0 } && patchedIdentifier != "new")
            {
                patchWarnings.Add($"timeline[{entryIndex}]: no arrangement clip with id \"{patchedIdentifier}\"; {(ReadBoolean(clipPatch["remove"]) == true ? "nothing removed" : "treated as a new clip")}");
            }
            // get_project's JSON names (trackId, startStep, lengthSteps, clip {name, pattern, automation}) work too, so its output round-trips into a patch.
            var nestedClip = clipPatch["clip"] as JsonObject;
            var trackNode = clipPatch["track"] ?? clipPatch["trackId"];
            var targetTrack = ResolvePatchTrack(patchedProject, trackNode)
                ?? patchedProject.Tracks.FirstOrDefault(track => track.Id == existingClip?.TrackId);
            if (targetTrack is null)
            {
                patchWarnings.Add($"timeline[{entryIndex}]: no track \"{ReadString(trackNode)}\"; entry ignored (tracks: {string.Join(", ", patchedProject.Tracks.Select(track => $"\"{track.Name}\""))})");
                continue;
            }
            if (targetTrack.AiLocked || patchedProject.Tracks.FirstOrDefault(track => track.Id == existingClip?.TrackId) is { AiLocked: true })
            {
                lockedEditsIgnored++;
                continue;
            }
            if (ReadBoolean(clipPatch["remove"]) == true)
            {
                if (existingClip is not null)
                {
                    patchedProject.Timeline.Remove(existingClip);
                }
                continue;
            }

            var patchedPattern = (clipPatch["pattern"] ?? nestedClip?["pattern"]) switch
            {
                JsonArray stepArray => stepArray.Select(stepNode => ReadString(stepNode) ?? NotationResolver.RestSymbol).ToList(),
                JsonValue patternValue => CompactPatternJsonConverter.SplitStepString(ReadString(patternValue)),
                _ => null,
            };
            if (patchedPattern is { Count: > 0 })
            {
                patchWarnings.AddRange(PatternLint.ChordSuffixWarnings(patchedPattern, $"timeline[{entryIndex}] (\"{targetTrack.Name}\"), "));
            }
            var timelineClip = existingClip;
            if (timelineClip is null)
            {
                if (patchedPattern is not { Count: > 0 })
                {
                    patchWarnings.Add($"timeline[{entryIndex}]: a new clip needs a \"pattern\"; entry ignored");
                    continue;
                }
                timelineClip = new TimelineClip { Clip = new SlawClip { Name = targetTrack.Name } };
                patchedProject.Timeline.Add(timelineClip);
            }
            timelineClip.TrackId = targetTrack.Id;
            if (patchedPattern is { Count: > 0 })
            {
                timelineClip.Clip.Pattern = patchedPattern;
            }
            timelineClip.Clip.Name = ReadString(clipPatch["name"] ?? nestedClip?["name"]) ?? timelineClip.Clip.Name;
            timelineClip.StartStep = (int)(ReadDouble(clipPatch["start"] ?? clipPatch["startStep"]) ?? timelineClip.StartStep);
            timelineClip.LengthSteps = (int)(ReadDouble(clipPatch["length"] ?? clipPatch["lengthSteps"]) ?? (existingClip is null ? timelineClip.Clip.Pattern.Count : timelineClip.LengthSteps));
            var automationOwner = clipPatch.ContainsKey("automation") ? clipPatch : nestedClip?.ContainsKey("automation") == true ? nestedClip : null;
            if (automationOwner is not null)
            {
                // Arrangement clip envelopes span the clip's whole length; the pattern loops underneath them.
                ApplyClipAutomation(targetTrack, timelineClip.Clip, automationOwner["automation"], timelineClip.LengthSteps, patchWarnings);
            }
        }
        return lockedEditsIgnored;
    }

    internal static string DescribeTrackReferences(SlawProject project) =>
        $"tracks are: {string.Join(", ", project.Tracks.Select(track => $"\"{track.Id}\" \"{track.Name}\""))}";

    /// <summary>A track by id, exact name, or index, the way models like to refer to them.</summary>
    private static SlawTrack? ResolvePatchTrack(SlawProject project, JsonNode? trackNode)
    {
        if (ReadString(trackNode) is not { Length: > 0 } trackReference)
        {
            return null;
        }
        return project.Tracks.FirstOrDefault(track => track.Id == trackReference)
            ?? project.Tracks.FirstOrDefault(track => string.Equals(track.Name, trackReference, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(trackReference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var trackIndex) && trackIndex >= 0 && trackIndex < project.Tracks.Count
                ? project.Tracks[trackIndex]
                : null);
    }

    // ── Lenient readers: models write numbers as strings and booleans as "yes" ──

    private static string? ReadString(JsonNode? node) => node is JsonValue jsonValue
        ? jsonValue.TryGetValue<string>(out var stringValue) ? stringValue : jsonValue.ToJsonString().Trim('"')
        : null;

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue jsonValue)
        {
            return null;
        }
        if (jsonValue.TryGetValue<double>(out var numericValue))
        {
            return double.IsFinite(numericValue) ? numericValue : null;
        }
        return jsonValue.TryGetValue<string>(out var stringValue) &&
               double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue)
            ? parsedValue
            : null;
    }

    private static bool? ReadBoolean(JsonNode? node)
    {
        if (node is not JsonValue jsonValue)
        {
            return null;
        }
        if (jsonValue.TryGetValue<bool>(out var booleanValue))
        {
            return booleanValue;
        }
        return ReadString(node)?.ToLowerInvariant() switch
        {
            "true" or "yes" or "1" or "on" => true,
            "false" or "no" or "0" or "off" => false,
            _ => null,
        };
    }

    /// <summary>Parses a model's patch, which must at least be a JSON object.</summary>
    private static void MaterializeEveryObject(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var (_, childNode) in jsonObject)
                {
                    MaterializeEveryObject(childNode);
                }
                break;
            case JsonArray jsonArray:
                foreach (var childNode in jsonArray)
                {
                    MaterializeEveryObject(childNode);
                }
                break;
        }
    }

    public static JsonObject ParsePatch(string extractedJsonPayload)
    {
        try
        {
            var parsedPatch = JsonNode.Parse(extractedJsonPayload, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject ?? throw new JsonException("the patch is not a JSON object");
            // JsonObject materializes lazily: a key written twice only throws when first read, deep inside Apply.
            // Walking the tree once here turns that into an ordinary, retryable parse error.
            MaterializeEveryObject(parsedPatch);
            return parsedPatch;
        }
        catch (JsonException parseRemediationOpportunity)
        {
            throw new FormatException($"invalid JSON: {parseRemediationOpportunity.Message}", parseRemediationOpportunity);
        }
        catch (ArgumentException duplicateKeyRemediationOpportunity)
        {
            throw new FormatException($"invalid JSON: a key appears twice in one object ({duplicateKeyRemediationOpportunity.Message}); merge them into one", duplicateKeyRemediationOpportunity);
        }
    }
}
