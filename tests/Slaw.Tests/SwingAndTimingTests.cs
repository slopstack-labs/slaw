using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Mcp;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;
using SlopstackLabs.Slaw.Workflows;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// Groove enablement: MPC-style swing (project-wide, overridable per track) and per-track timing
/// humanize, validated on behalf of regulators who still believe in the straight grid.
/// </summary>
public class SwingAndTimingTests
{
    private const int TestSampleRate = 22050;

    /// <summary>A short hat (onsets stay separable) on the arrangement and in session scene 0, at 60 BPM: one step is 5512.5 frames.</summary>
    private static SlawProject HatProject(string pattern, double projectSwing = GrooveTiming.StraightSwing, double? trackSwing = null, double timing = 0, string instrument = "hat")
    {
        var steps = CompactPatternJsonConverter.SplitStepString(pattern);
        var track = new SlawTrack
        {
            Name = "Hats",
            Instrument = instrument,
            Gain = 1,
            Swing = trackSwing,
            Timing = timing,
            InstrumentParameters = instrument == "hat" ? new Dictionary<string, double> { ["decay"] = 0.25 } : new Dictionary<string, double>(),
            Clips = new List<SlawClip?> { new() { Name = "hats", Pattern = steps } },
        };
        var project = ProjectSerializer.CreateBlankProject("groove probe", 60);
        project.Swing = projectSwing;
        project.Scenes = new List<string> { "A" };
        project.Tracks.Add(track);
        project.Timeline.Add(new TimelineClip { TrackId = track.Id, StartStep = 0, LengthSteps = steps.Count, Clip = new SlawClip { Name = "hats", Pattern = steps.ToList() } });
        return project;
    }

    private static double StepFrames(SlawProject project) => project.StepDurationSeconds * TestSampleRate;

    /// <summary>The engine starts step k on the first whole frame at or after k steps.</summary>
    private static int StraightStepStart(SlawProject project, int stepIndex) => (int)Math.Ceiling(stepIndex * StepFrames(project) - 1e-9);

    /// <summary>Frames where sound starts after at least <paramref name="minimumSilence"/> frames of (near) silence.</summary>
    private static List<int> Onsets(float[] samples, int minimumSilence = 200)
    {
        var onsets = new List<int>();
        var silentRun = minimumSilence;
        for (var frameIndex = 0; frameIndex < samples.Length; frameIndex++)
        {
            if (Math.Abs(samples[frameIndex]) > 1e-4f)
            {
                if (silentRun >= minimumSilence)
                {
                    onsets.Add(frameIndex);
                }
                silentRun = 0;
            }
            else
            {
                silentRun++;
            }
        }
        return onsets;
    }

    /// <summary>Runs the live engine block by block (odd block size on purpose); returns both channels.</summary>
    private static (float[] Left, float[] Right) RenderLive(SlawProject project, Action<SequencerEngine> start, int frameCount, int blockFrames = 333)
    {
        var engine = new SequencerEngine(TestSampleRate);
        engine.PublishProject(project);
        start(engine);
        var left = new float[frameCount];
        var right = new float[frameCount];
        for (var blockStart = 0; blockStart < frameCount; blockStart += blockFrames)
        {
            var blockLength = Math.Min(blockFrames, frameCount - blockStart);
            engine.RenderBlock(left.AsSpan(blockStart, blockLength), right.AsSpan(blockStart, blockLength));
        }
        return (left, right);
    }

    // ── Semantics ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(50, 0)]
    [InlineData(40, 0)]
    [InlineData(58, 0.16)]
    [InlineData(200.0 / 3, 1.0 / 3)]
    [InlineData(75, 0.5)]
    [InlineData(90, 0.5)]
    public void SwingPercentMapsToAFractionOfAStep(double swingPercent, double expectedDelaySteps) =>
        Assert.Equal(expectedDelaySteps, GrooveTiming.SwingDelaySteps(swingPercent), 9);

