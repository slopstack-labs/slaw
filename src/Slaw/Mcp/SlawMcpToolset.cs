using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;
using SlopstackLabs.Slaw.Workflows;

namespace SlopstackLabs.Slaw.Mcp;

/// <summary>
/// Every tool slaw offers to MCP clients. Mutations go through the
/// <see cref="StudioDocument"/>, so they are undoable, reach the live engine,
/// and refresh the studio exactly like a human edit would.
/// </summary>
internal sealed class SlawMcpToolset
{
    public const string ServerInstructions =
        "slaw is an AI-native step-sequencing DAW. A project has tracks (each with an instrument, effects and mixer settings), " +
        "scenes (rows of clips) and an arrangement (the song order: scene + repeats). Each track holds one clip per scene; a clip " +
        "is a pattern of sixteenth-note steps written as a space-separated string: \"x\" drum hit, \"C4\" note, \"A3+C4+E4\" chord, " +
        "\"_\" hold the previous note, \".\" silence. Start with get_project and get_catalog. Prefer apply_patch for multi-part edits. " +
        "Tracks marked LOCKED are protected from AI edits and must not be changed.";

    private sealed record ToolDefinition(string Name, string Description, JsonObject InputSchema, Func<JsonObject, string, CancellationToken, Task<string>> Handler);

    private readonly ISlawMcpHost host;
    private readonly Dictionary<string, ToolDefinition> toolDefinitions = new(StringComparer.Ordinal);

    public SlawMcpToolset(ISlawMcpHost host)
    {
        this.host = host;
        RegisterTools();
    }

    public bool HasTool(string toolName) => toolDefinitions.ContainsKey(toolName);

    public JsonArray DescribeTools() => new(toolDefinitions.Values.Select(toolDefinition => (JsonNode)new JsonObject
    {
        ["name"] = toolDefinition.Name,
        ["description"] = toolDefinition.Description,
        ["inputSchema"] = toolDefinition.InputSchema.DeepClone(),
    }).ToArray());

    public Task<string> InvokeAsync(string toolName, JsonObject toolArguments, string clientName, CancellationToken cancellationToken) =>
        toolDefinitions[toolName].Handler(toolArguments, clientName, cancellationToken);

    // ── Schema helpers ────────────────────────────────────────────────────────

    private static JsonObject Schema(JsonObject properties, params string[] requiredProperties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(requiredProperties.Select(requiredProperty => (JsonNode)requiredProperty).ToArray()),
    };

    private static JsonObject Property(string jsonType, string description) => new() { ["type"] = jsonType, ["description"] = description };

    private static JsonObject AnyProperty(string description) => new() { ["description"] = description };

