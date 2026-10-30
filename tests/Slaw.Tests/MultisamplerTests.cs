using System.IO.Compression;
using System.Text;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The multisampler instrument, and the Ableton presets that fill it.</summary>
public class MultisamplerTests
{
    private const int SampleRate = 48000;

    private static SlawProject OneSceneProject() => new()
    {
        Bpm = 120,
        Steps = 16,
        Scenes = new List<string> { "Only" },
        Arrangement = new List<ArrangementSection> { new() { Scene = 0, Repeats = 1 } },
        Tracks = new List<SlawTrack>(),
    };

    private static string WriteTone(string folder, string name, double frequency, float level = 0.5f)
    {
        var tone = Enumerable.Range(0, SampleRate * 2).Select(frame => (float)(level * Math.Sin(2 * Math.PI * frequency * frame / SampleRate))).ToArray();
        var path = Path.Combine(folder, name);
        AudioTakeAssembler.WriteTakeWave(path, tone, SampleRate);
        return path;
    }

    private static double Frequency(float[] samples, int fromFrame, int frameCount)
    {
        var crossings = 0;
        for (var frame = fromFrame + 1; frame < fromFrame + frameCount; frame++)
        {
            if (samples[frame - 1] < 0 && samples[frame] >= 0)
            {
                crossings++;
            }
        }
        return crossings * (double)SampleRate / frameCount;
    }

    private static SlawTrack Track(SlawProject project, List<SampleZone> zones, params (int Step, string Note)[] notes)
    {
        var track = AudioTakeAssembler.CreateTakeTrack(project, template: null);
        track.Instrument = "multisampler";
        track.Gain = 1;
        track.SampleZones = zones;
        track.InstrumentParameters["hold"] = 0;
        var clip = SlawClip.Empty("Only", 16);
        foreach (var (step, note) in notes)
        {
            clip.Pattern[step] = note;
        }
        track.Clips[0] = clip;
        project.Tracks.Add(track);
        return track;
    }

    [Fact]
    public void ANoteSelectsTheZonesThatCoverItsKeyAndVelocity()
    {
        var zones = new List<SampleZone>
        {
            new() { Sample = "soft", KeyLow = 60, KeyHigh = 71, VelocityLow = 1, VelocityHigh = 63 },
            new() { Sample = "hard", KeyLow = 60, KeyHigh = 71, VelocityLow = 64, VelocityHigh = 127 },
            new() { Sample = "high", KeyLow = 72, KeyHigh = 127 },
        };
        var random = new Random(1);

        Assert.Equal("soft", Assert.Single(SampleZoneMap.Select(zones, 60, 0.3, random)).Sample);
        Assert.Equal("hard", Assert.Single(SampleZoneMap.Select(zones, 60, 0.9, random)).Sample);
        Assert.Equal("high", Assert.Single(SampleZoneMap.Select(zones, 72, 0.3, random)).Sample);
        Assert.Empty(SampleZoneMap.Select(zones, 59, 0.9, random));
    }

    [Fact]
    public void OverlappingZonesLayerButARoundRobinGroupSendsOneMember()
    {
        var zones = new List<SampleZone>
        {
            new() { Sample = "layer" },
            new() { Sample = "take 1", RoundRobinGroup = 3 },
            new() { Sample = "take 2", RoundRobinGroup = 3 },
            new() { Sample = "take 3", RoundRobinGroup = 3 },
        };
        var random = new Random(1);
        var heard = new HashSet<string>();
        for (var strike = 0; strike < 40; strike++)
        {
            var selected = SampleZoneMap.Select(zones, 60, 1, random);
            Assert.Equal(2, selected.Count);
            Assert.Equal("layer", selected[0].Sample);
            heard.Add(selected[1].Sample);
        }
        Assert.Equal(3, heard.Count);
    }

