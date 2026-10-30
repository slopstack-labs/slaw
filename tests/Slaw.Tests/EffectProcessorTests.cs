using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Device chain conduct: effects may disrupt the music, never the silence, and now in stereo.</summary>
public class EffectProcessorTests
{
    private const int TestSampleRate = 48000;
    private static readonly EffectProcessingContext ProcessingContext = new(TestSampleRate, 0.1);

    private static float[] ToneBlock(int length = 4800) => Enumerable.Range(0, length).Select(index => (float)(0.5 * Math.Sin(index * 0.05))).ToArray();

    private static double Correlation(float[] left, float[] right, int from, int to)
    {
        double product = 0, leftEnergy = 0, rightEnergy = 0;
        for (var index = from; index < to; index++)
        {
            product += left[index] * right[index];
            leftEnergy += left[index] * left[index];
            rightEnergy += right[index] * right[index];
        }
        return product / Math.Sqrt(leftEnergy * rightEnergy + 1e-30);
    }

    [Fact]
    public void VibeDisruptionLeavesSilenceSilent()
    {
        var vibeProcessor = EffectProcessor.Create("vibe", TestSampleRate);
        var silentLeft = new float[4800];
        var silentRight = new float[4800];
        vibeProcessor.Process(silentLeft, silentRight, new Dictionary<string, double> { ["disruption"] = 1 }, ProcessingContext);
        Assert.All(silentLeft.Concat(silentRight), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void VibeDisruptionStillRoughensNotes()
    {
        var cleanLeft = ToneBlock();
        var cleanRight = ToneBlock();
        var disruptedLeft = ToneBlock();
        var disruptedRight = ToneBlock();
        EffectProcessor.Create("vibe", TestSampleRate).Process(cleanLeft, cleanRight, new Dictionary<string, double> { ["disruption"] = 0 }, ProcessingContext);
        EffectProcessor.Create("vibe", TestSampleRate).Process(disruptedLeft, disruptedRight, new Dictionary<string, double> { ["disruption"] = 1 }, ProcessingContext);
        Assert.NotEqual(cleanLeft, disruptedLeft);
    }

    [Fact]
    public void MonoThroughTheReverbComesOutWithADecorrelatedStereoTail()
    {
        // A short burst, then silence: everything after the burst is tail.
        static float[] Burst()
        {
            var burst = new float[TestSampleRate];
            var noiseSource = new Random(11);
            for (var index = 0; index < 2400; index++)
            {
                burst[index] = (float)(noiseSource.NextDouble() - 0.5);
            }
            return burst;
        }

        var left = Burst();
        var right = Burst();
        EffectProcessor.Create("reverb", TestSampleRate).Process(left, right, new Dictionary<string, double> { ["size"] = 0.8, ["mix"] = 1 }, ProcessingContext);
        Assert.True(left.Skip(4800).Max(Math.Abs) > 0.01, "the reverb should ring on after the burst");
        Assert.InRange(Correlation(left, right, 4800, left.Length), -0.9, 0.9);

        // Width 0 folds the tails back into one.
        var monoLeft = Burst();
        var monoRight = Burst();
        EffectProcessor.Create("reverb", TestSampleRate).Process(monoLeft, monoRight, new Dictionary<string, double> { ["size"] = 0.8, ["mix"] = 1, ["width"] = 0 }, ProcessingContext);
        Assert.Equal(monoLeft, monoRight);
    }

    [Fact]
    public void TheWidenerIsMonoAtZeroTransparentAtOneAndWidensMonoWithHaas()
    {
        var stereoLeft = ToneBlock();
        var stereoRight = Enumerable.Range(0, 4800).Select(index => (float)(0.3 * Math.Sin(index * 0.11))).ToArray();

        var collapsedLeft = stereoLeft.ToArray();
        var collapsedRight = stereoRight.ToArray();
        EffectProcessor.Create("widen", TestSampleRate).Process(collapsedLeft, collapsedRight, new Dictionary<string, double> { ["width"] = 0, ["haas"] = 0 }, ProcessingContext);
        Assert.Equal(collapsedLeft, collapsedRight);

        var untouchedLeft = stereoLeft.ToArray();
        var untouchedRight = stereoRight.ToArray();
        EffectProcessor.Create("widen", TestSampleRate).Process(untouchedLeft, untouchedRight, new Dictionary<string, double> { ["width"] = 1, ["haas"] = 0 }, ProcessingContext);
        for (var index = 0; index < stereoLeft.Length; index++)
        {
            Assert.Equal(stereoLeft[index], untouchedLeft[index], 4);
            Assert.Equal(stereoRight[index], untouchedRight[index], 4);
        }

        // A mono source has no sides to widen; the Haas delay is what gives it two.
        var monoLeft = ToneBlock();
        var monoRight = ToneBlock();
        EffectProcessor.Create("widen", TestSampleRate).Process(monoLeft, monoRight, new Dictionary<string, double> { ["width"] = 1, ["haas"] = 10 }, ProcessingContext);
        Assert.Equal(ToneBlock(), monoLeft);
        Assert.Equal(ToneBlock()[100], monoRight[100 + TestSampleRate / 100], 4);
    }

    [Fact]
    public void PingPongEchoesAlternateBetweenChannels()
    {
        // One click; at 0.1 s per step and 2 steps, echoes land every 9600 samples.
        var left = new float[48000];
        var right = new float[48000];
        left[0] = right[0] = 1f;
        EffectProcessor.Create("delay", TestSampleRate).Process(left, right,
            new Dictionary<string, double> { ["steps"] = 2, ["feedback"] = 0.5, ["mix"] = 1, ["pingpong"] = 1 }, ProcessingContext);
        const int echoSpacing = 9600;
        Assert.True(left[echoSpacing] > 0.5 && Math.Abs(right[echoSpacing]) < 1e-6, "first repeat on the left");
        Assert.True(right[2 * echoSpacing] > 0.25 && Math.Abs(left[2 * echoSpacing]) < 1e-6, "second repeat on the right");
        Assert.True(left[3 * echoSpacing] > 0.1 && Math.Abs(right[3 * echoSpacing]) < 1e-6, "third repeat back on the left");

        // Ping-pong 0 is the old behaviour: each side echoes itself.
        var plainLeft = new float[48000];
        var plainRight = new float[48000];
        plainLeft[0] = plainRight[0] = 1f;
        EffectProcessor.Create("delay", TestSampleRate).Process(plainLeft, plainRight,
            new Dictionary<string, double> { ["steps"] = 2, ["feedback"] = 0.5, ["mix"] = 1 }, ProcessingContext);
        Assert.Equal(plainLeft, plainRight);
        Assert.True(plainLeft[echoSpacing] > 0.5);
    }

    [Theory]
    [InlineData("lowpass", 15000.0, 0.0)]
    [InlineData("lowpass", 19000.0, 0.3)]
    [InlineData("highpass", 9000.0, 0.0)]
    public void FiltersStayStableAtHighCutoffs(string filterType, double cutoffHz, double resonance)
    {
        // A 1 kHz tone through a lowpass far above it passes almost untouched; through a
        // highpass far above it is mostly removed. Neither may blow up into the clamp.
        const int sampleRate = 44100;
        var left = Enumerable.Range(0, sampleRate / 2).Select(index => (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * index / sampleRate))).ToArray();
        var right = left.ToArray();
        EffectProcessor.Create(filterType, sampleRate).Process(left, right,
            new Dictionary<string, double> { ["cutoff"] = cutoffHz, ["resonance"] = resonance }, new EffectProcessingContext(sampleRate, 0.1));
        var settledPeak = left.Skip(sampleRate / 10).Max(Math.Abs);
        if (filterType == "lowpass")
        {
            Assert.InRange(settledPeak, 0.45, 0.56);
        }
        else
        {
            Assert.InRange(settledPeak, 0.0, 0.1);
        }
    }

    [Fact]
    public void FiltersKeepSeparateStateForEachChannel()
    {
        var left = ToneBlock();
        var right = new float[left.Length];
        EffectProcessor.Create("lowpass", TestSampleRate).Process(left, right, new Dictionary<string, double> { ["cutoff"] = 800 }, ProcessingContext);
        Assert.True(left.Max(Math.Abs) > 0.1);
        Assert.All(right, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void PanningAMonoTrackKeepsTheLevelsItAlwaysHad()
    {
        static SlawProject DroneProject(double pan) => new()
        {
            Bpm = 120,
            Scenes = new List<string> { "A" },
            Arrangement = new List<ArrangementSection> { new() { Scene = 0, Repeats = 1 } },
            Tracks = new List<SlawTrack>
            {
                new()
                {
                    Instrument = "sine",
                    Gain = 1,
                    Pan = pan,
                    InstrumentParameters = new Dictionary<string, double> { ["sustain"] = 1 },
                    Clips = new List<SlawClip?> { new() { Name = "A", Pattern = CompactPatternJsonConverter.SplitStepString("A3:16 . . . . . . . . . . . . . . .") } },
                },
            },
        };
        foreach (var pan in new[] { -1.0, -0.4, 0, 0.6 })
        {
            var rendered = OfflineArrangementRenderer.Render(DroneProject(pan), 22050);
            var leftPeak = rendered.Left.Skip(2000).Take(10000).Max(Math.Abs);
            var rightPeak = rendered.Right.Skip(2000).Take(10000).Max(Math.Abs);
            var panAngle = (pan + 1) * Math.PI / 4;
            // The constant-power law the mixer has always used: cos on the left, sin on the right.
            if (leftPeak > 1e-4 && rightPeak > 1e-4)
            {
                Assert.Equal(Math.Cos(panAngle) / Math.Sin(panAngle), leftPeak / rightPeak, 2);
            }
            else
            {
                Assert.Equal(-1.0, pan);
                Assert.True(rightPeak < 1e-4);
            }
        }
    }

    [Theory]
    [InlineData("lowpass")]
    [InlineData("highpass")]
    [InlineData("delay")]
    [InlineData("reverb")]
    [InlineData("drive")]
    [InlineData("bitcrush")]
    [InlineData("vibe")]
    [InlineData("widen")]
    public void TheMonoShortcutSoundsExactlyLikeTheStereoPath(string effectType)
    {
        // Per effect, the catalog defaults plus one setting that keeps mono mono where the effect has one.
        var parameterValues = new Dictionary<string, double> { ["haas"] = 0, ["disruption"] = 0, ["steps"] = 1, ["mix"] = 0.6 };
        var monoLeft = ToneBlock(9600);
        var monoRight = new float[monoLeft.Length];
        var stayedMono = EffectProcessor.Create(effectType, TestSampleRate).Process(monoLeft, monoRight, parameterValues, ProcessingContext with { MonoInput = true });
        if (stayedMono)
        {
            monoLeft.CopyTo(monoRight, 0);
        }

        var stereoLeft = ToneBlock(9600);
        var stereoRight = ToneBlock(9600);
        EffectProcessor.Create(effectType, TestSampleRate).Process(stereoLeft, stereoRight, parameterValues, ProcessingContext);
        for (var index = 0; index < monoLeft.Length; index++)
        {
            Assert.Equal(stereoLeft[index], monoLeft[index], 5);
            Assert.Equal(stereoRight[index], monoRight[index], 5);
        }
    }
}
