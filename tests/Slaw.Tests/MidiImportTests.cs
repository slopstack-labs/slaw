using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>MIDI import: turning other people's note data into our deliverables.</summary>
public class MidiImportTests
{
    private const int TicksPerQuarter = 96;
    private const int TicksPerStep = TicksPerQuarter / 4;

    private static byte[] VariableLength(int value)
    {
        var groups = new Stack<byte>();
        groups.Push((byte)(value & 0x7F));
        while ((value >>= 7) > 0)
            groups.Push((byte)((value & 0x7F) | 0x80));
        return groups.ToArray();
    }

    /// <summary>Events are (absoluteTick, bytes); sorted, delta-encoded, end-of-track appended.</summary>
    private static byte[] Track(params (int Tick, byte[] Data)[] events)
    {
        var body = new List<byte>();
        var previousTick = 0;
        foreach (var (tick, data) in events.OrderBy(entry => entry.Tick))
        {
            body.AddRange(VariableLength(tick - previousTick));
            body.AddRange(data);
            previousTick = tick;
        }
        body.AddRange(new byte[] { 0, 0xFF, 0x2F, 0 });
        var chunk = new List<byte> { (byte)'M', (byte)'T', (byte)'r', (byte)'k' };
        chunk.AddRange(BitConverter.GetBytes(body.Count).Reverse());
        chunk.AddRange(body);
        return chunk.ToArray();
    }

    private static byte[] MidiFile(int format, params byte[][] tracks)
    {
        var file = new List<byte> { (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, (byte)format, 0, (byte)tracks.Length, 0, TicksPerQuarter };
        foreach (var track in tracks)
            file.AddRange(track);
        return file.ToArray();
    }

    private static (int, byte[]) Tempo500000() => (0, new byte[] { 0xFF, 0x51, 3, 0x07, 0xA1, 0x20 });
    private static (int, byte[]) NoteOn(int stepTick, int channel, int note) => (stepTick, new byte[] { (byte)(0x90 | channel), (byte)note, 100 });
    private static (int, byte[]) NoteOff(int stepTick, int channel, int note) => (stepTick, new byte[] { (byte)(0x80 | channel), (byte)note, 0 });

    [Fact]
    public void FormatZeroChordAndTempoAreImported()
    {
        var file = MidiFile(0, Track(
            Tempo500000(),
            NoteOn(0, 0, 60), NoteOn(0, 0, 64), NoteOn(0, 0, 67),
            NoteOff(96, 0, 60), NoteOff(96, 0, 64), NoteOff(96, 0, 67),
            NoteOn(96, 0, 72), NoteOff(96 + TicksPerStep, 0, 72)));

        var result = MidiFileImporter.Import(file, "chordy");

        Assert.Equal(120, result.Project.Bpm);
        var pattern = result.Project.Tracks[0].Clips[0]!.Pattern;
        Assert.Equal(new[] { "C4+E4+G4", "_", "_", "_", "C5" }, pattern.Take(5));
        Assert.Contains(result.ImportLedger, line => line.Contains("imported 4 notes"));
    }

    [Fact]
    public void DrumChannelSplitsIntoKickSnareAndHat()
    {
        var file = MidiFile(0, Track(
            NoteOn(0, 9, 36), NoteOff(TicksPerStep, 9, 36),
            NoteOn(4 * TicksPerStep, 9, 38), NoteOff(5 * TicksPerStep, 9, 38),
            NoteOn(2 * TicksPerStep, 9, 42), NoteOff(3 * TicksPerStep, 9, 42)));

        var project = MidiFileImporter.Import(file, "beat").Project;

        string[] PatternOf(string instrument) => project.Tracks.Single(track => track.Instrument == instrument).Clips[0]!.Pattern.Take(8).ToArray();
        Assert.Equal(new[] { "x", ".", ".", ".", ".", ".", ".", "." }, PatternOf("kick"));
        Assert.Equal(new[] { ".", ".", ".", ".", "x", ".", ".", "." }, PatternOf("snare"));
        Assert.Equal(new[] { ".", ".", "x", ".", ".", ".", ".", "." }, PatternOf("hat"));
        Assert.Equal(0.4, project.Tracks.Single(track => track.Instrument == "hat").Gain);
    }

    [Fact]
    public void BassProgramMapsToSaw()
    {
        var file = MidiFile(0, Track(
            (0, new byte[] { 0xC0, 33 }),
            NoteOn(0, 0, 40), NoteOff(TicksPerStep, 0, 40)));

        var track = MidiFileImporter.Import(file, "bass").Project.Tracks.Single();

        Assert.Equal("saw", track.Instrument);
        Assert.Equal("Bass", track.Name);
    }

    [Fact]
    public void RepeatedFourBarSectionBecomesOneSceneWithRepeats()
    {
        var events = new List<(int, byte[])>();
        for (var section = 0; section < 2; section++)
        {
            var tick = section * 64 * TicksPerStep;
            events.Add(NoteOn(tick, 0, 60));
            events.Add(NoteOff(tick + TicksPerStep, 0, 60));
        }
        var file = MidiFile(0, Track(events.ToArray()));

        var project = MidiFileImporter.Import(file, "loopy").Project;

        Assert.Single(project.Scenes);
        var section2 = Assert.Single(project.Arrangement);
        Assert.Equal(2, section2.Repeats);
    }

    [Fact]
    public void GarbageBytesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => MidiFileImporter.Import(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }, "junk"));
        Assert.Throws<InvalidDataException>(() => MidiFileImporter.Import(Array.Empty<byte>(), "empty"));
    }

    [Fact]
    public void ImportAsPatternMergesChannelsPercussively()
    {
        var file = MidiFile(0, Track(
            NoteOn(0, 0, 60), NoteOff(TicksPerStep, 0, 60),
            NoteOn(2 * TicksPerStep, 9, 36), NoteOff(3 * TicksPerStep, 9, 36)));

        var pattern = MidiFileImporter.ImportAsPattern(file, 8, percussive: true);

        Assert.Equal(new[] { "x", ".", "x", ".", ".", ".", ".", "." }, pattern);
        Assert.True(MidiFileImporter.IsMidiFile("song.MID"));
        Assert.False(MidiFileImporter.IsMidiFile("song.wav"));
    }
}
