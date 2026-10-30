using Avalonia.Input;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The importer's thinking, the AIFF reader and the command palette's matching.</summary>
public class SoundImportTests
{
    [Theory]
    [InlineData("909 Kick 01.wav", "Kick")]
    [InlineData("BD_hard.flac", "Kick")]
    [InlineData("Tight Snare.aif", "Snare")]
    [InlineData("open hat 3.wav", "Open hat")]
    [InlineData("closed_hat.wav", "Closed hat")]
    [InlineData("HiHat.wav", "Hat")]
    [InlineData("Choir Pad.wav", "Sound")]
    [InlineData("Abdicate.wav", "Sound")]
    public void FileNamesSuggestWhatASoundIs(string fileName, string expectedLabel) =>
        Assert.Equal(expectedLabel, SoundImportPlanner.Classify(fileName).Label);

    [Fact]
    public void KitsStartWithTheKickAndCountNumbersProperly()
    {
        var sorted = SoundImportPlanner.SortForKit(new[] { "Pad.wav", "Snare.wav", "Kick 10.wav", "Kick 2.wav" });
        Assert.Equal(new[] { "Kick 2.wav", "Kick 10.wav", "Snare.wav", "Pad.wav" }, sorted);
    }

    [Fact]
    public void AKitJoinsItsSoundsAndEveryNotePlaysExactlyItsOwnSound()
    {
        var first = new SampleData(Enumerable.Repeat(0.25f, 1000).ToArray(), 48000, "a");
        var second = new SampleData(Enumerable.Repeat(0.5f, 500).ToArray(), 48000, "b");
        var third = new SampleData(Enumerable.Repeat(0.75f, 2000).ToArray(), 48000, "c");

        var kit = SoundImportPlanner.AssembleKit(new[] { first, second, third });

        Assert.Equal(3500, kit.Samples.Length);
        var settings = new SampleEditSettings { PlaybackMode = SampleEditSettings.SliceMode, SlicePointsSeconds = kit.SlicePointsSeconds };
        Assert.Equal((0, 1000), SampleEditProcessor.SliceRegionFor(settings, 3500, 48000, 60, 60));
        Assert.Equal((1000, 1500), SampleEditProcessor.SliceRegionFor(settings, 3500, 48000, 61, 60));
        Assert.Equal((1500, 3500), SampleEditProcessor.SliceRegionFor(settings, 3500, 48000, 62, 60));
    }

    [Fact]
    public void SoundsAtAnotherRateKeepTheirDurationInAKit()
    {
        var mostly = new SampleData(new float[48000], 48000, "a");
        var alsoMostly = new SampleData(new float[48000], 48000, "b");
        var slower = new SampleData(new float[44100], 44100, "c");

        var kit = SoundImportPlanner.AssembleKit(new[] { mostly, alsoMostly, slower });

        Assert.Equal(48000, kit.SampleRate);
        Assert.InRange(kit.Samples.Length, 3 * 48000 - 2, 3 * 48000 + 2);
    }

    [Fact]
    public void AKitAboveTheLengthLimitIsRefusedWithAReason()
    {
        var tooLong = new SampleData(new float[1000 * (SampleLibrary.MaximumDecodedSeconds + 1)], 1000, "a");
        Assert.Throws<InvalidDataException>(() => SoundImportPlanner.AssembleKit(new[] { tooLong }));
    }

    private static byte[] BuildAiff(int bits, string? compression, Func<int, byte[]> frameBytes, int frames)
    {
        var audio = Enumerable.Range(0, frames).SelectMany(frameBytes).ToArray();
        // 44100 as an 80-bit extended float.
        byte[] rate = { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 };
        var comm = new List<byte> { 0, 1 };
        comm.AddRange(BigEndian((uint)frames, 4));
        comm.AddRange(BigEndian((uint)bits, 2));
        comm.AddRange(rate);
        if (compression is not null)
        {
            comm.AddRange(System.Text.Encoding.ASCII.GetBytes(compression));
            comm.AddRange(new byte[] { 0, 0 });
        }
        var body = new List<byte>(System.Text.Encoding.ASCII.GetBytes(compression is null ? "AIFF" : "AIFC"));
        void Chunk(string tag, byte[] data)
        {
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
            body.AddRange(BigEndian((uint)data.Length, 4));
            body.AddRange(data);
            if (data.Length % 2 == 1)
            {
                body.Add(0);
            }
        }
        Chunk("COMM", comm.ToArray());
        Chunk("SSND", new byte[8].Concat(audio).ToArray());
        var file = new List<byte>(System.Text.Encoding.ASCII.GetBytes("FORM"));
        file.AddRange(BigEndian((uint)body.Count, 4));
        file.AddRange(body);
        return file.ToArray();
    }

    private static byte[] BigEndian(uint value, int byteCount) =>
        Enumerable.Range(0, byteCount).Select(index => (byte)(value >> (8 * (byteCount - 1 - index)))).ToArray();

