using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Plugins;
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
        "slaw is an AI-native step-sequencing DAW with two places for music, like Ableton Live. " +
        "TRACKS each have an instrument, effects and mixer settings. " +
        "The SESSION holds clips by scene: every track has one clip slot per scene (a grid of loops to jam with and launch). " +
        "The ARRANGEMENT is the song: one linear timeline, measured in sixteenth-note steps, 16 steps per bar (bar 1 starts at step 0, bar 2 at step 16). " +
        "An arrangement clip sits on one track at a start step for a length in steps; its pattern loops to fill that length " +
        "(a 16-step drum pattern with length 128 plays 8 bars). Scenes are NOT the song order any more: only the arrangement is played and rendered. " +
        "A pattern is a space-separated string of steps: \"x\" drum hit, \"C4\" note, \"A3+C4+E4\" chord, \"C4:8\" a note held 8 steps, " +
        "\"_\" hold the previous note, \".\" silence; \"C4@0.5\" or \"x@0.5\" sets a velocity (0..1, default 1). " +
        "Start with get_project and get_catalog. Write session clips with set_clip or apply_patch \"tracks\"/\"clips\"; place clips in the arrangement with " +
        "apply_patch \"timeline\" (track, start, length, pattern). Prefer apply_patch for multi-part edits. " +
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
                if (requestedFormat != "json")
                {
                    // Parameter titles come from the plugins themselves; ask them before writing the listing.
                    var pluginReferences = await host.InvokeAsync(() => host.Document.Project.Tracks
                        .SelectMany(track => track.Effects.Select(effect => effect.Plugin).Append(track.Plugin))
                        .OfType<PluginReference>()
                        .ToList());
                    await Task.WhenAll(pluginReferences.Select(pluginReference =>
                        Task.Run(() => Vst3ParameterDirectory.Get(pluginReference, PluginProbeTimeout))));
                }
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
                "\nPattern steps: \"x\" drum hit, \"C4\" note, \"A3+C4+E4\" chord, \"C4:8\" note held 8 steps, \"_\" hold previous note, \".\" silence. " +
                "Append \"@<velocity>\" (0..1, default 1) to a note or hit for dynamics: \"x@0.4\" ghost hit, \"C4:8@0.6+E4\" soft root under a full third. Clips are usually 16 steps (one bar of sixteenths)." +
                "\nClip envelopes (set_automation, or \"automation\" in apply_patch) move a parameter while a clip plays, as [step, value] breakpoints joined linearly. " +
                "Targets: gain (0..1.5), pan (-1..1), pitch (semitones -24..24; oscillators bend in place, samples/plucks/drums bend tape-style), " +
                "fx.<effectIndex>.<param> (an effect parameter, continuous), inst.<param> (an instrument parameter, sampled as each note starts). Values use the catalog's units; plugin instruments follow inst.<param> continuously." +
                "\nVST3 plugins: instrument \"vst3\" and effect type \"vst3\" host the user's installed plugins (see list_plugins). " +
                "Pass plugin {path, classId} (or the plugin's name) to add_track / add_effect / apply_patch. Their parameters are keys \"p<id>\" " +
                "with normalized values 0..1; describe_plugin lists them with titles." +
                "\nArrangement (apply_patch \"timeline\"): the song, as clips placed at any step on any track (16 steps per bar), independent of scenes. " +
                "Play it with transport play_arrangement (from a bar); render bounces it."));

        Register("apply_patch",
            "Apply a patch describing only what should change. Keys (all optional): bpm, name, scenes [names], " +
            "tracks [{id (existing id or \"new\"), name, instrument, sample, gain, pan, muted, solo, instrumentParameters {key: number}, " +
            "effects [{type, enabled, parameters}] (replaces the list), clips {\"<sceneIndex>\": \"x . . . x . . .\" or null}, " +
            "automation {\"<sceneIndex>\": {\"<target>\": [[step, value], ...] or null}} (merged into that clip's envelopes, see get_catalog), remove: true}], " +
            "timeline [{id (existing timeline clip id or \"new\"), track (id, name or index), start (steps, 16 per bar), length (steps; the pattern loops to fill it), " +
            "pattern, name, automation {\"<target>\": [[step, value], ...]}, remove: true}] (the arrangement: the song as clips anywhere in time, independent of scenes; " +
            "where clips overlap on a track the later-starting one plays), " +
            "arrangement [{scene, repeats}] (shortcut: lays those session scenes end to end and REPLACES the whole arrangement timeline with them), " +
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
                // New notes keep the clip's envelopes; clearing the clip clears them too.
                targetTrack.Clips[sceneIndex] = stepList is { Count: > 0 }
                    ? new SlawClip
                    {
                        Name = ReadString(toolArguments["name"]) ?? targetTrack.Clips[sceneIndex]?.Name ?? document.Project.Scenes[sceneIndex],
                        Pattern = stepList,
                        Automation = targetTrack.Clips[sceneIndex]?.Automation,
                    }
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

        Register("set_automation",
            "Write (or clear) a clip envelope: a parameter that moves while the clip plays. Targets: gain, pan, pitch, " +
            "fx.<effectIndex>.<param>, inst.<param> (see get_catalog). Points are [step, value] pairs, step 0..clip length, joined linearly; " +
            "values hold before the first and after the last point. E.g. a dubstep filter wobble: target \"fx.0.cutoff\", " +
            "points [[0,300],[2,2500],[4,300],[6,2500],[8,300]].",
            Schema(new JsonObject
            {
                ["track"] = AnyProperty(TrackReferenceDescription),
                ["scene"] = Property("integer", "0-based scene index of an existing clip."),
                ["target"] = Property("string", "Target key, e.g. \"fx.0.cutoff\", \"pitch\", \"gain\", \"inst.attack\"."),
                ["points"] = AnyProperty("Array of [step, value] pairs (or {step, value} objects); null or [] removes the envelope."),
            }, "track", "scene", "target", "points"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var (_, targetTrack) = ResolveTrack(toolArguments["track"]);
                RejectLocked(targetTrack);
                var sceneIndex = (int)(ReadDouble(toolArguments["scene"]) ?? throw new McpInvalidParamsException("scene is required"));
                if (targetTrack.ClipAt(sceneIndex) is not { } automatedClip)
                {
                    throw new McpInvalidParamsException($"{targetTrack.Name} has no clip in scene {sceneIndex}; write one with set_clip first");
                }
                var targetKey = ReadString(toolArguments["target"]) ?? throw new McpInvalidParamsException("target is required");
                if (ClipAutomation.ResolveTarget(targetTrack, targetKey) is not { } automationTarget)
                {
                    var validTargets = string.Join(", ", ClipAutomation.TargetsFor(targetTrack).Select(target => target.Key));
                    throw new McpInvalidParamsException($"{targetTrack.Name} has no automation target \"{targetKey}\". valid: {validTargets}");
                }
                document.BeginEdit($"{clientName}: envelope");
                ProjectPatchApplier.ApplyClipAutomation(targetTrack, automatedClip, new JsonObject { [targetKey] = toolArguments["points"]?.DeepClone() });
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} automated {automationTarget.Label} on {targetTrack.Name}", EditScope.Structure);
                host.OnAgentEdit(clientName, $"automated {automationTarget.Label} on {targetTrack.Name}");
                return automatedClip.Automation?.GetValueOrDefault(targetKey) is { } writtenPoints
                    ? $"{targetTrack.Name} · scene {sceneIndex}: {ClipAutomation.Describe(targetKey, writtenPoints)} ({automationTarget.Label})"
                    : $"removed the {automationTarget.Label} envelope from {targetTrack.Name} · scene {sceneIndex}";
            }));

        Register("add_track",
            "Add a track. instrument is one of the catalog instruments (or \"sampler\" with a sample path from list_samples, " +
            "or \"vst3\" with a plugin from list_plugins). Returns the new track id.",
            Schema(new JsonObject
            {
                ["instrument"] = Property("string", "Instrument name, e.g. kick, saw, pluck, sampler, vst3."),
                ["name"] = Property("string", "Optional track name."),
                ["sample"] = Property("string", "WAV path for sampler tracks."),
                ["plugin"] = AnyProperty("VST3 instrument for vst3 tracks: {\"path\": bundle path, \"classId\": 32 hex chars} from list_plugins, or the plugin's name."),
            }, "instrument"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                if (document.Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
                {
                    throw new McpInvalidParamsException($"the project already has {ProjectAlignmentRemediator.MaximumTrackCount} tracks");
                }
                var resolvedInstrument = ProjectAlignmentRemediator.ResolveInstrument(ReadString(toolArguments["instrument"]));
                if (toolArguments["plugin"] is not null || resolvedInstrument == DeviceTypes.Vst3)
                {
                    var instrumentPlugin = PluginDeviceOperations.ParseReference(toolArguments["plugin"])
                        ?? throw new McpInvalidParamsException("vst3 tracks need a plugin: pass {path, classId} or a name from list_plugins");
                    var pluginTrack = PluginDeviceOperations.CreateInstrumentTrack(document.Project, instrumentPlugin, sceneIndex: -1, ReadString(toolArguments["name"]));
                    document.BeginEdit($"{clientName}: add track");
                    document.Project.Tracks.Add(pluginTrack);
                    document.Project.AiProvenance = $"{clientName} via MCP";
                    document.CommitEdit($"{clientName} added the track {pluginTrack.Name}", EditScope.Structure);
                    host.OnAgentEdit(clientName, $"added the track {pluginTrack.Name} ({instrumentPlugin.Name})");
                    return $"added track id \"{pluginTrack.Id}\" \"{pluginTrack.Name}\" (vst3 \"{instrumentPlugin.Name}\"). Write clips with set_clip; see its knobs with describe_plugin.";
                }
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
                ["type"] = EnumProperty("Effect type (\"vst3\" for a plugin from list_plugins).", DeviceCatalog.EffectTypes.Append(DeviceTypes.Vst3).ToArray()),
                ["parameters"] = Property("object", "Optional parameter key → number (p<id> → 0..1 for vst3)."),
                ["plugin"] = AnyProperty("VST3 effect for type vst3: {\"path\", \"classId\"} from list_plugins, or the plugin's name."),
            }, "track", "type"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var (_, targetTrack) = ResolveTrack(toolArguments["track"]);
                RejectLocked(targetTrack);
                var effectType = ProjectAlignmentRemediator.ResolveEffect(ReadString(toolArguments["type"]))
                    ?? throw new McpInvalidParamsException($"unknown effect; use one of {string.Join(", ", DeviceCatalog.EffectTypes)}");
                var addedEffect = new EffectDevice { Type = effectType };
                if (effectType == DeviceTypes.Vst3)
                {
                    addedEffect.Plugin = PluginDeviceOperations.ParseReference(toolArguments["plugin"])
                        ?? throw new McpInvalidParamsException("vst3 effects need a plugin: pass {path, classId} or a name from list_plugins");
                }
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
                return $"{targetTrack.Name} chain: {string.Join(" → ", targetTrack.Effects.Select(PluginDeviceOperations.DeviceName))}";
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
            "Control playback in the running studio: play_arrangement (the song, from a bar), play_scene (launch a session scene; while the arrangement plays, " +
            "the scene's clips take their tracks over from the next bar), back_to_arrangement (return those tracks to the arrangement), or stop. Live studio only.",
            Schema(new JsonObject
            {
                ["action"] = EnumProperty("What to do. play_timeline is an alias of play_arrangement.", "play_arrangement", "play_scene", "back_to_arrangement", "stop", "play_timeline"),
                ["scene"] = Property("integer", "Scene index for play_scene."),
                ["bar"] = Property("integer", "0-based bar to start play_arrangement from (default 0)."),
            }, "action"),
            async (toolArguments, _, _) =>
            {
                if (!host.IsLive)
                {
                    throw new McpInvalidParamsException("transport needs the live studio; this is the headless server. Use render to hear the result as a WAV.");
                }
                var transportAction = ReadString(toolArguments["action"]) ?? "stop";
                var sceneIndex = (int)(ReadDouble(toolArguments["scene"]) ?? 0);
                var startBar = (int)(ReadDouble(toolArguments["bar"]) ?? 0);
                return await host.InvokeAsync(() => host.ControlTransport(transportAction, sceneIndex, startBar));
            });

        Register("render",
            "Bounce the arrangement (the song timeline) to a 16-bit WAV file and report its duration and peak level.",
            Schema(new JsonObject
            {
                ["path"] = Property("string", "Output .wav path (default: next to the project, or a temp file)."),
                ["source"] = EnumProperty("What to render: the arrangement (timeline is an alias).", "arrangement", "timeline"),
            }),
            async (toolArguments, _, cancellationToken) =>
            {
                var (projectSnapshot, defaultOutputPath) = await host.InvokeAsync(() => (
                    host.Document.Project.DeepClone(),
                    host.Document.FilePath is { } projectPath
                        ? Path.ChangeExtension(projectPath, ".wav")
                        : Path.Combine(Path.GetTempPath(), $"slaw-mcp-{Environment.ProcessId}.wav")));
                var outputPath = Path.GetFullPath(ReadString(toolArguments["path"]) ?? defaultOutputPath);
                if (projectSnapshot.Timeline.Count == 0)
                {
                    throw new McpInvalidParamsException("the arrangement is empty; place clips with apply_patch {\"timeline\": [{\"track\": ..., \"start\": 0, \"length\": 64, \"pattern\": ...}]} first");
                }
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

        Register("import_midi",
            "Replace the project with an imported Standard MIDI File (.mid): notes quantized to sixteenths, drums split into kick/snare/clap/hat, " +
            "General MIDI programs mapped to slaw instruments, the song chunked into 4-bar scenes. Undoable in the studio.",
            Schema(new JsonObject { ["path"] = Property("string", "Path to a .mid file.") }, "path"),
            async (toolArguments, clientName, _) =>
            {
                var midiFilePath = ReadString(toolArguments["path"]) ?? throw new McpInvalidParamsException("path is required");
                var importResult = MidiFileImporter.Import(midiFilePath);
                return await host.InvokeAsync(() =>
                {
                    importResult.Project.AiProvenance = $"{clientName} via MCP (MIDI import)";
                    host.Document.ReplaceProject(importResult.Project, $"{clientName}: import MIDI", undoable: true);
                    host.OnAgentEdit(clientName, $"imported {Path.GetFileName(midiFilePath)}");
                    return string.Join("\n", importResult.ImportLedger) + "\n\n" + PromptLibrary.DescribeProjectForPatching(host.Document.Project);
                });
            });

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

        Register("list_plugins",
            "List the VST3 plugins slaw can host: each with its kind (instrument/effect), bundle path, class id, and the folder it was found in. " +
            "Folders: Settings → Plugins, --plugin-folder, the VST3_PATH environment variable, and the system VST3 folders.",
            Schema(new JsonObject
            {
                ["filter"] = Property("string", "Optional case-insensitive substring of the name or vendor."),
                ["kind"] = EnumProperty("Optional: only instruments or only effects.", "instrument", "effect"),
                ["rescan"] = Property("boolean", "Rescan the plugin folders first."),
            }),
            (toolArguments, _, _) => Task.Run(() =>
            {
                var filterText = ReadString(toolArguments["filter"]) ?? string.Empty;
                var requestedKind = ReadString(toolArguments["kind"]);
                var scannedPlugins = toolArguments["rescan"] is JsonValue rescanValue && rescanValue.TryGetValue<bool>(out var rescanRequested) && rescanRequested
                    ? Vst3PluginCatalog.Rescan()
                    : Vst3PluginCatalog.Plugins();
                var matchingPlugins = scannedPlugins
                    .Where(plugin => requestedKind is null || plugin.KindLabel == requestedKind)
                    .Where(plugin => plugin.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase) || plugin.Vendor.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var pluginListing = new StringBuilder();
                pluginListing.Append(CultureInfo.InvariantCulture, $"scanned folders: {string.Join(", ", Vst3PluginCatalog.AllFolders())}\n");
                if (matchingPlugins.Count == 0)
                {
                    pluginListing.Append("no VST3 plugins found — the user can add plugin folders in slaw's Settings → Plugins");
                }
                foreach (var plugin in matchingPlugins)
                {
                    pluginListing.Append(CultureInfo.InvariantCulture,
                        $"{plugin.KindLabel} \"{plugin.Name}\" by {(plugin.Vendor.Length > 0 ? plugin.Vendor : "unknown")} · path \"{plugin.BundlePath}\" · classId {plugin.ClassId} · folder \"{plugin.SourceFolder}\"\n");
                }
                foreach (var scanProblem in Vst3PluginCatalog.LastScanProblems)
                {
                    pluginListing.Append(CultureInfo.InvariantCulture, $"could not scan: {scanProblem}\n");
                }
                return pluginListing.ToString().TrimEnd();
            }));

        Register("describe_plugin",
            "List a VST3 plugin's parameters: key (p<id>), title, unit, default and current value (normalized 0..1). " +
            "Name a track (its vst3 instrument, or effect index) or pass a plugin directly.",
            Schema(new JsonObject
            {
                ["track"] = AnyProperty(TrackReferenceDescription),
                ["effect"] = Property("integer", "0-based index in the track's device chain; omit for the track's instrument."),
                ["plugin"] = AnyProperty("Alternatively {\"path\", \"classId\"} or a plugin name from list_plugins."),
                ["filter"] = Property("string", "Optional case-insensitive substring of the parameter title."),
            }),
            async (toolArguments, _, _) =>
            {
                var (pluginReference, currentValues) = await host.InvokeAsync(() =>
                {
                    if (toolArguments["track"] is null)
                    {
                        return (PluginDeviceOperations.ParseReference(toolArguments["plugin"])
                                ?? throw new McpInvalidParamsException("pass a track, or a plugin from list_plugins"),
                            new Dictionary<string, double>());
                    }
                    var (_, targetTrack) = ResolveTrack(toolArguments["track"]);
                    if (ReadDouble(toolArguments["effect"]) is { } effectNumber)
                    {
                        var effectIndex = (int)effectNumber;
                        if (effectIndex < 0 || effectIndex >= targetTrack.Effects.Count || targetTrack.Effects[effectIndex].Plugin is not { } effectPlugin)
                        {
                            throw new McpInvalidParamsException($"{targetTrack.Name} has no vst3 effect at index {effectIndex}");
                        }
                        return (effectPlugin, new Dictionary<string, double>(targetTrack.Effects[effectIndex].Parameters));
                    }
                    return (targetTrack.Plugin ?? throw new McpInvalidParamsException($"{targetTrack.Name} does not play a VST3 instrument"),
                        new Dictionary<string, double>(targetTrack.InstrumentParameters));
                });
                var pluginParameters = await Task.Run(() => Vst3ParameterDirectory.Get(pluginReference, PluginProbeTimeout));
                if (pluginParameters.Count == 0)
                {
                    return Vst3ParameterDirectory.ProbeProblem(pluginReference) is { } probeProblem
                        ? $"{pluginReference.Name} could not be loaded: {probeProblem}"
                        : $"{pluginReference.Name} exposes no parameters";
                }
                var titleFilter = ReadString(toolArguments["filter"]) ?? string.Empty;
                var listedParameters = pluginParameters
                    .Where(parameter => parameter.IsUserFacing && parameter.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var parameterListing = new StringBuilder(FormattableString.Invariant($"{pluginReference.Name}: {listedParameters.Count} parameter(s)\n"));
                foreach (var parameter in listedParameters)
                {
                    var currentValue = currentValues.TryGetValue(parameter.Key, out var storedValue) ? storedValue : parameter.DefaultValue;
                    parameterListing.Append(CultureInfo.InvariantCulture,
                        $"{parameter.Key} \"{parameter.Title}\"{(parameter.Units.Length > 0 ? $" [{parameter.Units}]" : string.Empty)}" +
                        $"{(parameter.StepCount > 0 ? $" ({parameter.StepCount + 1} steps)" : string.Empty)} default {parameter.DefaultValue:0.###} current {currentValue:0.###}\n");
                }
                return parameterListing.ToString().TrimEnd();
            });

        Register("ask_copilot",
            "Ask slaw's own built-in AI co-pilot (whatever model the user configured) for its opinion of the project. " +
            "Yes, this is an AI asking another AI about music. It is exactly as useful as it sounds.",
            Schema(new JsonObject { ["question"] = Property("string", "Optional question; default is a vibe check.") }),
            async (toolArguments, _, cancellationToken) =>
            {
                var inferenceProvider = host.InferenceProvider
                    ?? throw new McpInvalidParamsException("slaw's AI features are switched off (or misconfigured) in Settings");
                var (projectSnapshot, recentConversation) = await host.InvokeAsync(() => (host.Document.Project.DeepClone(), host.RecentConversation));
                var question = ReadString(toolArguments["question"]);
                if (question is null)
                {
                    return await ProductionWorkflows.VibeCheckAsync(inferenceProvider, projectSnapshot, Array.Empty<string>(), cancellationToken, recentConversation);
                }
                var copilotAnswer = await inferenceProvider.StreamCompletionAsync(
                    $"You are slaw's AI co-pilot. Another AI agent is producing this song and asks: \"{question}\"\n" +
                    $"{PromptLibrary.ConversationMemory(recentConversation)}\n" +
                    $"{PromptLibrary.DescribeArrangement(projectSnapshot)}\n\nAnswer in at most five sentences.",
                    Array.Empty<string>(), cancellationToken);
                return $"{inferenceProvider.ModelIdentifier} says: {copilotAnswer.Trim()}";
            });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>How long a tool waits for a plugin to load before answering without its parameter titles.</summary>
    private static readonly TimeSpan PluginProbeTimeout = TimeSpan.FromSeconds(20);

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