    [Fact]
    public void StraightGrooveReproducesThePreSwingArithmetic()
    {
        const double stepFrameCount = 5512.5;
        var straight = new SequencerEngine.StepGroove(0, 7, 0);
        for (var strikeCount = 1; strikeCount <= 8; strikeCount++)
        {
            for (var strikeIndex = 0; strikeIndex < strikeCount; strikeIndex++)
            {
                Assert.Equal(SequencerEngine.StrikeOffsetFrames(strikeIndex, strikeCount, stepFrameCount), straight.StrikeOffsetFrames(strikeIndex, strikeCount, stepFrameCount));
                var finalStrike = strikeIndex == strikeCount - 1;
                var previousHeldSteps = strikeCount == 1 ? 3 : finalStrike ? 3 - strikeIndex / (double)strikeCount : 1.0 / strikeCount;
                Assert.Equal(previousHeldSteps, straight.HeldSteps(3, strikeIndex, strikeCount));
            }
        }

        // Swung: an odd step starts a third late and lasts two thirds; the even step before it lasts four thirds.
        var swungOdd = new SequencerEngine.StepGroove(1.0 / 3, 1, 0);
        var swungEven = new SequencerEngine.StepGroove(1.0 / 3, 2, 0);
        Assert.Equal(1.0 / 3, swungOdd.StartSteps, 9);
        Assert.Equal(2.0 / 3, swungOdd.SpanSteps, 9);
        Assert.Equal(4.0 / 3, swungEven.SpanSteps, 9);
        Assert.Equal(4.0 / 3, swungEven.NoteEndSteps(1), 9);
        Assert.Equal(2.0, swungEven.NoteEndSteps(2), 9);
        Assert.Equal(1.0 - 1.0 / 3, swungOdd.HeldSteps(1, 0, 1), 9);
    }

    // ── Engine ────────────────────────────────────────────────────────────────

    [Fact]
    public void StraightSwingAndZeroTimingRenderTheSameBytesAsNoGrooveAtAll()
    {
        var untouched = HatProject("x x*2 x x*3 . x x x");
        untouched.Tracks.Add(new SlawTrack
        {
            Name = "Bass",
            Instrument = "saw",
            Gain = 0.5,
            Clips = new List<SlawClip?> { new() { Name = "b", Pattern = CompactPatternJsonConverter.SplitStepString("A1 _ C2*2 . G1:3 . . E1") } },
        });
        untouched.Timeline.Add(new TimelineClip { TrackId = untouched.Tracks[1].Id, StartStep = 0, LengthSteps = 8, Clip = untouched.Tracks[1].Clips[0]! });
        var explicitlyStraight = untouched.DeepClone();
        explicitlyStraight.Swing = 50;
        explicitlyStraight.Tracks[0].Swing = 50;
        explicitlyStraight.Tracks[1].Timing = 0;

        Assert.Equal(OfflineArrangementRenderer.Render(untouched, TestSampleRate).Left, OfflineArrangementRenderer.Render(explicitlyStraight, TestSampleRate).Left);
        Assert.Equal(OfflineArrangementRenderer.RenderScene(untouched, 0, 1, TestSampleRate).Left, OfflineArrangementRenderer.RenderScene(explicitlyStraight, 0, 1, TestSampleRate).Left);

        // A project without groove writes no groove keys at all, so existing files stay byte-identical;
        // a straight project swing is left out too (a track's own 50 is kept: it pins the track straight).
        var untouchedJson = ProjectSerializer.Serialize(untouched);
        Assert.DoesNotContain("\"swing\"", untouchedJson);
        Assert.DoesNotContain("\"timing\"", untouchedJson);
        explicitlyStraight.Tracks[0].Swing = null;
        Assert.Equal(untouchedJson, ProjectSerializer.Serialize(explicitlyStraight));
    }

    [Fact]
    public void TripletSwingDelaysOddStepsByAThirdOfAStepInSessionArrangementAndBounce()
    {
        var project = HatProject("x x x x x x x x", projectSwing: GrooveTiming.TripletSwing);
        var stepFrames = StepFrames(project);
        var frameCount = (int)(8 * stepFrames);
        var bounced = OfflineArrangementRenderer.Render(project, TestSampleRate).Left.Take(frameCount).ToArray();
        var onsets = Onsets(bounced);
        Assert.Equal(8, onsets.Count);
        for (var stepIndex = 0; stepIndex < 8; stepIndex++)
        {
            var expectedDelay = stepIndex % 2 == 1 ? stepFrames / 3 : 0;
            Assert.InRange(onsets[stepIndex] - StraightStepStart(project, stepIndex) - expectedDelay, -1, 1);
        }

        var arrangement = RenderLive(project, engine => engine.PlayTimeline(), frameCount).Left;
        var session = RenderLive(project, engine => engine.LaunchClip(project.Tracks[0].Id, 0), frameCount).Left;
        Assert.Equal(onsets, Onsets(arrangement));
        Assert.Equal(onsets, Onsets(session));
        Assert.Equal(bounced, arrangement);

        // A session scene bounce agrees too.
        Assert.Equal(onsets, Onsets(OfflineArrangementRenderer.RenderScene(project, 0, 1, TestSampleRate).Left.Take(frameCount).ToArray()));
    }

