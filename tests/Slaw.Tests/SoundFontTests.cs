using System.Text;
using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Mcp;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.SoundFonts;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>SoundFonts: other people's samples, finally welcomed into the deliverable pipeline.</summary>
public class SoundFontTests
{
    private const int OutputSampleRate = 44100;
    private const int SamplePeriodFrames = 100;

    // ── A tiny SoundFont, built byte by byte ──────────────────────────────────

    private static byte[] Chunk(string id, byte[] body)
    {
        var chunk = new List<byte>(Encoding.ASCII.GetBytes(id));
        chunk.AddRange(BitConverter.GetBytes(body.Length));
        chunk.AddRange(body);
        if (body.Length % 2 == 1)
        {
            chunk.Add(0);
        }
        return chunk.ToArray();
    }

    private static byte[] List(string type, params byte[][] subchunks) =>
        Chunk("LIST", Encoding.ASCII.GetBytes(type).Concat(subchunks.SelectMany(subchunk => subchunk)).ToArray());

    private static byte[] Name(string name) => Encoding.ASCII.GetBytes(name.PadRight(20, '\0')[..20]);

    private static byte[] Generator(SoundFontGenerator generator, short amount) =>
        BitConverter.GetBytes((ushort)generator).Concat(BitConverter.GetBytes(amount)).ToArray();

    private static byte[] RangeGenerator(SoundFontGenerator generator, int low, int high) =>
        BitConverter.GetBytes((ushort)generator).Concat(new[] { (byte)low, (byte)high }).ToArray();

    /// <summary>
    /// One looping sine sample (period 100 frames at 44.1 kHz = 441 Hz, root A4) in
    /// one instrument with a global zone (release 0.5 s, attack 0.1 s) and a local
    /// zone limited to keys 40–96; preset "Test Sine" (0:0) and kit "Test Kit" (128:0) play it.
    /// </summary>
    internal static byte[] TinySoundFont()
    {
        const int sampleFrames = 4410;
        var sampleBytes = new List<byte>();
        for (var frame = 0; frame < sampleFrames; frame++)
        {
            sampleBytes.AddRange(BitConverter.GetBytes((short)(16000 * Math.Sin(2 * Math.PI * frame / SamplePeriodFrames))));
        }
        sampleBytes.AddRange(new byte[46 * 2]); // the spec's 46 zero frames after each sample

        static byte[] PresetHeader(string name, int program, int bank, int bagIndex) =>
            Name(name).Concat(BitConverter.GetBytes((ushort)program)).Concat(BitConverter.GetBytes((ushort)bank))
                .Concat(BitConverter.GetBytes((ushort)bagIndex)).Concat(new byte[12]).ToArray();
        static byte[] Bag(int generatorIndex) => BitConverter.GetBytes((ushort)generatorIndex).Concat(BitConverter.GetBytes((ushort)0)).ToArray();
        static byte[] SampleHeader(string name, int start, int end, int loopStart, int loopEnd, int originalPitch, int sampleType) =>
            Name(name).Concat(BitConverter.GetBytes(start)).Concat(BitConverter.GetBytes(end)).Concat(BitConverter.GetBytes(loopStart))
                .Concat(BitConverter.GetBytes(loopEnd)).Concat(BitConverter.GetBytes(OutputSampleRate)).Concat(new[] { (byte)originalPitch, (byte)0 })
                .Concat(BitConverter.GetBytes((ushort)0)).Concat(BitConverter.GetBytes((ushort)sampleType)).ToArray();

        var presetHeaders = PresetHeader("Test Sine", 0, 0, 0).Concat(PresetHeader("Test Kit", 0, 128, 1)).Concat(PresetHeader("EOP", 0, 0, 2)).ToArray();
        var presetBags = Bag(0).Concat(Bag(1)).Concat(Bag(2)).ToArray();
        var presetGenerators = Generator(SoundFontGenerator.Instrument, 0).Concat(Generator(SoundFontGenerator.Instrument, 0)).Concat(new byte[4]).ToArray();
        var instrumentHeaders = Name("Sine Inst").Concat(BitConverter.GetBytes((ushort)0)).Concat(Name("EOI")).Concat(BitConverter.GetBytes((ushort)2)).ToArray();
        // Global zone: generators 0–1; local zone: generators 2–4.
        var instrumentBags = Bag(0).Concat(Bag(2)).Concat(Bag(5)).ToArray();
        var instrumentGenerators = Generator(SoundFontGenerator.ReleaseVolumeEnvelope, (short)Math.Round(1200 * Math.Log2(0.5)))
            .Concat(Generator(SoundFontGenerator.AttackVolumeEnvelope, (short)Math.Round(1200 * Math.Log2(0.1))))
            .Concat(RangeGenerator(SoundFontGenerator.KeyRange, 40, 96))
            .Concat(Generator(SoundFontGenerator.SampleModes, 1))
            .Concat(Generator(SoundFontGenerator.SampleId, 0))
            .Concat(new byte[4]).ToArray();
        var sampleHeaders = SampleHeader("sine", 0, sampleFrames, 1000, 1000 + 20 * SamplePeriodFrames, 69, 1)
            .Concat(SampleHeader("EOS", 0, 0, 0, 0, 0, 0)).ToArray();

        var body = Encoding.ASCII.GetBytes("sfbk")
            .Concat(List("INFO", Chunk("ifil", new byte[] { 2, 0, 1, 0 }), Chunk("INAM", Encoding.ASCII.GetBytes("Tiny Test Font\0"))))
            .Concat(List("sdta", Chunk("smpl", sampleBytes.ToArray())))
            .Concat(List("pdta",
                Chunk("phdr", presetHeaders), Chunk("pbag", presetBags), Chunk("pmod", new byte[10]), Chunk("pgen", presetGenerators),
                Chunk("inst", instrumentHeaders), Chunk("ibag", instrumentBags), Chunk("imod", new byte[10]), Chunk("igen", instrumentGenerators),
                Chunk("shdr", sampleHeaders)))
            .ToArray();
        return Chunk("RIFF", body);
    }

