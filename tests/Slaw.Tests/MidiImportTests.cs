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
        Assert.Equal("Fingered Bass", track.Name);
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
        // Both repeats become one arrangement clip, eight bars of the one scene.
        Assert.Empty(project.Arrangement);
        var arrangementClip = Assert.Single(project.Timeline);
        Assert.Equal(0, arrangementClip.StartStep);
        Assert.Equal(2 * project.SceneLengthInSteps(0), arrangementClip.LengthSteps);
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

/// <summary>Real-world MIDI quirks, found in an Arabic GM file from 2010.</summary>
public class MidiImportRealWorldTests
{
    [Fact]
    public void DecodesLegacyArabicTrackNames()
    {
        // "يا طارشي" as written by a Windows-1256 sequencer.
        var windows1256Bytes = new byte[] { 0xED, 0xC7, 0x20, 0xD8, 0xC7, 0xD1, 0xD4, 0xED };
        Assert.Equal("يا طارشي", SlopstackLabs.Slaw.Session.MidiFileImporter.DecodeMidiText(windows1256Bytes));
        Assert.Equal("Piano", SlopstackLabs.Slaw.Session.MidiFileImporter.DecodeMidiText("Piano"u8));
        Assert.Equal("Café", SlopstackLabs.Slaw.Session.MidiFileImporter.DecodeMidiText("Café"u8));
    }

    [Fact]
    public void FoldsUnisonDoublesAndMapsHandPercussion()
    {
        // Format 0: channels 0 and 1 play the same violin line; channel 10 plays doum/tek/wood block.
        var events = new List<(int Tick, byte[] Bytes)>
        {
            (0, new byte[] { 0xC0, 40 }), (0, new byte[] { 0xC1, 40 }),
        };
        foreach (var (tick, note) in new[] { (0, 69), (96, 71), (192, 72) })
        {
            foreach (var channel in new[] { 0, 1 })
            {
                events.Add((tick, new byte[] { (byte)(0x90 | channel), (byte)note, 90 }));
                events.Add((tick + 90, new byte[] { (byte)(0x80 | channel), (byte)note, 0 }));
            }
        }
        foreach (var (tick, drum) in new[] { (0, 45), (96, 50), (144, 77) })
        {
            events.Add((tick, new byte[] { 0x99, (byte)drum, 100 }));
            events.Add((tick + 10, new byte[] { 0x89, (byte)drum, 0 }));
        }
        var trackBytes = new List<byte>();
        var previousTick = 0;
        foreach (var (tick, bytes) in events.OrderBy(entry => entry.Tick))
        {
            var delta = tick - previousTick;
            var vlq = new List<byte> { (byte)(delta & 0x7F) };
            for (delta >>= 7; delta > 0; delta >>= 7)
            {
                vlq.Insert(0, (byte)((delta & 0x7F) | 0x80));
            }
            trackBytes.AddRange(vlq);
            trackBytes.AddRange(bytes);
            previousTick = tick;
        }
        trackBytes.AddRange(new byte[] { 0x00, 0xFF, 0x2F, 0x00 });
        var midiFile = new List<byte>();
        midiFile.AddRange("MThd"u8.ToArray());
        midiFile.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 0, 96 });
        midiFile.AddRange("MTrk"u8.ToArray());
        midiFile.AddRange(BitConverter.GetBytes(trackBytes.Count).Reverse());
        midiFile.AddRange(trackBytes);

        var importResult = SlopstackLabs.Slaw.Session.MidiFileImporter.Import(midiFile.ToArray(), "test");
        var trackSummary = importResult.Project.Tracks.Select(track => (track.Name, track.Instrument)).ToList();

        Assert.Contains(("Violin ×2", "saw"), trackSummary);
        Assert.Contains(("Drums: Low Tom", "kick"), trackSummary);
        Assert.Contains(("Drums: High Tom", "snare"), trackSummary);
        Assert.Contains(("Drums: Low Wood Block", "clap"), trackSummary);
        Assert.Contains(importResult.ImportLedger, line => line.Contains("unison"));
    }
}
