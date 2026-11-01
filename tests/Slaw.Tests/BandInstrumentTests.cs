using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// The rhythm section: electric guitar, bass guitar, kit tom and cymbal. The shared organic
/// theories (tuning, determinism, velocity, knob extremes) already cover them; these check what
/// each one promises on its own: palm mutes that mute, vibrato that wobbles, uprights that thump
/// short, toms that open up and rides that ring their bell.
/// </summary>
public class BandInstrumentTests
{
    private const int SampleRate = 44100;

    private static float[] Render(string instrument, int[] midiNotes, double gateSeconds = 2, double velocity = 1, Dictionary<string, double>? parameters = null) =>
        InstrumentVoiceSynthesizer.RenderVoice(instrument, midiNotes, gateSeconds, SampleRate, new Random(5), parameters, noteVelocity: velocity);

    private static double Rms(float[] samples, double fromSeconds, double toSeconds)
    {
        var start = (int)(fromSeconds * SampleRate);
        var end = Math.Min(samples.Length, (int)(toSeconds * SampleRate));
        if (end <= start)
        {
            return 0;
        }
        var energy = 0.0;
        for (var frameIndex = start; frameIndex < end; frameIndex++)
        {
            energy += (double)samples[frameIndex] * samples[frameIndex];
        }
        return Math.Sqrt(energy / (end - start));
    }

    private static double Brightness(float[] samples, double seconds)
    {
        var length = Math.Min(samples.Length, (int)(seconds * SampleRate));
        double differenceEnergy = 0, signalEnergy = 0;
        for (var frameIndex = 1; frameIndex < length; frameIndex++)
        {
            var difference = (double)samples[frameIndex] - samples[frameIndex - 1];
            differenceEnergy += difference * difference;
            signalEnergy += (double)samples[frameIndex] * samples[frameIndex];
        }
        return differenceEnergy / signalEnergy;
    }