    private static string WriteTinySoundFont(string folder)
    {
        var fontPath = Path.Combine(folder, "tiny.sf2");
        File.WriteAllBytes(fontPath, TinySoundFont());
        return fontPath;
    }

    private static double EstimatedFrequency(float[] voice, int fromFrame, int toFrame)
    {
        var crossings = 0;
        for (var frame = fromFrame + 1; frame < toFrame; frame++)
        {
            if (voice[frame - 1] < 0 != voice[frame] < 0)
            {
                crossings++;
            }
        }
        return crossings / 2.0 / ((toFrame - fromFrame) / (double)OutputSampleRate);
    }

    private static double PeakBetween(float[] voice, double fromSeconds, double toSeconds) =>
        voice.Skip((int)(fromSeconds * OutputSampleRate)).Take((int)((toSeconds - fromSeconds) * OutputSampleRate)).DefaultIfEmpty(0f).Max(Math.Abs);

    [Fact]
    public void ParsesPresetsAndResolvesGlobalAndLocalZones()
    {
        var bank = SoundFontBank.Parse(TinySoundFont(), "fallback");
        Assert.Equal("Tiny Test Font", bank.Name);
        Assert.Equal(new[] { "0:0 Test Sine", "128:0 Test Kit" }, bank.Presets.Select(preset => preset.Label));
        var region = Assert.Single(bank.FindPreset(0, 0)!.Regions);
        Assert.Equal((40, 96), (region.KeyLow, region.KeyHigh));
        Assert.Equal(1, region[SoundFontGenerator.SampleModes]);
        Assert.Equal(-1200, region[SoundFontGenerator.ReleaseVolumeEnvelope]);
        // Program 40 is missing: the nearest program in bank 0 stands in. The kit is found by bank 128.
        Assert.Equal("Test Sine", bank.FindPreset(0, 40)?.Name);
        Assert.Equal("Test Kit", bank.DrumKit()?.Name);
    }

