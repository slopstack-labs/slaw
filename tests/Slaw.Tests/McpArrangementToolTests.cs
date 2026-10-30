using System.Text.Json;
using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Mcp;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// The arrangement as an agent meets it over MCP, without having read a line of
/// this repository: dedicated clip tools, envelopes over whole clips, plain
/// results, and warnings instead of silent adjustments.
/// </summary>
public class McpArrangementToolTests
{
    private static (McpProtocolDispatcher Dispatcher, StudioDocument Document) CreateHeadlessServer()
    {
        var servedDocument = new StudioDocument(ProjectSerializer.CreateStarterProject("mcp"), filePath: null);
        servedDocument.Project.Timeline.Clear();
        var headlessHost = new HeadlessMcpHost(servedDocument, new StudioPreferences { AiFeaturesEnabled = false });
        return (new McpProtocolDispatcher(new SlawMcpToolset(headlessHost)), servedDocument);
    }

    private static async Task<(string Text, bool IsError)> CallToolAsync(McpProtocolDispatcher dispatcher, string toolName, JsonObject toolArguments)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = toolArguments } };
        var result = JsonNode.Parse((await dispatcher.HandleAsync(request.ToJsonString(), CancellationToken.None))!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task PlacingEditingAndRemovingArrangementClipsSpeaksInBarsAndSteps()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();

        var (placed, placeFailed) = await CallToolAsync(dispatcher, "arrangement_place_clip", new JsonObject
        {
            ["track"] = "Synergy Kick", ["bar"] = 9, ["bars"] = 8, ["pattern"] = "x . . . x . . . x . . . x . . .",
        });
        Assert.False(placeFailed, placed);
        var placedClip = Assert.Single(servedDocument.Project.Timeline);
        Assert.Equal((128, 128), (placedClip.StartStep, placedClip.LengthSteps));
        Assert.Contains($"placed clip id \"{placedClip.Id}\"", placed);
        Assert.Contains("bars 9.1–17.1 (start 128, length 128 steps = 8 bars, ends before step 256); pattern 16 steps ×8", placed);

        var (edited, _) = await CallToolAsync(dispatcher, "arrangement_edit_clip", new JsonObject { ["clip"] = placedClip.Id, ["start"] = 32, ["length"] = 64 });
        Assert.Equal((32, 64), (placedClip.StartStep, placedClip.LengthSteps));
        Assert.Contains("bars 3.1–7.1", edited);

        var (missing, missingFailed) = await CallToolAsync(dispatcher, "arrangement_edit_clip", new JsonObject { ["clip"] = "nope" });
        Assert.True(missingFailed);
        Assert.Contains("no arrangement clip with id \"nope\"", missing);

        var (removed, _) = await CallToolAsync(dispatcher, "arrangement_remove_clips", new JsonObject { ["clips"] = new JsonArray(placedClip.Id) });
        Assert.StartsWith("removed 1 arrangement clip(s)", removed);
        Assert.Empty(servedDocument.Project.Timeline);
    }

    [Fact]
    public async Task ArrangementEnvelopesSpanTheWholeClipAndOutOfRangePointsAreReported()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        await CallToolAsync(dispatcher, "arrangement_place_clip", new JsonObject
        {
            ["track"] = "Bottom-Line Bass", ["start"] = 0, ["length"] = 128, ["pattern"] = string.Join(' ', Enumerable.Repeat("A1 .", 32)),
        });
        var bassClip = Assert.Single(servedDocument.Project.Timeline);
        Assert.Equal(64, bassClip.Clip.Pattern.Count);

        // A ramp over the whole 128-step clip is kept as written, even though the pattern is 64 steps.
        var (written, _) = await CallToolAsync(dispatcher, "set_automation", new JsonObject
        {
            ["clip"] = bassClip.Id, ["target"] = "fx.0.cutoff", ["points"] = JsonNode.Parse("[[0, 300], [128, 4000]]"),
        });
        Assert.Contains("spans steps 0..128", written);
        Assert.DoesNotContain("warnings", written);
        Assert.Equal(128, bassClip.Clip.Automation!["fx.0.cutoff"][^1].Step);

        // Past the end: moved, and said so.
        var (adjusted, _) = await CallToolAsync(dispatcher, "set_automation", new JsonObject
        {
            ["clip"] = bassClip.Id, ["target"] = "fx.0.cutoff", ["points"] = JsonNode.Parse("[[0, 300], [200, 4000]]"),
        });
        Assert.Contains("warnings:", adjusted);
        Assert.Contains("step(s) 200 outside 0..128", adjusted);

        // The same through apply_patch.
        var (patched, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""{"timeline": [{"id": "CLIP", "automation": {"fx.0.cutoff": [[0, 300], [128, 90000]], "fx.7.nothing": [[0, 1]]} }]}""".Replace("CLIP", bassClip.Id)),
        });
        Assert.Contains("value(s) 90000 outside", patched);
        Assert.Contains("unknown envelope target \"fx.7.nothing\"", patched);
    }

    [Fact]
    public async Task ScenesAreRemovedExplicitlyAndIgnoredKeysAreReported()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        Assert.Equal(2, servedDocument.Project.Scenes.Count);

        var (ignored, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject { ["patch"] = JsonNode.Parse("""{"scenes": [], "arrangement": [], "tempo": 90}""") });
        Assert.Contains("scenes: [] ignored", ignored);
        Assert.Contains("arrangement: [] ignored", ignored);
        Assert.Contains("ignored unknown key \"tempo\"", ignored);
        Assert.Equal(2, servedDocument.Project.Scenes.Count);

        var (renamedOnly, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject { ["patch"] = JsonNode.Parse("""{"scenes": ["Verse"]}""") });
        Assert.Contains("never deletes", renamedOnly);
        Assert.Equal(new[] { "Verse", "Q4 Drop" }, servedDocument.Project.Scenes);
        var dropClipOfKick = servedDocument.Project.Tracks[0].ClipAt(1);

        var (removed, _) = await CallToolAsync(dispatcher, "remove_scene", new JsonObject { ["scene"] = 0 });
        Assert.Contains("removed scene 0 (\"Verse\")", removed);
        Assert.Equal(new[] { "Q4 Drop" }, servedDocument.Project.Scenes);
        Assert.Same(dropClipOfKick, servedDocument.Project.Tracks[0].ClipAt(0));

        var (lastOne, lastFailed) = await CallToolAsync(dispatcher, "remove_scene", new JsonObject { ["scene"] = 0 });
        Assert.True(lastFailed);
        Assert.Contains("only one scene left", lastOne);
    }

    [Fact]
    public async Task ResultsStatePlainFactsJsonStaysJsonAndRendersReportTrackLevels()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        var (removedTrack, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject
        {
            ["patch"] = JsonNode.Parse($$"""{"tracks": [{"id": "{{servedDocument.Project.Tracks[1].Id}}", "remove": true}]}"""),
        });
        Assert.Contains("removed track \"Stakeholder Clap\"", removedTrack);

        var (projectJson, _) = await CallToolAsync(dispatcher, "get_project", new JsonObject { ["format"] = "json" });
        using (JsonDocument.Parse(projectJson))
        {
        }

        var (transport, transportFailed) = await CallToolAsync(dispatcher, "transport", new JsonObject { ["action"] = "play_arrangement" });
        Assert.True(transportFailed);
        Assert.Contains("only works in the live studio", transport);

        await CallToolAsync(dispatcher, "arrangement_place_clip", new JsonObject { ["track"] = "Synergy Kick", ["bar"] = 1, ["bars"] = 2, ["pattern"] = "x . . . x . . ." });
        var renderPath = Path.Combine(Path.GetTempPath(), $"slaw-levels-test-{Environment.ProcessId}.wav");
        try
        {
            var (rendered, renderFailed) = await CallToolAsync(dispatcher, "render", new JsonObject { ["path"] = renderPath, ["levels"] = true });
            Assert.False(renderFailed, rendered);
            Assert.Contains("2 bars at 124 BPM", rendered);
            Assert.Contains("release tail", rendered);
            Assert.Contains("Hearing protection", rendered);
            Assert.DoesNotContain("shipped to production", rendered);
            Assert.Contains("- \"Synergy Kick\": RMS", rendered);
            Assert.Contains("not playing in the arrangement:", rendered);
        }
        finally
        {
            File.Delete(renderPath);
        }
    }

    [Fact]
    public async Task PatchTracksAcceptNamesAndNeverInventTracksForUnknownIds()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        var trackCount = servedDocument.Project.Tracks.Count;

        var (byName, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject { ["patch"] = JsonNode.Parse("""{"tracks": [{"id": "Synergy Kick", "gain": 0.5}]}""") });
        Assert.Equal(trackCount, servedDocument.Project.Tracks.Count);
        Assert.Equal(0.5, servedDocument.Project.Tracks.Single(track => track.Name == "Synergy Kick").Gain);
        Assert.Contains("Synergy Kick: gain 0.95 → 0.50", byName);

        var (unknownEdit, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject { ["patch"] = JsonNode.Parse("""{"tracks": [{"id": "Kik", "gain": 0.2}]}""") });
        Assert.Equal(trackCount, servedDocument.Project.Tracks.Count);
        Assert.Contains("no track \"Kik\"; entry ignored", unknownEdit);

        var (unknownWithInstrument, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject { ["patch"] = JsonNode.Parse("""{"tracks": [{"id": "lead1", "name": "Lead", "instrument": "square"}]}""") });
        Assert.Equal(trackCount + 1, servedDocument.Project.Tracks.Count);
        Assert.Contains("so a new track was added", unknownWithInstrument);
    }

    [Fact]
    public async Task StandingConditionsAreNotReportedAgainOnEveryCall()
    {
        var (dispatcher, _) = CreateHeadlessServer();
        await CallToolAsync(dispatcher, "add_track", new JsonObject { ["instrument"] = "sampler", ["name"] = "Vocal" });
        var (tempoChange, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject { ["patch"] = JsonNode.Parse("""{"bpm": 110}""") });
        Assert.Contains("tempo 124 → 110 BPM", tempoChange);
        Assert.DoesNotContain("no sample loaded", tempoChange);
    }

    [Fact]
    public void TheInstructionsTeachUnitsAndTheToolsAreThere()
    {
        Assert.Contains("step 0 is the start of bar 1, 16 steps per bar", SlawMcpToolset.ServerInstructions);
        Assert.Contains("on a SESSION clip they span the pattern", SlawMcpToolset.ServerInstructions);
        var toolset = new SlawMcpToolset(new HeadlessMcpHost(new StudioDocument(ProjectSerializer.CreateStarterProject("x"), null), new StudioPreferences()));
        foreach (var toolName in new[] { "arrangement_place_clip", "arrangement_edit_clip", "arrangement_remove_clips", "remove_scene" })
        {
            Assert.True(toolset.HasTool(toolName), toolName);
        }
        var patchSchema = toolset.DescribeTools().First(tool => tool!["name"]!.GetValue<string>() == "apply_patch")!["inputSchema"]!["properties"]!["patch"]!;
        Assert.NotNull(patchSchema["properties"]!["timeline"]!["items"]!["properties"]!["start"]);
        Assert.NotNull(patchSchema["properties"]!["tracks"]!["items"]!["properties"]!["plugin"]);
    }
}
