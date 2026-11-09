using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Verifies that the autotune sample edit moves sung pitches onto the key and leaves everything else where it was.</summary>
public class AutotuneTests
{
    private const int SampleRate = 44100;

    private static float[] Sine(double hertz, double seconds, float amplitude = 0.5f) =>
        Enumerable.Range(0, (int)(seconds * SampleRate))
            .Select(frame => amplitude * (float)Math.Sin(2 * Math.PI * hertz * frame / SampleRate))
            .ToArray();

    /// <summary>Frequency from upward zero crossings between two times: independent of the corrector's own detector.</summary>
    private static double MeasuredHertz(float[] samples, double fromSeconds, double toSeconds)
    {
        var from = (int)(fromSeconds * SampleRate);
        var to = (int)(toSeconds * SampleRate);
        double? firstCrossing = null;
        double lastCrossing = 0;
        var crossingCount = 0;
        for (var frame = from + 1; frame < to; frame++)
        {
            if (samples[frame - 1] < 0 && samples[frame] >= 0)
            {
                var crossing = frame - 1 + samples[frame - 1] / (samples[frame - 1] - samples[frame]);
                firstCrossing ??= crossing;
                lastCrossing = crossing;
                crossingCount++;
            }
        }
        return (crossingCount - 1) / ((lastCrossing - firstCrossing!.Value) / SampleRate);
    }

    private static SampleEditOperation Autotune(string key, double retuneMilliseconds, double start = 0, double end = 1) =>
        new() { Type = "autotune", StartSeconds = start, EndSeconds = end, Amount = retuneMilliseconds, Key = key };

    [Fact]
    public void HardAutotuneSnapsASharpNoteToTheNearestSemitone()
    {
        var corrected = SampleEditProcessor.Apply(Sine(452, 1), SampleRate, new[] { Autotune("chromatic", 0) });

        Assert.Equal(1 * SampleRate, corrected.Length);
        Assert.InRange(MeasuredHertz(corrected, 0.2, 0.8), 437, 443);
    }

    [Fact]
    public void TheKeyDecidesWhichNoteIsNearest()
    {
        // 460 Hz sits between A4 (440) and A#4 (466.2): every semitone picks A#, C major has no A# and picks A.
        var chromatic = SampleEditProcessor.Apply(Sine(460, 1), SampleRate, new[] { Autotune("chromatic", 0) });
        var cMajor = SampleEditProcessor.Apply(Sine(460, 1), SampleRate, new[] { Autotune("C major", 0) });

        Assert.InRange(MeasuredHertz(chromatic, 0.2, 0.8), 463, 469);
        Assert.InRange(MeasuredHertz(cMajor, 0.2, 0.8), 437, 443);
    }

    [Fact]
    public void ASlowRetuneLeavesTheStartOfANoteAlone()
    {
        var corrected = SampleEditProcessor.Apply(Sine(452, 2), SampleRate, new[] { Autotune("chromatic", 300, 0, 2) });

        Assert.InRange(MeasuredHertz(corrected, 0.02, 0.07), 448, 456);
        Assert.InRange(MeasuredHertz(corrected, 1.4, 1.9), 437, 443);
    }

    [Fact]
    public void AudioOutsideTheRegionAndSilenceAreUntouched()
    {
        var source = Sine(452, 0.5).Concat(new float[SampleRate / 2]).Concat(Sine(452, 0.5)).ToArray();
        var corrected = SampleEditProcessor.Apply(source, SampleRate, new[] { Autotune("A minor", 0, 0, 1) });

        Assert.Equal(source.AsSpan(SampleRate).ToArray(), corrected.AsSpan(SampleRate).ToArray());
        Assert.All(corrected.AsSpan(SampleRate * 6 / 10, SampleRate * 3 / 10).ToArray(), sample => Assert.Equal(0f, sample));
        Assert.InRange(MeasuredHertz(corrected, 0.1, 0.4), 437, 443);
    }

    [Fact]
    public void AutotuneIsDeterministic()
    {
        var operations = new[] { Autotune("D minor", 40) };
        Assert.Equal(SampleEditProcessor.Apply(Sine(300, 1), SampleRate, operations), SampleEditProcessor.Apply(Sine(300, 1), SampleRate, operations));
    }

    [Fact]
    public void TheKeyIsPartOfTheCacheFingerprint()
    {
        string FingerprintOf(string key) => SampleEditProcessor.Fingerprint(new SampleEditSettings { Operations = { Autotune(key, 0) } });
        Assert.NotEqual(FingerprintOf("C major"), FingerprintOf("A minor"));
    }

    [Fact]
    public void RemediationRepairsAnUnknownKeyAndClampsTheRetuneTime()
    {
        var project = new SlawProject
        {
            Tracks =
            {
                new SlawTrack
                {
                    Name = "vox",
                    Instrument = "sampler",
                    SampleEditing = new SampleEditSettings { Operations = { Autotune("H# lydian-ish", 9000), Autotune("Bbm", 50) } },
                },
            },
        };
        var ledger = ProjectAlignmentRemediator.Remediate(ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)));
        var reloaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        ProjectAlignmentRemediator.Remediate(reloaded);
        var operations = reloaded.Tracks[0].SampleEditing!.Operations;

        Assert.Contains(ledger, entry => entry.Contains("autotune key", StringComparison.Ordinal));
        Assert.Equal(PitchCorrector.ChromaticKey, operations[0].Key);
        Assert.Equal(PitchCorrector.MaximumRetuneMilliseconds, operations[0].Amount);
        Assert.Equal("Bbm", operations[1].Key);
        Assert.Equal(50, operations[1].Amount);
    }
}