    [Fact]
    public void TrackSwingOverridesTheProjectsSwing()
    {
        // Two hat tracks panned hard apart: left keeps its own straight swing, right follows the project's 66.
        var project = HatProject("x x x x", projectSwing: GrooveTiming.TripletSwing, trackSwing: 50);
        project.Tracks[0].Pan = -1;
        var swungTrack = new SlawTrack
        {
            Name = "Shaker",
            Instrument = "hat",
            Gain = 1,
            Pan = 1,
            InstrumentParameters = new Dictionary<string, double> { ["decay"] = 0.25 },
            Clips = new List<SlawClip?> { new() { Name = "s", Pattern = CompactPatternJsonConverter.SplitStepString("x x x x") } },
        };
        project.Tracks.Add(swungTrack);
        project.Timeline.Add(new TimelineClip { TrackId = swungTrack.Id, StartStep = 0, LengthSteps = 4, Clip = swungTrack.Clips[0]! });
        var stepFrames = StepFrames(project);
        var rendered = OfflineArrangementRenderer.Render(project, TestSampleRate);
        var straightOnsets = Onsets(rendered.Left);
        var swungOnsets = Onsets(rendered.Right);
        Assert.Equal(4, straightOnsets.Count);
        Assert.Equal(4, swungOnsets.Count);
        Assert.InRange(straightOnsets[1] - StraightStepStart(project, 1), -1, 1);
        Assert.InRange(swungOnsets[1] - StraightStepStart(project, 1) - stepFrames / 3, -1, 1);
        Assert.Equal(straightOnsets[2], swungOnsets[2]);

        // And the other way round: a straight project, one swung track.
        var trackOnly = HatProject("x x x x", projectSwing: 50, trackSwing: 75);
        var trackOnlyOnsets = Onsets(OfflineArrangementRenderer.Render(trackOnly, TestSampleRate).Left);
        Assert.InRange(trackOnlyOnsets[3] - StraightStepStart(trackOnly, 3) - stepFrames / 2, -1, 1);
    }

    [Fact]
    public void RatchetsSpreadAcrossTheirSwungStep()
    {
        var project = HatProject("x*2 x*2 . .", projectSwing: GrooveTiming.TripletSwing);
        var stepFrames = StepFrames(project);
        var onsets = Onsets(OfflineArrangementRenderer.Render(project, TestSampleRate).Left);
        Assert.Equal(4, onsets.Count);
        // Even step 0 lasts 4/3 of a step: strikes at 0 and 2/3. Odd step 1 runs from 4/3 to 2: strikes at 4/3 and 5/3.
        var expected = new[] { 0.0, 2.0 / 3, 4.0 / 3, 5.0 / 3 };
        for (var strikeIndex = 0; strikeIndex < 4; strikeIndex++)
        {
            Assert.InRange(onsets[strikeIndex] - expected[strikeIndex] * stepFrames, -2, 2);
        }

        var arrangement = RenderLive(project, engine => engine.PlayTimeline(), (int)(4 * stepFrames)).Left;
        Assert.Equal(onsets, Onsets(arrangement));
    }

    [Fact]
    public void AHeldNoteEndsWhereTheStepAfterItReallyStarts()
    {
        static SlawProject Note(double swing)
        {
            var project = HatProject("C3 . . . . . . .", projectSwing: swing, instrument: "sine");
            project.Tracks[0].InstrumentParameters = new Dictionary<string, double> { ["attack"] = 1, ["release"] = 5, ["sustain"] = 1 };
            return project;
        }
        static int LastSound(float[] samples) => Array.FindLastIndex(samples, sample => Math.Abs(sample) > 1e-3f);
        var straight = LastSound(OfflineArrangementRenderer.Render(Note(50), TestSampleRate).Left);
        var swung = LastSound(OfflineArrangementRenderer.Render(Note(75), TestSampleRate).Left);
        // Step 0 is even, so under swing 75 the note runs on to step 1's late start: half a step longer (gated at 92%).
        var stepFrames = StepFrames(Note(50));
        Assert.InRange(swung - straight - 0.5 * stepFrames * 0.92, -30, 30);
    }

