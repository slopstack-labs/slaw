using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The free timeline: clips that answer to no scene.</summary>
public class FreeTimelineTests
{
    private const int TestSampleRate = 22050;

    private static SlawProject OneTrackProject(string instrument = "kick")
    {
        var track = new SlawTrack { Name = "Track", Instrument = instrument, Gain = 1, Clips = new List<SlawClip?> { null } };
        return new SlawProject
        {
            Bpm = 120,
            Scenes = new List<string> { "A" },
            Arrangement = new List<ArrangementSection> { new() { Scene = 0, Repeats = 1 } },
            Tracks = new List<SlawTrack> { track },
        };
    }

    private static TimelineClip Place(SlawProject project, int startStep, int lengthSteps, string pattern) => new()
    {
        TrackId = project.Tracks[0].Id,
        StartStep = startStep,
        LengthSteps = lengthSteps,
        Clip = new SlawClip { Name = "clip", Pattern = CompactPatternJsonConverter.SplitStepString(pattern) },
    };

    /// <summary>Steps at which a block-by-block engine run first produces sound after silence.</summary>
    private static List<long> OnsetSteps(SlawProject project, Action<SequencerEngine> start, int stepCount)
    {
        var engine = new SequencerEngine(TestSampleRate);
        engine.PublishProject(project);
        start(engine);
        var stepFrames = (int)Math.Round(project.StepDurationSeconds * TestSampleRate);
        var left = new float[stepFrames];
        var right = new float[stepFrames];
        var onsets = new List<long>();
        var previousPeak = 0f;
        for (var step = 0; step < stepCount; step++)
        {
            engine.RenderBlock(left, right);
            var peak = left.Take(200).Max(Math.Abs);
            if (peak > 0.05f && peak > previousPeak * 2)
            {
                onsets.Add(step);
            }
            previousPeak = left.Skip(stepFrames - 200).Max(Math.Abs);
        }
        return onsets;
    }

    [Fact]
    public void PlaysAClipAtItsStartAndLoopsItsPatternInsideItsLength()
    {
        var project = OneTrackProject();
        // A 4-step pattern ("x . . .") stretched over 12 steps, starting at step 8: hits at 8, 12, 16.
        project.Timeline.Add(Place(project, 8, 12, "x . . ."));
        var onsets = OnsetSteps(project, engine => engine.PlayTimeline(loop: false), 24);
        Assert.Equal(new long[] { 8, 12, 16 }, onsets);
    }

    [Fact]
    public void LaterStartingClipWinsWhereClipsOverlap()
    {
        var project = OneTrackProject();
        var longClip = Place(project, 0, 16, "x . . .");
        var lateClip = Place(project, 6, 4, ". . x .");
        project.Timeline.AddRange(new[] { lateClip, longClip });
        var trackClips = FreeTimeline.ClipsForTrack(project, project.Tracks[0].Id);

        Assert.Same(longClip, FreeTimeline.ClipAt(trackClips, 5));
        Assert.Same(lateClip, FreeTimeline.ClipAt(trackClips, 6));
        Assert.Same(lateClip, FreeTimeline.ClipAt(trackClips, 9));
        Assert.Same(longClip, FreeTimeline.ClipAt(trackClips, 10));
        Assert.Null(FreeTimeline.ClipAt(trackClips, 16));
        // The covered hit at step 8 is replaced by the late clip's hit at 6 + 2.
        Assert.Equal(new long[] { 0, 4, 8, 12 }, OnsetSteps(project, engine => engine.PlayTimeline(loop: false), 16));
    }

    [Fact]
    public void LoopBraceWrapsLivePlayback()
    {
        var project = OneTrackProject();
        project.Timeline.Add(Place(project, 0, 32, "x . . . . . . . . . . . . . . ."));
        project.TimelineLoop = new TimelineLoopRegion { StartStep = 16, EndStep = 32 };
        // 0, then the second bar's downbeat at 16, then the brace sends it back to 16 every 16 steps.
        Assert.Equal(new long[] { 0, 16, 32, 48 }, OnsetSteps(project, engine => engine.PlayTimeline(), 56));
    }