    [Fact]
    public void RendersAtTheRootKeyPitchLoopsWhileHeldAndReleases()
    {
        var preset = SoundFontBank.Parse(TinySoundFont(), "tiny").FindPreset(0, 0)!;
        var rootVoice = SoundFontVoiceRenderer.RenderNote(preset, 69, gateDurationSeconds: 1.0, OutputSampleRate, transposeSemitones: 0, releaseScale: 1);
        var octaveVoice = SoundFontVoiceRenderer.RenderNote(preset, 81, 1.0, OutputSampleRate, 0, 1);

        // Root key plays the sample as recorded (441 Hz); an octave up doubles it.
        Assert.InRange(EstimatedFrequency(rootVoice, OutputSampleRate / 5, OutputSampleRate / 2), 430, 452);
        Assert.InRange(EstimatedFrequency(octaveVoice, OutputSampleRate / 5, OutputSampleRate / 2), 860, 904);

        // The sample is 0.1 s long: only the loop keeps it sounding for the whole 1 s gate, then the 0.5 s release tail.
        Assert.True(PeakBetween(rootVoice, 0.8, 0.95) > 0.3, "the loop sustains the note while held");
        Assert.InRange(rootVoice.Length / (double)OutputSampleRate, 1.45, 1.55);
        Assert.True(PeakBetween(rootVoice, 1.40, 1.5) < 0.01, "the release falls away");

        // The 0.1 s attack from the global zone: quiet at first.
        Assert.True(PeakBetween(rootVoice, 0, 0.01) < PeakBetween(rootVoice, 0.2, 0.3) * 0.2);

        // Outside the zone's key range nothing plays.
        Assert.Empty(SoundFontVoiceRenderer.RenderNote(preset, 30, 1.0, OutputSampleRate, 0, 1));
    }

