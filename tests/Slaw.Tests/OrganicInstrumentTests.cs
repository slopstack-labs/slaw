using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// The organic timbre substrate: electric piano, organ, mallets, flute, guitar, congas and a
/// shaker. Validated for pitch, dynamics and determinism on behalf of regulators, and for not
/// exploding on behalf of everyone's speakers.
/// </summary>
public class OrganicInstrumentTests
{
    private const int SampleRate = 44100;

    public static readonly TheoryData<string> OrganicInstruments = new() { "epiano", "organ", "mallet", "flute", "guitar", "conga", "shaker" };

    /// <summary>Windowed DFT magnitude at one frequency.</summary>
    private static double Magnitude(float[] samples, int start, int length, double frequencyHz)
    {
        length = Math.Min(length, samples.Length - start);
        double real = 0, imaginary = 0;
        for (var frameIndex = 0; frameIndex < length; frameIndex++)
        {
            var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * frameIndex / (length - 1));
            var angle = 2 * Math.PI * frequencyHz * frameIndex / SampleRate;
            real += samples[start + frameIndex] * window * Math.Cos(angle);
            imaginary += samples[start + frameIndex] * window * Math.Sin(angle);
        }
        return Math.Sqrt(real * real + imaginary * imaginary) / length;
    }

    /// <summary>Where the spectral peak within ±50 cents of the expected frequency is, in cents from it.</summary>
    private static double PeakOffsetCents(float[] samples, int start, int length, double expectedHz)
    {
        var bestMagnitude = 0.0;
        var bestCents = 0.0;
        for (var candidateCents = -50.0; candidateCents <= 50; candidateCents += 0.25)
        {
            var candidateMagnitude = Magnitude(samples, start, length, expectedHz * Math.Pow(2, candidateCents / 1200));
            if (candidateMagnitude > bestMagnitude)
            {
                bestMagnitude = candidateMagnitude;
                bestCents = candidateCents;
            }
        }
        return bestCents;
    }

    private static double Rms(float[] samples, int start, int length)
    {
        length = Math.Min(length, samples.Length - start);
        var energy = 0.0;
        for (var frameIndex = 0; frameIndex < length; frameIndex++)
        {
            energy += (double)samples[start + frameIndex] * samples[start + frameIndex];
        }
        return Math.Sqrt(energy / Math.Max(1, length));
    }

    /// <summary>A cheap brightness measure: the energy of the first difference over the energy of the signal (grows with the spectral centroid).</summary>
    private static double Brightness(float[] samples, int length)
    {
        length = Math.Min(length, samples.Length);
        double differenceEnergy = 0, signalEnergy = 0;
        for (var frameIndex = 1; frameIndex < length; frameIndex++)
        {
            var difference = (double)samples[frameIndex] - samples[frameIndex - 1];
            differenceEnergy += difference * difference;
            signalEnergy += (double)samples[frameIndex] * samples[frameIndex];
        }
        return differenceEnergy / signalEnergy;
    }

    private static float[] Render(string instrument, int[] midiNotes, double gateSeconds = 1, double velocity = 1, Dictionary<string, double>? parameters = null, int seed = 7) =>
        InstrumentVoiceSynthesizer.RenderVoice(instrument, midiNotes, gateSeconds, SampleRate, new Random(seed), parameters, noteVelocity: velocity);

    [Theory]
    [InlineData("epiano", 45)]
    [InlineData("epiano", 60)]
    [InlineData("epiano", 84)]
    [InlineData("organ", 36)]
    [InlineData("organ", 60)]
    [InlineData("organ", 79)]
    [InlineData("mallet", 48)]
    [InlineData("mallet", 72)]
    [InlineData("mallet", 88)]
    [InlineData("flute", 60)]
    [InlineData("flute", 74)]
    [InlineData("flute", 91)]
    [InlineData("guitar", 40)]
    [InlineData("guitar", 64)]
    [InlineData("guitar", 84)]
    [InlineData("conga", 55)]
    [InlineData("conga", 60)]
    [InlineData("conga", 72)]
    public void PlaysInTuneWithinAFewCents(string instrument, int midiNote)
    {
        var voice = Render(instrument, new[] { midiNote });
        Assert.All(voice, sample => Assert.True(float.IsFinite(sample)));
        Assert.True(voice.Max(Math.Abs) > 0.05, $"{instrument} is near silent");

        // After the attack (the flute's scoop, the conga's settling skin), before the tail.
        var analysisStart = (int)(0.1 * SampleRate);
        var analysisLength = Math.Min((int)(0.4 * SampleRate), voice.Length - analysisStart);
        var offsetCents = PeakOffsetCents(voice, analysisStart, analysisLength, NotationResolver.MidiNoteToFrequency(midiNote));
        Assert.InRange(offsetCents, -3, 3);
    }

    [Theory]
    [MemberData(nameof(OrganicInstruments))]
    public void RendersAreDeterministicAndFinite(string instrument)
    {
        var chord = new[] { 57, 60, 64 };
        var first = Render(instrument, chord, 0.6, 0.8, seed: 11);
        var again = Render(instrument, chord, 0.6, 0.8, seed: 11);
        Assert.Equal(first, again);
        Assert.All(first, sample => Assert.True(float.IsFinite(sample)));
        Assert.True(Rms(first, 0, first.Length) > 0.005, $"{instrument} chord is near silent");
        Assert.True(first.Max(Math.Abs) < 1.5, $"{instrument} chord peaks at {first.Max(Math.Abs)}");
    }

    [Theory]
    [MemberData(nameof(OrganicInstruments))]
    public void HarderNotesAreBrighter(string instrument)
    {
        var softVoice = Render(instrument, new[] { 60 }, velocity: 0.3);
        var hardVoice = Render(instrument, new[] { 60 }, velocity: 1);
        Assert.NotEqual(softVoice, hardVoice);
        var analysisLength = (int)(0.25 * SampleRate);
        var softBrightness = Brightness(softVoice, analysisLength);
        var hardBrightness = Brightness(hardVoice, analysisLength);
        Assert.True(hardBrightness > softBrightness * 1.05, $"{instrument}: soft {softBrightness:0.0000}, hard {hardBrightness:0.0000}");
    }

    [Theory]
    [InlineData("epiano")]
    [InlineData("mallet")]
    [InlineData("conga")]
    [InlineData("saw")]
    public void TheEngineAppliesVelocityAsLevelAndOnlyReshapesTimbreWhereItShould(string instrument)
    {
        float[] RenderPattern(string pattern)
        {
            var project = ProjectSerializer.CreateBlankProject("velocity probe", 120);
            var track = new SlawTrack { Name = instrument, Instrument = instrument, Gain = 1 };
            project.Tracks.Add(track);
            project.Timeline.Add(new TimelineClip
            {
                TrackId = track.Id,
                StartStep = 0,
                LengthSteps = 8,
                Clip = new SlawClip { Name = instrument, Pattern = CompactPatternJsonConverter.SplitStepString(pattern) },
            });
            return OfflineArrangementRenderer.Render(project, SampleRate).Left;
        }

        var soft = RenderPattern("C4:4@0.4 . . . . . . .");
        var loud = RenderPattern("C4:4 . . . . . . .");
        var analysisLength = SampleRate / 2;
        var levelRatio = Rms(soft, 0, analysisLength) / Rms(loud, 0, analysisLength);
        Assert.InRange(levelRatio, 0.12, 0.45);

        // Scale the soft note up to the loud one's level: an oscillator is then the same waveform,
        // an organic instrument is not.
        var normalizedDifference = 0.0;
        var loudEnergy = 0.0;
        for (var frameIndex = 0; frameIndex < analysisLength; frameIndex++)
        {
            var difference = soft[frameIndex] / levelRatio - loud[frameIndex];
            normalizedDifference += difference * difference;
            loudEnergy += (double)loud[frameIndex] * loud[frameIndex];
        }
        var relativeDifference = Math.Sqrt(normalizedDifference / loudEnergy);
        if (InstrumentVoiceSynthesizer.ShapesTimbreByVelocity(instrument))
        {
            Assert.True(relativeDifference > 0.03, $"{instrument} sounds the same soft and loud ({relativeDifference})");
        }
        else
        {
            Assert.True(relativeDifference < 0.02, $"{instrument} changed timbre with velocity ({relativeDifference})");
        }
    }

    [Theory]
    [InlineData("epiano")]
    [InlineData("organ")]
    [InlineData("mallet")]
    [InlineData("flute")]
    [InlineData("guitar")]
    public void ChordsSoundEveryNote(string instrument)
    {
        var chord = new[] { 57, 60, 64 };
        var chordVoice = Render(instrument, chord);
        var analysisStart = (int)(0.15 * SampleRate);
        var analysisLength = Math.Min((int)(0.4 * SampleRate), chordVoice.Length - analysisStart);
        var noteMagnitudes = chord.Select(midiNote => Magnitude(chordVoice, analysisStart, analysisLength, NotationResolver.MidiNoteToFrequency(midiNote))).ToList();
        // A note between the chord tones, which none of them should be playing.
        var gapMagnitude = Magnitude(chordVoice, analysisStart, analysisLength, NotationResolver.MidiNoteToFrequency(58.5));
        Assert.All(noteMagnitudes, noteMagnitude => Assert.True(noteMagnitude > 4 * gapMagnitude, $"{instrument}: {string.Join(", ", noteMagnitudes)} vs gap {gapMagnitude}"));
        Assert.InRange(noteMagnitudes.Max() / noteMagnitudes.Min(), 1, 4);
    }

    [Fact]
    public void GuitarChordsStrumLowToHigh()
    {
        var strum = new Dictionary<string, double> { ["strum"] = 40 };
        var strummed = Render("guitar", new[] { 64, 52, 40 }, parameters: strum);
        var lowString = Render("guitar", new[] { 40 }, parameters: strum);
        // The low E starts first; the strummed chord is the 40 ms strum longer per extra string.
        Assert.Equal(lowString.Length + 2 * (int)(0.04 * SampleRate), strummed.Length);
        var firstMillisecondsAfterOnset = (int)(0.035 * SampleRate);
        var chordScale = (float)(1 / Math.Sqrt(3));
        var scaledLowString = lowString.Take(firstMillisecondsAfterOnset).Select(sample => sample * chordScale).ToArray();
        Assert.Equal(scaledLowString, strummed.Take(firstMillisecondsAfterOnset).ToArray());
    }

    [Fact]
    public void GuitarIsDampedWhenTheNoteEndsAndMalletsRingOut()
    {
        var shortGuitar = Render("guitar", new[] { 52 }, gateSeconds: 0.2);
        var longGuitar = Render("guitar", new[] { 52 }, gateSeconds: 2);
        Assert.True(shortGuitar.Length < longGuitar.Length / 2);
        Assert.Equal(Render("mallet", new[] { 60 }, gateSeconds: 0.1), Render("mallet", new[] { 60 }, gateSeconds: 2));
        Assert.Equal(Render("conga", new[] { 60 }, gateSeconds: 0.1), Render("conga", new[] { 60 }, gateSeconds: 2));
    }

    [Fact]
    public void ShakerIsADrumThatAnswersToX()
    {
        Assert.True(InstrumentVoiceSynthesizer.IsPercussive("shaker"));
        Assert.Contains(DeviceCatalog.InstrumentParametersFor("shaker"), specification => specification.Key == "pitch");
        Assert.Equal("x@0.6", NotationResolver.Canonicalize("C4@0.6", percussiveInstrument: true));
        var shake = Render("shaker", new[] { NotationResolver.DefaultTriggerMidiNote });
        Assert.True(Rms(shake, 0, shake.Length) > 0.01);
        // Its energy lives where shakers live, kilohertz above a conga's.
        var conga = Render("conga", new[] { 60 });
        Assert.True(Brightness(shake, shake.Length) > 100 * Brightness(conga, conga.Length), $"shaker {Brightness(shake, shake.Length)}, conga {Brightness(conga, conga.Length)}");
    }

    [Fact]
    public void CongaTakesItsPitchFromTheNoteAndXPlaysMiddleC()
    {
        Assert.False(InstrumentVoiceSynthesizer.IsPercussive("conga"));
        var triggered = Render("conga", new[] { NotationResolver.DefaultTriggerMidiNote });
        Assert.Equal(Render("conga", new[] { 60 }), triggered);
        var tumba = Render("conga", new[] { 55 });
        var analysisStart = (int)(0.08 * SampleRate);
        var analysisLength = (int)(0.2 * SampleRate);
        Assert.InRange(PeakOffsetCents(tumba, analysisStart, analysisLength, NotationResolver.MidiNoteToFrequency(55)), -3, 3);
    }

    [Theory]
    [MemberData(nameof(OrganicInstruments))]
    public void StaysFiniteAtEveryKnobExtreme(string instrument)
    {
        var specifications = DeviceCatalog.InstrumentParametersFor(instrument);
        var allMinimum = specifications.ToDictionary(specification => specification.Key, specification => specification.Minimum);
        var allMaximum = specifications.ToDictionary(specification => specification.Key, specification => specification.Maximum);
        var outOfRange = specifications.ToDictionary(specification => specification.Key, specification => specification.Maximum * 1000 + 1e9);
        foreach (var parameters in new[] { allMinimum, allMaximum, outOfRange })
        {
            foreach (var midiNote in new[] { 0, 21, 108, 127 })
            {
                foreach (var velocity in new[] { 0.0, 0.01, 1.0 })
                {
                    foreach (var gateSeconds in new[] { 0.0, 0.03, 1.5 })
                    {
                        var voice = Render(instrument, new[] { midiNote }, gateSeconds, velocity, parameters);
                        Assert.All(voice, sample => Assert.True(float.IsFinite(sample), $"{instrument} note {midiNote} velocity {velocity} gate {gateSeconds}"));
                        Assert.True(voice.Length == 0 || voice.Max(Math.Abs) < 4, $"{instrument} note {midiNote} velocity {velocity} gate {gateSeconds} peaks at {voice.Max(Math.Abs)}");
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(OrganicInstruments))]
    public void CatalogDescribesEveryOrganicInstrument(string instrument)
    {
        Assert.Contains(instrument, InstrumentVoiceSynthesizer.SupportedInstruments);
        Assert.True(InstrumentVoiceSynthesizer.ShapesTimbreByVelocity(instrument));
        Assert.False(InstrumentVoiceSynthesizer.BendsAtRender(instrument));
        Assert.DoesNotContain("Oscillator", DeviceCatalog.InstrumentDisplayName(instrument));
        Assert.NotEmpty(DeviceCatalog.InstrumentDescription(instrument));
        var specifications = DeviceCatalog.InstrumentParametersFor(instrument);
        Assert.Contains(specifications, specification => specification.Key == "humanize");
        Assert.Contains(specifications, specification => specification.Key == "cutoff");
        Assert.All(specifications, specification => Assert.InRange(specification.Default, specification.Minimum, specification.Maximum));
        Assert.Equal(specifications.Count, specifications.Select(specification => specification.Key).Distinct().Count());
    }

    [Fact]
    public void ExistingInstrumentsIgnoreVelocityAtRenderTime()
    {
        foreach (var instrument in new[] { "kick", "snare", "hat", "clap", "sine", "square", "saw", "triangle", "pluck", "noise" })
        {
            Assert.False(InstrumentVoiceSynthesizer.ShapesTimbreByVelocity(instrument));
            Assert.Equal(Render(instrument, new[] { 57 }, 0.3, 1), Render(instrument, new[] { 57 }, 0.3, 0.2));
        }
    }

    [Theory]
    [InlineData("Rhodes", "epiano")]
    [InlineData("warm electric piano", "epiano")]
    [InlineData("piano", "epiano")]
    [InlineData("Hammond B3", "organ")]
    [InlineData("church organ", "organ")]
    [InlineData("b3", "organ")]
    [InlineData("marimba", "mallet")]
    [InlineData("soft marimba", "mallet")]
    [InlineData("kalimba", "mallet")]
    [InlineData("vibes", "mallet")]
    [InlineData("bells", "mallet")]
    [InlineData("pan flute", "flute")]
    [InlineData("breathy flute", "flute")]
    [InlineData("nylon guitar", "guitar")]
    [InlineData("bass guitar", "saw")]
    [InlineData("bongos", "conga")]
    [InlineData("tumba", "conga")]
    [InlineData("hand drums", "conga")]
    [InlineData("maracas", "shaker")]
    [InlineData("shaker", "shaker")]
    [InlineData("Deep House Kick", "kick")]
    [InlineData("Bb3 bass", "saw")]
    [InlineData("good vibes pad", "triangle")]
    [InlineData("rim", "snare")]
    public void RemediatorMapsOrganicNames(string requestedName, string expectedInstrument) =>
        Assert.Equal(expectedInstrument, ProjectAlignmentRemediator.ResolveInstrument(requestedName));
}
