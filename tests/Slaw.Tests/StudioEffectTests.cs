using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// The studio effects: limiter, transient shaper, phaser, auto-wah and the Open Plan Office
/// reverb. Each is held to the one thing it promises, and all of them to finite output at every
/// knob extreme and to surviving the remediator.
/// </summary>
public class StudioEffectTests
{
    private const int Rate = 44100;

    public static readonly TheoryData<string> StudioEffects = new() { "limiter", "transient", "phaser", "autowah", "chamber" };

    private static float[] Sine(double frequencyHz, double amplitude, int frames) =>
        Enumerable.Range(0, frames).Select(frame => (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * frame / Rate))).ToArray();

    private static double Rms(ReadOnlySpan<float> samples)
    {
        var total = 0.0;
        foreach (var sample in samples)
        {
            total += sample * sample;
        }
        return Math.Sqrt(total / Math.Max(1, samples.Length));
    }

    /// <summary>Runs a stereo signal through a fresh effect in 512-frame blocks, like the engine.</summary>
    private static (float[] Left, float[] Right) Run(string effectType, float[] left, float[] right, Dictionary<string, double> parameters, bool mono = false)
    {
        var processor = EffectProcessor.Create(effectType, Rate);
        var outputLeft = (float[])left.Clone();
        var outputRight = mono ? new float[left.Length] : (float[])right.Clone();
        var stillMono = mono;
        for (var start = 0; start < left.Length; start += 512)
        {
            var length = Math.Min(512, left.Length - start);
            var leftBlock = outputLeft.AsSpan(start, length);
            var rightBlock = outputRight.AsSpan(start, length);
            stillMono = processor.Process(leftBlock, rightBlock, parameters, new EffectProcessingContext(Rate, 0.125, MonoInput: mono));
            if (mono && !stillMono)
            {
                mono = false;
            }
        }
        if (stillMono)
        {
            outputLeft.CopyTo(outputRight, 0);
        }
        return (outputLeft, outputRight);
    }

    [Fact]
    public void TheLimiterNeverPassesTheCeilingAndLeavesQuietAudioAloneButLate()
    {
        var loud = Sine(220, 2.0, Rate);
        var (limitedLeft, limitedRight) = Run("limiter", loud, loud, new() { ["ceiling"] = -1 });
        var ceiling = Math.Pow(10, -1 / 20.0);
        Assert.All(limitedLeft, sample => Assert.True(Math.Abs(sample) <= ceiling + 1e-6, $"{sample} over the ceiling"));
        Assert.Equal(limitedLeft, limitedRight);
        Assert.True(limitedLeft.Skip(Rate / 2).Max(Math.Abs) > 0.95 * ceiling, "a pushed signal sits at the ceiling");

        var quiet = Sine(220, 0.1, Rate / 4);
        var (passedLeft, _) = Run("limiter", quiet, quiet, new());
        var lookahead = CeilingPolicyLimiterProcessor.LookaheadFramesAt(Rate);
        for (var frame = lookahead; frame < quiet.Length; frame++)
        {
            Assert.Equal(quiet[frame - lookahead], passedLeft[frame]);
        }
    }

    [Fact]
    public void TheLimiterKeepsMonoAndStereoInStepWhenATrackTurnsStereo()
    {
        var processor = EffectProcessor.Create("limiter", Rate);
        var signal = Sine(330, 1.5, 2048);
        var left = (float[])signal.Clone();
        var right = new float[2048];
        processor.Process(left.AsSpan(0, 1024), right.AsSpan(0, 1024), new Dictionary<string, double>(), new EffectProcessingContext(Rate, 0.125, MonoInput: true));
        signal.AsSpan(1024).CopyTo(right.AsSpan(1024));
        processor.Process(left.AsSpan(1024), right.AsSpan(1024), new Dictionary<string, double>(), new EffectProcessingContext(Rate, 0.125));
        for (var frame = 1024; frame < 2048; frame++)
        {
            Assert.Equal(left[frame], right[frame]);
        }
    }

    /// <summary>A drum-like hit: a burst of 200 Hz with a sharp attack and an exponential tail.</summary>
    private static float[] Hits(int count)
    {
        var signal = new float[count * Rate / 4];
        for (var hit = 0; hit < count; hit++)
        {
            for (var frame = 0; frame < Rate / 4; frame++)
            {
                signal[hit * Rate / 4 + frame] = (float)(0.5 * Math.Exp(-frame / (0.06 * Rate)) * Math.Sin(2 * Math.PI * 200 * frame / Rate));
            }
        }
        return signal;
    }

    [Fact]
    public void TheTransientShaperMovesHitsAndTailsAndIsTransparentAtZero()
    {
        var hits = Hits(4);
        var (neutral, _) = Run("transient", hits, hits, new());
        Assert.Equal(hits, neutral);

        double HitToTail(float[] signal)
        {
            var start = 3 * Rate / 4;
            return Rms(signal.AsSpan(start, Rate / 200)) / Rms(signal.AsSpan(start + Rate / 10, Rate / 20));
        }
        var (punchy, _) = Run("transient", hits, hits, new() { ["attack"] = 1 });
        var (tight, _) = Run("transient", hits, hits, new() { ["sustain"] = -1 });
        Assert.True(HitToTail(punchy) > 1.5 * HitToTail(hits), $"attack: {HitToTail(hits):0.00} → {HitToTail(punchy):0.00}");
        Assert.True(HitToTail(tight) > 1.5 * HitToTail(hits), $"sustain: {HitToTail(hits):0.00} → {HitToTail(tight):0.00}");
    }

    [Fact]
    public void ThePhaserSweepsANotchAndSpreadsMonoIntoStereo()
    {
        var tone = Sine(700, 0.5, 4 * Rate);
        var (left, right) = Run("phaser", tone, tone, new() { ["rate"] = 1, ["feedback"] = 0.6 }, mono: true);
        var blockLevels = Enumerable.Range(0, 80).Select(block => Rms(left.AsSpan(Rate + block * 1000, 1000))).ToList();
        Assert.True(blockLevels.Max() > 3 * blockLevels.Min(), $"swept {blockLevels.Min():0.000} … {blockLevels.Max():0.000}");
        Assert.NotEqual(left, right);

        var (bypassed, _) = Run("phaser", tone, tone, new() { ["mix"] = 0 });
        Assert.Equal(tone, bypassed);
    }

    [Fact]
    public void TheAutoWahOpensUpWhenPlayedHarder()
    {
        var noise = new Random(3);
        var white = Enumerable.Range(0, Rate).Select(_ => (float)(noise.NextDouble() * 2 - 1)).ToArray();
        double Brightness(float[] signal)
        {
            double differenceEnergy = 0, energy = 0;
            for (var frame = Rate / 2 + 1; frame < signal.Length; frame++)
            {
                var difference = signal[frame] - signal[frame - 1];
                differenceEnergy += difference * difference;
                energy += signal[frame] * signal[frame];
            }
            return differenceEnergy / energy;
        }
        var soft = white.Select(sample => sample * 0.01f).ToArray();
        var hard = white.Select(sample => sample * 0.5f).ToArray();
        var parameters = new Dictionary<string, double> { ["sensitivity"] = 0.4 };
        Assert.True(Brightness(Run("autowah", hard, hard, parameters).Left) > 2 * Brightness(Run("autowah", soft, soft, parameters).Left));
    }

    [Fact]
    public void TheHallRingsLongerThanTheRoomInStereoAndEverySpaceDecays()
    {
        var impulse = new float[4 * Rate];
        impulse[0] = 1;
        double Tail(int space)
        {
            var (left, _) = Run("chamber", impulse, impulse, new() { ["space"] = space, ["mix"] = 1, ["predelay"] = 0 });
            return Rms(left.AsSpan(Rate, Rate / 2)) / Rms(left.AsSpan(0, Rate / 4));
        }
        Assert.True(Tail(3) > 5 * Tail(0), $"hall {Tail(3):0.0000}, room {Tail(0):0.0000}");

        foreach (var space in new[] { 0, 1, 2, 3 })
        {
            var (left, right) = Run("chamber", impulse, impulse, new() { ["space"] = space, ["mix"] = 1 }, mono: true);
            Assert.All(left, sample => Assert.True(float.IsFinite(sample)));
            Assert.True(Rms(left.AsSpan(0, Rate / 2)) > 1e-4, $"space {space} is silent");
            Assert.True(Rms(left.AsSpan(3 * Rate, Rate)) < 0.01 * Rms(left.AsSpan(0, Rate / 2)), $"space {space} never decays");
            var correlation = Correlation(left.AsSpan(0, Rate), right.AsSpan(0, Rate));
            Assert.True(correlation < 0.5, $"space {space}: L/R correlation {correlation:0.00}");
        }
        var (dry, _) = Run("chamber", impulse, impulse, new() { ["mix"] = 0 });
        Assert.Equal(impulse, dry);
    }

    private static double Correlation(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        double product = 0, leftEnergy = 0, rightEnergy = 0;
        for (var frame = 0; frame < left.Length; frame++)
        {
            product += left[frame] * right[frame];
            leftEnergy += left[frame] * left[frame];
            rightEnergy += right[frame] * right[frame];
        }
        return product / Math.Sqrt(leftEnergy * rightEnergy + 1e-30);
    }

    [Theory]
    [MemberData(nameof(StudioEffects))]
    public void StaysFiniteAtEveryKnobExtremeAndIsDeterministic(string effectType)
    {
        var specifications = DeviceCatalog.EffectParametersFor(effectType);
        Assert.NotEmpty(specifications);
        var noise = new Random(9);
        var signal = Enumerable.Range(0, Rate).Select(_ => (float)(noise.NextDouble() * 2 - 1)).ToArray();
        foreach (var parameters in new[]
                 {
                     specifications.ToDictionary(specification => specification.Key, specification => specification.Minimum),
                     specifications.ToDictionary(specification => specification.Key, specification => specification.Maximum),
                     specifications.ToDictionary(specification => specification.Key, specification => specification.Maximum * 1000 + 1e9),
                 })
        {
            var (left, right) = Run(effectType, signal, signal, parameters);
            Assert.All(left, sample => Assert.True(float.IsFinite(sample) && Math.Abs(sample) < 50, $"{effectType}: {sample}"));
            Assert.All(right, sample => Assert.True(float.IsFinite(sample) && Math.Abs(sample) < 50, $"{effectType}: {sample}"));
            Assert.Equal(left, Run(effectType, signal, signal, parameters).Left);
        }
    }

    [Theory]
    [MemberData(nameof(StudioEffects))]
    public void SurvivesTheRemediatorAndIsDescribed(string effectType)
    {
        var project = ProjectSerializer.CreateStarterProject("fx");
        project.Tracks[0].Effects.Add(new EffectDevice { Type = effectType, Parameters = new Dictionary<string, double> { ["nonsense"] = 3 } });
        ProjectAlignmentRemediator.Remediate(project);
        var effect = project.Tracks[0].Effects[^1];
        Assert.Equal(effectType, effect.Type);
        Assert.DoesNotContain("nonsense", effect.Parameters.Keys);
        Assert.NotEmpty(DeviceCatalog.EffectDescription(effectType));
        Assert.NotEqual(effectType, DeviceCatalog.EffectDisplayName(effectType));
    }

    [Theory]
    [InlineData("brickwall", "limiter")]
    [InlineData("maximizer", "limiter")]
    [InlineData("transient shaper", "transient")]
    [InlineData("wah", "autowah")]
    [InlineData("envelope filter", "autowah")]
    [InlineData("phaser", "phaser")]
    [InlineData("plate reverb", "chamber")]
    [InlineData("spring reverb", "chamber")]
    [InlineData("hall", "reverb")]
    public void TheRemediatorMapsStudioEffectNames(string requested, string expected) =>
        Assert.Equal(expected, ProjectAlignmentRemediator.ResolveEffect(requested));
}
