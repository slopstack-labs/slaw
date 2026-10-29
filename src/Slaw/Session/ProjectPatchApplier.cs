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
            targetTrack.Effects = effectArray.Select(effectNode => effectNode switch
            {
                JsonObject effectObject => new EffectDevice
                {
                    Type = ReadString(effectObject["type"]) ?? "lowpass",
                    Enabled = ReadBoolean(effectObject["enabled"]) ?? true,
                    Parameters = (effectObject["parameters"] as JsonObject ?? effectObject["params"] as JsonObject)?
                        .Where(parameterPair => ReadDouble(parameterPair.Value) is not null)
                        .ToDictionary(parameterPair => parameterPair.Key, parameterPair => ReadDouble(parameterPair.Value)!.Value)
                        ?? new Dictionary<string, double>(),
                },
                _ => new EffectDevice { Type = ReadString(effectNode) ?? "lowpass" },
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

        // Drum steps on a melodic instrument (and vice versa) are re-spelled to match.
        PatternEditingOperations.ChangeInstrument(targetTrack, targetTrack.Instrument);
    }

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
        targetTrack.Clips[sceneIndex] = new SlawClip { Name = clipName, Pattern = patchedPattern };
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
