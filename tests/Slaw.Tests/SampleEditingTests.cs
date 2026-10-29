using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Verifies the non-destructive sample edit pipeline, which is deterministic by regulatory necessity.</summary>
public class SampleEditingTests
{
    private const int TestSampleRate = 1000;

    private static float[] Ramp(int frameCount) =>
        Enumerable.Range(0, frameCount).Select(frameIndex => frameIndex / (float)frameCount).ToArray();

    [Fact]
    public void LengthChangingOperationsProduceExpectedDurations()
    {
        var sourceSamples = Ramp(1000);
        SampleEditOperation Operation(string operationType) => new() { Type = operationType, StartSeconds = 0.25, EndSeconds = 0.5 };

        Assert.Equal(250, SampleEditProcessor.Apply(sourceSamples, TestSampleRate, new[] { Operation("trim") }).Length);
        Assert.Equal(750, SampleEditProcessor.Apply(sourceSamples, TestSampleRate, new[] { Operation("delete") }).Length);
        Assert.Equal(1250, SampleEditProcessor.Apply(sourceSamples, TestSampleRate, new[] { Operation("duplicate") }).Length);
        Assert.Equal(1000, sourceSamples.Length);
    }

    [Fact]
    public void RegionOperationsOnlyTouchTheirRegion()
    {
        var sourceSamples = Enumerable.Repeat(0.5f, 1000).ToArray();
        var silenced = SampleEditProcessor.Apply(sourceSamples, TestSampleRate,
            new[] { new SampleEditOperation { Type = "silence", StartSeconds = 0.1, EndSeconds = 0.2 } });
        var normalized = SampleEditProcessor.Apply(sourceSamples, TestSampleRate,
            new[] { new SampleEditOperation { Type = "normalize", StartSeconds = 0, EndSeconds = 1, Amount = 0 } });

        Assert.Equal(0f, silenced[150]);
        Assert.Equal(0.5f, silenced[50]);
        Assert.Equal(1f, normalized.Max(), 3);
    }

    [Fact]
    public void SliceModePlaysTheSliceBelongingToEachNote()
    {
        // Three flat segments; slice boundaries at their edges.
        var segmentedSamples = Enumerable.Range(0, 3000).Select(frameIndex => frameIndex < 1000 ? 0.1f : frameIndex < 2000 ? 0.5f : 0.9f).ToArray();
        var segmentedSample = new SampleData(segmentedSamples, TestSampleRate, "segments.wav");
        var sliceEditing = new SampleEditSettings
        {
            PlaybackMode = SampleEditSettings.SliceMode,
            SlicePointsSeconds = { 1.0, 2.0 },
        };
        var samplerParameters = new Dictionary<string, double> { ["root"] = 60 };

        float[] RenderNote(int midiNote) => InstrumentVoiceSynthesizer.RenderVoice(
            "sampler", new[] { midiNote }, 0.5, TestSampleRate, new Random(1), samplerParameters, segmentedSample, sliceEditing);

        var secondSlice = RenderNote(61);
        Assert.InRange(secondSlice[secondSlice.Length / 2], 0.45f, 0.55f);
        Assert.InRange(RenderNote(62)[500], 0.85f, 0.95f);
        Assert.Empty(RenderNote(63));
        Assert.Empty(RenderNote(59));
    }

    [Fact]
    public void SustainLoopKeepsANoteSoundingWhileHeld()
    {
        var shortSample = new SampleData(Enumerable.Repeat(0.5f, 500).ToArray(), TestSampleRate, "short.wav");
        var loopEditing = new SampleEditSettings { LoopEnabled = true, LoopStartSeconds = 0.1, LoopEndSeconds = 0.4 };
        var heldParameters = new Dictionary<string, double> { ["hold"] = 1, ["release"] = 50 };

        var heldVoice = InstrumentVoiceSynthesizer.RenderVoice(
            "sampler", new[] { 60 }, 2.0, TestSampleRate, new Random(1), heldParameters, shortSample, loopEditing);

        // Two seconds of gate from a half-second sample: only possible by looping.
        Assert.True(heldVoice.Length >= 2000);
        Assert.InRange(heldVoice[1800], 0.45f, 0.55f);
    }
}