    [Fact]
    public void SoundFontTracksRenderThroughTheEngineAndSaveTheirFontRelative()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-sf2-").FullName;
        try
        {
            var fontPath = WriteTinySoundFont(workingFolder);
            var project = new SlawProject { Bpm = 120, Scenes = new List<string> { "A" } };
            var fontTrack = SoundFontDeviceOperations.CreateTrack(project, SoundFontDeviceOperations.Resolve(fontPath, 0, 0));
            fontTrack.Gain = 1;
            project.Tracks.Add(fontTrack);
            project.Timeline.Add(new TimelineClip { TrackId = fontTrack.Id, StartStep = 0, LengthSteps = 16, Clip = new SlawClip { Name = "A", Pattern = CompactPatternJsonConverter.SplitStepString("A4:8 . . . . . . . . . . . . . . .") } });

            var render = OfflineArrangementRenderer.Render(project, OutputSampleRate).Left;
            Assert.True(render.Take(OutputSampleRate / 2).Max(Math.Abs) > 0.05, "the note plays");

            var projectPath = Path.Combine(workingFolder, "song.slaw");
            ProjectSerializer.Save(project, projectPath);
            Assert.Contains("\"path\": \"tiny.sf2\"", File.ReadAllText(projectPath));
            var reloaded = ProjectSerializer.Load(projectPath);
            Assert.Equal(Path.GetFullPath(fontPath), reloaded.Tracks[0].SoundFont!.Path);
            Assert.Equal("Test Sine", reloaded.Tracks[0].SoundFont!.PresetName);
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    // ── MIDI with a SoundFont ─────────────────────────────────────────────────

    private const int TicksPerQuarter = 96;
    private const int TicksPerStep = TicksPerQuarter / 4;

    private static byte[] MidiFileWith(params (int Tick, byte[] Data)[] events)
    {
        var body = new List<byte>();
        var previousTick = 0;
        foreach (var (tick, data) in events.OrderBy(entry => entry.Tick))
        {
            var delta = tick - previousTick;
            var groups = new Stack<byte>();
            groups.Push((byte)(delta & 0x7F));
            while ((delta >>= 7) > 0)
            {
                groups.Push((byte)((delta & 0x7F) | 0x80));
            }
            body.AddRange(groups);
            body.AddRange(data);
            previousTick = tick;
        }
        body.AddRange(new byte[] { 0, 0xFF, 0x2F, 0 });
        var file = new List<byte> { (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 0, 0, 1, 0, TicksPerQuarter, (byte)'M', (byte)'T', (byte)'r', (byte)'k' };
        file.AddRange(BitConverter.GetBytes(body.Count).Reverse());
        file.AddRange(body);
        return file.ToArray();
    }

    /// <summary>
    /// Channel 0: violin (program 40), bend range set to 1 semitone by RPN 0, bent a
    /// full semitone up at step 2. Channel 1: strings (program 48), unbent. Channel 9: kick and snare.
    /// </summary>
    private static byte[] BentSongWithDrums() => MidiFileWith(
        (0, new byte[] { 0xC0, 40 }),
        (0, new byte[] { 0xC1, 48 }),
        (0, new byte[] { 0xB0, 101, 0 }), (0, new byte[] { 0xB0, 100, 0 }), (0, new byte[] { 0xB0, 6, 1 }), (0, new byte[] { 0xB0, 38, 0 }),
        (0, new byte[] { 0x90, 69, 100 }), (8 * TicksPerStep, new byte[] { 0x80, 69, 0 }),
        (2 * TicksPerStep, new byte[] { 0xE0, 0x7F, 0x7F }),
        (0, new byte[] { 0x91, 60, 100 }), (4 * TicksPerStep, new byte[] { 0x81, 60, 0 }),
        (0, new byte[] { 0x99, 36, 100 }), (1 * TicksPerStep, new byte[] { 0x89, 36, 0 }),
        (4 * TicksPerStep, new byte[] { 0x99, 38, 100 }), (5 * TicksPerStep, new byte[] { 0x89, 38, 0 }));

    [Fact]
    public void ImportsChannelsOntoFontPresetsAndDrumsOntoOneKitTrack()
    {
        var bank = SoundFontBank.Parse(TinySoundFont(), "tiny");
        var result = MidiFileImporter.Import(BentSongWithDrums(), "song", new MidiSoundFontChoice("/fonts/tiny.sf2", bank));
        var tracks = result.Project.Tracks;

        Assert.All(tracks, track => Assert.Equal("soundfont", track.Instrument));
        var kit = Assert.Single(tracks, track => track.SoundFont!.Bank == 128);
        Assert.Equal("Drums", kit.Name);
        // The kit keeps the real drum notes (36, 38), so the font plays the right drum for each.
        var kitNotes = result.Project.Timeline.Where(clip => clip.TrackId == kit.Id)
            .SelectMany(clip => clip.Clip.Pattern).Where(step => step != NotationResolver.RestSymbol && step != NotationResolver.SustainSymbol).ToList();
        Assert.Equal(new[] { "C2", "D2" }, kitNotes);
        Assert.Contains(result.ImportLedger, line => line.StartsWith("voiced with SoundFont tiny.sf2"));

        // Without a font the old behaviour stands: drums split by kind, programs on built-ins.
        var builtIn = MidiFileImporter.Import(BentSongWithDrums(), "song");
        Assert.DoesNotContain(builtIn.Project.Tracks, track => track.Instrument == "soundfont");
        Assert.Contains(builtIn.Project.Tracks, track => track.Instrument == "kick");
    }

    [Fact]
    public void PitchBendsBecomePitchEnvelopesUsingTheChannelsBendRange()
    {
        var result = MidiFileImporter.Import(BentSongWithDrums(), "song");
        var violin = result.Project.Tracks.Single(track => track.Name == "Violin");
        var violinClip = result.Project.Timeline.Single(clip => clip.TrackId == violin.Id).Clip;
        var pitchPoints = violinClip.Automation![ClipAutomation.PitchTarget];
        // RPN 0 set ±1 semitone; a full bend at step 2 holds 0 until then, then steps to +1.
        Assert.Equal(0, ClipAutomation.Evaluate(pitchPoints, 1.9), 3);
        Assert.Equal(1, ClipAutomation.Evaluate(pitchPoints, 2), 3);
        Assert.Equal(1, ClipAutomation.Evaluate(pitchPoints, 6), 3);
        Assert.Contains(result.ImportLedger, line => line == "imported 1 pitch bend(s) as pitch envelopes on 1 track(s)");

        // Unbent channels and drums carry no pitch envelope.
        Assert.All(result.Project.Tracks.Where(track => track.Id != violin.Id), track =>
            Assert.All(result.Project.Timeline.Where(clip => clip.TrackId == track.Id), clip => Assert.Null(clip.Clip.Automation)));
    }

    [Fact]
    public void FindsTheSoundFontThatCameWithAMidiFile()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-sf2-").FullName;
        try
        {
            var midiPath = Path.Combine(workingFolder, "song.mid");
            File.WriteAllBytes(midiPath, BentSongWithDrums());
            Assert.Null(MidiFileImporter.FindCompanionSoundFont(midiPath));
            var onlyFont = WriteTinySoundFont(workingFolder);
            Assert.Equal(onlyFont, MidiFileImporter.FindCompanionSoundFont(midiPath));
            File.WriteAllBytes(Path.Combine(workingFolder, "other.sf2"), TinySoundFont());
            Assert.Null(MidiFileImporter.FindCompanionSoundFont(midiPath));
            File.WriteAllBytes(Path.Combine(workingFolder, "song.sf2"), TinySoundFont());
            Assert.Equal(Path.Combine(workingFolder, "song.sf2"), MidiFileImporter.FindCompanionSoundFont(midiPath));
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    [Fact]
    public async Task McpDescribesFontsAddsFontTracksAndImportsMidiWithTheCompanionFont()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-sf2-").FullName;
        try
        {
            var fontPath = WriteTinySoundFont(workingFolder);
            var midiPath = Path.Combine(workingFolder, "song.mid");
            File.WriteAllBytes(midiPath, BentSongWithDrums());
            var servedDocument = new StudioDocument(ProjectSerializer.CreateStarterProject("mcp"), filePath: null);
            var dispatcher = new McpProtocolDispatcher(new SlawMcpToolset(new HeadlessMcpHost(servedDocument, new StudioPreferences { AiFeaturesEnabled = false })));
            async Task<string> CallAsync(string toolName, JsonObject toolArguments)
            {
                var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = toolArguments } };
                var response = JsonNode.Parse((await dispatcher.HandleAsync(request.ToJsonString(), CancellationToken.None))!)!;
                return (response["result"]!["isError"]!.GetValue<bool>() ? "ERROR " : string.Empty) + response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            }

            Assert.Contains("128:0 \"Test Kit\" (drum kit", await CallAsync("describe_soundfont", new JsonObject { ["path"] = fontPath }));
            var added = await CallAsync("add_track", new JsonObject { ["instrument"] = "soundfont", ["soundfont"] = new JsonObject { ["path"] = fontPath, ["bank"] = 0, ["program"] = 0 } });
            Assert.Contains("(soundfont tiny.sf2 0:0 Test Sine)", added);
            Assert.StartsWith("ERROR", await CallAsync("add_track", new JsonObject { ["instrument"] = "soundfont", ["soundfont"] = new JsonObject { ["path"] = Path.Combine(workingFolder, "missing.sf2") } }));

            var imported = await CallAsync("import_midi", new JsonObject { ["path"] = midiPath });
            Assert.Contains("voiced with SoundFont tiny.sf2", imported);
            Assert.Contains("soundfont \"tiny.sf2 128:0 Test Kit\"", imported);
            var builtIn = await CallAsync("import_midi", new JsonObject { ["path"] = midiPath, ["soundfont"] = "none" });
            Assert.DoesNotContain("voiced with SoundFont", builtIn);
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    /// <summary>The user's real files, when pointed at with SLAW_TEST_MIDI and SLAW_TEST_SF2.</summary>
    [Fact]
    public void RealMidiFileWithItsSoundFontPlaysEveryTrack()
    {
        var midiPath = Environment.GetEnvironmentVariable("SLAW_TEST_MIDI");
        var fontPath = Environment.GetEnvironmentVariable("SLAW_TEST_SF2");
        if (string.IsNullOrEmpty(midiPath) || string.IsNullOrEmpty(fontPath) || !File.Exists(midiPath) || !File.Exists(fontPath))
        {
            return;
        }
        var result = MidiFileImporter.Import(midiPath, new MidiSoundFontChoice(fontPath, SoundFontLibrary.Load(fontPath)));
        Assert.All(result.Project.Tracks, track => Assert.Equal("soundfont", track.Instrument));
        foreach (var track in result.Project.Tracks)
        {
            var soloed = result.Project.DeepClone();
            foreach (var soloTrack in soloed.Tracks)
            {
                soloTrack.Muted = soloTrack.Id != track.Id;
            }
            var render = OfflineArrangementRenderer.Render(soloed, 22050).Left;
            Assert.True(render.Max(Math.Abs) > 0.01, $"{track.Name} is silent");
        }
    }
}