    private static JsonObject EnumProperty(string description, params string[] allowedValues) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(allowedValues.Select(allowedValue => (JsonNode)allowedValue).ToArray()),
    };

    private const string TrackReferenceDescription = "Track id (preferred, see get_project), exact track name, or 0-based track index.";

    private void Register(string toolName, string description, JsonObject inputSchema, Func<JsonObject, string, CancellationToken, Task<string>> handler) =>
        toolDefinitions[toolName] = new ToolDefinition(toolName, description, inputSchema, handler);

    // ── Tools ─────────────────────────────────────────────────────────────────

    private void RegisterTools()
    {
        Register("get_project",
            "Describe the open project: tempo, scenes, arrangement, every track with its id, instrument, effects, mixer settings and clip patterns. " +
            "Use format \"json\" for the complete project file.",
            Schema(new JsonObject { ["format"] = EnumProperty("listing (compact, default) or json (full project file)", "listing", "json") }),
            async (toolArguments, _, _) =>
            {
                var requestedFormat = ReadString(toolArguments["format"]) ?? "listing";
                return await host.InvokeAsync(() =>
                {
                    var openProject = host.Document.Project;
                    var hostHeader = $"[{host.HostDescription}{(host.Document.FilePath is { } filePath ? $" · {filePath}" : string.Empty)}{(host.Document.UnsavedChanges ? " · unsaved changes" : string.Empty)}]\n";
                    return requestedFormat == "json"
                        ? hostHeader + ProjectSerializer.Serialize(openProject)
                        : hostHeader + PromptLibrary.DescribeProjectForPatching(openProject);
                });
            });

        Register("get_catalog",
            "List every instrument and audio effect with its parameter keys and ranges, plus the pattern step vocabulary.",
            Schema(new JsonObject()),
            (_, _, _) => Task.FromResult(PromptLibrary.DescribeDeviceCatalog() +
                "\nPattern steps: \"x\" drum hit, \"C4\" note, \"A3+C4+E4\" chord, \"_\" hold previous note, \".\" silence. Clips are usually 16 steps (one bar of sixteenths)."));

        Register("apply_patch",
            "Apply a patch describing only what should change. Keys (all optional): bpm, name, scenes [names], arrangement [{scene, repeats}], " +
            "tracks [{id (existing id or \"new\"), name, instrument, sample, gain, pan, muted, solo, instrumentParameters {key: number}, " +
            "effects [{type, enabled, parameters}] (replaces the list), clips {\"<sceneIndex>\": \"x . . . x . . .\" or null}, remove: true}], " +
            "master {gain, drive, lowpassHz, delaySteps, delayFeedback, delayMix}. Undoable in the studio.",
            Schema(new JsonObject { ["patch"] = Property("object", "The patch object.") }, "patch"),
            async (toolArguments, clientName, _) =>
            {
                var projectPatch = toolArguments["patch"] as JsonObject
                    ?? (toolArguments["patch"] is JsonValue patchText && patchText.TryGetValue<string>(out var serializedPatch)
                        ? ProjectPatchApplier.ParsePatch(serializedPatch)
                        : throw new McpInvalidParamsException("patch must be a JSON object"));
                return await host.InvokeAsync(() =>
                {
                    var previousProject = host.Document.Project;
                    var patchOutcome = ProjectPatchApplier.Apply(previousProject, projectPatch);
                    var lockEnforcementCount = ProductionWorkflows.EnforceAiLocks(previousProject, patchOutcome.Project) + patchOutcome.LockedTrackEditsIgnored;
                    var remediationLedger = ProjectAlignmentRemediator.Remediate(patchOutcome.Project);
                    patchOutcome.Project.AiProvenance = $"{clientName} via MCP";
                    var changeSummary = ProjectChangeSummarizer.Summarize(previousProject, patchOutcome.Project);
                    host.Document.ReplaceProject(patchOutcome.Project, $"{clientName}: patch", undoable: true);
                    var editDescription = patchOutcome.ModelSummary ?? string.Join("; ", changeSummary.Take(3));
                    host.OnAgentEdit(clientName, editDescription);
                    return DescribeOutcome(changeSummary, remediationLedger, lockEnforcementCount);
                });
            });

        Register("set_clip",
            "Write (or clear) the clip of one track in one scene. scene may equal the scene count to append a new scene.",
            Schema(new JsonObject
            {
                ["track"] = AnyProperty(TrackReferenceDescription),
                ["scene"] = Property("integer", "0-based scene index."),
                ["pattern"] = AnyProperty("Space-separated steps, e.g. \"x . . . x . . .\", or an array of steps, or null to delete the clip."),
                ["name"] = Property("string", "Optional clip name."),
            }, "track", "scene", "pattern"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var (trackIndex, targetTrack) = ResolveTrack(toolArguments["track"]);
                RejectLocked(targetTrack);
                var sceneIndex = (int)(ReadDouble(toolArguments["scene"]) ?? throw new McpInvalidParamsException("scene is required"));
                if (sceneIndex < 0 || sceneIndex > document.Project.Scenes.Count)
                {
                    throw new McpInvalidParamsException($"scene must be between 0 and {document.Project.Scenes.Count}");
                }
                var stepList = toolArguments["pattern"] switch
                {
                    null => null,
                    JsonArray stepArray => stepArray.Select(stepNode => ReadString(stepNode) ?? NotationResolver.RestSymbol).ToList(),
                    var patternNode => CompactPatternJsonConverter.SplitStepString(ReadString(patternNode)),
                };
                document.BeginEdit($"{clientName}: clip");
                if (sceneIndex == document.Project.Scenes.Count)
                {
                    document.Project.Scenes.Add($"Scene {sceneIndex + 1}");
                }
                while (targetTrack.Clips.Count <= sceneIndex)
                {
                    targetTrack.Clips.Add(null);
                }
                targetTrack.Clips[sceneIndex] = stepList is { Count: > 0 }
                    ? new SlawClip { Name = ReadString(toolArguments["name"]) ?? targetTrack.Clips[sceneIndex]?.Name ?? document.Project.Scenes[sceneIndex], Pattern = stepList }
                    : null;
                ProjectAlignmentRemediator.Remediate(document.Project);
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} wrote a clip for {targetTrack.Name}", EditScope.Structure);
                host.OnAgentEdit(clientName, $"wrote a clip for {targetTrack.Name}");
                var writtenClip = targetTrack.ClipAt(sceneIndex);
                return writtenClip is null
                    ? $"cleared the clip of {targetTrack.Name} in scene {sceneIndex}"
                    : $"{targetTrack.Name} · scene {sceneIndex} ({document.Project.Scenes[sceneIndex]}): {string.Join(' ', writtenClip.Pattern)}";
            }));

        Register("add_track",
            "Add a track. instrument is one of the catalog instruments (or \"sampler\" with a sample path from list_samples). Returns the new track id.",
            Schema(new JsonObject
            {
                ["instrument"] = Property("string", "Instrument name, e.g. kick, saw, pluck, sampler."),
                ["name"] = Property("string", "Optional track name."),
                ["sample"] = Property("string", "WAV path for sampler tracks."),
            }, "instrument"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                if (document.Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
                {
                    throw new McpInvalidParamsException($"the project already has {ProjectAlignmentRemediator.MaximumTrackCount} tracks");
                }
                var resolvedInstrument = ProjectAlignmentRemediator.ResolveInstrument(ReadString(toolArguments["instrument"]));
                var samplePath = ReadString(toolArguments["sample"]);
                if (samplePath is not null && !File.Exists(samplePath))
                {
                    throw new McpInvalidParamsException($"sample not found: {samplePath}");
                }
                var addedTrack = new SlawTrack
                {
                    Name = ReadString(toolArguments["name"]) ?? $"{resolvedInstrument} {document.Project.Tracks.Count + 1}",
                    Instrument = samplePath is null ? resolvedInstrument : "sampler",
                    Sample = samplePath is null ? null : Path.GetFullPath(samplePath),
                    Gain = 0.7,
                    Clips = Enumerable.Repeat<SlawClip?>(null, document.Project.Scenes.Count).ToList(),
                };
                document.BeginEdit($"{clientName}: add track");
                document.Project.Tracks.Add(addedTrack);
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} added the track {addedTrack.Name}", EditScope.Structure);
                host.OnAgentEdit(clientName, $"added the track {addedTrack.Name}");
                return $"added track id \"{addedTrack.Id}\" \"{addedTrack.Name}\" ({addedTrack.Instrument}). Write clips with set_clip.";
            }));

        Register("update_track",
            "Change a track's name, instrument, mixer settings or instrument parameters. Only the given fields change.",
            Schema(new JsonObject
            {
                ["track"] = AnyProperty(TrackReferenceDescription),
                ["name"] = Property("string", "New name."),
                ["instrument"] = Property("string", "New instrument."),
                ["gain"] = Property("number", "0 to 1.5."),
                ["pan"] = Property("number", "-1 (left) to 1 (right)."),
                ["muted"] = Property("boolean", "Mute."),
                ["solo"] = Property("boolean", "Solo."),
                ["instrumentParameters"] = Property("object", "Parameter key → number, see get_catalog."),
            }, "track"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var (_, targetTrack) = ResolveTrack(toolArguments["track"]);
                RejectLocked(targetTrack);
                var trackPatch = toolArguments.DeepClone().AsObject();
                trackPatch.Remove("track");
                trackPatch["id"] = targetTrack.Id;
                var patchOutcome = ProjectPatchApplier.Apply(document.Project, new JsonObject { ["tracks"] = new JsonArray(trackPatch) });
                ProjectAlignmentRemediator.Remediate(patchOutcome.Project);
                patchOutcome.Project.AiProvenance = $"{clientName} via MCP";
                var changeSummary = ProjectChangeSummarizer.Summarize(document.Project, patchOutcome.Project);
                document.ReplaceProject(patchOutcome.Project, $"{clientName}: update track", undoable: true);
                host.OnAgentEdit(clientName, $"updated {targetTrack.Name}");
                return string.Join("\n", changeSummary);
            }));

        Register("add_effect",
            "Add an audio effect to a track's device chain (see get_catalog for types and parameters).",
            Schema(new JsonObject
            {
                ["track"] = AnyProperty(TrackReferenceDescription),
                ["type"] = EnumProperty("Effect type.", DeviceCatalog.EffectTypes.ToArray()),
                ["parameters"] = Property("object", "Optional parameter key → number."),
            }, "track", "type"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var (_, targetTrack) = ResolveTrack(toolArguments["track"]);
                RejectLocked(targetTrack);
                var effectType = ProjectAlignmentRemediator.ResolveEffect(ReadString(toolArguments["type"]))
                    ?? throw new McpInvalidParamsException($"unknown effect; use one of {string.Join(", ", DeviceCatalog.EffectTypes)}");
                var addedEffect = new EffectDevice { Type = effectType };
                if (toolArguments["parameters"] is JsonObject parameterObject)
                {
                    foreach (var (parameterKey, parameterNode) in parameterObject)
                    {
                        if (ReadDouble(parameterNode) is { } parameterValue)
                        {
                            addedEffect.Parameters[parameterKey] = parameterValue;
                        }
                    }
                }
                document.BeginEdit($"{clientName}: add effect");
                targetTrack.Effects.Add(addedEffect);
                ProjectAlignmentRemediator.Remediate(document.Project);
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} added {DeviceCatalog.EffectDisplayName(effectType)} to {targetTrack.Name}", EditScope.Structure);
                host.OnAgentEdit(clientName, $"added {DeviceCatalog.EffectDisplayName(effectType)} to {targetTrack.Name}");
                return $"{targetTrack.Name} chain: {string.Join(" → ", targetTrack.Effects.Select(effect => effect.Type))}";
            }));

        Register("set_tempo",
            "Set the tempo in BPM (40–300). Heard immediately if the studio is playing.",
            Schema(new JsonObject { ["bpm"] = Property("number", "Beats per minute.") }, "bpm"),
            async (toolArguments, clientName, _) =>
            {
                var requestedTempo = ReadDouble(toolArguments["bpm"]) ?? throw new McpInvalidParamsException("bpm is required");
                return await host.InvokeAsync(() =>
                {
                    host.Document.Edit($"{clientName} set the tempo to {requestedTempo:0}", project =>
                    {
                        project.Bpm = Math.Clamp(requestedTempo, 40, 300);
                        project.AiProvenance = $"{clientName} via MCP";
                    });
                    host.OnAgentEdit(clientName, $"set the tempo to {requestedTempo:0} BPM");
                    return FormattableString.Invariant($"tempo is now {host.Document.Project.Bpm:0} BPM");
                });
            });

        Register("transport",
            "Control playback in the running studio: play_scene (launch a scene), play_arrangement (from a section), or stop. Live studio only.",
            Schema(new JsonObject
            {
                ["action"] = EnumProperty("What to do.", "play_scene", "play_arrangement", "stop"),
                ["scene"] = Property("integer", "Scene index for play_scene."),
                ["section"] = Property("integer", "Arrangement section index for play_arrangement (default 0)."),
            }, "action"),
            async (toolArguments, _, _) =>
            {
                if (!host.IsLive)
                {
                    throw new McpInvalidParamsException("transport needs the live studio; this is the headless server. Use render to hear the result as a WAV.");
                }
                var transportAction = ReadString(toolArguments["action"]) ?? "stop";
                var sceneIndex = (int)(ReadDouble(toolArguments["scene"]) ?? 0);
                var sectionIndex = (int)(ReadDouble(toolArguments["section"]) ?? 0);
                return await host.InvokeAsync(() => host.ControlTransport(transportAction, sceneIndex, sectionIndex));
            });

        Register("render",
            "Bounce the arrangement to a 16-bit WAV file and report its duration and peak level.",
            Schema(new JsonObject { ["path"] = Property("string", "Output .wav path (default: next to the project, or a temp file).") }),
            async (toolArguments, _, cancellationToken) =>
            {
                var (projectSnapshot, defaultOutputPath) = await host.InvokeAsync(() => (
                    host.Document.Project.DeepClone(),
                    host.Document.FilePath is { } projectPath
                        ? Path.ChangeExtension(projectPath, ".wav")
                        : Path.Combine(Path.GetTempPath(), $"slaw-mcp-{Environment.ProcessId}.wav")));
                var outputPath = Path.GetFullPath(ReadString(toolArguments["path"]) ?? defaultOutputPath);
                var renderedArrangement = await Task.Run(() =>
                {
                    var arrangementBuffer = OfflineArrangementRenderer.Render(projectSnapshot);
                    WaveFileMaterializer.Write(outputPath, arrangementBuffer);
                    return arrangementBuffer;
                }, cancellationToken);
                var peakDecibels = 20 * Math.Log10(Math.Max(1e-6, renderedArrangement.PeakAmplitude()));
                return FormattableString.Invariant(
                    $"rendered {renderedArrangement.DurationSeconds:0.0} s to {outputPath} (peak {peakDecibels:0.0} dBFS, 44.1 kHz stereo). Radio-ready deliverable shipped to production.");
            });

        Register("save_project",
            "Save the project (optionally to a new path).",
            Schema(new JsonObject { ["path"] = Property("string", "Optional .slaw path; defaults to the current file.") }),
            async (toolArguments, _, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var savePath = ReadString(toolArguments["path"]) ?? document.FilePath
                    ?? throw new McpInvalidParamsException("this project has never been saved; pass a path");
                ProjectSerializer.Save(document.Project, savePath);
                document.FilePath = Path.GetFullPath(savePath);
                document.UnsavedChanges = false;
                return $"saved to {document.FilePath}";
            }));

        Register("undo", "Undo the last edit (by anyone — agent or human).", Schema(new JsonObject()),
            async (_, _, _) => await host.InvokeAsync(() => host.Document.Undo() is { } undoneLabel ? $"undid: {undoneLabel}" : "nothing to undo"));

        Register("redo", "Redo the last undone edit.", Schema(new JsonObject()),
            async (_, _, _) => await host.InvokeAsync(() => host.Document.Redo() is { } redoneLabel ? $"redid: {redoneLabel}" : "nothing to redo"));

        Register("list_samples",
            "List WAV samples in the user's sample folders (for sampler tracks).",
            Schema(new JsonObject
            {
                ["filter"] = Property("string", "Optional case-insensitive substring of the file name."),
                ["limit"] = Property("integer", "Maximum results (default 50)."),
            }),
            (toolArguments, _, _) =>
            {
                var filterText = ReadString(toolArguments["filter"]) ?? string.Empty;
                var resultLimit = (int)Math.Clamp(ReadDouble(toolArguments["limit"]) ?? 50, 1, 500);
                var matchingSamples = host.SampleLibraryFiles
                    .Where(samplePath => Path.GetFileName(samplePath).Contains(filterText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return Task.FromResult(matchingSamples.Count == 0
                    ? "no samples found — the user can add sample folders in slaw's Settings → Samples"
                    : $"{matchingSamples.Count} sample(s){(matchingSamples.Count > resultLimit ? $", first {resultLimit}" : string.Empty)}:\n" +
                      string.Join("\n", matchingSamples.Take(resultLimit)));
            });

        Register("ask_copilot",
            "Ask slaw's own built-in AI co-pilot (whatever model the user configured) for its opinion of the project. " +
            "Yes, this is an AI asking another AI about music. It is exactly as useful as it sounds.",
            Schema(new JsonObject { ["question"] = Property("string", "Optional question; default is a vibe check.") }),
            async (toolArguments, _, cancellationToken) =>
            {
                var inferenceProvider = host.InferenceProvider
                    ?? throw new McpInvalidParamsException("slaw's AI features are switched off (or misconfigured) in Settings");
                var projectSnapshot = await host.InvokeAsync(() => host.Document.Project.DeepClone());
                var question = ReadString(toolArguments["question"]);
                if (question is null)
                {
                    return await ProductionWorkflows.VibeCheckAsync(inferenceProvider, projectSnapshot, Array.Empty<string>(), cancellationToken);
                }
                var copilotAnswer = await inferenceProvider.StreamCompletionAsync(
                    $"You are slaw's AI co-pilot. Another AI agent is producing this song and asks: \"{question}\"\n\n" +
                    $"{PromptLibrary.DescribeArrangement(projectSnapshot)}\n\nAnswer in at most five sentences.",
                    Array.Empty<string>(), cancellationToken);
                return $"{inferenceProvider.ModelIdentifier} says: {copilotAnswer.Trim()}";
            });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (int TrackIndex, SlawTrack Track) ResolveTrack(JsonNode? trackReference)
    {
        var tracks = host.Document.Project.Tracks;
        var referenceText = ReadString(trackReference) ?? throw new McpInvalidParamsException("track is required");
        var trackIndex = tracks.FindIndex(track => track.Id == referenceText);
        if (trackIndex < 0)
        {
            trackIndex = tracks.FindIndex(track => string.Equals(track.Name, referenceText, StringComparison.OrdinalIgnoreCase));
        }
        if (trackIndex < 0 && int.TryParse(referenceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericIndex) &&
            numericIndex >= 0 && numericIndex < tracks.Count)
        {
            trackIndex = numericIndex;
        }
        return trackIndex >= 0
            ? (trackIndex, tracks[trackIndex])
            : throw new McpInvalidParamsException($"no track \"{referenceText}\". Tracks: {string.Join(", ", tracks.Select(track => $"{track.Id} \"{track.Name}\""))}");
    }

    private static void RejectLocked(SlawTrack track)
    {
        if (track.AiLocked)
        {
            throw new McpInvalidParamsException($"\"{track.Name}\" is LOCKED: the user protected it from AI edits");
        }
    }

    private static string DescribeOutcome(IReadOnlyList<string> changeSummary, IReadOnlyList<string> remediationLedger, int lockEnforcementCount)
    {
        var outcomeDescription = new StringBuilder();
        outcomeDescription.AppendLine("applied:");
        foreach (var changeLine in changeSummary)
        {
            outcomeDescription.Append("- ").AppendLine(changeLine);
        }
        if (remediationLedger.Count > 0)
        {
            outcomeDescription.AppendLine($"remediated {remediationLedger.Count} misalignment(s): {string.Join("; ", remediationLedger.Take(6))}");
        }
        if (lockEnforcementCount > 0)
        {
            outcomeDescription.AppendLine($"ignored edits to {lockEnforcementCount} LOCKED track(s)");
        }
        return outcomeDescription.ToString().TrimEnd();
    }

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
            return numericValue;
        }
        return jsonValue.TryGetValue<string>(out var stringValue) &&
               double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue)
            ? parsedValue
            : null;
    }

    /// <summary>For hosts that render tool output as JSON.</summary>
    public static string Serialize(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