    [Theory]
    [InlineData("C4@0.3", 220)]
    [InlineData("C4@0.9", 330)]
    [InlineData("C#4@0.3", 233.08)]
    [InlineData("C5@0.9", 1059.46)] // Root C5 with its zone tuned up 100 cents.
    public void EachNoteSoundsAtItsExpectedPitch(string note, double expectedHz)
    {
        var folder = Directory.CreateTempSubdirectory("slaw-zones-").FullName;
        try
        {
            var zones = new List<SampleZone>
            {
                new() { Sample = WriteTone(folder, "soft.wav", 220), RootNote = 60, KeyLow = 0, KeyHigh = 71, VelocityLow = 1, VelocityHigh = 63 },
                new() { Sample = WriteTone(folder, "hard.wav", 330), RootNote = 60, KeyLow = 0, KeyHigh = 71, VelocityLow = 64, VelocityHigh = 127 },
                new() { Sample = WriteTone(folder, "high.wav", 1000), RootNote = 72, KeyLow = 72, KeyHigh = 127, TuneCents = 100 },
            };
            var project = OneSceneProject();
            Track(project, zones, (0, note));

            var render = OfflineArrangementRenderer.Render(project, SampleRate).Left;

            Assert.InRange(Frequency(render, 2000, SampleRate / 2), expectedHz * 0.98, expectedHz * 1.02);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(0.3, 0.75)]
    [InlineData(1.0, 0.1643)]
    public void VelToVolumeLimitsHowMuchASoftNoteIsTurnedDown(double sensitivity, double expectedGain)
    {
        var parameters = new Dictionary<string, double> { ["velscale"] = sensitivity };
        Assert.Equal(expectedGain, InstrumentVoiceSynthesizer.VelocityGain("multisampler", parameters, 0.3), 2);
        Assert.Equal(0.1643, InstrumentVoiceSynthesizer.VelocityGain("sine", parameters, 0.3), 2);
    }

    [Fact]
    public void BouncesOfRoundRobinTracksAreTheSameBytesEveryTime()
    {
        var folder = Directory.CreateTempSubdirectory("slaw-zones-").FullName;
        try
        {
            var zones = new[] { 200.0, 300, 400, 500 }
                .Select((hz, index) => new SampleZone { Sample = WriteTone(folder, $"t{index}.wav", hz), RootNote = 60, RoundRobinGroup = 0 })
                .ToList();
            var project = OneSceneProject();
            Track(project, zones, (0, "C4"), (4, "C4"), (8, "C4"), (12, "C4"));

            var first = OfflineArrangementRenderer.Render(project, SampleRate).Left;
            var second = OfflineArrangementRenderer.Render(project, SampleRate).Left;

            Assert.Equal(first, second);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SavingWritesPathsBesideTheProjectAndLoadingRestoresThem()
    {
        var folder = Directory.CreateTempSubdirectory("slaw-zones-").FullName;
        try
        {
            var sample = WriteTone(folder, "note.wav", 220);
            var project = OneSceneProject();
            Track(project, new List<SampleZone> { new() { Sample = sample, RootNote = 48, KeyLow = 10, KeyHigh = 20, VelocityLow = 5, VelocityHigh = 50, Gain = 0.5, TuneCents = -8, RoundRobinGroup = 2 } });
            var projectPath = Path.Combine(folder, "song.slaw");

            ProjectSerializer.Save(project, projectPath);

            Assert.Contains("\"note.wav\"", File.ReadAllText(projectPath));
            var zone = Assert.Single(ProjectSerializer.Load(projectPath).Tracks[0].SampleZones!);
            Assert.Equal(Path.GetFullPath(sample), Path.GetFullPath(zone.Sample));
            Assert.Equal((48, 10, 20, 5, 50, 0.5, -8.0, 2), (zone.RootNote, zone.KeyLow, zone.KeyHigh, zone.VelocityLow, zone.VelocityHigh, zone.Gain, zone.TuneCents, zone.RoundRobinGroup));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void RemediationClampsZonesAndDiscardsTheUnusable()
    {
        var project = OneSceneProject();
        Track(project, new List<SampleZone>
        {
            new() { Sample = "kept.wav", RootNote = 400, KeyHigh = 500, VelocityLow = 0, Gain = double.NaN },
            new() { Sample = string.Empty },
            new() { Sample = "inverted.wav", KeyLow = 90, KeyHigh = 10 },
        });

        var ledger = ProjectAlignmentRemediator.Remediate(project);

        var kept = Assert.Single(project.Tracks[0].SampleZones!);
        Assert.Equal((127, 127, 1, 1.0), (kept.RootNote, kept.KeyHigh, kept.VelocityLow, kept.Gain));
        Assert.Contains(ledger, entry => entry.Contains("2 unusable sample zone(s)", StringComparison.Ordinal));
    }

    private static string Part(string file, int root, int keyLow, int keyHigh, int velocityLow, int velocityHigh, double detune = 0) => $"""
        <MultiSamplePart Id="0">
          <IsActive Value="true" /><RootKey Value="{root}" /><Detune Value="{detune}" /><Volume Value="1" /><SampleStart Value="0" /><SampleEnd Value="0" />
          <KeyRange><Min Value="{keyLow}" /><Max Value="{keyHigh}" /></KeyRange>
          <VelocityRange><Min Value="{velocityLow}" /><Max Value="{velocityHigh}" /></VelocityRange>
          <SampleRef><FileRef><RelativePath Value="{file}" /><Path Value="{file}" /></FileRef></SampleRef>
        </MultiSamplePart>
        """;

    private static string Simpler(string parts, bool roundRobin = false, int playbackMode = 0) => $"""
        <AbletonDevicePreset><Device><OriginalSimpler Id="0">
          <Globals><PlaybackMode Value="{playbackMode}" /></Globals>
          <VolumeAndPan><Volume><Manual Value="-6" /></Volume><Envelope><AttackTime><Manual Value="2" /></AttackTime><ReleaseTime><Manual Value="900" /></ReleaseTime></Envelope></VolumeAndPan>
          <Player><MultiSampleMap><RoundRobin Value="{(roundRobin ? "true" : "false")}" /><SampleParts>{parts}</SampleParts></MultiSampleMap></Player>
        </OriginalSimpler></Device></AbletonDevicePreset>
        """;

    private static string Rack(string device, string branches, string macros = "") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Ableton MajorVersion="5"><GroupDevicePreset><Device><{device} Id="0">{macros}</{device}></Device><BranchPresets>{branches}</BranchPresets></GroupDevicePreset></Ableton>
        """;

    private static string WritePreset(string folder, string name, string xml)
    {
        var path = Path.Combine(folder, name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        gzip.Write(Encoding.UTF8.GetBytes(xml.TrimStart()));
        return path;
    }

    [Fact]
    public void AnInstrumentRackBecomesZonesWithItsMapAndWhatWasLeftOutIsNamed()
    {
        var folder = Directory.CreateTempSubdirectory("slaw-adg-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Samples"));
            WriteTone(folder, "Samples/soft.wav", 220, 0.25f);
            WriteTone(folder, "Samples/hard.wav", 220, 0.5f);
            var branch = $"""
                <InstrumentBranchPreset><DevicePresets>
                  {Simpler(Part("Samples/soft.wav", 48, 0, 50, 1, 63, -8) + Part("Samples/hard.wav", 48, 0, 50, 64, 127, -8) + Part("Samples/gone.wav", 60, 51, 60, 1, 127))}
                  <AbletonDevicePreset><Device><Chorus2 Id="0" /></Device></AbletonDevicePreset>
                </DevicePresets></InstrumentBranchPreset>
                """;
            var preset = WritePreset(folder, "Keys.adg", Rack("InstrumentGroupDevice", branch, """<MacroDisplayNames.0 Value="Impact" /><MacroDisplayNames.1 Value="Macro 2" />"""));

            var rack = AbletonRackImporter.Read(preset);

            Assert.Equal(("Keys", "Instrument"), (rack.Name, rack.Kind));
            Assert.Equal(2, rack.Zones.Count);
            var soft = rack.Zones[0];
            Assert.Equal((48, 0, 50, 1, 63, -8.0), (soft.RootNote, soft.KeyLow, soft.KeyHigh, soft.VelocityLow, soft.VelocityHigh, soft.TuneCents));
            Assert.Equal(1, rack.InstrumentParameters["hold"]);
            Assert.Equal(900, rack.InstrumentParameters["release"]);
            // Balance between takes survives; the loudest is brought to a usable level.
            Assert.Equal(1.0, rack.Zones[0].Gain / rack.Zones[1].Gain, 3);
            Assert.InRange(0.5 * rack.Zones[1].Gain, 0.75, 0.85);
            Assert.Contains("gone.wav", rack.MissingFiles);
            Assert.Contains("Chorus-Ensemble", rack.LeftOut);
            Assert.Contains("1 macro", rack.LeftOut);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AChainsFaderBalancesItsLayerAndAMutedChainStaysSilent()
    {
        var folder = Directory.CreateTempSubdirectory("slaw-adg-").FullName;
        try
        {
            WriteTone(folder, "main.wav", 220);
            WriteTone(folder, "quiet.wav", 330);
            WriteTone(folder, "muted.wav", 440);
            string Chain(string file, string volume, string speaker) => $"""
                <InstrumentBranchPreset><DevicePresets>{Simpler(Part(file, 60, 0, 127, 1, 127))}</DevicePresets>
                <MixerPreset><AbletonDevicePreset><Device><AudioBranchMixerDevice Id="0"><Speaker><Manual Value="{speaker}" /></Speaker><Volume><Manual Value="{volume}" /></Volume></AudioBranchMixerDevice></Device></AbletonDevicePreset></MixerPreset>
                </InstrumentBranchPreset>
                """;
            var preset = WritePreset(folder, "Layers.adg", Rack("InstrumentGroupDevice", Chain("main.wav", "1", "true") + Chain("quiet.wav", "0.1", "true") + Chain("muted.wav", "1", "false")));

            var rack = AbletonRackImporter.Read(preset);

            Assert.Equal(2, rack.Zones.Count);
            Assert.Equal(0.1, rack.Zones[1].Gain / rack.Zones[0].Gain, 3);
            Assert.InRange(0.5 * rack.Zones[0].Gain, 0.75, 0.85);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DrumRackPadsLandOnTheirOwnNotesAtOriginalPitch()
    {
        var folder = Directory.CreateTempSubdirectory("slaw-adg-").FullName;
        try
        {
            WriteTone(folder, "a.wav", 220);
            WriteTone(folder, "b.wav", 330);
            string Pad(int receiving, string file) => $"""
                <DrumBranchPreset><DevicePresets>{Simpler(Part(file, 60, 0, 127, 1, 127), playbackMode: 1)}</DevicePresets>
                <ZoneSettings><ReceivingNote Value="{receiving}" /><SendingNote Value="60" /><ChokeGroup Value="0" /></ZoneSettings></DrumBranchPreset>
                """;
            var preset = WritePreset(folder, "Kit.adg", Rack("DrumGroupDevice", Pad(92, "a.wav") + Pad(91, "b.wav")));

            var rack = AbletonRackImporter.Read(preset);

            Assert.Equal("Drum Rack", rack.Kind);
            Assert.Equal(0, rack.InstrumentParameters["hold"]);
            Assert.Equal(new[] { (36, 36, 36), (37, 37, 37) }, rack.Zones.Select(zone => (zone.KeyLow, zone.KeyHigh, zone.RootNote)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ARoundRobinDeviceGivesItsZonesOneGroup()
    {
        var folder = Directory.CreateTempSubdirectory("slaw-adg-").FullName;
        try
        {
            WriteTone(folder, "a.wav", 220);
            WriteTone(folder, "b.wav", 330);
            var branch = $"""<InstrumentBranchPreset><DevicePresets>{Simpler(Part("a.wav", 60, 0, 127, 1, 127) + Part("b.wav", 60, 0, 127, 1, 127), roundRobin: true)}</DevicePresets></InstrumentBranchPreset>""";

            var rack = AbletonRackImporter.Read(WritePreset(folder, "Hits.adg", Rack("InstrumentGroupDevice", branch)));

            Assert.All(rack.Zones, zone => Assert.Equal(0, zone.RoundRobinGroup));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void PresetsWithNothingPlayableSayWhy()
    {
        var folder = Directory.CreateTempSubdirectory("slaw-adg-").FullName;
        try
        {
            var branch = """<InstrumentBranchPreset><DevicePresets><AbletonDevicePreset><Device><Operator Id="0" /></Device></AbletonDevicePreset></DevicePresets></InstrumentBranchPreset>""";
            var synthOnly = Assert.Throws<InvalidDataException>(() => AbletonRackImporter.Read(WritePreset(folder, "Synth.adg", Rack("InstrumentGroupDevice", branch))));
            Assert.Contains("Operator", synthOnly.Message);

            var notAPreset = Path.Combine(folder, "Fake.adg");
            File.WriteAllText(notAPreset, "this is not xml");
            Assert.Throws<InvalidDataException>(() => AbletonRackImporter.Read(notAPreset));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
