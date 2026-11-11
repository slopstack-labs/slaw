using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The metronome: clicks in the live mix on the beat, louder on the bar, never in a bounce, and a count-in that holds the transport back.</summary>
public class MetronomeTests
{
    private const int EngineSampleRate = 8000;

    // 120 BPM: 0.125 s per step, 1000 frames per step at 8 kHz, 4000 per beat, 16000 per bar.
    private const int FramesPerStep = 1000;
    private const int FramesPerBeat = 4 * FramesPerStep;
    private const float SilenceThreshold = 1e-4f;

    /// <summary>One track whose session clip and arrangement clip are all rests: the only sound is the click.</summary>
    private static SlawProject SilentProject()
    {
        var restTrack = new SlawTrack
        {
            Name = "Rests",
            Instrument = "sine",
            Clips = new List<SlawClip?> { new() { Name = "rests", Pattern = CompactPatternJsonConverter.SplitStepString(". . . . . . . . . . . . . . . .") } },
        };
        var project = new SlawProject
        {
            Bpm = 120,
            Scenes = new List<string> { "Jam" },
            Tracks = new List<SlawTrack> { restTrack },
        };
        project.Timeline.Add(new TimelineClip { TrackId = restTrack.Id, StartStep = 0, LengthSteps = 64, Clip = new SlawClip { Name = "song", Pattern = CompactPatternJsonConverter.SplitStepString(". . . . . . . . . . . . . . . .") } });
        return project;
    }

    /// <summary>Renders <paramref name="stepCount"/> steps, one step per block, into one left channel.</summary>
    private static float[] RenderSteps(SequencerEngine engine, int stepCount, List<TransportSnapshot>? snapshotsAfterEachStep = null)
    {
        var renderedLeft = new float[stepCount * FramesPerStep];
        var leftBlock = new float[FramesPerStep];
        var rightBlock = new float[FramesPerStep];
        for (var stepIndex = 0; stepIndex < stepCount; stepIndex++)
        {
            engine.RenderBlock(leftBlock, rightBlock);
            Assert.Equal(leftBlock, rightBlock);
            leftBlock.CopyTo(renderedLeft, stepIndex * FramesPerStep);
            snapshotsAfterEachStep?.Add(engine.Transport);
        }
        return renderedLeft;
    }

    private static float PeakIn(float[] renderedLeft, int startFrame, int frameCount) =>
        renderedLeft.Skip(startFrame).Take(frameCount).Select(Math.Abs).DefaultIfEmpty(0f).Max();

    private static int FirstSoundingFrameFrom(float[] renderedLeft, int startFrame) =>
        Array.FindIndex(renderedLeft, startFrame, sample => Math.Abs(sample) > SilenceThreshold);

