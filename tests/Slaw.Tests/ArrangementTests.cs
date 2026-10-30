using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Mcp;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;
using SlopstackLabs.Slaw.Workflows;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>One arrangement (the timeline), a session to jam over it, and the scene sequences of old, migrated.</summary>
public class ArrangementTests
{
    private const int EngineSampleRate = 8000;

    // 120 BPM: 0.125 s per step, 1000 frames per step at 8 kHz, 16000 per bar.
    private const int FramesPerStep = 1000;

    /// <summary>Two tracks: each with one session clip in scene 0 and an eight-bar arrangement clip.</summary>
    private static SlawProject TwoTrackProject()
    {
        static SlawTrack Track(string name, string sessionPattern) => new()
        {
            Name = name,
            Instrument = "sine",
            Clips = new List<SlawClip?> { new() { Name = name, Pattern = CompactPatternJsonConverter.SplitStepString(sessionPattern) } },
        };
        var project = new SlawProject
        {
            Bpm = 120,
            Scenes = new List<string> { "Jam" },
            Tracks = new List<SlawTrack> { Track("Lead", "C5 . . . . . . . . . . . . . . ."), Track("Bass", "C2 . . . . . . . . . . . . . . .") },
        };
        foreach (var track in project.Tracks)
        {
            project.Timeline.Add(new TimelineClip { TrackId = track.Id, StartStep = 0, LengthSteps = 128, Clip = new SlawClip { Name = "song", Pattern = CompactPatternJsonConverter.SplitStepString("A3 . . . . . . . . . . . . . . .") } });
        }
        return project;
    }

    private static void Advance(SequencerEngine engine, int steps)
    {
        var left = new float[FramesPerStep];
        var right = new float[FramesPerStep];
        for (var step = 0; step < steps; step++)
        {
            engine.RenderBlock(left, right);
        }
    }

