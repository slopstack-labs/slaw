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
    /// <summary>What applying a patch did, besides change the project.</summary>
    internal sealed record PatchOutcome(SlawProject Project, string? ModelSummary, int LockedTrackEditsIgnored);

    public static PatchOutcome Apply(SlawProject baseProject, JsonObject projectPatch)
    {
        var patchedProject = baseProject.DeepClone();
        var lockedTrackEditsIgnored = 0;

        if (ReadString(projectPatch["name"]) is { Length: > 0 } patchedName)
        {
            patchedProject.Name = patchedName;
        }
        if (ReadDouble(projectPatch["bpm"]) is { } patchedTempo)
        {
            patchedProject.Bpm = patchedTempo;
        }
        if (projectPatch["scenes"] is JsonArray sceneArray)
        {
            var patchedScenes = sceneArray
                .Select(sceneNode => ReadString(sceneNode) ?? ReadString(sceneNode?["name"]))
                .Select((sceneName, sceneIndex) => string.IsNullOrWhiteSpace(sceneName) ? $"Scene {sceneIndex + 1}" : sceneName!)
                .ToList();
            if (patchedScenes.Count > 0)
            {
                patchedProject.Scenes = patchedScenes;
            }
        }
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
                patchedProject.Arrangement = patchedArrangement;
            }
        }

        if (projectPatch["tracks"] is JsonArray trackPatchArray)
        {
            foreach (var trackPatch in trackPatchArray.OfType<JsonObject>())
            {
                var patchedIdentifier = ReadString(trackPatch["id"]);
                var existingTrack = patchedProject.Tracks.FirstOrDefault(track => track.Id == patchedIdentifier);
                if (existingTrack is { AiLocked: true })
                {
                    lockedTrackEditsIgnored++;
                    continue;
                }
                if (ReadBoolean(trackPatch["remove"]) == true)
                {
                    if (existingTrack is not null)
                    {
                        patchedProject.Tracks.Remove(existingTrack);
                    }
                    continue;
                }
                var targetTrack = existingTrack;
                if (targetTrack is null)
                {
                    targetTrack = new SlawTrack
                    {
                        Id = SlawTrack.NewIdentifier(),
                        Name = $"AI Track {patchedProject.Tracks.Count + 1}",
                        Gain = 0.7,
                    };
                    patchedProject.Tracks.Add(targetTrack);
                }
                ApplyTrackPatch(targetTrack, trackPatch, patchedProject.Steps);
            }
        }

        if (projectPatch["timeline"] is JsonArray timelinePatchArray)
        {
            lockedTrackEditsIgnored += ApplyTimelinePatch(patchedProject, timelinePatchArray);
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
        }

        return new PatchOutcome(patchedProject, ReadString(projectPatch["summary"]), lockedTrackEditsIgnored);
    }

    private static void ApplyTrackPatch(SlawTrack targetTrack, JsonObject trackPatch, int defaultStepCount)
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
            var previousEffects = targetTrack.Effects;
            targetTrack.Effects = effectArray.Select((effectNode, effectIndex) => effectNode switch
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
                },
                _ => new EffectDevice { Type = ReadString(effectNode) ?? "lowpass", Plugin = PluginInSlot(previousEffects, effectIndex) },
            }).ToList();
        }

        switch (trackPatch["clips"])
        {
            case JsonObject clipsBySceneIndex:
                foreach (var (sceneKey, clipNode) in clipsBySceneIndex)
                {
                    if (int.TryParse(sceneKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sceneIndex) && sceneIndex is >= 0 and < 64)
                    {
                        SetClip(targetTrack, sceneIndex, clipNode, defaultStepCount);
                    }
                }
                break;
            case JsonArray clipsInSceneOrder:
                for (var sceneIndex = 0; sceneIndex < clipsInSceneOrder.Count && sceneIndex < 64; sceneIndex++)
                {
                    SetClip(targetTrack, sceneIndex, clipsInSceneOrder[sceneIndex], defaultStepCount);
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
                    ApplyClipAutomation(targetTrack, automatedClip, sceneEnvelopes);
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
    internal static void ApplyClipAutomation(SlawTrack track, SlawClip clip, JsonNode? envelopesNode)
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
            if (parsedPoints is not { Count: > 0 } || ClipAutomation.ResolveTarget(track, targetKey) is not { } automationTarget)
            {
                mergedEnvelopes.Remove(targetKey);
                continue;
            }
            mergedEnvelopes[targetKey] = ClipAutomation.Tidy(parsedPoints, clip.Pattern.Count, automationTarget.Specification);
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

    /// <summary>A model rewriting the chain as [{"type": "vst3"}] keeps the plugin that was already in that slot.</summary>
    private static PluginReference? PluginInSlot(List<EffectDevice> previousEffects, int effectIndex) =>
        effectIndex < previousEffects.Count ? previousEffects[effectIndex].Plugin : null;

    private static void SetClip(SlawTrack targetTrack, int sceneIndex, JsonNode? clipNode, int defaultStepCount)
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
    /// Free-timeline edits: <c>[{"id": "new", "track": "Bass", "start": 16, "length": 32, "pattern": "A1 . . .", "automation": {...}}]</c>.
    /// An existing id updates only the fields given; <c>"remove": true</c> deletes. Start and length are in steps (16 per bar).
    /// Returns how many edits were refused because they touched a locked track.
    /// </summary>
    private static int ApplyTimelinePatch(SlawProject patchedProject, JsonArray timelinePatchArray)
    {
        var lockedEditsIgnored = 0;
        foreach (var clipPatch in timelinePatchArray.OfType<JsonObject>())
        {
            var patchedIdentifier = ReadString(clipPatch["id"]);
            var existingClip = patchedProject.Timeline.FirstOrDefault(timelineClip => timelineClip.Id == patchedIdentifier);
            var targetTrack = ResolvePatchTrack(patchedProject, clipPatch["track"])
                ?? patchedProject.Tracks.FirstOrDefault(track => track.Id == existingClip?.TrackId);
            if (targetTrack is null)
            {
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

            var patchedPattern = clipPatch["pattern"] switch
            {
                JsonArray stepArray => stepArray.Select(stepNode => ReadString(stepNode) ?? NotationResolver.RestSymbol).ToList(),
                JsonValue patternValue => CompactPatternJsonConverter.SplitStepString(ReadString(patternValue)),
                _ => null,
            };
            var timelineClip = existingClip;
            if (timelineClip is null)
            {
                if (patchedPattern is not { Count: > 0 })
                {
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
            timelineClip.Clip.Name = ReadString(clipPatch["name"]) ?? timelineClip.Clip.Name;
            timelineClip.StartStep = (int)(ReadDouble(clipPatch["start"]) ?? timelineClip.StartStep);
            timelineClip.LengthSteps = (int)(ReadDouble(clipPatch["length"]) ?? (existingClip is null ? timelineClip.Clip.Pattern.Count : timelineClip.LengthSteps));
            if (clipPatch.ContainsKey("automation"))
            {
                ApplyClipAutomation(targetTrack, timelineClip.Clip, clipPatch["automation"]);
            }
        }
        return lockedEditsIgnored;
    }

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
    public static JsonObject ParsePatch(string extractedJsonPayload)
    {
        try
        {
            return JsonNode.Parse(extractedJsonPayload, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject ?? throw new JsonException("the patch is not a JSON object");
        }
        catch (JsonException parseRemediationOpportunity)
        {
            throw new FormatException($"invalid JSON: {parseRemediationOpportunity.Message}", parseRemediationOpportunity);
        }
    }
}
