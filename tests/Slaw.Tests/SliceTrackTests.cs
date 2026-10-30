using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>A piece of a recording, promoted to its own track and pitched across the keyboard.</summary>
public class SliceTrackTests
{
    private const int SampleRate = 48000;
    private const double LowToneHz = 220;
    private const double HighToneHz = 440;

    private static SlawProject OneSceneProject() => new()
    {
        Bpm = 120,
        Steps = 16,
        Scenes = new List<string> { "Only" },
        Arrangement = new List<ArrangementSection> { new() { Scene = 0, Repeats = 1 } },
        Tracks = new List<SlawTrack>(),
    };

    /// <summary>Two seconds of take: one of the low tone, then one of the high tone.</summary>
    private static string WriteTwoToneTake(string workingFolder)
    {
        var takeSamples = Enumerable.Range(0, 2 * SampleRate)
            .Select(frame => (float)(0.5 * Math.Sin(2 * Math.PI * (frame < SampleRate ? LowToneHz : HighToneHz) * frame / SampleRate)))
            .ToArray();
        var takePath = Path.Combine(workingFolder, "take.wav");
        AudioTakeAssembler.WriteTakeWave(takePath, takeSamples, SampleRate);
        return takePath;
    }

    private static SlawTrack TakeTrack(SlawProject project, string takePath)
    {
        var takeTrack = AudioTakeAssembler.CreateTakeTrack(project, template: null);
        takeTrack.Name = "Lead";
        takeTrack.Sample = takePath;
        takeTrack.InstrumentParameters["hold"] = 1;
        takeTrack.InstrumentParameters["start"] = 0.25;
        takeTrack.InstrumentParameters["end"] = 0.75;
        takeTrack.Clips[0] = SlawClip.Empty("Only", 16);
        project.Tracks.Add(takeTrack);
        return takeTrack;
    }

    private static double DominantFrequency(float[] samples, int fromFrame, int frameCount)
    {
        var crossings = 0;
        for (var frame = fromFrame + 1; frame < fromFrame + frameCount; frame++)
        {
            if (samples[frame - 1] < 0 && samples[frame] >= 0)
            {
                crossings++;
            }
        }
        return crossings * (double)SampleRate / frameCount;
    }

    [Fact]
    public void ASliceTrackIsATrimmedPitchedCopyAndLeavesTheSourceAlone()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-slice-").FullName;
        try
        {
            var project = OneSceneProject();
            var source = TakeTrack(project, WriteTwoToneTake(workingFolder));
            source.SampleEditing = new SampleEditSettings
            {
                PlaybackMode = SampleEditSettings.SliceMode,
                SlicePointsSeconds = new List<double> { 1 },
                Operations = new List<SampleEditOperation> { new() { Type = "gain", StartSeconds = 0, EndSeconds = 2, Amount = -3 } },
            };

            var slice = SliceTrackAssembler.CreatePitchedTrack(project, source, 1, 2, sceneIndex: 0);

            Assert.Equal("Lead · slice 1", slice.Name);
            Assert.NotEqual(source.Id, slice.Id);
            Assert.Equal(source.Sample, slice.Sample);
            Assert.Equal(SampleEditSettings.PitchedMode, slice.SampleEditing!.PlaybackMode);
            Assert.Empty(slice.SampleEditing.SlicePointsSeconds);
            Assert.Equal(new[] { "gain", "trim" }, slice.SampleEditing.Operations.Select(operation => operation.Type));
            Assert.Equal(1, slice.InstrumentParameters["hold"]);
            Assert.False(slice.InstrumentParameters.ContainsKey("start"));
            Assert.False(slice.InstrumentParameters.ContainsKey("end"));
            Assert.NotNull(slice.Clips[0]);

            Assert.Single(source.SampleEditing.Operations);
            Assert.Equal(SampleEditSettings.SliceMode, source.SampleEditing.PlaybackMode);
            Assert.Equal(0.25, source.InstrumentParameters["start"]);

            var processed = SampleEditProcessor.LoadProcessedNative(slice.Sample!, slice.SampleEditing);
            Assert.Equal(1, processed.DurationSeconds, 2);
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    [Fact]
    public void SlicesOfSlicesCountFromTheOriginal()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-slice-").FullName;
        try
        {
            var project = OneSceneProject();
            var source = TakeTrack(project, WriteTwoToneTake(workingFolder));
            var first = SliceTrackAssembler.CreatePitchedTrack(project, source, 0, 1, 0);
            project.Tracks.Add(first);
            var second = SliceTrackAssembler.CreatePitchedTrack(project, first, 0, 0.5, 0);
            project.Tracks.Add(second);
            var third = SliceTrackAssembler.CreatePitchedTrack(project, source, 1, 2, 0);

            Assert.Equal("Lead · slice 2", second.Name);
            Assert.Equal("Lead · slice 3", third.Name);
            Assert.Equal(2, second.SampleEditing!.Operations.Count);
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    [Fact]
    public void TheSliceSoundsAtTheNotesItIsGivenAndOnlyTheChosenAudioPlays()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-slice-").FullName;
        try
        {
            var project = OneSceneProject();
            var source = TakeTrack(project, WriteTwoToneTake(workingFolder));
            source.Gain = 0;
            source.InstrumentParameters.Remove("start");
            source.InstrumentParameters.Remove("end");

            var slice = SliceTrackAssembler.CreatePitchedTrack(project, source, 1, 2, sceneIndex: 0);
            slice.Gain = 1;
            project.Tracks.Add(slice);
            var pattern = slice.Clips[0]!.Pattern;
            pattern[0] = "C4:8";
            pattern[8] = "C5:8";

            // The clip is one scene long; step 0 and step 8 are a second apart at 120 BPM (0.125 s per step).
            var render = OfflineArrangementRenderer.Render(project, SampleRate).Left;
            var measuredFrames = SampleRate / 2;
            var originalPitch = DominantFrequency(render, SampleRate / 10, measuredFrames);
            var octaveUp = DominantFrequency(render, SampleRate + SampleRate / 20, measuredFrames / 2);

            Assert.InRange(originalPitch, HighToneHz * 0.97, HighToneHz * 1.03);
            Assert.InRange(octaveUp, HighToneHz * 2 * 0.97, HighToneHz * 2 * 1.03);
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.1, 0, 1)]
    [InlineData(1.0, 1, 1.5)]
    [InlineData(1.7, 1.5, 2)]
    [InlineData(2.0, 1.5, 2)]
    public void ARightClickFindsTheSliceThePointerIsIn(double position, double expectedStart, double expectedEnd)
    {
        var slice = SliceTrackAssembler.SliceAt(new[] { 1.5, 1.0 }, 2, position);
        Assert.Equal((expectedStart, expectedEnd), slice);
    }

    [Fact]
    public void NothingIsFoundOutsideTheAudio()
    {
        Assert.Null(SliceTrackAssembler.SliceAt(new[] { 1.0 }, 2, 2.5));
        Assert.Null(SliceTrackAssembler.SliceAt(new[] { 1.0 }, 0, 0));
    }
}
