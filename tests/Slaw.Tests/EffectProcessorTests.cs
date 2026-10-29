using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Device chain conduct: effects may disrupt the music, never the silence.</summary>
public class EffectProcessorTests
{
    private static readonly EffectProcessingContext ProcessingContext = new(48000, 0.1);

    [Fact]
    public void VibeDisruptionLeavesSilenceSilent()
    {
        var vibeProcessor = EffectProcessor.Create("vibe", 48000);
        var silentBlock = new float[4800];
        vibeProcessor.Process(silentBlock, new Dictionary<string, double> { ["disruption"] = 1 }, ProcessingContext);
        Assert.All(silentBlock, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void VibeDisruptionStillRoughensNotes()
    {
        static float[] ToneBlock() => Enumerable.Range(0, 4800).Select(index => (float)(0.5 * Math.Sin(index * 0.05))).ToArray();
        var cleanBlock = ToneBlock();
        var disruptedBlock = ToneBlock();
        EffectProcessor.Create("vibe", 48000).Process(cleanBlock, new Dictionary<string, double> { ["disruption"] = 0 }, ProcessingContext);
        EffectProcessor.Create("vibe", 48000).Process(disruptedBlock, new Dictionary<string, double> { ["disruption"] = 1 }, ProcessingContext);
        Assert.NotEqual(cleanBlock, disruptedBlock);
    }
}