    [Fact]
    public void ClipEnvelopesFollowTheTimelineClip()
    {
        var project = OneTrackProject("sine");
        project.Tracks[0].InstrumentParameters = new Dictionary<string, double> { ["sustain"] = 1, ["release"] = 5 };
        var fadingClip = Place(project, 0, 16, "A3 " + string.Join(' ', Enumerable.Repeat("_", 15)));
        fadingClip.Clip.Automation = new Dictionary<string, List<AutomationPoint>>
        {
            [ClipAutomation.GainTarget] = new() { new() { Step = 0, Value = 0 }, new() { Step = 16, Value = 1 } },
        };
        project.Timeline.Add(fadingClip);
        var rendered = OfflineArrangementRenderer.RenderTimeline(project, TestSampleRate).Left;
        var window = TestSampleRate / 8;
        var openingPeak = rendered.Skip(100).Take(window).Max(Math.Abs);
        var closingPeak = rendered.Skip((int)(1.6 * TestSampleRate) - window).Take(window).Max(Math.Abs);
        Assert.True(closingPeak > openingPeak * 5, $"closing {closingPeak} vs opening {openingPeak}");
    }

    [Fact]
    public void RenderedTimelineLastsUntilItsLastClipEnds()
    {
        var project = OneTrackProject();
        project.Timeline.Add(Place(project, 32, 16, "x . . ."));
        var rendered = OfflineArrangementRenderer.RenderTimeline(project, TestSampleRate);
        // 48 steps at 120 BPM (0.125 s each) = 6 s, plus the 1.5 s release tail; nothing before the clip at 4 s.
        Assert.InRange(rendered.DurationSeconds, 7.49, 7.51);
        Assert.Equal(0f, rendered.Left.Take(TestSampleRate * 4 - 100).Max(Math.Abs));
        Assert.True(rendered.Left.Skip(TestSampleRate * 4).Take(TestSampleRate / 4).Max(Math.Abs) > 0.05f);
    }

    [Fact]
    public void FlattensTheArrangementIntoOneClipPerTrackPerSection()
    {
        var project = ProjectSerializer.CreateStarterProject("flatten");
        var flattened = FreeTimeline.FlattenArrangement(project);

        var expectedClipCount = project.Arrangement.Sum(section => project.Tracks.Count(track => track.ClipAt(section.Scene) is not null));
        Assert.Equal(expectedClipCount, flattened.Count);
        var sectionStart = 0;
        foreach (var section in project.Arrangement)
        {
            var sectionLength = project.SceneLengthInSteps(section.Scene) * section.Repeats;
            Assert.All(flattened.Where(timelineClip => timelineClip.StartStep == sectionStart), timelineClip => Assert.Equal(sectionLength, timelineClip.LengthSteps));
            sectionStart += sectionLength;
        }
        Assert.Equal(project.ArrangementLengthInSteps(), flattened.Max(timelineClip => timelineClip.EndStep));
        // Copies, not references: editing the timeline leaves the scenes alone.
        flattened[0].Clip.Pattern[0] = "C7";
        Assert.NotEqual("C7", project.Tracks.Single(track => track.Id == flattened[0].TrackId).Clips.OfType<SlawClip>().First().Pattern[0]);
    }

    [Fact]
    public void RoundTripsAndStaysOutOfSceneOnlyFiles()
    {
        var sceneOnly = OneTrackProject();
        Assert.DoesNotContain("timeline", ProjectSerializer.Serialize(sceneOnly));

        var project = OneTrackProject();
        project.Timeline.Add(Place(project, 4, 8, "x . x ."));
        project.TimelineLoop = new TimelineLoopRegion { StartStep = 0, EndStep = 16 };
        var reloaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        var reloadedClip = Assert.Single(reloaded.Timeline);
        Assert.Equal((4, 8, "x"), (reloadedClip.StartStep, reloadedClip.LengthSteps, reloadedClip.Clip.Pattern[2]));
        Assert.Equal(16, reloaded.TimelineLoop!.EndStep);
    }

