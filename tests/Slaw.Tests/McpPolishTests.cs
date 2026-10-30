using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Mcp;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// The second blind-agent report, item by item: chord suffixes that bind to one
/// note, results that echo envelopes, track removal said once, a clean slate,
/// level help, and get_project JSON that pastes back into a patch.
/// </summary>
public class McpPolishTests
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
    public void ChordSuffixesOnTheLastNoteAloneAreFlaggedAndLegitimateOnesAreNot()
    {
        var flagged = PatternLint.DescribeTrailingSuffix("A3+C4+E4@0.7");
        Assert.Equal(("E4", "@0.7", "A3@0.7+C4@0.7+E4@0.7"), flagged);
        Assert.Equal("A3:8@0.5+C4:8@0.5", PatternLint.DescribeTrailingSuffix("A3+C4:8@0.5")?.Suggestion);

        Assert.Null(PatternLint.DescribeTrailingSuffix("C4:8@0.6+E4"));
        Assert.Null(PatternLint.DescribeTrailingSuffix("A3@0.7+C4@0.7+E4@0.7"));
        Assert.Null(PatternLint.DescribeTrailingSuffix("E4@0.7"));
        Assert.Null(PatternLint.DescribeTrailingSuffix("x@0.4"));

        var warnings = PatternLint.ChordSuffixWarnings(new[] { "A3+C4+E4@0.7", ".", ".", "." }, "Chords, ");
        Assert.Single(warnings);
        Assert.StartsWith("Chords, step 0: \"A3+C4+E4@0.7\" applies \"@0.7\" to E4 only", warnings[0]);
    }

    [Fact]
    public async Task EveryPatternToolWarnsAboutChordSuffixesAndPatchesEchoEnvelopes()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();

        var (placed, _) = await CallToolAsync(dispatcher, "arrangement_place_clip", new JsonObject
        {
            ["track"] = "Thought Leader Chords",
            ["bar"] = 1,
            ["bars"] = 2,
            ["pattern"] = "A3+C4+E4@0.7 . . . . . . . . . . . . . . .",
        });
        Assert.Contains("for the whole chord write \"A3@0.7+C4@0.7+E4@0.7\"", placed);

        var (sessionWritten, _) = await CallToolAsync(dispatcher, "set_clip", new JsonObject { ["track"] = "Thought Leader Chords", ["scene"] = 0, ["pattern"] = "F3+A3+C4@0.5 . . ." });
        Assert.Contains("applies \"@0.5\" to C4 only", sessionWritten);

        var (patched, patchFailed) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject
        {
            ["patch"] = new JsonObject
            {
                ["timeline"] = new JsonArray(new JsonObject
                {
                    ["id"] = "new",
                    ["track"] = "Bottom-Line Bass",
                    ["start"] = 64,
                    ["length"] = 128,
                    ["pattern"] = "A1+E2@0.6 . . .",
                    ["automation"] = new JsonObject { ["fx.0.cutoff"] = JsonNode.Parse("[[0, 700], [128, 6000]]") },
                }),
            },
        });
        Assert.False(patchFailed, patched);
        Assert.Contains("envelopes: fx.0.cutoff 0:700 → 128:6000", patched);
        Assert.Contains("timeline[0] (\"Bottom-Line Bass\"), step 0: \"A1+E2@0.6\"", patched);
        Assert.Equal(2, servedDocument.Project.Timeline.Count);
    }

    [Fact]
    public async Task RemovingATrackSaysOnceThatItsClipsWentWithIt()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        var bass = servedDocument.Project.Tracks.Single(track => track.Name == "Bottom-Line Bass");
        await CallToolAsync(dispatcher, "arrangement_place_clip", new JsonObject { ["track"] = bass.Name, ["bar"] = 1, ["pattern"] = "A1 . . ." });
        await CallToolAsync(dispatcher, "arrangement_place_clip", new JsonObject { ["track"] = bass.Name, ["bar"] = 3, ["pattern"] = "A1 . . ." });

        var (removed, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject
        {
            ["patch"] = new JsonObject { ["tracks"] = new JsonArray(new JsonObject { ["id"] = bass.Id, ["remove"] = true }) },
        });

        Assert.Contains("removed track \"Bottom-Line Bass\" and its 2 arrangement clips", removed);
        Assert.DoesNotContain("arrangement: removed", removed);
        Assert.DoesNotContain("adjusted by the project checker", removed);
        Assert.DoesNotContain("misalignment", removed);
        Assert.Empty(servedDocument.Project.Timeline);
    }

    [Fact]
    public async Task NewProjectAndResetStartFromAnEmptyProjectInOneUndoableStep()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        await CallToolAsync(dispatcher, "arrangement_place_clip", new JsonObject { ["track"] = "Synergy Kick", ["bar"] = 1, ["pattern"] = "x . . ." });

        var (cleared, clearFailed) = await CallToolAsync(dispatcher, "new_project", new JsonObject { ["name"] = "Fresh", ["bpm"] = 140 });
        Assert.False(clearFailed, cleared);
        Assert.StartsWith("new empty project \"Fresh\" at 140 BPM", cleared);
        Assert.Empty(servedDocument.Project.Tracks);
        Assert.Empty(servedDocument.Project.Timeline);
        Assert.Equal(new[] { "Scene 1" }, servedDocument.Project.Scenes);
        Assert.Equal(new MasterChainParameters().Gain, servedDocument.Project.Master.Gain);

        Assert.Contains("undid", (await CallToolAsync(dispatcher, "undo", new JsonObject())).Text);
        Assert.NotEmpty(servedDocument.Project.Tracks);

        var (reset, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject
        {
            ["patch"] = new JsonObject
            {
                ["reset"] = true,
                ["bpm"] = 100,
                ["tracks"] = new JsonArray(new JsonObject { ["id"] = "new", ["name"] = "Kick", ["instrument"] = "kick" }),
                ["timeline"] = new JsonArray(new JsonObject { ["id"] = "new", ["track"] = "Kick", ["start"] = 0, ["length"] = 64, ["pattern"] = "x . . . x . . . x . . . x . . ." }),
            },
        });
        Assert.DoesNotContain("ignored unknown key", reset);
        Assert.Equal(new[] { "Kick" }, servedDocument.Project.Tracks.Select(track => track.Name));
        Assert.Single(servedDocument.Project.Timeline);
        Assert.Single(servedDocument.Project.Scenes);
        Assert.Equal(100, servedDocument.Project.Bpm);
    }

    [Fact]
    public async Task GetProjectJsonArrangementClipsPasteBackIntoAPatch()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        await CallToolAsync(dispatcher, "arrangement_place_clip", new JsonObject { ["track"] = "Synergy Kick", ["bar"] = 1, ["bars"] = 2, ["pattern"] = "x . . . x . . ." });
        var json = JsonNode.Parse((await CallToolAsync(dispatcher, "get_project", new JsonObject { ["format"] = "json" })).Text)!;
        var exportedClip = json["timeline"]![0]!.AsObject();
        Assert.True(exportedClip.ContainsKey("startStep") && exportedClip.ContainsKey("trackId"));

        var pastedClip = exportedClip.DeepClone().AsObject();
        pastedClip["id"] = "new";
        pastedClip["startStep"] = 64;
        var (patched, _) = await CallToolAsync(dispatcher, "apply_patch", new JsonObject { ["patch"] = new JsonObject { ["timeline"] = new JsonArray(pastedClip) } });

        Assert.DoesNotContain("warnings", patched);
        var copy = servedDocument.Project.Timeline.Single(timelineClip => timelineClip.StartStep == 64);
        Assert.Equal((32, "x"), (copy.LengthSteps, copy.Clip.Pattern[0]));
        Assert.Equal(servedDocument.Project.Tracks[0].Id, copy.TrackId);
    }

    [Fact]
    public async Task LevelHelpNamesQuietTracksAndCatalogAndPluginsTalkAboutLevels()
    {
        var (dispatcher, servedDocument) = CreateHeadlessServer();
        foreach (var track in servedDocument.Project.Tracks.Take(4))
        {
            await CallToolAsync(dispatcher, "arrangement_place_clip", new JsonObject
            {
                ["track"] = track.Name,
                ["bar"] = 1,
                ["bars"] = 8,
                ["pattern"] = InstrumentVoiceSynthesizer.IsPercussive(track.Instrument) ? "x . . . x . . . x . . . x . . ." : "A2:4 . . . C3:4 . . . E3:4 . . . A2:4 . . .",
            });
        }
        servedDocument.Project.Tracks[2].Gain = 0.02;
        var renderPath = Path.Combine(Path.GetTempPath(), $"slaw-levels-{Guid.NewGuid():N}.wav");
        try
        {
            var (rendered, renderFailed) = await CallToolAsync(dispatcher, "render", new JsonObject { ["path"] = renderPath, ["levels"] = true });
            Assert.False(renderFailed, rendered);
            Assert.Contains("mix RMS by bars (dBFS): bars 1–4 ", rendered);
            Assert.Contains("bars 5–8 ", rendered);
            Assert.Contains($"balance: \"{servedDocument.Project.Tracks[2].Name}\" is ", rendered);
            Assert.Contains("below the median track", rendered);
        }
        finally
        {
            File.Delete(renderPath);
        }

        var (catalog, _) = await CallToolAsync(dispatcher, "get_catalog", new JsonObject());
        Assert.Contains("Built-in instrument levels at gain 1", catalog);
        Assert.Contains("kick ", catalog);
        Assert.Contains("bind to the single note they follow", catalog);
        Assert.Contains("the knob's own value applies", catalog);
    }

    [Fact]
    public void PluginRolesComeFromTheirSubcategories()
    {
        static SlopstackLabs.Slaw.Plugins.Vst3PluginDescriptor Plugin(string subCategories, bool instrument) =>
            new("/x.vst3", new string('A', 32), "X", "Vendor", subCategories, instrument, "/");
        Assert.Equal("synth", SlawMcpToolset.DescribePluginRole(Plugin("Instrument|Synth", true)));
        Assert.Equal("drum, sampler", SlawMcpToolset.DescribePluginRole(Plugin("Instrument|Drum|Sampler", true)));
        Assert.Equal("reverb", SlawMcpToolset.DescribePluginRole(Plugin("Fx|Reverb|Stereo", false)));
        Assert.Equal("not declared", SlawMcpToolset.DescribePluginRole(Plugin("Instrument", true)));
    }

    [Fact]
    public void ClipListingsSaySingularBars()
    {
        var oneBar = new TimelineClip { StartStep = 0, LengthSteps = 16, Clip = new SlawClip { Pattern = Enumerable.Repeat(".", 16).ToList() } };
        Assert.Contains("= 1 bar,", FreeTimeline.DescribeSpan(oneBar));
    }

    [Fact]
    public void AfterAnEnvelopesClipEndsTheKnobValueAppliesAgain()
    {
        // Two consecutive arrangement clips on one track: the first sweeps the filter, the second has no envelope.
        static SlawProject TwoClipProject(bool firstClipAutomated)
        {
            var project = ProjectSerializer.CreateBlankProject("fallback", 120);
            var drone = new SlawTrack
            {
                Name = "Drone",
                Instrument = "saw",
                Gain = 1,
                InstrumentParameters = new Dictionary<string, double> { ["sustain"] = 1, ["release"] = 5 },
                Effects = new List<EffectDevice> { new() { Type = "lowpass", Parameters = { ["cutoff"] = 400, ["resonance"] = 0.2 } } },
            };
            project.Tracks.Add(drone);
            var firstClip = new SlawClip { Pattern = CompactPatternJsonConverter.SplitStepString("A2:16 . . . . . . . . . . . . . . .") };
            if (firstClipAutomated)
            {
                firstClip.Automation = new() { ["fx.0.cutoff"] = new() { new() { Step = 0, Value = 400 }, new() { Step = 16, Value = 9000 } } };
            }
            project.Timeline.Add(new TimelineClip { TrackId = drone.Id, StartStep = 0, LengthSteps = 16, Clip = firstClip });
            project.Timeline.Add(new TimelineClip { TrackId = drone.Id, StartStep = 16, LengthSteps = 16, Clip = new SlawClip { Pattern = CompactPatternJsonConverter.SplitStepString("A2:16 . . . . . . . . . . . . . . .") } });
            return project;
        }

        const int sampleRate = 22050;
        var automated = OfflineArrangementRenderer.Render(TwoClipProject(firstClipAutomated: true), sampleRate).Left;
        var static_ = OfflineArrangementRenderer.Render(TwoClipProject(firstClipAutomated: false), sampleRate).Left;
        // Bar 2 (0.25 s in, after the filter state settles) sounds as if the envelope never existed.
        var secondBarStart = (int)(2.25 * sampleRate);
        var difference = Enumerable.Range(secondBarStart, sampleRate / 2).Max(frameIndex => Math.Abs(automated[frameIndex] - static_[frameIndex]));
        Assert.True(difference < 1e-3, $"bar 2 differs by {difference}");
        // And the envelope really did something in bar 1.
        Assert.True(Enumerable.Range(sampleRate, sampleRate / 2).Max(frameIndex => Math.Abs(automated[frameIndex] - static_[frameIndex])) > 0.01);
    }
}