    [Fact]
    public void TimingHumanizeIsLateOnlyWithinRangeAndDeterministic()
    {
        var project = HatProject(string.Join(' ', Enumerable.Repeat("x", 16)), timing: 30);
        var stepFrames = StepFrames(project);
        var maximumDelayFrames = 0.030 * TestSampleRate;
        var bounced = OfflineArrangementRenderer.Render(project, TestSampleRate).Left;
        var onsets = Onsets(bounced);
        Assert.Equal(16, onsets.Count);
        var delays = onsets.Select((onset, stepIndex) => onset - StraightStepStart(project, stepIndex)).ToList();
        Assert.All(delays, delay => Assert.InRange(delay, 0, maximumDelayFrames + 1));
        Assert.True(delays.Distinct().Count() > 8, $"delays look fixed, not humanized: {string.Join(", ", delays)}");
        Assert.True(delays.Max() > maximumDelayFrames / 2, "no step was pushed late by more than half the range");

        // The same project renders the same bytes, live or bounced.
        Assert.Equal(bounced, OfflineArrangementRenderer.Render(project, TestSampleRate).Left);
        var live = RenderLive(project, engine => engine.PlayTimeline(), (int)(16 * stepFrames)).Left;
        Assert.Equal(bounced.Take(live.Length), live);

        // Timing humanize uses its own random source: a silent neighbour's timing leaves the gain humanize of other tracks untouched.
        static SlawProject WithNeighbour(double neighbourTiming)
        {
            var humanized = HatProject("x x x x");
            humanized.Tracks[0].InstrumentParameters["humanize"] = 1;
            var neighbour = new SlawTrack { Name = "Timed", Instrument = "hat", Gain = 0, Timing = neighbourTiming, Clips = new List<SlawClip?> { new() { Name = "t", Pattern = CompactPatternJsonConverter.SplitStepString("x x x x") } } };
            humanized.Tracks.Add(neighbour);
            humanized.Timeline.Add(new TimelineClip { TrackId = neighbour.Id, StartStep = 0, LengthSteps = 4, Clip = neighbour.Clips[0]! });
            return humanized;
        }
        Assert.Equal(OfflineArrangementRenderer.Render(WithNeighbour(0), TestSampleRate).Left, OfflineArrangementRenderer.Render(WithNeighbour(20), TestSampleRate).Left);
    }

    [Fact]
    public void SwingAndTimingCombine()
    {
        var project = HatProject("x x x x x x x x", projectSwing: 75, timing: 20);
        var stepFrames = StepFrames(project);
        var onsets = Onsets(OfflineArrangementRenderer.Render(project, TestSampleRate).Left);
        Assert.Equal(8, onsets.Count);
        for (var stepIndex = 0; stepIndex < 8; stepIndex++)
        {
            var lateness = onsets[stepIndex] - StraightStepStart(project, stepIndex) - (stepIndex % 2 == 1 ? stepFrames / 2 : 0);
            Assert.InRange(lateness, -1, 0.020 * TestSampleRate + 1);
        }
    }

    // ── Project format, patches, listings ────────────────────────────────────

    [Fact]
    public void GrooveSurvivesSerializationAndIsRemediated()
    {
        var project = HatProject("x x x x", projectSwing: 62, trackSwing: 66, timing: 12);
        var serialized = ProjectSerializer.Serialize(project);
        Assert.Contains("\"swing\": 62", serialized);
        Assert.Contains("\"swing\": 66", serialized);
        Assert.Contains("\"timing\": 12", serialized);
        var reloaded = ProjectSerializer.Deserialize(serialized);
        Assert.Equal(62, reloaded.Swing);
        Assert.Equal(66, reloaded.Tracks[0].Swing);
        Assert.Equal(12, reloaded.Tracks[0].Timing);
        Assert.Equal(serialized, ProjectSerializer.Serialize(reloaded));
        Assert.Empty(ProjectAlignmentRemediator.Remediate(reloaded));

        var unruly = ProjectSerializer.Deserialize("""{"swing": 0.6, "tracks": [{"name": "Hats", "instrument": "hat", "swing": 90, "timing": 80}, {"name": "Kick", "instrument": "kick", "timing": -3}]}""");
        var ledger = ProjectAlignmentRemediator.Remediate(unruly);
        Assert.Equal(50, unruly.Swing);
        Assert.Equal(75, unruly.Tracks[0].Swing);
        Assert.Equal(GrooveTiming.MaximumTimingMilliseconds, unruly.Tracks[0].Timing);
        Assert.Equal(0, unruly.Tracks[1].Timing);
        Assert.Null(unruly.Tracks[1].Swing);
        Assert.Contains(ledger, entry => entry.StartsWith("swing: 0.6 clamped to 50 (straight)", StringComparison.Ordinal));
        Assert.Contains("Hats.swing: 90 clamped to 75 (the maximum swing)", ledger);
        Assert.Contains("Hats.timing: 80 clamped to 50", ledger);
        Assert.Contains("Kick.timing: -3 clamped to 0", ledger);
    }

