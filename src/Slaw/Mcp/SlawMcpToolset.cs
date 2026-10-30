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
        "slaw is a step-sequencing DAW. Music lives in two places, like Ableton Live.\n" +
        "- SESSION: a grid of loops, one clip slot per track per scene. Tools: set_clip, remove_scene, apply_patch tracks[].clips. Scenes are NOT the song order.\n" +
        "- ARRANGEMENT: the song. Clips placed on tracks along one timeline. render bounces it; Play in the studio plays it. " +
        "Tools: arrangement_place_clip, arrangement_edit_clip, arrangement_remove_clips, or apply_patch \"timeline\" for many clips at once. " +
        "arrangement_place_audio puts a WAV (a vocal, a stem) on the arrangement at its original speed: set the tempo to the recording's tempo first.\n" +
        "UNITS: positions and lengths are sixteenth-note STEPS, 0-based: step 0 is the start of bar 1, 16 steps per bar, bar N starts at step (N-1)*16. " +
        "Arguments named start/length/step take steps; arguments named bar/bars take 1-based bars / bar counts. Listings print both, e.g. " +
        "\"bars 9.1–17.1 (start 128, length 128 steps = 8 bars, ends before step 256); pattern 64 steps ×2\". Ends are exclusive.\n" +
        "ARRANGEMENT CLIP = track + start + length + pattern. The pattern loops to fill the length (a 16-step beat with length 128 plays 8 bars). " +
        "Where clips overlap on one track, the later-starting clip plays.\n" +
        "PATTERN: space-separated steps. \"x\" drum hit, \"C4\" note, \"A3+C4+E4\" chord, \".\" rest, \"_\" holds the previous step's note one more step, " +
        "\"C4:8\" holds C4 for 8 steps by itself (follow it with \".\" or with other notes, which sound on top), \"C4@0.5\" / \"x@0.5\" velocity 0..1 (default 1). " +
        "Velocity @ and length : bind to the ONE note they follow: \"A3+C4+E4@0.7\" softens only E4; a soft chord is \"A3@0.7+C4@0.7+E4@0.7\" " +
        "(and \"C4:8@0.6+E4\" is a soft held root under a full-strength E4). " +
        "RATCHET \"*N\" (N 2..8; larger counts play as 8) is the opposite: it applies to the WHOLE step, written once at its very end: the step is struck N times, evenly spaced inside its one " +
        "sixteenth, and the pattern keeps its length (\"x*2\" two 32nd hits, \"x*4\" a snare roll, \"x@0.5*3\" a soft triplet roll, \"A3+C4*2\" the chord twice). Each strike keeps its note's @velocity; " +
        "every strike but the last stops at the next one, and the last is held as the note would be (\"_\" or :length). A bare \"*\" is still a plain hit.\n" +
        "ENVELOPES (set_automation, or \"automation\" in a clip): [step, value] points joined linearly. On an ARRANGEMENT clip the steps count from the clip's " +
        "start across its whole length (0..length) and do not repeat with the pattern; on a SESSION clip they span the pattern (0..pattern length) and loop with it. " +
        "Values are absolute: a gain envelope (0..1.5) replaces the track's gain while the clip plays, it does not multiply it. " +
        "Outside an envelope (before the clip, after it ends, or in clips without that envelope) the knob's own value applies again.\n" +
        "SIDECHAIN AND MASTER: an effect's \"sidechain\" names the track whose audio keys it, e.g. {\"type\": \"duck\", \"sidechain\": \"Kick\"} on a pad " +
        "turns the pad down on every kick (VST3 effects with a sidechain input get the audio there; the source is taken after its effects and before its fader, " +
        "so a muted track still keys; no track may key itself or close a loop). The MASTER chain (apply_patch master.effects, or add_effect with track \"master\") " +
        "processes the sum of all tracks before the master's drive/lowpass/delay/gain; master effects may use a sidechain too.\n" +
        "START FROM SCRATCH: new_project (or apply_patch with \"reset\": true) clears every track, clip and scene in one undoable step. " +
        "open_project loads an existing .slaw file instead (save_project first to keep the current one).\n" +
        "WORKFLOW: get_catalog → add_track / apply_patch tracks (instruments, effects) → place arrangement clips → set_automation → " +
        "render with levels:true (writes a WAV and reports each track's level, per bar range, and tracks far quieter than the rest) → get_project to check positions. " +
        "Removing a track (apply_patch remove: true) also removes its session and arrangement clips.\n" +
        "EXAMPLE apply_patch, an 8-bar verse then an 8-bar chorus with a filter opening across the chorus:\n" +
        "{\"bpm\": 100, \"tracks\": [{\"id\": \"new\", \"name\": \"Kick\", \"instrument\": \"kick\"},\n" +
        "  {\"id\": \"new\", \"name\": \"Bass\", \"instrument\": \"saw\", \"effects\": [{\"type\": \"lowpass\", \"parameters\": {\"cutoff\": 400}}]}],\n" +
        " \"timeline\": [{\"id\": \"new\", \"track\": \"Kick\", \"start\": 0, \"length\": 256, \"pattern\": \"x . . . x . . . x . . . x . . .\"},\n" +
        "  {\"id\": \"new\", \"track\": \"Bass\", \"start\": 0, \"length\": 128, \"pattern\": \"A1:4 . . . A1:4 . . . C2:4 . . . G1:4 . . .\"},\n" +
        "  {\"id\": \"new\", \"track\": \"Bass\", \"start\": 128, \"length\": 128, \"pattern\": \"F1:8 . . . . . . . G1:8 . . . . . . .\",\n" +
        "   \"automation\": {\"fx.0.cutoff\": [[0, 300], [128, 4000]]}}]}\n" +
        "Results say plainly what changed (with the ids of new clips), then list warnings for anything ignored or adjusted. " +
        "Tracks marked LOCKED are protected from AI edits and must not be changed.\n" +
        SoundFontPrimer;

    /// <summary>The SoundFont paragraph, shared by the instructions and get_catalog.</summary>
    private const string SoundFontPrimer =
        "SoundFonts: instrument \"soundfont\" plays one preset of a .sf2 file, chosen by MIDI bank and program (bank 128 is the drum kit, where every MIDI note is a different drum). " +
        "describe_soundfont lists a file's presets; add_track {\"instrument\": \"soundfont\", \"soundfont\": {\"path\", \"bank\", \"program\"}} or apply_patch tracks[].soundfont loads one. " +
        "import_midi voices every channel with the .sf2 next to the .mid (or the one you pass) and imports pitch bends as pitch envelopes.";

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

    /// <summary>"Stakeholder Ducking ← Kick" for a keyed effect, the device name otherwise.</summary>
    private string DescribeChainDevice(EffectDevice effectDevice) =>
        SidechainRouting.DescribeSource(host.Document.Project, effectDevice) is { } sourceName
            ? $"{PluginDeviceOperations.DeviceName(effectDevice)} (sidechain ← {sourceName})"
            : PluginDeviceOperations.DeviceName(effectDevice);

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
            "Describe the open project: tempo, session scenes, every track (id, instrument, effects, mixer, session clips) and the arrangement " +
            "(every clip with its id, track, bars, start/length in steps, pattern loops and envelopes). Use format \"json\" for the complete project file as pure JSON; " +
            "its arrangement clips (\"timeline\") use the field names trackId, startStep, lengthSteps and clip {name, pattern, automation}, which apply_patch \"timeline\" " +
            "also accepts (as aliases of track, start, length, name/pattern/automation), so JSON entries can be pasted back into a patch.",
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
                    // JSON stays parseable: the header only goes on the listing.
                    return requestedFormat == "json"
                        ? ProjectSerializer.Serialize(openProject)
                        : hostHeader + PromptLibrary.DescribeProjectForPatching(openProject);
                });
            });

        Register("get_catalog",
            "List every instrument and audio effect with its parameter keys and ranges, plus the pattern step vocabulary.",
            Schema(new JsonObject()),
            (_, _, _) => Task.FromResult(PromptLibrary.DescribeDeviceCatalog() +
                "\nPattern steps: \"x\" drum hit, \"C4\" note, \"A3+C4+E4\" chord, \".\" silence, \"_\" holds the previous step's note one more step, " +
                "\"C4:8\" holds C4 for 8 steps by itself (the next steps may be \".\" or other notes, which sound on top; \"_\" only extends notes written without :length). " +
                "Append \"@<velocity>\" (0..1, default 1) to a note or hit for dynamics: \"x@0.4\" ghost hit, \"C4:8@0.6+E4\" soft root under a full third. " +
                "Velocity and :length bind to the single note they follow, never to the whole chord: \"A3+C4+E4@0.7\" softens only E4; write \"A3@0.7+C4@0.7+E4@0.7\" for a soft chord. " +
                "Ratchet: \"*N\" (N 2..8, larger plays as 8) at the very end of a step strikes the WHOLE step (every chord note) N times, evenly spaced inside that one step, adding no steps: " +
                "\"x*2\" two 32nd-note hits, \"x*4\" a roll, \"x@0.5*3\" a soft triplet, \"C2*2 _ _ _\" two strikes then held, \"A3@0.6+C4@0.6*2\" a soft chord twice. " +
                "Strikes keep each note's velocity; all but the last end at the next strike, the last lasts as long as the note would without *N. A bare \"*\" is a plain hit. " +
                "Clips are usually 16 steps (one bar of sixteenths)." +
                "\nClip envelopes (set_automation, or \"automation\" in apply_patch) move a parameter while a clip plays, as [step, value] breakpoints joined linearly. " +
                "Outside the envelope's clip (and in clips without that envelope) the knob's own value applies. " +
                "Arrangement clips: steps 0..clip length, counted from the clip's start, played once across the clip. Session clips: steps 0..pattern length, looping with the pattern. " +
                "Targets: gain (the track's volume, absolute 0..1.5: it REPLACES the track's gain while the clip plays, it does not multiply it), pan (absolute -1..1, replaces the track's pan), " +
                "pitch (semitones -24..24; oscillators bend in place, samples/plucks/drums bend tape-style), " +
                "fx.<effectIndex>.<param> (an effect parameter, continuous), inst.<param> (an instrument parameter, sampled as each note starts). Values use the catalog's units; plugin instruments follow inst.<param> continuously." +
                "\nVST3 plugins: instrument \"vst3\" and effect type \"vst3\" host the user's installed plugins (see list_plugins). " +
                "Pass plugin {path, classId} (or the plugin's name) to add_track / add_effect / apply_patch. Their parameters are keys \"p<id>\" " +
                "with normalized values 0..1; describe_plugin lists them with titles." +
                "\nArrangement: the song, as clips placed at any step on any track (0-based steps, 16 per bar; bar N starts at step (N-1)*16), independent of scenes. " +
                "Place clips with arrangement_place_clip or apply_patch \"timeline\"; render bounces it; transport play_arrangement plays it from a 1-based bar in the live studio." +
                "\nStereo: tracks are stereo end to end. Built-in voices start mono; widen (width, haas ms), reverb width and delay pingpong spread them, " +
                "VST3 plugins process left and right, and pan is a constant-power balance." +
                "\n" + BuiltInInstrumentLevels.Value +
                "\nVST3 instruments vary widely in output level (often 10–20 dB apart); after placing clips, render with levels:true and adjust gains." +
                "\n" + SoundFontPrimer));

        Register("apply_patch",
            "Apply a patch describing only what should change; every key is optional. Best for many edits at once. " +
            "Keys: reset (true = start from an empty project first: no tracks, no clips, one empty scene, default master; then the rest of the patch applies), " +
            "bpm, name, scenes [names] (renames scenes in order; never deletes), removeScenes [0-based scene indices], " +
            "tracks [{id (existing id, exact name or index, or \"new\" to add), name, instrument, plugin (vst3 instrument: name or {path, classId}), sample, gain, pan, muted, solo, " +
            "instrumentParameters {key: number}, effects [{type, enabled, parameters, plugin, sidechain (track id or name that keys it; null clears)}] (replaces the whole chain), " +
            "clips {\"<sceneIndex>\": pattern or null} (SESSION clips), automation {\"<sceneIndex>\": {\"<target>\": [[step, value], ...] or null}}, " +
            "remove: true (deletes the track together with its session and arrangement clips)}], " +
            "timeline [{id (existing arrangement clip id, or \"new\"), track (id, name or index; may name a track added earlier in this patch), " +
            "start (0-based step, 16 per bar), length (steps; the pattern loops to fill it), pattern, name, automation {\"<target>\": [[step, value], ...]} " +
            "(steps 0..length from the clip's start), remove: true}] (ARRANGEMENT clips; get_project JSON names trackId/startStep/lengthSteps/clip{...} work too), " +
            "arrangement [{scene, repeats}] (legacy shortcut: lays those session scenes end to end and REPLACES the whole arrangement), " +
            "master {gain, drive, lowpassHz, delaySteps, delayFeedback, delayMix, effects [same shape as track effects] (replaces the master chain; it processes the sum of all tracks)}. " +
            "Pattern velocities and lengths bind per note (\"A3@0.7+C4@0.7+E4@0.7\" for a soft chord); a ratchet \"*N\" at the end of a step repeats the whole step N times inside it (\"x*2\"). " +
            "The result lists what changed (with new clip ids and their envelopes) and warnings for anything ignored or adjusted. Undoable in the studio.",
            Schema(new JsonObject { ["patch"] = PatchSchema() }, "patch"),
            async (toolArguments, clientName, _) =>
            {
                // Agents often send the patch itself as the arguments; any recognised patch key means that is what they did.
                var projectPatch = toolArguments["patch"] as JsonObject
                    ?? (toolArguments["patch"] is JsonValue patchText && patchText.TryGetValue<string>(out var serializedPatch)
                        ? ProjectPatchApplier.ParsePatch(serializedPatch)
                        : toolArguments["patch"] is null && toolArguments.Any(argument => ProjectPatchApplier.KnownPatchKeys.Contains(argument.Key))
                            ? toolArguments
                            : throw new McpInvalidParamsException(
                                "apply_patch takes {\"patch\": {...}}: wrap the patch object (e.g. {\"patch\": {\"bpm\": 120}}). No patch keys were found in the arguments."));
                return await host.InvokeAsync(() =>
                {
                    var previousProject = host.Document.Project;
                    var patchOutcome = ProjectPatchApplier.Apply(previousProject, projectPatch);
                    var lockEnforcementCount = ProductionWorkflows.EnforceAiLocks(previousProject, patchOutcome.Project) + patchOutcome.LockedTrackEditsIgnored;
                    var remediationLedger = NewRemediations(previousProject, ProjectAlignmentRemediator.Remediate(patchOutcome.Project));
                    patchOutcome.Project.AiProvenance = $"{clientName} via MCP";
                    var changeSummary = ProjectChangeSummarizer.Summarize(previousProject, patchOutcome.Project);
                    host.Document.ReplaceProject(patchOutcome.Project, $"{clientName}: patch", undoable: true);
                    var editDescription = patchOutcome.ModelSummary ?? string.Join("; ", changeSummary.Take(3));
                    host.OnAgentEdit(clientName, editDescription);
                    return DescribeOutcome(changeSummary, remediationLedger, lockEnforcementCount, patchOutcome.Warnings);
                });
            });

        Register("set_clip",
            "SESSION only: write (or clear) the clip of one track in one scene of the session grid. scene may equal the scene count to append a new scene. " +
            "This does not touch the arrangement (the song): place song clips with arrangement_place_clip.",
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
                // Lint what the agent wrote, before the checker tidies a misplaced "*N" into its canonical place.
                var writtenWarnings = stepList is null ? new List<string>() : PatternLint.ChordSuffixWarnings(stepList, string.Empty);
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
                    : AppendWarnings($"{targetTrack.Name} · scene {sceneIndex} ({document.Project.Scenes[sceneIndex]}): {string.Join(' ', writtenClip.Pattern)}",
                        writtenWarnings);
            }));

        Register("set_automation",
            "Write (or clear) a clip envelope: a parameter that moves while the clip plays. Address an ARRANGEMENT clip by `clip` (its id from get_project " +
            "or arrangement_place_clip) or a SESSION clip by `track` + `scene`. Targets: gain, pan, pitch, fx.<effectIndex>.<param>, inst.<param> (see get_catalog). " +
            "Values are absolute in the target's units: a gain envelope (0..1.5) replaces the track's gain while the clip plays, it does not multiply it. " +
            "Points are [step, value] pairs joined linearly; values hold before the first and after the last point. " +
            "Arrangement clip: steps 0..clip length from the clip's start, once across the clip. Session clip: steps 0..pattern length, looping with the pattern. " +
            "E.g. a filter opening over an 8-bar arrangement clip: target \"fx.0.cutoff\", points [[0,300],[128,4000]].",
            Schema(new JsonObject
            {
                ["clip"] = Property("string", "Arrangement clip id. Use this OR track + scene."),
                ["track"] = AnyProperty(TrackReferenceDescription + " For session clips (with scene)."),
                ["scene"] = Property("integer", "0-based session scene index (with track)."),
                ["target"] = Property("string", "Target key, e.g. \"fx.0.cutoff\", \"pitch\", \"gain\", \"inst.attack\"."),
                ["points"] = AnyProperty("Array of [step, value] pairs (or {step, value} objects); null or [] removes the envelope."),
            }, "target", "points"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var (targetTrack, automatedClip, envelopeLength, clipLabel) = ResolveAutomatedClip(toolArguments);
                RejectLocked(targetTrack);
                var targetKey = ReadString(toolArguments["target"]) ?? throw new McpInvalidParamsException("target is required");
                if (ClipAutomation.ResolveTarget(targetTrack, targetKey) is not { } automationTarget)
                {
                    var validTargets = string.Join(", ", ClipAutomation.TargetsFor(targetTrack).Select(target => target.Key));
                    throw new McpInvalidParamsException($"{targetTrack.Name} has no automation target \"{targetKey}\". valid: {validTargets}");
                }
                var envelopeWarnings = new List<string>();
                document.BeginEdit($"{clientName}: envelope");
                ProjectPatchApplier.ApplyClipAutomation(targetTrack, automatedClip, new JsonObject { [targetKey] = toolArguments["points"]?.DeepClone() }, envelopeLength, envelopeWarnings);
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} automated {automationTarget.Label} on {targetTrack.Name}", EditScope.Structure);
                host.OnAgentEdit(clientName, $"automated {automationTarget.Label} on {targetTrack.Name}");
                var envelopeResult = automatedClip.Automation?.GetValueOrDefault(targetKey) is { } writtenPoints
                    ? $"set envelope on {clipLabel} (spans steps 0..{envelopeLength}): {ClipAutomation.Describe(targetKey, writtenPoints)} ({automationTarget.Label})"
                    : $"removed the {automationTarget.Label} envelope from {clipLabel}";
                return AppendWarnings(envelopeResult, envelopeWarnings);
            }));

        Register("remove_scene",
            "Delete one SESSION scene and every track's clip slot in it; later scenes move up one index. Arrangement clips are copies and keep playing. " +
            "The last remaining scene cannot be deleted. To delete several, call it from the highest index down, or use apply_patch removeScenes.",
            Schema(new JsonObject { ["scene"] = Property("integer", "0-based scene index.") }, "scene"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var sceneIndex = (int)(ReadDouble(toolArguments["scene"]) ?? throw new McpInvalidParamsException("scene is required"));
                if (document.Project.Scenes.Count <= 1)
                {
                    throw new McpInvalidParamsException("the project has only one scene left; it cannot be deleted");
                }
                if (sceneIndex < 0 || sceneIndex >= document.Project.Scenes.Count)
                {
                    throw new McpInvalidParamsException($"there is no scene {sceneIndex}; scenes are 0..{document.Project.Scenes.Count - 1}");
                }
                if (document.Project.Tracks.FirstOrDefault(track => track.AiLocked && track.ClipAt(sceneIndex) is not null) is { } lockedTrack)
                {
                    throw new McpInvalidParamsException($"scene {sceneIndex} holds a clip of the LOCKED track \"{lockedTrack.Name}\"; it cannot be deleted");
                }
                var removedSceneName = document.Project.Scenes[sceneIndex];
                document.BeginEdit($"{clientName}: delete scene");
                SceneOperations.RemoveScene(document.Project, sceneIndex);
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} deleted the scene {removedSceneName}", EditScope.Structure);
                host.OnAgentEdit(clientName, $"deleted the scene {removedSceneName}");
                return $"removed scene {sceneIndex} (\"{removedSceneName}\"). Session scenes are now: " +
                       string.Join(", ", document.Project.Scenes.Select((sceneName, remainingIndex) => $"{remainingIndex}=\"{sceneName}\""));
            }));

        Register("open_project",
            "Open a .slaw project file as the current project, like File → Open (in the live studio it appears in the window; " +
            "the headless server then autosaves to that file). Refuses while the open project has unsaved changes unless discardUnsaved is true. " +
            "Not undoable: save_project first if the current project should be kept.",
            Schema(new JsonObject
            {
                ["path"] = Property("string", "Path to a .slaw file."),
                ["discardUnsaved"] = Property("boolean", "Open even though the current project has unsaved changes (they are lost). Default false."),
            }, "path"),
            async (toolArguments, clientName, _) =>
            {
                var projectPath = Path.GetFullPath(ReadString(toolArguments["path"]) ?? throw new McpInvalidParamsException("path is required: a .slaw file"));
                SlawProject openedProject;
                try
                {
                    openedProject = await Task.Run(() => ProjectSerializer.Load(projectPath));
                }
                catch (Exception openRemediationOpportunity) when (openRemediationOpportunity is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                {
                    throw new McpInvalidParamsException($"cannot open {projectPath}: {openRemediationOpportunity.Message}");
                }
                return await host.InvokeAsync(() =>
                {
                    var document = host.Document;
                    if (document.UnsavedChanges && !(toolArguments["discardUnsaved"] is JsonValue discardValue && discardValue.TryGetValue<bool>(out var discardRequested) && discardRequested))
                    {
                        throw new McpInvalidParamsException(
                            $"the open project \"{document.Project.Name}\" has unsaved changes: save_project first, or pass discardUnsaved: true to drop them");
                    }
                    host.OpenProject(openedProject, projectPath);
                    host.OnAgentEdit(clientName, $"opened {Path.GetFileName(projectPath)}");
                    return $"opened {projectPath}\n\n" + PromptLibrary.DescribeProjectForPatching(host.Document.Project);
                });
            });

        Register("new_project",
            "Start from scratch: replace the open project with an empty one (no tracks, no session or arrangement clips, one empty scene, default master bus), " +
            "optionally named and at a tempo. One undoable step in the studio. The project keeps its file path, so the next save overwrites that file " +
            "(the headless `slaw mcp` server saves after every edit): call save_project with a new path first if the current song should be kept.",
            Schema(new JsonObject
            {
                ["name"] = Property("string", "Project name (default \"untitled slop\")."),
                ["bpm"] = Property("number", "Tempo, 40..300 (default 120)."),
            }),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                if (document.Project.Tracks.FirstOrDefault(track => track.AiLocked) is { } lockedTrack)
                {
                    throw new McpInvalidParamsException($"\"{lockedTrack.Name}\" is LOCKED: the user protected it from AI edits, so the project cannot be cleared");
                }
                var previousProject = document.Project;
                var blankProject = ProjectSerializer.CreateBlankProject(ReadString(toolArguments["name"]) ?? "untitled slop", ReadDouble(toolArguments["bpm"]) ?? 120);
                blankProject.AiProvenance = $"{clientName} via MCP";
                document.ReplaceProject(blankProject, $"{clientName}: new project", undoable: true);
                host.OnAgentEdit(clientName, $"started the empty project {blankProject.Name}");
                return string.Create(CultureInfo.InvariantCulture,
                    $"new empty project \"{blankProject.Name}\" at {blankProject.Bpm:0} BPM: removed {previousProject.Tracks.Count} track(s), " +
                    $"{previousProject.Timeline.Count} arrangement clip(s) and {previousProject.Scenes.Count} scene(s); now 0 tracks and one empty scene 0=\"Scene 1\". ") +
                    (document.FilePath is { } filePath ? $"The file is still {filePath}; the next save overwrites it." : "The project has no file yet; save_project needs a path.");
            }));

        Register("arrangement_place_clip",
            "Place a clip in the ARRANGEMENT (the song): on a track, at a start, for a length, playing a pattern that loops to fill the length. " +
            "Give the start as `start` (0-based step) or `bar` (1-based bar); the length as `length` (steps) or `bars` (default: one pattern). " +
            "E.g. an 8-bar beat from bar 9: {\"track\": \"Kick\", \"bar\": 9, \"bars\": 8, \"pattern\": \"x . . . x . . . x . . . x . . .\"}. " +
            "Returns the new clip's id and where it sits. Optional automation {\"<target>\": [[step, value], ...]} spans the whole clip (steps 0..length).",
            Schema(new JsonObject
            {
                ["track"] = AnyProperty(TrackReferenceDescription),
                ["start"] = Property("integer", "0-based start step (16 per bar). Use this or bar."),
                ["bar"] = Property("number", "1-based bar to start at (bar 1 = step 0). Use this or start."),
                ["length"] = Property("integer", "Length in steps. Use this or bars; default is the pattern's length."),
                ["bars"] = Property("number", "Length in bars (16 steps each). Use this or length."),
                ["pattern"] = AnyProperty("Space-separated steps (see get_catalog), or an array of steps. Loops to fill the length."),
                ["name"] = Property("string", "Optional clip name."),
                ["automation"] = Property("object", "Optional envelopes: {\"<target>\": [[step, value], ...]}, steps 0..length from the clip's start."),
            }, "track", "pattern"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var (_, targetTrack) = ResolveTrack(toolArguments["track"]);
                RejectLocked(targetTrack);
                var placedPattern = ReadPattern(toolArguments["pattern"]) ?? throw new McpInvalidParamsException("pattern is required, e.g. \"x . . . x . . .\"");
                var startStep = ReadStartStep(toolArguments) ?? 0;
                var lengthSteps = ReadLengthSteps(toolArguments) ?? placedPattern.Count;
                ValidatePlacement(startStep, lengthSteps);
                var placedClip = new TimelineClip
                {
                    TrackId = targetTrack.Id,
                    StartStep = startStep,
                    LengthSteps = lengthSteps,
                    Clip = new SlawClip { Name = ReadString(toolArguments["name"]) ?? targetTrack.Name, Pattern = placedPattern },
                };
                var envelopeWarnings = PatternLint.ChordSuffixWarnings(placedPattern, string.Empty);
                if (toolArguments["automation"] is JsonObject envelopes)
                {
                    ProjectPatchApplier.ApplyClipAutomation(targetTrack, placedClip.Clip, envelopes.DeepClone(), placedClip.LengthSteps, envelopeWarnings);
                }
                document.BeginEdit($"{clientName}: place clip");
                document.Project.Timeline.Add(placedClip);
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} placed a clip on {targetTrack.Name}", EditScope.Structure);
                host.OnAgentEdit(clientName, $"placed a clip on {targetTrack.Name} at bar {FreeTimeline.FormatPosition(startStep)}");
                return AppendWarnings($"placed clip id \"{placedClip.Id}\" on \"{targetTrack.Name}\": {FreeTimeline.DescribeSpan(placedClip)}" +
                    DescribeClipEnvelopes(placedClip.Clip), envelopeWarnings);
            }));

        Register("arrangement_place_audio",
            "Place a WAV file (a vocal, a stem, a loop, a whole recording) in the ARRANGEMENT, playing at its original pitch and speed from a start bar. " +
            "Long files are cut into consecutive 4-bar slices, one arrangement clip each, so any length plays through without gaps. " +
            "Set the project tempo (set_tempo or apply_patch bpm) BEFORE placing: the slices are cut at the current tempo and the audio itself is never time-stretched, " +
            "so a vocal recorded at 112 BPM lines up with the grid only when the project is at 112 BPM. " +
            "E.g. {\"path\": \"/music/vocals.wav\", \"bar\": 1, \"name\": \"Lead Vocal\"}. Without `track` a new sampler track is created.",
            Schema(new JsonObject
            {
                ["path"] = Property("string", "Path to a WAV file (see list_samples)."),
                ["track"] = AnyProperty("Optional existing track to play it on. " + TrackReferenceDescription + " If that track already plays other audio, a new track is made next to it."),
                ["start"] = Property("integer", "0-based start step (16 per bar). Use this or bar; default bar 1."),
                ["bar"] = Property("number", "1-based bar to start at (bar 1 = step 0)."),
                ["name"] = Property("string", "Name for a new track (default: the file name)."),
            }, "path"),
            async (toolArguments, clientName, _) =>
            {
                var audioPath = ReadString(toolArguments["path"]) ?? throw new McpInvalidParamsException("path is required: a WAV file, see list_samples");
                SampleData placedAudio;
                try
                {
                    placedAudio = await Task.Run(() => SampleLibrary.Load(Path.GetFullPath(audioPath)));
                }
                catch (Exception loadRemediationOpportunity) when (loadRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
                {
                    throw new McpInvalidParamsException($"cannot read {audioPath}: {loadRemediationOpportunity.Message}");
                }
                return await host.InvokeAsync(() =>
                {
                    var document = host.Document;
                    var project = document.Project;
                    var startStep = ReadStartStep(toolArguments) ?? 0;
                    ValidatePlacement(startStep, 1);
                    SlawTrack? requestedTrack = null;
                    if (toolArguments["track"] is not null)
                    {
                        requestedTrack = ResolveTrack(toolArguments["track"]).Track;
                        RejectLocked(requestedTrack);
                    }
                    var needsNewTrack = requestedTrack is null || AudioTakeAssembler.NeedsFreshTimelineTrack(project, requestedTrack);
                    if (needsNewTrack && project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
                    {
                        throw new McpInvalidParamsException($"the project already has the maximum of {ProjectAlignmentRemediator.MaximumTrackCount} tracks");
                    }
                    document.BeginEdit($"{clientName}: place audio");
                    var audioTrack = requestedTrack;
                    if (needsNewTrack)
                    {
                        audioTrack = AudioTakeAssembler.CreateTakeTrack(project, requestedTrack);
                        if (requestedTrack is null)
                        {
                            audioTrack.Name = ReadString(toolArguments["name"]) is { Length: > 0 } requestedName ? requestedName : Path.GetFileNameWithoutExtension(audioPath);
                        }
                        var insertionIndex = requestedTrack is null ? project.Tracks.Count : project.Tracks.IndexOf(requestedTrack) + 1;
                        project.Tracks.Insert(insertionIndex, audioTrack);
                    }
                    var placedClips = AudioTakeAssembler.ApplyTimelineTake(project, audioTrack!, Path.GetFullPath(audioPath), startStep, placedAudio.Samples.Length, placedAudio.SampleRate);
                    project.AiProvenance = $"{clientName} via MCP";
                    document.CommitEdit($"{clientName} placed {Path.GetFileName(audioPath)} on {audioTrack!.Name}", EditScope.Structure);
                    host.OnAgentEdit(clientName, $"placed {Path.GetFileName(audioPath)} on {audioTrack.Name}");
                    var audioSeconds = placedAudio.Samples.Length / (double)placedAudio.SampleRate;
                    var lastClip = placedClips[^1];
                    return FormattableString.Invariant(
                        $"placed \"{Path.GetFileName(audioPath)}\" ({audioSeconds:0.0} s) on {(needsNewTrack ? "new " : string.Empty)}track \"{audioTrack.Name}\" (id {audioTrack.Id}) as {placedClips.Count} arrangement clip(s) from bar {startStep / 16 + 1} (step {startStep}) to bar {lastClip.EndStep / 16 + 1}, step {lastClip.EndStep % 16} within it (absolute step {lastClip.EndStep}, exclusive), cut at {project.Bpm:0.##} BPM; clip ids: {string.Join(", ", placedClips.Select(clip => clip.Id))}.") +
                        (requestedTrack is not null && needsNewTrack ? $" \"{requestedTrack.Name}\" already played other audio, so this went on a new track next to it." : string.Empty) +
                        " Change the tempo afterwards and the slices drift: set the tempo first, then place audio.";
                });
            });

        Register("arrangement_edit_clip",
            "Change an ARRANGEMENT clip by id: move it (`start` step or `bar`), resize it (`length` steps or `bars`), replace its pattern, rename it, " +
            "or move it to another track. Only the given fields change; its envelopes stay (and still count from the clip's start).",
            Schema(new JsonObject
            {
                ["clip"] = Property("string", "Arrangement clip id (from get_project or arrangement_place_clip)."),
                ["start"] = Property("integer", "New 0-based start step."),
                ["bar"] = Property("number", "New 1-based start bar."),
                ["length"] = Property("integer", "New length in steps."),
                ["bars"] = Property("number", "New length in bars."),
                ["pattern"] = AnyProperty("New pattern (string or array of steps)."),
                ["name"] = Property("string", "New clip name."),
                ["track"] = AnyProperty("Move the clip to this track. " + TrackReferenceDescription),
            }, "clip"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var editedClip = ResolveArrangementClip(toolArguments["clip"]);
                var owningTrack = document.Project.Tracks.First(track => track.Id == editedClip.TrackId);
                RejectLocked(owningTrack);
                var destinationTrack = toolArguments["track"] is null ? owningTrack : ResolveTrack(toolArguments["track"]).Track;
                RejectLocked(destinationTrack);
                var revisedStart = ReadStartStep(toolArguments) ?? editedClip.StartStep;
                var revisedLength = ReadLengthSteps(toolArguments) ?? editedClip.LengthSteps;
                ValidatePlacement(revisedStart, revisedLength);
                var revisedPattern = toolArguments["pattern"] is null ? null : ReadPattern(toolArguments["pattern"]) ?? throw new McpInvalidParamsException("pattern must not be empty");
                document.BeginEdit($"{clientName}: edit clip");
                editedClip.StartStep = revisedStart;
                editedClip.LengthSteps = revisedLength;
                editedClip.TrackId = destinationTrack.Id;
                if (revisedPattern is not null)
                {
                    editedClip.Clip.Pattern = revisedPattern;
                }
                editedClip.Clip.Name = ReadString(toolArguments["name"]) ?? editedClip.Clip.Name;
                var envelopeWarnings = revisedPattern is null ? new List<string>() : PatternLint.ChordSuffixWarnings(revisedPattern, string.Empty);
                if (editedClip.Clip.Automation is { Count: > 0 } envelopes && envelopes.Values.SelectMany(points => points).Any(point => point.Step > revisedLength))
                {
                    envelopeWarnings.Add($"envelope points past the new length ({revisedLength} steps) are kept but never reached; the value holds at the clip's end");
                }
                if (destinationTrack != owningTrack && editedClip.Clip.Automation is { Count: > 0 } movedEnvelopes)
                {
                    var droppedTargets = movedEnvelopes.Keys.Where(targetKey => ClipAutomation.ResolveTarget(destinationTrack, targetKey) is null).ToList();
                    foreach (var droppedTarget in droppedTargets)
                    {
                        movedEnvelopes.Remove(droppedTarget);
                    }
                    if (droppedTargets.Count > 0)
                    {
                        envelopeWarnings.Add($"removed envelopes {string.Join(", ", droppedTargets)}: \"{destinationTrack.Name}\" has no such target");
                    }
                }
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} edited a clip on {destinationTrack.Name}", EditScope.Structure);
                host.OnAgentEdit(clientName, $"edited a clip on {destinationTrack.Name}");
                return AppendWarnings($"clip id \"{editedClip.Id}\" on \"{destinationTrack.Name}\" is now {FreeTimeline.DescribeSpan(editedClip)}" +
                    DescribeClipEnvelopes(editedClip.Clip), envelopeWarnings);
            }));

        Register("arrangement_remove_clips",
            "Remove ARRANGEMENT clips: by `clips` (array of ids) or `clip` (one id), or every clip on a `track` that starts within an optional bar range " +
            "(`fromBar` inclusive, `toBar` exclusive, 1-based). Session clips are not touched.",
            Schema(new JsonObject
            {
                ["clip"] = Property("string", "One arrangement clip id."),
                ["clips"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Several arrangement clip ids." },
                ["track"] = AnyProperty("Remove this track's arrangement clips (optionally only those starting in fromBar..toBar). " + TrackReferenceDescription),
                ["fromBar"] = Property("number", "1-based bar; with track, only clips starting at or after it."),
                ["toBar"] = Property("number", "1-based bar; with track, only clips starting before it."),
            }),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var timeline = document.Project.Timeline;
                List<TimelineClip> removedClips;
                if (toolArguments["clip"] is not null || toolArguments["clips"] is JsonArray)
                {
                    var requestedIds = (toolArguments["clips"] as JsonArray)?.Select(ReadString).OfType<string>().ToList() ?? new List<string>();
                    if (ReadString(toolArguments["clip"]) is { } singleId)
                    {
                        requestedIds.Add(singleId);
                    }
                    var unknownIds = requestedIds.Where(clipId => timeline.All(timelineClip => timelineClip.Id != clipId)).ToList();
                    if (unknownIds.Count > 0)
                    {
                        throw new McpInvalidParamsException($"no arrangement clip with id {string.Join(", ", unknownIds.Select(clipId => $"\"{clipId}\""))}; see get_project for ids");
                    }
                    removedClips = timeline.Where(timelineClip => requestedIds.Contains(timelineClip.Id)).ToList();
                }
                else if (toolArguments["track"] is not null)
                {
                    var (_, targetTrack) = ResolveTrack(toolArguments["track"]);
                    var fromStep = ReadDouble(toolArguments["fromBar"]) is { } fromBar ? (int)Math.Round((fromBar - 1) * FreeTimeline.StepsPerBar) : int.MinValue;
                    var toStep = ReadDouble(toolArguments["toBar"]) is { } toBar ? (int)Math.Round((toBar - 1) * FreeTimeline.StepsPerBar) : int.MaxValue;
                    removedClips = timeline.Where(timelineClip => timelineClip.TrackId == targetTrack.Id && timelineClip.StartStep >= fromStep && timelineClip.StartStep < toStep).ToList();
                }
                else
                {
                    throw new McpInvalidParamsException("pass clip, clips, or track (with optional fromBar/toBar)");
                }
                if (removedClips.Select(timelineClip => document.Project.Tracks.First(track => track.Id == timelineClip.TrackId)).FirstOrDefault(track => track.AiLocked) is { } lockedTrack)
                {
                    throw new McpInvalidParamsException($"\"{lockedTrack.Name}\" is LOCKED: the user protected it from AI edits");
                }
                if (removedClips.Count == 0)
                {
                    return "removed 0 arrangement clips: none matched";
                }
                var removalLines = removedClips.Select(timelineClip =>
                    $"- id \"{timelineClip.Id}\" on \"{document.Project.Tracks.First(track => track.Id == timelineClip.TrackId).Name}\" {FreeTimeline.DescribeSpan(timelineClip)}").ToList();
                document.BeginEdit($"{clientName}: remove clips");
                timeline.RemoveAll(removedClips.Contains);
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} removed {removedClips.Count} arrangement clip(s)", EditScope.Structure);
                host.OnAgentEdit(clientName, $"removed {removedClips.Count} arrangement clip(s)");
                return $"removed {removedClips.Count} arrangement clip(s):\n{string.Join("\n", removalLines)}";
            }));

        Register("add_track",
            "Add a track. instrument is one of the catalog instruments (or \"sampler\" with a sample path from list_samples, " +
            "\"vst3\" with a plugin from list_plugins, or \"soundfont\" with a soundfont {path, bank, program} from describe_soundfont). Returns the new track id.",
            Schema(new JsonObject
            {
                ["instrument"] = Property("string", "Instrument name, e.g. kick, saw, pluck, sampler, vst3."),
                ["name"] = Property("string", "Optional track name."),
                ["sample"] = Property("string", "WAV path for sampler tracks."),
                ["plugin"] = AnyProperty("VST3 instrument for vst3 tracks: {\"path\": bundle path, \"classId\": 32 hex chars} from list_plugins, or the plugin's name."),
                ["soundfont"] = AnyProperty("SoundFont preset for soundfont tracks: {\"path\": \".sf2 path\", \"bank\": 0, \"program\": 40} (see describe_soundfont), or just the path for its first preset."),
            }, "instrument"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                if (document.Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
                {
                    throw new McpInvalidParamsException($"the project already has {ProjectAlignmentRemediator.MaximumTrackCount} tracks, the most a project can hold");
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
                    return $"added track id \"{pluginTrack.Id}\" \"{pluginTrack.Name}\" (vst3 \"{instrumentPlugin.Name}\"). {NextStepsForNewTrack} See its knobs with describe_plugin.";
                }
                if (toolArguments["soundfont"] is not null || resolvedInstrument == "soundfont")
                {
                    SoundFontReference soundFontReference;
                    try
                    {
                        soundFontReference = SoundFontDeviceOperations.ParseReference(toolArguments["soundfont"]);
                    }
                    catch (InvalidDataException soundFontRemediationOpportunity)
                    {
                        throw new McpInvalidParamsException(soundFontRemediationOpportunity.Message);
                    }
                    var soundFontTrack = SoundFontDeviceOperations.CreateTrack(document.Project, soundFontReference, ReadString(toolArguments["name"]));
                    document.BeginEdit($"{clientName}: add track");
                    document.Project.Tracks.Add(soundFontTrack);
                    document.Project.AiProvenance = $"{clientName} via MCP";
                    document.CommitEdit($"{clientName} added the track {soundFontTrack.Name}", EditScope.Structure);
                    host.OnAgentEdit(clientName, $"added the track {soundFontTrack.Name} ({SoundFontDeviceOperations.Describe(soundFontReference)})");
                    return $"added track id \"{soundFontTrack.Id}\" \"{soundFontTrack.Name}\" (soundfont {SoundFontDeviceOperations.Describe(soundFontReference)}). {NextStepsForNewTrack}";
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
                return $"added track id \"{addedTrack.Id}\" \"{addedTrack.Name}\" ({addedTrack.Instrument}). {NextStepsForNewTrack}";
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
                return AppendWarnings(string.Join("\n", changeSummary), patchOutcome.Warnings);
            }));

        Register("add_effect",
            "Add an audio effect to the end of a track's device chain, or to the master chain with track \"master\" " +
            "(master effects process the sum of every track, before the master's drive/lowpass/delay/gain). See get_catalog for types and parameters. " +
            "sidechain names the track whose audio keys the effect: the \"duck\" effect (Stakeholder Ducking) turns its track down while that track plays, " +
            "and VST3 effects with a sidechain input receive it there. Example: {\"track\": \"Pad\", \"type\": \"duck\", \"sidechain\": \"Kick\", \"parameters\": {\"amount\": 0.8}}.",
            Schema(new JsonObject
            {
                ["track"] = AnyProperty(TrackReferenceDescription + " Or \"master\" for the master chain."),
                ["type"] = EnumProperty("Effect type (\"vst3\" for a plugin from list_plugins).", DeviceCatalog.EffectTypes.Append(DeviceTypes.Vst3).ToArray()),
                ["parameters"] = Property("object", "Optional parameter key → number (p<id> → 0..1 for vst3)."),
                ["plugin"] = AnyProperty("VST3 effect for type vst3: {\"path\", \"classId\"} from list_plugins, or the plugin's name."),
                ["sidechain"] = Property("string", "Optional: the track (id or name) that keys this effect. Only \"duck\" and VST3 effects with a sidechain input listen; a track cannot key itself or close a loop."),
            }, "track", "type"),
            async (toolArguments, clientName, _) => await host.InvokeAsync(() =>
            {
                var document = host.Document;
                var targetsMaster = string.Equals(ReadString(toolArguments["track"]), "master", StringComparison.OrdinalIgnoreCase);
                SlawTrack? targetTrack = null;
                if (!targetsMaster)
                {
                    targetTrack = ResolveTrack(toolArguments["track"]).Track;
                    RejectLocked(targetTrack);
                }
                var effectType = ProjectAlignmentRemediator.ResolveEffect(ReadString(toolArguments["type"]))
                    ?? throw new McpInvalidParamsException($"unknown effect; use one of {string.Join(", ", DeviceCatalog.EffectTypes)}");
                var addedEffect = new EffectDevice { Type = effectType };
                if (effectType == DeviceTypes.Vst3)
                {
                    addedEffect.Plugin = PluginDeviceOperations.ParseReference(toolArguments["plugin"])
                        ?? throw new McpInvalidParamsException("vst3 effects need a plugin: pass {path, classId} or a name from list_plugins");
                }
                if (ReadString(toolArguments["sidechain"]) is { Length: > 0 } sidechainReference)
                {
                    var sourceTrack = SidechainRouting.ResolveTrack(document.Project, sidechainReference)
                        ?? throw new McpInvalidParamsException($"sidechain \"{sidechainReference}\" matches no track; {ProjectPatchApplier.DescribeTrackReferences(document.Project)}");
                    if (!SidechainRouting.AcceptsSidechain(addedEffect))
                    {
                        throw new McpInvalidParamsException($"{DeviceCatalog.EffectDisplayName(effectType)} has no sidechain input; use type \"duck\" or a VST3 effect with one");
                    }
                    if (SidechainRouting.WouldCreateCycle(document.Project, targetTrack?.Id, sourceTrack.Id))
                    {
                        throw new McpInvalidParamsException(sourceTrack == targetTrack
                            ? "a track cannot key its own effect"
                            : $"{sourceTrack.Name} already listens to {targetTrack!.Name}, so {targetTrack.Name} cannot listen to {sourceTrack.Name} (a sidechain loop)");
                    }
                    addedEffect.SidechainSource = sourceTrack.Id;
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
                var chain = targetTrack?.Effects ?? document.Project.Master.Effects;
                var chainOwner = targetTrack?.Name ?? "master";
                if (chain.Count >= ProjectAlignmentRemediator.MaximumEffectsPerTrack)
                {
                    throw new McpInvalidParamsException($"{chainOwner} already has {ProjectAlignmentRemediator.MaximumEffectsPerTrack} effects (the limit per chain)");
                }
                document.BeginEdit($"{clientName}: add effect");
                chain.Add(addedEffect);
                ProjectAlignmentRemediator.Remediate(document.Project);
                document.Project.AiProvenance = $"{clientName} via MCP";
                document.CommitEdit($"{clientName} added {DeviceCatalog.EffectDisplayName(effectType)} to {chainOwner}", EditScope.Structure);
                host.OnAgentEdit(clientName, $"added {DeviceCatalog.EffectDisplayName(effectType)} to {chainOwner}");
                return $"{chainOwner} chain: {string.Join(" → ", chain.Select(DescribeChainDevice))}";
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
            "LIVE STUDIO ONLY (the headless server has no audio output; use render instead). Control playback: play_arrangement (the song, from a 1-based bar), " +
            "play_scene (launch a session scene; while the arrangement plays, the scene's clips take their tracks over from the next bar), " +
            "back_to_arrangement (return those tracks to the arrangement), or stop.",
            Schema(new JsonObject
            {
                ["action"] = EnumProperty("What to do. play_timeline is an alias of play_arrangement.", "play_arrangement", "play_scene", "back_to_arrangement", "stop", "play_timeline"),
                ["scene"] = Property("integer", "Scene index for play_scene."),
                ["bar"] = Property("integer", "1-based bar to start play_arrangement from (default 1 = the start)."),
            }, "action"),
            async (toolArguments, _, _) =>
            {
                if (!host.IsLive)
                {
                    throw new McpInvalidParamsException("transport only works in the live studio (slaw studio --mcp); this is the headless server, which has no audio output. Use render to bounce the arrangement to a WAV.");
                }
                var transportAction = ReadString(toolArguments["action"]) ?? "stop";
                var sceneIndex = (int)(ReadDouble(toolArguments["scene"]) ?? 0);
                var startBar = Math.Max(1, (int)(ReadDouble(toolArguments["bar"]) ?? 1)) - 1;
                return await host.InvokeAsync(() => host.ControlTransport(transportAction, sceneIndex, startBar));
            });

        Register("render",
            "Bounce the ARRANGEMENT (the song) to a 16-bit stereo WAV and report its musical length, the release tail, and the peak level. " +
            "A hearing-protection limiter caps peaks at -1 dBFS; the result says whether it engaged and by how much (a -1.0 dBFS peak is the ceiling, not headroom). " +
            "With levels:true it also renders every track soloed and reports each one's RMS and peak (slower; finds tracks that are too quiet or too loud).",
            Schema(new JsonObject
            {
                ["path"] = Property("string", "Output .wav path. Relative paths resolve next to the project file (or the server's working folder if it has none). Default: the project's name with .wav, next to it."),
                ["levels"] = Property("boolean", "Also report each track's level overall and per bar range, the mix per bar range, and tracks far quieter or louder than the rest (renders each track soloed; slower)."),
                ["source"] = EnumProperty("Optional; the arrangement is always what is rendered (timeline is an alias).", "arrangement", "timeline"),
            }),
            async (toolArguments, _, cancellationToken) =>
            {
                var (projectSnapshot, defaultOutputPath, projectFolder) = await host.InvokeAsync(() => (
                    host.Document.Project.DeepClone(),
                    host.Document.FilePath is { } projectPath
                        ? Path.ChangeExtension(projectPath, ".wav")
                        : Path.Combine(Path.GetTempPath(), $"slaw-mcp-{Environment.ProcessId}.wav"),
                    host.Document.FilePath is { } savedPath ? Path.GetDirectoryName(Path.GetFullPath(savedPath)) : null));
                // Relative paths land next to the project, whatever folder the server was started from.
                var requestedPath = ReadString(toolArguments["path"]);
                var outputPath = requestedPath is null
                    ? Path.GetFullPath(defaultOutputPath)
                    : Path.GetFullPath(Path.IsPathRooted(requestedPath) || projectFolder is null ? requestedPath : Path.Combine(projectFolder, requestedPath));
                if (projectSnapshot.Timeline.Count == 0)
                {
                    throw new McpInvalidParamsException("the arrangement is empty, so there is nothing to render; place clips with arrangement_place_clip or apply_patch \"timeline\" first");
                }
                var (renderedArrangement, hearingProtection) = await Task.Run(() =>
                {
                    var arrangementBuffer = OfflineArrangementRenderer.Render(projectSnapshot, OfflineArrangementRenderer.DefaultSampleRate, out var protectionReport);
                    WaveFileMaterializer.Write(outputPath, arrangementBuffer);
                    return (arrangementBuffer, protectionReport);
                }, cancellationToken);
                var arrangementSteps = FreeTimeline.LengthInSteps(projectSnapshot);
                var musicalSeconds = arrangementSteps * projectSnapshot.StepDurationSeconds;
                var peakDecibels = AmplitudeToDecibels(renderedArrangement.PeakAmplitude());
                var renderReport = new StringBuilder();
                renderReport.Append(CultureInfo.InvariantCulture,
                    $"rendered {outputPath}: {arrangementSteps / (double)FreeTimeline.StepsPerBar:0.##} bars at {projectSnapshot.Bpm:0} BPM = {musicalSeconds:0.0} s of music ");
                renderReport.Append(CultureInfo.InvariantCulture,
                    $"+ {renderedArrangement.DurationSeconds - musicalSeconds:0.0} s release tail = {renderedArrangement.DurationSeconds:0.0} s, 44.1 kHz 16-bit stereo. ");
                renderReport.Append(CultureInfo.InvariantCulture,
                    $"Peak {peakDecibels:0.0} dBFS, RMS {AmplitudeToDecibels(RootMeanSquare(renderedArrangement)):0.0} dBFS. ");
                // A peak of -1.0 is the ceiling, not headroom: say how much it took to get there.
                if (hearingProtection.Engaged)
                {
                    renderReport.Append(CultureInfo.InvariantCulture,
                        $"Hearing protection engaged: the limiter pulled {hearingProtection.LimitedFrames} frames ({hearingProtection.LimitedFrames * 1000.0 / OfflineArrangementRenderer.DefaultSampleRate:0} ms) down by up to {-hearingProtection.DeepestLimiterReductionDecibels:0.0} dB");
                    if (hearingProtection.NormalizationDecibels < -0.01)
                    {
                        renderReport.Append(CultureInfo.InvariantCulture, $", then the whole render was scaled down {-hearingProtection.NormalizationDecibels:0.0} dB");
                    }
                    renderReport.Append(". The mix is louder than the -1 dBFS ceiling; lower track or master gain for clean peaks.");
                }
                else
                {
                    renderReport.Append("Hearing protection did not engage (no peak reached the -1 dBFS ceiling).");
                }
                if (toolArguments["levels"] is JsonValue levelsValue && levelsValue.TryGetValue<bool>(out var levelsRequested) && levelsRequested)
                {
                    var barRanges = LevelBarRanges(arrangementSteps);
                    var stepFrames = projectSnapshot.StepDurationSeconds * renderedArrangement.SampleRate;
                    string DescribeRanges(StereoBuffer renderedBuffer) => string.Join(", ", barRanges.Select(barRange =>
                        string.Create(CultureInfo.InvariantCulture,
                            $"bars {barRange.FirstBar}–{barRange.LastBar} {AmplitudeToDecibels(RootMeanSquare(renderedBuffer, (int)((barRange.FirstBar - 1) * 16 * stepFrames), (int)(barRange.LastBar * 16 * stepFrames))):0.0}")));
                    renderReport.Append("\nmix RMS by bars (dBFS): ").Append(DescribeRanges(renderedArrangement));

                    var trackLevels = await Task.Run(() => OfflineArrangementRenderer.RenderStems(projectSnapshot, stemProject => OfflineArrangementRenderer.Render(stemProject))
                        .Select(stem => (stem.Track.Name, Rms: AmplitudeToDecibels(RootMeanSquare(stem.Stem)), Peak: AmplitudeToDecibels(stem.Stem.PeakAmplitude()), Ranges: DescribeRanges(stem.Stem)))
                        .ToList(), cancellationToken);
                    renderReport.Append("\ntrack levels (each track soloed through the master bus; RMS over the whole render, then RMS by bars; loudest first):");
                    foreach (var (trackName, trackRms, trackPeak, trackRanges) in trackLevels.OrderByDescending(level => level.Rms))
                    {
                        renderReport.Append(CultureInfo.InvariantCulture, $"\n- \"{trackName}\": RMS {trackRms:0.0} dBFS, peak {trackPeak:0.0} dBFS; {trackRanges}");
                    }
                    // Name the outliers outright: a track 12 dB under the rest is effectively missing from the mix.
                    if (trackLevels.Count >= 3)
                    {
                        var sortedRms = trackLevels.Select(level => level.Rms).Order().ToList();
                        var medianRms = sortedRms[sortedRms.Count / 2];
                        foreach (var (trackName, trackRms, _, _) in trackLevels.OrderBy(level => level.Rms))
                        {
                            if (trackRms <= medianRms - 12)
                            {
                                renderReport.Append(CultureInfo.InvariantCulture, $"\n- balance: \"{trackName}\" is {medianRms - trackRms:0.0} dB below the median track ({medianRms:0.0} dBFS); raise its gain (or the instrument's level) if it should be heard");
                            }
                            else if (trackRms >= medianRms + 12)
                            {
                                renderReport.Append(CultureInfo.InvariantCulture, $"\n- balance: \"{trackName}\" is {trackRms - medianRms:0.0} dB above the median track ({medianRms:0.0} dBFS); lower its gain unless it should dominate");
                            }
                        }
                    }
                    var silentTracks = projectSnapshot.Tracks.Select(track => track.Name).Except(trackLevels.Select(level => level.Name)).ToList();
                    if (silentTracks.Count > 0)
                    {
                        renderReport.Append($"\n- not playing in the arrangement: {string.Join(", ", silentTracks.Select(trackName => $"\"{trackName}\""))}");
                    }
                }
                return renderReport.ToString();
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
            "Replace the project with an imported Standard MIDI File (.mid): notes quantized to sixteenths, pitch bends as pitch envelopes, the song placed on the arrangement. " +
            "With a SoundFont (the .sf2 next to the .mid with the same name or the only one in its folder, or the one passed as soundfont), every channel plays its " +
            "General MIDI program from the font and the drum channel plays the font's drum kit as one track; without one, programs map to built-in instruments and drums split " +
            "into kick/snare/clap/hat. The result names the SoundFont used. Undoable in the studio.",
            Schema(new JsonObject
            {
                ["path"] = Property("string", "Path to a .mid file."),
                ["soundfont"] = Property("string", "Optional .sf2 path to voice the tracks with; \"none\" ignores a SoundFont next to the .mid. Default: the companion .sf2 if there is one."),
            }, "path"),
            async (toolArguments, clientName, _) =>
            {
                var midiFilePath = ReadString(toolArguments["path"]) ?? throw new McpInvalidParamsException("path is required");
                var requestedSoundFont = ReadString(toolArguments["soundfont"]);
                var soundFontPath = requestedSoundFont is null
                    ? MidiFileImporter.FindCompanionSoundFont(midiFilePath)
                    : string.Equals(requestedSoundFont, "none", StringComparison.OrdinalIgnoreCase) ? null : requestedSoundFont;
                MidiSoundFontChoice? soundFontChoice = null;
                if (soundFontPath is not null)
                {
                    try
                    {
                        soundFontChoice = new MidiSoundFontChoice(Path.GetFullPath(soundFontPath), SoundFonts.SoundFontLibrary.Load(soundFontPath));
                    }
                    catch (Exception soundFontRemediationOpportunity) when (soundFontRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        throw new McpInvalidParamsException($"cannot use SoundFont {soundFontPath}: {soundFontRemediationOpportunity.Message}");
                    }
                }
                var importResult = MidiFileImporter.Import(midiFilePath, soundFontChoice);
                return await host.InvokeAsync(() =>
                {
                    importResult.Project.AiProvenance = $"{clientName} via MCP (MIDI import)";
                    host.Document.ReplaceProject(importResult.Project, $"{clientName}: import MIDI", undoable: true);
                    host.OnAgentEdit(clientName, $"imported {Path.GetFileName(midiFilePath)}");
                    return string.Join("\n", importResult.ImportLedger) + "\n\n" + PromptLibrary.DescribeProjectForPatching(host.Document.Project);
                });
            });

        Register("describe_soundfont",
            "List the presets of a SoundFont (.sf2) file as \"bank:program name\" (bank 128 = drum kit), for add_track or apply_patch tracks[].soundfont.",
            Schema(new JsonObject
            {
                ["path"] = Property("string", "Path to a .sf2 file."),
                ["filter"] = Property("string", "Optional case-insensitive substring of the preset name."),
            }, "path"),
            (toolArguments, _, _) =>
            {
                var soundFontPath = ReadString(toolArguments["path"]) ?? throw new McpInvalidParamsException("path is required");
                SoundFonts.SoundFontBank describedBank;
                try
                {
                    describedBank = SoundFonts.SoundFontLibrary.Load(soundFontPath);
                }
                catch (Exception soundFontRemediationOpportunity) when (soundFontRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    throw new McpInvalidParamsException($"cannot read SoundFont {soundFontPath}: {soundFontRemediationOpportunity.Message}");
                }
                var presetLines = SoundFontDeviceOperations.DescribePresets(describedBank, ReadString(toolArguments["filter"]));
                return Task.FromResult($"{Path.GetFileName(soundFontPath)} \"{describedBank.Name}\": {describedBank.Presets.Count} preset(s)\n" +
                                       (presetLines.Length == 0 ? "no preset matches the filter" : presetLines));
            });

        Register("undo", "Undo the last edit (by anyone — agent or human).", Schema(new JsonObject()),
            async (_, _, _) => await host.InvokeAsync(() => host.Document.Undo() is { } undoneLabel ? $"undid: {undoneLabel}" : "nothing to undo"));

        Register("redo", "Redo the last undone edit.", Schema(new JsonObject()),
            async (_, _, _) => await host.InvokeAsync(() => host.Document.Redo() is { } redoneLabel ? $"redid: {redoneLabel}" : "nothing to redo"));

        Register("list_samples",
            "List WAV samples in the user's sample folders (for sampler tracks), grouped by the folder they are in. " +
            "Each group starts with a line `folder \"<label>\" (<n> samples):` where the label is the folder relative to its sample folder " +
            "(e.g. \"BPB Cassette 909 / clean\"); the lines under it are full paths to pass as a track's `sample`. " +
            "Sample packs often repeat file names across folders (clean/bd01.wav, cassette 1/bd01.wav), so pick by path.",
            Schema(new JsonObject
            {
                ["filter"] = Property("string", "Optional case-insensitive substring of the file name or of the folder label; a folder match lists that whole folder."),
                ["limit"] = Property("integer", "Maximum sample paths listed (default 50)."),
            }),
            async (toolArguments, _, _) =>
            {
                var filterText = ReadString(toolArguments["filter"]) ?? string.Empty;
                var resultLimit = (int)Math.Clamp(ReadDouble(toolArguments["limit"]) ?? 50, 1, 500);
                var sampleFolders = await host.InvokeAsync(() => host.SampleFolders.ToList());
                var matchingGroups = SampleLibrary.GroupByFolder(host.SampleLibraryFiles, sampleFolders)
                    .Select(folderGroup => folderGroup.Label.Contains(filterText, StringComparison.OrdinalIgnoreCase)
                        ? folderGroup
                        : folderGroup with
                        {
                            SamplePaths = folderGroup.SamplePaths
                                .Where(samplePath => Path.GetFileName(samplePath).Contains(filterText, StringComparison.OrdinalIgnoreCase))
                                .ToList(),
                        })
                    .Where(folderGroup => folderGroup.SamplePaths.Count > 0)
                    .ToList();
                var matchingSampleCount = matchingGroups.Sum(folderGroup => folderGroup.SamplePaths.Count);
                if (matchingSampleCount == 0)
                {
                    return "no samples found — the user can add sample folders in slaw's Settings → Samples";
                }
                var sampleListing = new StringBuilder();
                sampleListing.Append(CultureInfo.InvariantCulture,
                    $"{matchingSampleCount} sample(s) in {matchingGroups.Count} folder(s){(matchingSampleCount > resultLimit ? $", first {resultLimit} listed" : string.Empty)}:");
                var remainingListings = resultLimit;
                foreach (var folderGroup in matchingGroups)
                {
                    if (remainingListings == 0)
                    {
                        break;
                    }
                    sampleListing.Append(CultureInfo.InvariantCulture, $"\nfolder \"{folderGroup.Label}\" ({folderGroup.SamplePaths.Count} samples):");
                    foreach (var samplePath in folderGroup.SamplePaths.Take(remainingListings))
                    {
                        sampleListing.Append("\n  ").Append(samplePath);
                    }
                    remainingListings -= Math.Min(remainingListings, folderGroup.SamplePaths.Count);
                }
                return sampleListing.ToString();
            });

        Register("list_plugins",
            "List the VST3 plugins slaw can host: each with its kind (instrument/effect), its role as the plugin declares it (e.g. synth, sampler, drum, reverb, delay), " +
            "bundle path, class id, and the folder it was found in. Folders: Settings → Plugins, --plugin-folder, the VST3_PATH environment variable, and the system VST3 folders. " +
            "Plugin output levels vary widely: render with levels:true after placing clips.",
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
                        $"{plugin.KindLabel} \"{plugin.Name}\" by {(plugin.Vendor.Length > 0 ? plugin.Vendor : "unknown")} · role {DescribePluginRole(plugin)} · path \"{plugin.BundlePath}\" · classId {plugin.ClassId} · folder \"{plugin.SourceFolder}\"\n");
                }
                if (matchingPlugins.Count > 0)
                {
                    pluginListing.Append("Output levels differ a lot between plugins (often 10–20 dB): render with levels:true after placing clips and adjust track gains.\n");
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
            "Ask slaw's built-in AI co-pilot (the model the user configured in Settings, often a small local one) a question about the open project; " +
            "without a question it returns a short review of the song. Returns the model's text answer and changes nothing.",
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

    private static string DescribeOutcome(IReadOnlyList<string> changeSummary, IReadOnlyList<string> remediationLedger, int lockEnforcementCount, IReadOnlyList<string> warnings)
    {
        var outcomeDescription = new StringBuilder();
        outcomeDescription.AppendLine("applied:");
        foreach (var changeLine in changeSummary)
        {
            outcomeDescription.Append("- ").AppendLine(changeLine);
        }
        if (remediationLedger.Count > 0)
        {
            outcomeDescription.AppendLine($"adjusted by the project checker ({remediationLedger.Count}): {string.Join("; ", remediationLedger.Take(6))}");
        }
        if (lockEnforcementCount > 0)
        {
            outcomeDescription.AppendLine($"ignored edits to {lockEnforcementCount} LOCKED track(s)");
        }
        return AppendWarnings(outcomeDescription.ToString().TrimEnd(), warnings);
    }

    /// <summary>
    /// Only what this call caused: conditions the project already had (an empty sampler
    /// waiting for its sample, say) are not news on every call.
    /// </summary>
    private static List<string> NewRemediations(SlawProject previousProject, IReadOnlyList<string> remediationLedger)
    {
        var standingConditions = ProjectAlignmentRemediator.Remediate(previousProject.DeepClone()).ToHashSet();
        return remediationLedger.Where(remediation => !standingConditions.Contains(remediation)).ToList();
    }

    private static string AppendWarnings(string result, IReadOnlyCollection<string> warnings) =>
        warnings.Count == 0 ? result : $"{result}\nwarnings:\n{string.Join("\n", warnings.Select(warning => $"- {warning}"))}";

    private const string NextStepsForNewTrack =
        "Place it in the arrangement (the song) with arrangement_place_clip; session clips go in with set_clip.";

    private static double AmplitudeToDecibels(double amplitude) => 20 * Math.Log10(Math.Max(1e-6, amplitude));

    /// <summary>"synth", "drum, sampler", "reverb"…, from the VST3 subcategories the plugin declares; "not declared" when it declares nothing useful.</summary>
    internal static string DescribePluginRole(Vst3PluginDescriptor plugin)
    {
        var roleWords = plugin.SubCategories
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(subCategory => subCategory is not ("Instrument" or "Fx" or "Stereo" or "Mono" or "Surround" or "Ambisonics" or "Up-Downmix" or "Only Realtime" or "Only Offline" or "Only OfflineProcess" or "NoOfflineProcess"))
            .Select(subCategory => subCategory.ToLowerInvariant())
            .Distinct()
            .ToList();
        return roleWords.Count == 0 ? "not declared" : string.Join(", ", roleWords);
    }

    private static double RootMeanSquare(StereoBuffer renderedBuffer) => RootMeanSquare(renderedBuffer, 0, renderedBuffer.SampleFrameCount);

    private static double RootMeanSquare(StereoBuffer renderedBuffer, int firstFrame, int endFrame)
    {
        firstFrame = Math.Clamp(firstFrame, 0, renderedBuffer.SampleFrameCount);
        endFrame = Math.Clamp(endFrame, firstFrame, renderedBuffer.SampleFrameCount);
        double energy = 0;
        for (var frameIndex = firstFrame; frameIndex < endFrame; frameIndex++)
        {
            energy += renderedBuffer.Left[frameIndex] * renderedBuffer.Left[frameIndex] + renderedBuffer.Right[frameIndex] * renderedBuffer.Right[frameIndex];
        }
        return Math.Sqrt(energy / Math.Max(1, 2 * (endFrame - firstFrame)));
    }

    /// <summary>Bar ranges for level reports: 4 bars each up to 32 bars, then 8, so a song stays a dozen or so numbers.</summary>
    private static List<(int FirstBar, int LastBar)> LevelBarRanges(int arrangementSteps)
    {
        var totalBars = Math.Max(1, (int)Math.Ceiling(arrangementSteps / (double)FreeTimeline.StepsPerBar));
        var barsPerRange = totalBars <= 32 ? 4 : 8;
        return Enumerable.Range(0, (totalBars + barsPerRange - 1) / barsPerRange)
            .Select(rangeIndex => (rangeIndex * barsPerRange + 1, Math.Min(totalBars, (rangeIndex + 1) * barsPerRange)))
            .ToList();
    }

    /// <summary>
    /// What each built-in instrument puts out at gain 1, measured once by rendering one bar of it,
    /// with a suggested starting gain for a track RMS around -24 dBFS. Agents cannot guess this.
    /// </summary>
    private static readonly Lazy<string> BuiltInInstrumentLevels = new(() =>
    {
        const double TargetTrackRms = -24;
        var levelLines = new List<string>();
        foreach (var instrumentName in InstrumentVoiceSynthesizer.SupportedInstruments.Where(instrumentName => instrumentName != "sampler"))
        {
            var measuredTrack = new SlawTrack { Name = instrumentName, Instrument = instrumentName, Gain = 1 };
            var measuredPattern = InstrumentVoiceSynthesizer.IsPercussive(instrumentName)
                ? "x . . . x . . . x . . . x . . ."
                : "A3:4 . . . C4:4 . . . E4:4 . . . A3:4 . . .";
            var measuredProject = ProjectSerializer.CreateBlankProject("level probe", 120);
            measuredProject.Tracks.Add(measuredTrack);
            measuredProject.Timeline.Add(new TimelineClip
            {
                TrackId = measuredTrack.Id,
                StartStep = 0,
                LengthSteps = 16,
                Clip = new SlawClip { Name = instrumentName, Pattern = CompactPatternJsonConverter.SplitStepString(measuredPattern) },
            });
            var renderedBar = OfflineArrangementRenderer.Render(measuredProject);
            var barFrames = (int)(16 * measuredProject.StepDurationSeconds * renderedBar.SampleRate);
            var measuredRms = AmplitudeToDecibels(RootMeanSquare(renderedBar, 0, barFrames));
            var suggestedGain = Math.Clamp(Math.Pow(10, (TargetTrackRms - measuredRms) / 20), 0.05, 1.5);
            levelLines.Add(string.Create(CultureInfo.InvariantCulture, $"{instrumentName} {measuredRms:0} dBFS (start near gain {suggestedGain:0.##})"));
        }
        return "Built-in instrument levels at gain 1 (RMS of one busy bar through the default master; suggested starting gain for about -24 dBFS per track): " +
               string.Join(", ", levelLines) + ". sampler: depends on the sample.";
    });

    private static string DescribeClipEnvelopes(SlawClip clip) =>
        clip.Automation is { Count: > 0 } envelopes
            ? "; envelopes: " + string.Join("; ", envelopes.Select(envelope => ClipAutomation.Describe(envelope.Key, envelope.Value)))
            : string.Empty;

    private static List<string>? ReadPattern(JsonNode? patternNode)
    {
        var steps = patternNode switch
        {
            JsonArray stepArray => stepArray.Select(stepNode => ReadString(stepNode) ?? NotationResolver.RestSymbol).ToList(),
            JsonValue => CompactPatternJsonConverter.SplitStepString(ReadString(patternNode)),
            _ => null,
        };
        return steps is { Count: > 0 } ? steps : null;
    }

    /// <summary>`start` (0-based step) or `bar` (1-based); null when neither is given.</summary>
    private static int? ReadStartStep(JsonObject toolArguments)
    {
        if (ReadDouble(toolArguments["start"]) is { } startStep)
        {
            return (int)Math.Round(startStep);
        }
        return ReadDouble(toolArguments["bar"]) is { } startBar
            ? (int)Math.Round((startBar - 1) * FreeTimeline.StepsPerBar)
            : null;
    }

    /// <summary>`length` (steps) or `bars`; null when neither is given.</summary>
    private static int? ReadLengthSteps(JsonObject toolArguments)
    {
        if (ReadDouble(toolArguments["length"]) is { } lengthSteps)
        {
            return (int)Math.Round(lengthSteps);
        }
        return ReadDouble(toolArguments["bars"]) is { } lengthBars
            ? (int)Math.Round(lengthBars * FreeTimeline.StepsPerBar)
            : null;
    }

    private static void ValidatePlacement(int startStep, int lengthSteps)
    {
        if (startStep < 0 || startStep > FreeTimeline.MaximumStep)
        {
            throw new McpInvalidParamsException($"start must be a step between 0 and {FreeTimeline.MaximumStep} (bar 1 = step 0); got {startStep}");
        }
        if (lengthSteps < 1 || lengthSteps > FreeTimeline.MaximumClipLength)
        {
            throw new McpInvalidParamsException($"length must be between 1 and {FreeTimeline.MaximumClipLength} steps (16 per bar); got {lengthSteps}");
        }
    }

    private TimelineClip ResolveArrangementClip(JsonNode? clipReference)
    {
        var clipId = ReadString(clipReference) ?? throw new McpInvalidParamsException("clip (an arrangement clip id) is required");
        return host.Document.Project.Timeline.FirstOrDefault(timelineClip => timelineClip.Id == clipId)
            ?? throw new McpInvalidParamsException($"no arrangement clip with id \"{clipId}\"; get_project lists every clip id");
    }

    /// <summary>The clip an envelope tool addresses: an arrangement clip by id, or a session clip by track + scene.</summary>
    private (SlawTrack Track, SlawClip Clip, int EnvelopeLength, string Label) ResolveAutomatedClip(JsonObject toolArguments)
    {
        if (toolArguments["clip"] is not null)
        {
            var arrangementClip = ResolveArrangementClip(toolArguments["clip"]);
            var owningTrack = host.Document.Project.Tracks.First(track => track.Id == arrangementClip.TrackId);
            return (owningTrack, arrangementClip.Clip, arrangementClip.LengthSteps,
                $"arrangement clip id \"{arrangementClip.Id}\" on \"{owningTrack.Name}\" (bars {FreeTimeline.FormatPosition(arrangementClip.StartStep)}–{FreeTimeline.FormatPosition(arrangementClip.EndStep)})");
        }
        var (_, sessionTrack) = ResolveTrack(toolArguments["track"]);
        var sceneIndex = (int)(ReadDouble(toolArguments["scene"]) ?? throw new McpInvalidParamsException("pass clip (an arrangement clip id), or track + scene for a session clip"));
        var sessionClip = sessionTrack.ClipAt(sceneIndex)
            ?? throw new McpInvalidParamsException($"{sessionTrack.Name} has no session clip in scene {sceneIndex}; write one with set_clip, or pass clip for an arrangement clip");
        return (sessionTrack, sessionClip, sessionClip.Pattern.Count, $"session clip {sessionTrack.Name} · scene {sceneIndex}");
    }

    private static JsonObject ArrayOf(JsonObject itemSchema, string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = itemSchema,
    };

    private static JsonObject ObjectOf(JsonObject properties, string description) => new()
    {
        ["type"] = "object",
        ["description"] = description,
        ["properties"] = properties,
    };

    /// <summary>apply_patch's patch, typed, so agents see every key without reading the description twice.</summary>
    private static JsonObject PatchSchema() => ObjectOf(new JsonObject
    {
        ["reset"] = Property("boolean", "true: start from an empty project (no tracks or clips, one empty scene, default master) before applying the rest."),
        ["bpm"] = Property("number", "Tempo, 40..300."),
        ["name"] = Property("string", "Project name."),
        ["summary"] = Property("string", "Optional one-line description of the change, shown to the user."),
        ["scenes"] = ArrayOf(new JsonObject { ["type"] = "string" }, "Renames session scenes in order (never deletes)."),
        ["removeScenes"] = ArrayOf(new JsonObject { ["type"] = "integer" }, "0-based session scene indices to delete."),
        ["tracks"] = ArrayOf(ObjectOf(new JsonObject
        {
            ["id"] = Property("string", "Existing track id, or \"new\" to add a track."),
            ["name"] = Property("string", "Track name; arrangement clips in the same patch may refer to it."),
            ["instrument"] = Property("string", "kick, snare, hat, clap, sine, square, saw, triangle, pluck, noise, sampler or vst3 (see get_catalog)."),
            ["plugin"] = AnyProperty("VST3 instrument: its name or {path, classId} from list_plugins."),
            ["sample"] = Property("string", "WAV path for sampler tracks."),
            ["gain"] = Property("number", "0..1.5."),
            ["pan"] = Property("number", "-1..1."),
            ["muted"] = Property("boolean", "Mute."),
            ["solo"] = Property("boolean", "Solo."),
            ["instrumentParameters"] = Property("object", "Parameter key → number (see get_catalog; p<id> → 0..1 for vst3)."),
            ["effects"] = ArrayOf(ObjectOf(new JsonObject
            {
                ["type"] = Property("string", "Effect type from get_catalog, or vst3."),
                ["enabled"] = Property("boolean", "Default true."),
                ["parameters"] = Property("object", "Parameter key → number."),
                ["plugin"] = AnyProperty("VST3 effect: name or {path, classId}."),
                ["sidechain"] = AnyProperty("Track id or name whose audio keys this effect (\"duck\" or a VST3 effect with a sidechain input); null clears; omitted keeps the slot's current source."),
            }, "One effect."), "Replaces the whole effect chain, in order (effect index 0 first)."),
            ["clips"] = Property("object", "SESSION clips: {\"<sceneIndex>\": pattern string, or null to clear}."),
            ["automation"] = Property("object", "SESSION clip envelopes: {\"<sceneIndex>\": {\"<target>\": [[step, value], ...] or null}}."),
            ["remove"] = Property("boolean", "true deletes the track and its session and arrangement clips."),
        }, "One track."), "Tracks to add, change or remove."),
        ["timeline"] = ArrayOf(ObjectOf(new JsonObject
        {
            ["id"] = Property("string", "Existing arrangement clip id, or \"new\"."),
            ["track"] = Property("string", "Track id, name or index."),
            ["start"] = Property("integer", "0-based start step (16 per bar; bar N starts at (N-1)*16)."),
            ["length"] = Property("integer", "Length in steps; the pattern loops to fill it. Default: the pattern's length."),
            ["pattern"] = Property("string", "Space-separated steps (required for new clips)."),
            ["name"] = Property("string", "Clip name."),
            ["automation"] = Property("object", "{\"<target>\": [[step, value], ...]} with steps 0..length from the clip's start."),
            ["remove"] = Property("boolean", "true deletes the clip."),
            ["trackId"] = Property("string", "Alias of track (get_project's JSON name)."),
            ["startStep"] = Property("integer", "Alias of start (get_project's JSON name)."),
            ["lengthSteps"] = Property("integer", "Alias of length (get_project's JSON name)."),
            ["clip"] = Property("object", "get_project's JSON form: {name, pattern, automation}, used when the top-level fields are absent."),
        }, "One arrangement clip."), "ARRANGEMENT (song) clips to place, change or remove."),
        ["arrangement"] = ArrayOf(ObjectOf(new JsonObject
        {
            ["scene"] = Property("integer", "0-based session scene."),
            ["repeats"] = Property("integer", "How many times."),
        }, "One section."), "Legacy: REPLACES the whole arrangement with these session scenes end to end."),
        ["master"] = Property("object", "{gain, drive, lowpassHz, delaySteps, delayFeedback, delayMix, effects [{type, enabled, parameters, plugin, sidechain}]}: effects replace the master chain, which processes the sum of all tracks."),
    }, "The patch: only the keys you want to change.");

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