    [Fact]
    public void RemediatorDropsOrphansAndSquaresUpClips()
    {
        var project = OneTrackProject();
        project.Timeline.Add(new TimelineClip { TrackId = "ghost", Clip = new SlawClip { Pattern = new List<string> { "x" } } });
        project.Timeline.Add(new TimelineClip { TrackId = project.Tracks[0].Id, StartStep = -5, LengthSteps = 0, Clip = new SlawClip { Pattern = new List<string> { "x", "." } } });
        project.TimelineLoop = new TimelineLoopRegion { StartStep = 8, EndStep = 8 };
        var ledger = ProjectAlignmentRemediator.Remediate(project);

        var survivingClip = Assert.Single(project.Timeline);
        Assert.Equal((0, 2), (survivingClip.StartStep, survivingClip.LengthSteps));
        Assert.Null(project.TimelineLoop);
        Assert.Contains(ledger, entry => entry.Contains("timeline"));
    }

    [Fact]
    public void PatchesAddMoveAndRemoveTimelineClips()
    {
        var project = OneTrackProject();
        var added = ProjectPatchApplier.Apply(project, JsonNode.Parse("""
            {"timeline":[{"id":"new","track":"Track","start":16,"length":32,"pattern":"x . . .","automation":{"gain":[[0,0.2],[4,1]]}}]}
            """)!.AsObject()).Project;
        var addedClip = Assert.Single(added.Timeline);
        Assert.Equal((project.Tracks[0].Id, 16, 32), (addedClip.TrackId, addedClip.StartStep, addedClip.LengthSteps));
        Assert.Equal(2, addedClip.Clip.Automation![ClipAutomation.GainTarget].Count);

        var moved = ProjectPatchApplier.Apply(added, JsonNode.Parse($$"""
            {"timeline":[{"id":"{{addedClip.Id}}","start":0,"pattern":"x x . ."}]}
            """)!.AsObject()).Project;
        var movedClip = Assert.Single(moved.Timeline);
        Assert.Equal((0, 32, "x"), (movedClip.StartStep, movedClip.LengthSteps, movedClip.Clip.Pattern[1]));
        Assert.NotNull(movedClip.Clip.Automation);

        var removed = ProjectPatchApplier.Apply(moved, JsonNode.Parse($$"""
            {"timeline":[{"id":"{{addedClip.Id}}","remove":true}]}
            """)!.AsObject()).Project;
        Assert.Empty(removed.Timeline);
    }

    [Fact]
    public async Task AgentsPatchListAndRenderTheTimelineOverMcp()
    {
        var servedDocument = new Desktop.StudioDocument(ProjectSerializer.CreateStarterProject("mcp"), filePath: null);
        var headlessHost = new Mcp.HeadlessMcpHost(servedDocument, new Desktop.StudioPreferences { AiFeaturesEnabled = false });
        var dispatcher = new Mcp.McpProtocolDispatcher(new Mcp.SlawMcpToolset(headlessHost));
        async Task<string> CallToolAsync(string toolName, JsonObject toolArguments)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = toolArguments } };
            var response = JsonNode.Parse((await dispatcher.HandleAsync(request.ToJsonString(), CancellationToken.None))!)!;
            Assert.False(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
            return response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        }

        await CallToolAsync("apply_patch", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""{"timeline":[{"id":"new","track":"Synergy Kick","start":32,"length":16,"pattern":"x . . . x . . ."}]}"""),
        });
        Assert.Equal(32, Assert.Single(servedDocument.Project.Timeline).StartStep);
        Assert.Contains("track \"Synergy Kick\" start 32 (bar 3.1) length 16", await CallToolAsync("get_project", new JsonObject()));

        var renderPath = Path.Combine(Path.GetTempPath(), $"slaw-timeline-test-{Environment.ProcessId}.wav");
        try
        {
            // 48 steps at the starter's 124 BPM plus the release tail.
            Assert.Contains("rendered 7.3 s", await CallToolAsync("render", new JsonObject { ["path"] = renderPath, ["source"] = "timeline" }));
        }
        finally
        {
            File.Delete(renderPath);
        }
    }
}