    [Fact]
    public void LegacySceneSequencesAreFlattenedOntoTheTimelineAndNotWrittenBack()
    {
        var legacyFile = """
            {"format":"slaw-project/v2","name":"old","bpm":120,"steps":16,"scenes":["A","B"],
             "tracks":[{"id":"k1","name":"Kick","instrument":"kick","clips":[{"name":"A","pattern":"x . . . x . . . x . . . x . . ."},{"name":"B","pattern":"x . x ."}]}],
             "arrangement":[{"scene":0,"repeats":2},{"scene":1,"repeats":3}]}
            """;
        var project = ProjectSerializer.Deserialize(legacyFile);
        var legacyRender = OfflineArrangementRenderer.Render(project, EngineSampleRate);

        ProjectAlignmentRemediator.Remediate(project);

        Assert.Empty(project.Arrangement);
        // Scene B lasts as long as its longest clip (4 steps), so three repeats are 12 steps.
        Assert.Equal(new[] { (0, 32), (32, 12) }, project.Timeline.Select(timelineClip => (timelineClip.StartStep, timelineClip.LengthSteps)));
        Assert.DoesNotContain("\"arrangement\"", ProjectSerializer.Serialize(project));
        // The migrated song renders exactly like the legacy one.
        Assert.Equal(legacyRender.Left, OfflineArrangementRenderer.Render(project, EngineSampleRate).Left);

        // An arrangement emptied on purpose stays empty: nothing invents sections any more.
        project.Timeline.Clear();
        ProjectAlignmentRemediator.Remediate(ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)));
        Assert.Empty(ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)).Timeline);
    }

    [Fact]
    public void ALegacyArrangementPatchKeyReplacesTheTimelineWithThoseScenes()
    {
        var project = TwoTrackProject();
        var patched = ProjectPatchApplier.Apply(project, JsonNode.Parse("""{"arrangement":[{"scene":0,"repeats":2}]}""")!.AsObject()).Project;

        Assert.Empty(patched.Arrangement);
        Assert.Equal(2, patched.Timeline.Count);
        Assert.All(patched.Timeline, timelineClip => Assert.Equal((0, 32), (timelineClip.StartStep, timelineClip.LengthSteps)));
        Assert.Equal(patched.Tracks.Select(track => track.Id).Order(), patched.Timeline.Select(timelineClip => timelineClip.TrackId).Order());
    }

    [Fact]
    public void ComposedProjectsWithoutASongOrderPlayEverySceneTwice()
    {
        const string modelOutput = """
            {"name":"Pitch Deck","bpm":100,"scenes":["Intro","Drop"],
             "tracks":[{"name":"Bass","instrument":"saw","clips":[{"pattern":"A1 . . . A1 . . . A1 . . . A1 . . ."},{"pattern":"C2 . C2 . C2 . C2 . C2 . C2 . C2 . C2 ."}]}]}
            """;
        var composedProject = ProductionWorkflows.ParseArrangement(modelOutput, inferenceProvider: null).Project;

        Assert.Equal(new[] { (0, 32), (32, 32) }, composedProject.Timeline.Select(timelineClip => (timelineClip.StartStep, timelineClip.LengthSteps)));
    }

    [Fact]
    public void ALaunchedSessionClipTakesItsTrackOverUntilBackToArrangement()
    {
        var project = TwoTrackProject();
        var lead = project.Tracks[0];
        var bass = project.Tracks[1];
        var engine = new SequencerEngine(EngineSampleRate);
        engine.PublishProject(project);
        engine.PlayTimeline();
        Advance(engine, 3);
        Assert.NotNull(engine.Transport.Tracks[lead.Id].TimelineClipId);

        // Quantized: still the arrangement until the next bar.
        engine.LaunchClip(lead.Id, 0);
        Advance(engine, 2);
        Assert.False(engine.Transport.Tracks[lead.Id].OverridesArrangement);
        Advance(engine, 14);
        var leadState = engine.Transport.Tracks[lead.Id];
        Assert.True(leadState.OverridesArrangement);
        Assert.Equal(0, leadState.PlayingScene);
        Assert.Null(leadState.TimelineClipId);
        Assert.True(engine.Transport.ArrangementOverridden);
        // The other track keeps following the arrangement.
        Assert.False(engine.Transport.Tracks[bass.Id].OverridesArrangement);
        Assert.NotNull(engine.Transport.Tracks[bass.Id].TimelineClipId);

        // Back to Arrangement, also on the bar.
        engine.BackToArrangement();
        Advance(engine, 1);
        Assert.True(engine.Transport.ReturnToArrangementQueued);
        Assert.True(engine.Transport.Tracks[lead.Id].OverridesArrangement);
        Advance(engine, 16);
        Assert.False(engine.Transport.ArrangementOverridden);
        Assert.NotNull(engine.Transport.Tracks[lead.Id].TimelineClipId);
    }

    [Fact]
    public void StoppingOrLaunchingAScenesOverTheArrangementFollowsAbletonsRules()
    {
        var project = TwoTrackProject();
        project.Tracks[1].Clips[0] = null;
        var lead = project.Tracks[0];
        var bass = project.Tracks[1];
        var engine = new SequencerEngine(EngineSampleRate);
        engine.PublishProject(project);
        engine.PlayTimeline();
        Advance(engine, 1);

        // A scene only takes over the tracks it has clips for.
        engine.LaunchScene(0);
        Advance(engine, 16);
        Assert.True(engine.Transport.Tracks[lead.Id].OverridesArrangement);
        Assert.False(engine.Transport.Tracks[bass.Id].OverridesArrangement);

        // Stopping a track over the arrangement silences it until Back to Arrangement.
        engine.StopTrack(bass.Id);
        Advance(engine, 16);
        var bassState = engine.Transport.Tracks[bass.Id];
        Assert.True(bassState.OverridesArrangement);
        Assert.Null(bassState.PlayingScene);
        Assert.Null(bassState.TimelineClipId);

        // Stop forgets every override; playing again starts from the arrangement.
        engine.Stop();
        engine.PlayTimeline();
        Advance(engine, 1);
        Assert.False(engine.Transport.ArrangementOverridden);
    }

    [Fact]
    public void ScenesAndClipsArePlacedInTheArrangementAtTheCursor()
    {
        var document = new StudioDocument(TwoTrackProject(), filePath: null);
        document.Project.Timeline.Clear();

        Assert.Equal(2, document.PlaceSceneInArrangement(0, startStep: 48));
        Assert.All(document.Project.Timeline, timelineClip => Assert.Equal((48, 16), (timelineClip.StartStep, timelineClip.LengthSteps)));
        Assert.True(document.PlaceClipInArrangement(0, 0, startStep: 64));
        Assert.Equal(3, document.Project.Timeline.Count);

        document.Undo();
        document.Undo();
        Assert.Empty(document.Project.Timeline);
    }

    [Fact]
    public void DuplicatingATrackCopiesItsSessionAndArrangementClipsWithFreshIds()
    {
        var document = new StudioDocument(TwoTrackProject(), filePath: null);
        var lead = document.Project.Tracks[0];
        var leadTimelineClip = document.Project.Timeline.Single(timelineClip => timelineClip.TrackId == lead.Id);

        document.DuplicateTrack(0);

        var copy = document.Project.Tracks[1];
        Assert.Equal("Lead (copy)", copy.Name);
        Assert.NotEqual(lead.Id, copy.Id);
        Assert.Equal(lead.Clips[0]!.Pattern, copy.Clips[0]!.Pattern);
        Assert.NotSame(lead.Clips[0], copy.Clips[0]);
        var copiedTimelineClip = Assert.Single(document.Project.Timeline, timelineClip => timelineClip.TrackId == copy.Id);
        Assert.NotEqual(leadTimelineClip.Id, copiedTimelineClip.Id);
        Assert.Equal((leadTimelineClip.StartStep, leadTimelineClip.LengthSteps), (copiedTimelineClip.StartStep, copiedTimelineClip.LengthSteps));
        Assert.Equal(1, document.SelectedTrackIndex);
        Assert.Equal(3, document.Project.Timeline.Count);
    }

    [Fact]
    public void DeletingATrackTakesItsArrangementClipsAndUndoBringsThemBack()
    {
        var document = new StudioDocument(TwoTrackProject(), filePath: null);
        var lead = document.Project.Tracks[0];

        document.RemoveTrack(0);
        Assert.DoesNotContain(document.Project.Tracks, track => track.Id == lead.Id);
        Assert.DoesNotContain(document.Project.Timeline, timelineClip => timelineClip.TrackId == lead.Id);

        document.Undo();
        Assert.Equal(lead.Id, document.Project.Tracks[0].Id);
        Assert.Single(document.Project.Timeline, timelineClip => timelineClip.TrackId == lead.Id);
    }

    [Fact]
    public async Task McpRendersTheArrangementByDefaultAndListsItAsTheSong()
    {
        var document = new StudioDocument(TwoTrackProject(), filePath: null);
        var dispatcher = new McpProtocolDispatcher(new SlawMcpToolset(new HeadlessMcpHost(document, new StudioPreferences { AiFeaturesEnabled = false })));
        async Task<JsonNode> CallToolAsync(string toolName, JsonObject toolArguments)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = toolArguments } };
            return JsonNode.Parse((await dispatcher.HandleAsync(request.ToJsonString(), CancellationToken.None))!)!["result"]!;
        }
        static string TextOf(JsonNode result) => result["content"]![0]!["text"]!.GetValue<string>();

        var listing = TextOf(await CallToolAsync("get_project", new JsonObject()));
        Assert.Contains("ends at bar 9.1 = 8 bars", listing);
        Assert.DoesNotContain("arrangement: scene", listing);

        var renderPath = Path.Combine(Path.GetTempPath(), $"slaw-arrangement-test-{Environment.ProcessId}.wav");
        try
        {
            // Eight bars at 120 BPM plus the release tail.
            Assert.Contains("8 bars at 120 BPM = 16.0 s of music + 1.5 s release tail = 17.5 s", TextOf(await CallToolAsync("render", new JsonObject { ["path"] = renderPath })));
        }
        finally
        {
            File.Delete(renderPath);
        }

        // The headless server has no transport; an empty arrangement refuses to render.
        Assert.True((await CallToolAsync("transport", new JsonObject { ["action"] = "play_arrangement" }))["isError"]!.GetValue<bool>());
        document.Project.Timeline.Clear();
        var emptyRender = await CallToolAsync("render", new JsonObject { ["path"] = renderPath });
        Assert.True(emptyRender["isError"]!.GetValue<bool>());
        Assert.Contains("arrangement is empty", TextOf(emptyRender));
    }
}