    [Fact]
    public void AiffBigEndianPcmDecodesAtItsRate()
    {
        var aiff = BuildAiff(16, null, frame => frame == 0 ? new byte[] { 0x40, 0x00 } : new byte[] { 0xC0, 0x00 }, 2);
        var decoded = AiffFileDecoder.Decode(aiff, "x.aif");
        Assert.Equal(44100, decoded.SampleRate);
        Assert.Equal(new[] { 0.5f, -0.5f }, decoded.Samples);
    }

    [Fact]
    public void AifcLittleEndianDecodes()
    {
        var aiff = BuildAiff(16, "sowt", _ => new byte[] { 0x00, 0x40 }, 1);
        Assert.Equal(0.5f, AiffFileDecoder.Decode(aiff, "x.aifc").Samples[0]);
    }

    [Fact]
    public void CompressedAifcSaysWhatToDoInstead()
    {
        var aiff = BuildAiff(16, "ima4", _ => new byte[2], 1);
        var refusal = Assert.Throws<InvalidDataException>(() => AiffFileDecoder.Decode(aiff, "x.aifc"));
        Assert.Contains("ima4", refusal.Message);
    }

    // 200 frames of a 440 Hz sine, left and right in opposite phase, encoded by the reference `flac` tool.
    private const string ReferenceFlacBase64 = "ZkxhQwAAACIQABAAAABwAABwCsRC8AAAAMjOjGu1m/7Fu0AvYfnHZscPhAAALyAAAAByZWZlcmVuY2UgbGliRkxBQyAxLjUuMCAyMDI1MDIxMQEAAAADAAAAeD15//hpqADHDgAAABmAAAH1A+gF14BfayMUkUxTORW+iXk+3zLGUjonqbxijDmnjPkVLdUdvNYpnkRdyHMs1uypYjsoh8Q5r0kqTt0sTpdukk12tIuymWaSkHDCm/SSys6WbybxHbyZzPE830sT8wD2yA==";

    [Fact]
    public void AFileFromTheReferenceEncoderDecodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"slaw-{Guid.NewGuid():N}.flac");
        var decoded = FlacFileDecoder.Decode(Convert.FromBase64String(ReferenceFlacBase64), path);

        Assert.Equal(44100, decoded.SampleRate);
        Assert.Equal(200, decoded.Samples.Length);
        // Opposite phases mix to silence, which only happens if both channels decoded exactly.
        Assert.All(decoded.Samples, sample => Assert.Equal(0f, sample, 4));
    }

    [Fact]
    public void GarbageIsAReadableErrorInBothDecoders()
    {
        var garbage = new byte[64];
        Assert.Throws<InvalidDataException>(() => AiffFileDecoder.Decode(garbage, "x.aif"));
        Assert.Throws<InvalidDataException>(() => FlacFileDecoder.Decode(garbage, "x.flac"));
    }

    [Fact]
    public void EveryCommandReachesThePalette()
    {
        var titled = StudioCommandTitles.All.Select(entry => entry.Command).ToHashSet();
        var missing = Enum.GetValues<StudioCommand>().Where(command => command is not (StudioCommand.Dismiss or StudioCommand.ShowCommandPalette) && !titled.Contains(command));
        Assert.Empty(missing);
        Assert.Equal(StudioCommandTitles.All.Count, StudioCommandTitles.All.Select(entry => entry.Title).Distinct().Count());
    }

    [Theory]
    [InlineData(Key.P, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.P, KeyModifiers.Meta | KeyModifiers.Shift)]
    public void ThePaletteOpensWithItsShortcutOnEveryPlatform(Key key, KeyModifiers modifiers)
    {
        var shortcut = StudioShortcutMap.Resolve(key, modifiers)!;
        Assert.Equal(StudioCommand.ShowCommandPalette, shortcut.Command);
        Assert.True(StudioShortcutMap.Reaches(shortcut, typingInTextField: true, controlOwnsKeys: true));
    }

    private static CommandPaletteEntry Entry(string title) => new(title, null, () => { });

    [Theory]
    [InlineData("save", "File: Save")]
    [InlineData("fullscr", "View: Full Screen")]
    [InlineData("kick", "Add Track: Kick Synthesizer")]
    [InlineData("addkick", "Add Track: Kick Synthesizer")]
    [InlineData("SECOND", "View: Open / Close Second Window")]
    public void ThePaletteFindsCommandsFromAFewLetters(string query, string expectedFirst)
    {
        var entries = new[] { "File: Save As…", "File: Save", "View: Full Screen", "Add Track: Kick Synthesizer", "View: Open / Close Second Window", "File: Settings…" }.Select(Entry);
        Assert.Equal(expectedFirst, CommandPaletteMatcher.Rank(query, entries)[0].Title);
    }

    [Fact]
    public void ThePaletteShowsEverythingForNoQueryAndNothingForNonsense()
    {
        var entries = new[] { Entry("File: Save"), Entry("View: Full Screen") };
        Assert.Equal(2, CommandPaletteMatcher.Rank("  ", entries).Count);
        Assert.Empty(CommandPaletteMatcher.Rank("zzzq", entries));
    }
}