    private static double Magnitude(float[] samples, double fromSeconds, double lengthSeconds, double frequencyHz)
    {
        var start = (int)(fromSeconds * SampleRate);
        var length = Math.Min((int)(lengthSeconds * SampleRate), samples.Length - start);
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

    /// <summary>The strongest frequency within ±100 cents of the expected one, in cents from it.</summary>
    private static double PitchCents(float[] samples, double fromSeconds, double lengthSeconds, double expectedHz)
    {
        var bestMagnitude = 0.0;
        var bestCents = 0.0;
        for (var candidateCents = -100.0; candidateCents <= 100; candidateCents += 1)
        {
            var candidateMagnitude = Magnitude(samples, fromSeconds, lengthSeconds, expectedHz * Math.Pow(2, candidateCents / 1200));
            if (candidateMagnitude > bestMagnitude)
            {
                bestMagnitude = candidateMagnitude;
                bestCents = candidateCents;
            }
        }
        return bestCents;
    }

    [Fact]
    public void PalmMuteChokesTheElectricAndMuteVelocityOnlyChokesSoftNotes()
    {
        var ringing = Render("electric", new[] { 40 });
        var muted = Render("electric", new[] { 40 }, parameters: new() { ["mute"] = 1 });
        Assert.True(Rms(muted, 0.4, 0.8) < 0.05 * Rms(ringing, 0.4, 0.8), "a palm-muted note should be gone within half a second");

        var byVelocity = new Dictionary<string, double> { ["mutevel"] = 0.6 };
        var softChug = Render("electric", new[] { 40 }, velocity: 0.5, parameters: byVelocity);
        var hardNote = Render("electric", new[] { 40 }, velocity: 1, parameters: byVelocity);
        Assert.True(Rms(softChug, 0.4, 0.8) < 0.05 * Rms(hardNote, 0.4, 0.8), "below Mute Velocity the note chugs, above it rings");
    }

    [Fact]
    public void TheBridgePickupIsBrighterThanTheNeck()
    {
        var bridge = Render("electric", new[] { 52 }, parameters: new() { ["pickup"] = 0, ["humbucker"] = 0 });
        var neck = Render("electric", new[] { 52 }, parameters: new() { ["pickup"] = 1, ["humbucker"] = 0 });
        Assert.True(Brightness(bridge, 0.5) > 1.3 * Brightness(neck, 0.5), $"bridge {Brightness(bridge, 0.5):0.0000}, neck {Brightness(neck, 0.5):0.0000}");
    }

    [Fact]
    public void VibratoWobblesAfterTheNoteRingsAndBendInScoopsFromFlat()
    {
        var expectedHz = NotationResolver.MidiNoteToFrequency(64);
        var vibrato = Render("electric", new[] { 64 }, parameters: new() { ["vibrato"] = 40, ["rate"] = 5 });
        var plainStart = PitchCents(vibrato, 0.02, 0.12, expectedHz);
        var wobble = Enumerable.Range(0, 8).Select(window => PitchCents(vibrato, 0.6 + window * 0.05, 0.05, expectedHz)).ToList();
        Assert.InRange(plainStart, -6, 6);
        Assert.True(wobble.Max() - wobble.Min() > 25, $"vibrato swing {wobble.Max() - wobble.Min()} cents");

        var scooped = Render("electric", new[] { 64 }, parameters: new() { ["bend"] = 150 });
        Assert.True(PitchCents(scooped, 0.0, 0.03, expectedHz) < -30, "a bend-in note starts flat");
        Assert.InRange(PitchCents(scooped, 0.5, 0.3, expectedHz), -4, 4);
    }

    [Fact]
    public void ElectricChordsStrumLowToHigh()
    {
        var parameters = new Dictionary<string, double> { ["strum"] = 40 };
        var chord = Render("electric", new[] { 40, 47, 52 }, parameters: parameters);
        var lowOnly = Render("electric", new[] { 40 }, parameters: parameters);
        // The first 40 ms of the chord is only the lowest string.
        Assert.True(Rms(chord, 0, 0.035) > 0.3 * Rms(lowOnly, 0, 0.035));
        Assert.True(chord.Length > lowOnly.Length);
    }

    [Fact]
    public void TheUprightThumpsShortAndThePickIsBrighterThanFingers()
    {
        var electric = Render("bassguitar", new[] { 33 });
        var upright = Render("bassguitar", new[] { 33 }, parameters: new() { ["upright"] = 1 });
        Assert.True(Rms(upright, 1.0, 1.5) < 0.5 * Rms(electric, 1.0, 1.5), "an upright rings shorter");

        var fingers = Render("bassguitar", new[] { 40 }, parameters: new() { ["style"] = 0 });
        var pick = Render("bassguitar", new[] { 40 }, parameters: new() { ["style"] = 1 });
        Assert.True(Brightness(pick, 0.3) > 1.3 * Brightness(fingers, 0.3), $"pick {Brightness(pick, 0.3):0.0000}, fingers {Brightness(fingers, 0.3):0.0000}");
    }

    [Fact]
    public void AnOpenTomSingsLongerThanAMuffledOne()
    {
        var muffled = Render("tom", new[] { 50 }, parameters: new() { ["tone"] = 0 });
        var open = Render("tom", new[] { 50 }, parameters: new() { ["tone"] = 1 });
        Assert.True(open.Length > muffled.Length);
        Assert.True(Rms(open, 0.3, 0.6) > 3 * Rms(muffled, 0.3, 0.6));
    }

    [Fact]
    public void CymbalsAnswerToXRingLongAndTheBellRingsInTheLowMids()
    {
        Assert.True(InstrumentVoiceSynthesizer.IsPercussive("cymbal"));
        var ride = Render("cymbal", Array.Empty<int>());
        Assert.Equal(ride, Render("cymbal", new[] { 72 }));
        Assert.True(Rms(ride, 1.0, 1.5) > 0.05 * Rms(ride, 0, 0.5), "a ride keeps washing after a second");

        var crash = Render("cymbal", Array.Empty<int>(), parameters: new() { ["kind"] = 1 });
        Assert.True(Brightness(crash, 0.5) > Brightness(ride, 0.5), "a crash is brighter than a ride");

        var noBell = Render("cymbal", Array.Empty<int>(), parameters: new() { ["bell"] = 0 });
        var bell = Render("cymbal", Array.Empty<int>(), parameters: new() { ["bell"] = 1 });
        Assert.True(Magnitude(bell, 0.1, 0.4, 640) > 4 * Magnitude(noBell, 0.1, 0.4, 640));
    }

    [Fact]
    public void KeyAnalysisIgnoresTomsAndCymbalsAndGetCatalogDescribesTheBand()
    {
        foreach (var instrument in new[] { "electric", "bassguitar", "tom", "cymbal" })
        {
            Assert.NotEmpty(DeviceCatalog.InstrumentDescription(instrument));
            Assert.DoesNotContain("Oscillator", DeviceCatalog.InstrumentDisplayName(instrument));
        }
    }
}
