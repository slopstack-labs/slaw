using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Mcp;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The MCP surface, exercised the way a client would: JSON-RPC in, JSON-RPC out.</summary>
public class McpServerTests
{
    private static (McpProtocolDispatcher Dispatcher, StudioDocument Document) CreateHeadlessServer()
    {
        var servedDocument = new StudioDocument(ProjectSerializer.CreateStarterProject("mcp"), filePath: null);
        var headlessHost = new HeadlessMcpHost(servedDocument, new StudioPreferences { AiFeaturesEnabled = false });
        return (new McpProtocolDispatcher(new SlawMcpToolset(headlessHost)), servedDocument);
    }

    private static async Task<JsonNode> CallAsync(McpProtocolDispatcher dispatcher, int requestIdentifier, string method, JsonObject? parameters = null)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestIdentifier, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        var response = await dispatcher.HandleAsync(request.ToJsonString(), CancellationToken.None);
        return JsonNode.Parse(response!)!;
    }

    [Fact]
    public async Task InitializeListAndCallTools()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();

        var initializeResponse = await CallAsync(dispatcher, 1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["clientInfo"] = new JsonObject { ["name"] = "test-agent" },
        });
        Assert.Equal("2025-03-26", initializeResponse["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Null(await dispatcher.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None));

        var toolNames = (await CallAsync(dispatcher, 2, "tools/list"))["result"]!["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("apply_patch", toolNames);

        var clipResponse = await CallAsync(dispatcher, 3, "tools/call", new JsonObject
        {
            ["name"] = "set_clip",
            ["arguments"] = new JsonObject { ["track"] = "Synergy Kick", ["scene"] = 0, ["pattern"] = "x x x x . . . . x x x x . . . ." },
        });
        Assert.False(clipResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("x", servedDocument.Project.Tracks[0].Clips[0]!.Pattern[1]);
        Assert.Equal("test-agent via MCP", servedDocument.Project.AiProvenance);

        var undoResponse = await CallAsync(dispatcher, 4, "tools/call", new JsonObject { ["name"] = "undo" });
        Assert.Contains("undid", undoResponse["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(".", servedDocument.Project.Tracks[0].Clips[0]!.Pattern[1]);
    }

    [Fact]
    public async Task LockedTracksAndUnknownMethodsAreRefused()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        servedDocument.Project.Tracks[0].AiLocked = true;

        var lockedResponse = await CallAsync(dispatcher, 1, "tools/call", new JsonObject
        {
            ["name"] = "set_tempo",
            ["arguments"] = new JsonObject { ["bpm"] = 150 },
        });
        Assert.False(lockedResponse["result"]!["isError"]!.GetValue<bool>());

        var lockedClipResponse = await CallAsync(dispatcher, 2, "tools/call", new JsonObject
        {
            ["name"] = "set_clip",
            ["arguments"] = new JsonObject { ["track"] = 0, ["scene"] = 0, ["pattern"] = "x x x x" },
        });
        Assert.True(lockedClipResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("LOCKED", lockedClipResponse["result"]!["content"]![0]!["text"]!.GetValue<string>());

        var unknownMethodResponse = await CallAsync(dispatcher, 3, "sampling/createMessage");
        Assert.Equal(-32601, unknownMethodResponse["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task SetAutomationWritesListsKeepsAndUndoesEnvelopes()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        static string TextOf(JsonNode response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();

        var envelopeResponse = await CallAsync(dispatcher, 1, "tools/call", new JsonObject
        {
            ["name"] = "set_automation",
            ["arguments"] = new JsonObject
            {
                ["track"] = "Bottom-Line Bass",
                ["scene"] = 0,
                ["target"] = "fx.0.cutoff",
                ["points"] = JsonNode.Parse("[[0, 300], [8, 2500], [16, 300]]"),
            },
        });
        Assert.False(envelopeResponse["result"]!["isError"]!.GetValue<bool>(), TextOf(envelopeResponse));
        var bassTrack = servedDocument.Project.Tracks.Single(track => track.Name == "Bottom-Line Bass");
        Assert.Equal(3, bassTrack.Clips[0]!.Automation!["fx.0.cutoff"].Count);
        Assert.Contains("envelope fx.0.cutoff 0:300 → 8:2500 → 16:300", TextOf(await CallAsync(dispatcher, 2, "tools/call", new JsonObject { ["name"] = "get_project" })));

        // New notes keep the sweep.
        await CallAsync(dispatcher, 3, "tools/call", new JsonObject
        {
            ["name"] = "set_clip",
            ["arguments"] = new JsonObject { ["track"] = "Bottom-Line Bass", ["scene"] = 0, ["pattern"] = "A1 . A1 . A1 . A1 ." },
        });
        Assert.NotNull(bassTrack.Clips[0]!.Automation);

        var unknownTargetResponse = await CallAsync(dispatcher, 4, "tools/call", new JsonObject
        {
            ["name"] = "set_automation",
            ["arguments"] = new JsonObject { ["track"] = "Bottom-Line Bass", ["scene"] = 0, ["target"] = "fx.9.cutoff", ["points"] = JsonNode.Parse("[[0, 1]]") },
        });
        Assert.True(unknownTargetResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("fx.0.cutoff", TextOf(unknownTargetResponse));

        await CallAsync(dispatcher, 5, "tools/call", new JsonObject { ["name"] = "undo" });
        await CallAsync(dispatcher, 6, "tools/call", new JsonObject { ["name"] = "undo" });
        Assert.Null(servedDocument.Project.Tracks.Single(track => track.Name == "Bottom-Line Bass").Clips[0]!.Automation);
    }

    [Fact]
    public async Task ApplyPatchAcceptsAnUnwrappedPatchAndExplainsTheWrapperOtherwise()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        var unwrappedResponse = await CallAsync(dispatcher, 1, "tools/call", new JsonObject
        {
            ["name"] = "apply_patch",
            ["arguments"] = new JsonObject { ["bpm"] = 133 },
        });
        Assert.False(unwrappedResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(133, servedDocument.Project.Bpm);

        var emptyResponse = await CallAsync(dispatcher, 2, "tools/call", new JsonObject
        {
            ["name"] = "apply_patch",
            ["arguments"] = new JsonObject { ["tempo"] = 90 },
        });
        Assert.True(emptyResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("{\"patch\": {...}}", emptyResponse["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }
}