    [Fact]
    public void PatchesSetAndClearGrooveAndSayWhatChanged()
    {
        var project = HatProject("x x x x");
        project.Tracks.Add(new SlawTrack { Name = "Kick", Instrument = "kick", Swing = 58, Clips = new List<SlawClip?> { null } });
        var patch = JsonNode.Parse("""{"swing": 62, "tracks": [{"id": "Hats", "swing": 66, "timing": 10}, {"id": "Kick", "swing": null}]}""")!.AsObject();
        var outcome = ProjectPatchApplier.Apply(project, patch);
        Assert.Empty(outcome.Warnings);
        ProjectAlignmentRemediator.Remediate(outcome.Project);
        Assert.Equal(62, outcome.Project.Swing);
        Assert.Equal(66, outcome.Project.Tracks[0].Swing);
        Assert.Equal(10, outcome.Project.Tracks[0].Timing);
        Assert.Null(outcome.Project.Tracks[1].Swing);

        var summary = ProjectChangeSummarizer.Summarize(project, outcome.Project);
        Assert.Contains(summary, line => line.StartsWith("swing 50 (straight) → 62 (project", StringComparison.Ordinal));
        Assert.Contains("Hats: swing (project's) → 66, timing 0 → 10 ms", summary);
        Assert.Contains("Kick: swing 58 → (project's)", summary);

        // The listing agents and the autopilot read shows the groove.
        var listing = PromptLibrary.DescribeProjectForPatching(outcome.Project);
        Assert.Contains("bpm 60, swing 62\n", listing);
        Assert.Contains("pan 0.00 swing 66 timing 10ms", listing);

        // "swing": null on the project straightens it.
        var straightened = ProjectPatchApplier.Apply(outcome.Project, JsonNode.Parse("""{"swing": null}""")!.AsObject()).Project;
        Assert.Equal(50, straightened.Swing);
    }

    [Fact]
    public async Task McpAgentsCanSwingATrackAndHearAboutClamping()
    {
        var servedDocument = new StudioDocument(ProjectSerializer.CreateStarterProject("mcp"), filePath: null);
        var dispatcher = new McpProtocolDispatcher(new SlawMcpToolset(new HeadlessMcpHost(servedDocument, new StudioPreferences { AiFeaturesEnabled = false })));
        async Task<string> CallToolAsync(string toolName, JsonObject arguments)
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments },
            };
            var response = JsonNode.Parse((await dispatcher.HandleAsync(request.ToJsonString(), CancellationToken.None))!)!;
            return response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        }
        var hatTrack = servedDocument.Project.Tracks[2];

        var patchResult = await CallToolAsync("apply_patch", new JsonObject { ["patch"] = new JsonObject { ["swing"] = 57 } });
        Assert.Contains("swing 50 (straight) → 57", patchResult);
        Assert.Equal(57, servedDocument.Project.Swing);

        // A track added with its own groove says so in the result, not only in get_project.
        var addResult = await CallToolAsync("apply_patch", new JsonObject
        {
            ["patch"] = JsonNode.Parse("""{"tracks": [{"id": "new", "name": "Shaker", "instrument": "hat", "swing": 66.67, "timing": 9}]}"""),
        });
        Assert.Contains("\"Shaker\" (hat, swing 66.67, timing 9 ms)", addResult);

        var updateResult = await CallToolAsync("update_track", new JsonObject { ["track"] = hatTrack.Name, ["swing"] = 90, ["timing"] = 12 });
        Assert.Contains("swing 56 → 75", updateResult);
        Assert.Contains("timing 0 → 12 ms", updateResult);
        Assert.Contains($"{hatTrack.Name}.swing: 90 clamped to 75", updateResult);
        Assert.Equal(75, servedDocument.Project.Tracks[2].Swing);

        var listing = await CallToolAsync("get_project", new JsonObject());
        Assert.Contains("swing 57", listing);
        Assert.Contains("swing 75 timing 12ms", listing);

        var catalog = await CallToolAsync("get_catalog", new JsonObject());
        Assert.Contains("(swing - 50) / 50", catalog);
        Assert.Contains("GROOVE: swing is a percentage from 50 to 75", SlawMcpToolset.ServerInstructions);
    }
}
