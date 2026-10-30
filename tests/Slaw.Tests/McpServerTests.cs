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
    public async Task ListSamplesGroupsByFolderAndFiltersOnFolderNames()
    {
        var sampleFolder = Directory.CreateTempSubdirectory("slaw-mcp-samples-").FullName;
        try
        {
            foreach (var relativePath in new[] { "909/clean/bd01.wav", "909/cassette 1/bd01.wav", "vocals/hey.wav" })
            {
                var samplePath = Path.Combine(sampleFolder, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(samplePath)!);
                File.WriteAllBytes(samplePath, Array.Empty<byte>());
            }
            var servedDocument = new StudioDocument(ProjectSerializer.CreateStarterProject("mcp"), filePath: null);
            var preferences = new StudioPreferences { AiFeaturesEnabled = false, SampleFolders = { sampleFolder } };
            var dispatcher = new McpProtocolDispatcher(new SlawMcpToolset(new HeadlessMcpHost(servedDocument, preferences)));
            var folderName = Path.GetFileName(sampleFolder);

            async Task<string> ListAsync(string filter) =>
                (await CallAsync(dispatcher, 1, "tools/call", new JsonObject
                {
                    ["name"] = "list_samples",
                    ["arguments"] = new JsonObject { ["filter"] = filter },
                }))["result"]!["content"]![0]!["text"]!.GetValue<string>();

            var kickListing = await ListAsync("bd01");
            Assert.StartsWith("2 sample(s) in 2 folder(s):", kickListing);
            Assert.Contains($"folder \"{folderName} / 909 / cassette 1\" (1 samples):\n  {Path.Combine(sampleFolder, "909", "cassette 1", "bd01.wav")}", kickListing);
            Assert.Contains($"folder \"{folderName} / 909 / clean\" (1 samples):", kickListing);
            Assert.DoesNotContain("hey.wav", kickListing);

            var vocalListing = await ListAsync("vocals");
            Assert.StartsWith("1 sample(s) in 1 folder(s):", vocalListing);
            Assert.Contains("hey.wav", vocalListing);
        }
        finally
        {
            Directory.Delete(sampleFolder, true);
        }
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

    [Fact]
    public async Task PlaceAudioSlicesALongFileOntoTheArrangement()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-place-audio-").FullName;
        try
        {
            // 20 s of 220 Hz at 120 BPM (0.125 s steps) is 160 steps: slices of 64 + 64 + 32.
            const int sampleRate = 44100;
            var tone = Enumerable.Range(0, 20 * sampleRate).Select(frame => (float)(0.5 * Math.Sin(2 * Math.PI * 220 * frame / sampleRate))).ToArray();
            var wavePath = Path.Combine(workingFolder, "Lead Vocal Stem.wav");
            AudioTakeAssembler.WriteTakeWave(wavePath, tone, sampleRate);

            var (dispatcher, servedDocument) = CreateHeadlessServer();
            servedDocument.Project.Bpm = 120;
            var placeResponse = await CallAsync(dispatcher, 1, "tools/call", new JsonObject
            {
                ["name"] = "arrangement_place_audio",
                ["arguments"] = new JsonObject { ["path"] = wavePath, ["bar"] = 1 },
            });
            var placeText = placeResponse["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.False(placeResponse["result"]!["isError"]!.GetValue<bool>(), placeText);
            Assert.Contains("as 3 arrangement clip(s)", placeText);

            var vocalTrack = servedDocument.Project.Tracks.Single(track => track.Name == "Lead Vocal Stem");
            Assert.Equal(new[] { (0, 64), (64, 64), (128, 32) },
                servedDocument.Project.Timeline.Where(clip => clip.TrackId == vocalTrack.Id).OrderBy(clip => clip.StartStep).Select(clip => (clip.StartStep, clip.LengthSteps)));

            // The same track already plays audio, so a second file goes on a new track beside it.
            var secondResponse = await CallAsync(dispatcher, 2, "tools/call", new JsonObject
            {
                ["name"] = "arrangement_place_audio",
                ["arguments"] = new JsonObject { ["path"] = wavePath, ["track"] = "Lead Vocal Stem", ["bar"] = 3 },
            });
            Assert.Contains("went on a new track", secondResponse["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    [Fact]
    public async Task OpenProjectRefusesToDropUnsavedWorkUnlessToldTo()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-open-project-").FullName;
        try
        {
            var savedProject = ProjectSerializer.CreateBlankProject("Opened Song", 97);
            var projectPath = Path.Combine(workingFolder, "opened-song.slaw");
            ProjectSerializer.Save(savedProject, projectPath);

            var (dispatcher, servedDocument) = CreateHeadlessServer();
            servedDocument.UnsavedChanges = true;
            var refused = await CallAsync(dispatcher, 1, "tools/call", new JsonObject
            {
                ["name"] = "open_project",
                ["arguments"] = new JsonObject { ["path"] = projectPath },
            });
            Assert.True(refused["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("unsaved changes", refused["result"]!["content"]![0]!["text"]!.GetValue<string>());

            var opened = await CallAsync(dispatcher, 2, "tools/call", new JsonObject
            {
                ["name"] = "open_project",
                ["arguments"] = new JsonObject { ["path"] = projectPath, ["discardUnsaved"] = true },
            });
            Assert.False(opened["result"]!["isError"]!.GetValue<bool>());
            Assert.Equal("Opened Song", servedDocument.Project.Name);
            Assert.Equal(97, servedDocument.Project.Bpm);
            Assert.Equal(Path.GetFullPath(projectPath), servedDocument.FilePath);
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }
}