    /// <summary>The beats (counted from frame 0) on which a click starts, and each click's peak.</summary>
    private static List<(int Beat, float Peak)> ClicksIn(float[] renderedLeft)
    {
        var clicks = new List<(int Beat, float Peak)>();
        for (var beatIndex = 0; beatIndex * FramesPerBeat < renderedLeft.Length; beatIndex++)
        {
            var beatStart = beatIndex * FramesPerBeat;
            var peak = PeakIn(renderedLeft, beatStart, FramesPerStep);
            if (peak > SilenceThreshold)
            {
                clicks.Add((beatIndex, peak));
            }
            // Between beats: nothing at all.
            Assert.True(PeakIn(renderedLeft, beatStart + FramesPerStep, FramesPerBeat - FramesPerStep) <= SilenceThreshold,
                $"sound between beats after beat {beatIndex}");
        }
        return clicks;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLiveMixClicksOnEveryBeatAndAccentsTheDownbeat(bool arrangement)
    {
        var engine = new SequencerEngine(EngineSampleRate);
        engine.PublishProject(SilentProject());
        engine.ConfigureMetronome(audible: true, volume: 1);
        if (arrangement)
        {
            engine.PlayTimeline();
        }
        else
        {
            engine.LaunchScene(0);
        }

        var renderedLeft = RenderSteps(engine, 32);

        var clicks = ClicksIn(renderedLeft);
        Assert.Equal(Enumerable.Range(0, 8), clicks.Select(click => click.Beat));
        // Each click starts on its step's first frames, not later.
        foreach (var (beat, _) in clicks)
        {
            Assert.InRange(FirstSoundingFrameFrom(renderedLeft, beat * FramesPerBeat) - beat * FramesPerBeat, 0, 20);
        }
        var downbeatPeaks = clicks.Where(click => click.Beat % 4 == 0).Select(click => click.Peak).ToList();
        var offbeatPeaks = clicks.Where(click => click.Beat % 4 != 0).Select(click => click.Peak).ToList();
        Assert.All(downbeatPeaks, peak => Assert.InRange(peak, MetronomeClickSubstrate.DownbeatPeak * 0.95f, MetronomeClickSubstrate.DownbeatPeak * 1.01f));
        Assert.All(offbeatPeaks, peak => Assert.InRange(peak, MetronomeClickSubstrate.BeatPeak * 0.95f, MetronomeClickSubstrate.BeatPeak * 1.01f));
        Assert.True(downbeatPeaks.Min() > offbeatPeaks.Max() * 1.3f);
    }

    [Fact]
    public void VolumeScalesTheClick()
    {
        var engine = new SequencerEngine(EngineSampleRate);
        engine.PublishProject(SilentProject());
        engine.ConfigureMetronome(audible: true, volume: 0.5);
        engine.PlayTimeline();

        var renderedLeft = RenderSteps(engine, 4);

        Assert.InRange(PeakIn(renderedLeft, 0, FramesPerStep), MetronomeClickSubstrate.DownbeatPeak * 0.5f * 0.95f, MetronomeClickSubstrate.DownbeatPeak * 0.5f * 1.01f);
    }

    [Fact]
    public void TheMetronomeIsSilentWhenOff()
    {
        var engine = new SequencerEngine(EngineSampleRate);
        engine.PublishProject(SilentProject());
        engine.PlayTimeline();

        Assert.All(RenderSteps(engine, 32), sample => Assert.Equal(0f, sample));

        // Turned on mid-song, it starts with the next beat; turned off, it stops.
        engine.ConfigureMetronome(audible: true, volume: 1);
        Assert.NotEmpty(ClicksIn(RenderSteps(engine, 16)));
        engine.ConfigureMetronome(audible: false, volume: 1);
        var afterOff = RenderSteps(engine, 16);
        Assert.All(afterOff.Skip(FramesPerStep), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void ClicksStayOnTheStraightGridUnderSwing()
    {
        var project = SilentProject();
        project.Swing = 66;
        var engine = new SequencerEngine(EngineSampleRate);
        engine.PublishProject(project);
        engine.ConfigureMetronome(audible: true, volume: 1);
        engine.PlayTimeline();

        var renderedLeft = RenderSteps(engine, 16);

        Assert.Equal(new[] { 0, 1, 2, 3 }, ClicksIn(renderedLeft).Select(click => click.Beat));
    }

    [Fact]
    public void OfflineRendersNeverContainTheClick()
    {
        var project = SilentProject();
        Assert.Equal(0f, OfflineArrangementRenderer.Render(project, EngineSampleRate).PeakAmplitude());

        // And a real song bounces to the same bytes it always did.
        var starterProject = ProjectSerializer.CreateStarterProject("click-free");
        Assert.Equal(OfflineArrangementRenderer.Render(starterProject).Left, OfflineArrangementRenderer.Render(starterProject).Left);
    }

    [Fact]
    public void ACountInClicksItsBarsBeforeTheTransportStarts()
    {
        var engine = new SequencerEngine(EngineSampleRate);
        engine.PublishProject(SilentProject());
        // Metronome off: the count-in clicks anyway, and nothing clicks once the song plays.
        engine.PlayTimelineAfterCountIn(startStep: 0, countInBars: 1);

        var snapshots = new List<TransportSnapshot>();
        var renderedLeft = RenderSteps(engine, 32, snapshots);

        // Sixteen steps of count-in with the transport stopped, then the arrangement from step 0.
        for (var stepIndex = 0; stepIndex < 16; stepIndex++)
        {
            Assert.Equal(TransportMode.Stopped, snapshots[stepIndex].Mode);
            Assert.Equal(16 - stepIndex, snapshots[stepIndex].CountInStepsRemaining);
            Assert.True(snapshots[stepIndex].CountingIn);
        }
        Assert.Equal(TransportMode.Timeline, snapshots[16].Mode);
        Assert.Equal(0, snapshots[16].TimelineStep);
        Assert.False(snapshots[16].CountingIn);
        Assert.Equal(15, snapshots[31].TimelineStep);

        var clicks = ClicksIn(renderedLeft);
        Assert.Equal(new[] { 0, 1, 2, 3 }, clicks.Select(click => click.Beat));
        Assert.True(clicks[0].Peak > clicks[1].Peak * 1.3f);
    }

    [Fact]
    public void WithTheMetronomeOnTheCountInRunsStraightIntoTheSong()
    {
        var engine = new SequencerEngine(EngineSampleRate);
        engine.PublishProject(SilentProject());
        engine.ConfigureMetronome(audible: true, volume: 1);
        engine.LaunchSceneAfterCountIn(sceneIndex: 0, countInBars: 2);

        var snapshots = new List<TransportSnapshot>();
        var renderedLeft = RenderSteps(engine, 48, snapshots);

        Assert.Equal(TransportMode.Stopped, snapshots[31].Mode);
        Assert.Equal(TransportMode.Session, snapshots[32].Mode);
        Assert.Equal(0, snapshots[32].Tracks.Values.Single().ClipStep);
        // Two bars counted, then the first bar of the song: a click every 4000 frames without a gap, accents on bars.
        var clicks = ClicksIn(renderedLeft);
        Assert.Equal(Enumerable.Range(0, 12), clicks.Select(click => click.Beat));
        Assert.All(clicks.Where(click => click.Beat % 4 == 0), downbeat => Assert.True(downbeat.Peak > clicks[1].Peak * 1.3f));
    }

    [Fact]
    public void StopCancelsACountInAndAPlayingTransportSkipsIt()
    {
        var engine = new SequencerEngine(EngineSampleRate);
        engine.PublishProject(SilentProject());
        engine.PlayTimelineAfterCountIn(startStep: 0, countInBars: 2);
        RenderSteps(engine, 3);
        Assert.True(engine.Transport.CountingIn);

        engine.Stop();
        var afterStop = RenderSteps(engine, 40);
        Assert.Equal(TransportMode.Stopped, engine.Transport.Mode);
        Assert.False(engine.Transport.CountingIn);
        Assert.All(afterStop.Skip(FramesPerStep), sample => Assert.Equal(0f, sample));

        // Already playing: the start happens at once (here, a launch over the arrangement, quantized as usual).
        engine.PlayTimeline();
        RenderSteps(engine, 2);
        engine.LaunchClipAfterCountIn(engine.Transport.Tracks.Keys.Single(), sceneIndex: 0, countInBars: 2);
        RenderSteps(engine, 1);
        Assert.False(engine.Transport.CountingIn);
        Assert.Equal(TransportMode.Timeline, engine.Transport.Mode);
        Assert.Equal(0, engine.Transport.Tracks.Values.Single().QueuedScene);
    }

    [Fact]
    public void MetronomeSettingsSurviveTheSettingsFile()
    {
        var preferences = new StudioPreferences
        {
            MetronomeEnabled = true,
            MetronomeOnlyWhileRecording = true,
            MetronomeVolume = 0.35,
            MetronomeCountInBars = 2,
        };
        var restored = System.Text.Json.JsonSerializer.Deserialize<StudioPreferences>(
            System.Text.Json.JsonSerializer.Serialize(preferences, ProjectSerializer.LenientSerializationOptions),
            ProjectSerializer.LenientSerializationOptions)!;
        Assert.True(restored.MetronomeEnabled);
        Assert.True(restored.MetronomeOnlyWhileRecording);
        Assert.Equal(0.35, restored.MetronomeVolume);
        Assert.Equal(2, restored.MetronomeCountInBars);

        var copied = new StudioPreferences();
        preferences.CopyMetronomeTo(copied);
        Assert.Equal((true, true, 0.35, 2), (copied.MetronomeEnabled, copied.MetronomeOnlyWhileRecording, copied.MetronomeVolume, copied.MetronomeCountInBars));

        // Defaults: off, no count-in, a sensible volume.
        var defaults = new StudioPreferences();
        Assert.False(defaults.MetronomeEnabled);
        Assert.Equal(0, defaults.MetronomeCountInBars);
        Assert.Equal(SequencerEngine.DefaultMetronomeVolume, defaults.MetronomeVolume, 3);
        Assert.Equal(new[] { 0, 1, 2 }, MetronomeCountInChoices.All.Select(choice => choice.Bars));
    }
}
