using System.IO.Compression;
using System.Text;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;
using Xunit;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The "operator" instrument, and Ableton Operator presets arriving on it.</summary>
public class OperatorRackTests
{
    private const int SampleRate = 44100;

    private static float[] Render(Dictionary<string, double> parameters, int midiNote = 69, double gateSeconds = 0.5) =>
        InstrumentVoiceSynthesizer.RenderVoice(OperatorPatch.InstrumentName, new[] { midiNote }, gateSeconds, SampleRate, new Random(7), parameters);

    private static double Rms(float[] voice, int firstFrame, int frameCount)
    {
        var sum = 0.0;
        for (var frame = firstFrame; frame < firstFrame + frameCount; frame++)
        {
            sum += voice[frame] * voice[frame];
        }
        return Math.Sqrt(sum / frameCount);
    }

    private static double RisingCrossingsPerSecond(float[] voice, int firstFrame, int frameCount)
    {
        var crossings = 0;
        for (var frame = firstFrame + 1; frame < firstFrame + frameCount; frame++)
        {
            if (voice[frame - 1] < 0 && voice[frame] >= 0)
            {
                crossings++;
            }
        }
        return crossings / (double)frameCount * SampleRate;
    }

    /// <summary>Counting crossings resolves pitch to about a percent over these windows, which is as far as it can be trusted.</summary>
    private static void AssertPitch(double expectedHertz, double measuredHertz) =>
        Assert.InRange(measuredHertz, expectedHertz * 0.98, expectedHertz * 1.02);

    /// <summary>Energy of the frame-to-frame difference relative to the signal: a rough measure of brightness.</summary>
    private static double Brightness(float[] voice, int firstFrame, int frameCount)
    {
        var difference = new float[frameCount - 1];
        for (var frame = 0; frame < difference.Length; frame++)
        {
            difference[frame] = voice[firstFrame + frame + 1] - voice[firstFrame + frame];
        }
        return Rms(difference, 0, difference.Length) / Math.Max(1e-9, Rms(voice, firstFrame, frameCount));
    }

    [Fact]
    public void A_lone_sine_plays_the_note_at_the_right_pitch()
    {
        var voice = Render(new Dictionary<string, double> { ["aOn"] = 1 });
        AssertPitch(440, RisingCrossingsPerSecond(voice, 2000, 15000));
    }

    [Theory]
    [InlineData(1, 0, 440)]
    [InlineData(2, 0, 880)]
    [InlineData(0, 0, 220)]
    [InlineData(1, 500, 660)]
    public void Coarse_and_fine_set_the_ratio_with_coarse_zero_an_octave_down(double coarse, double fine, double expectedHertz)
    {
        var voice = Render(new Dictionary<string, double> { ["aCoarse"] = coarse, ["aFine"] = fine });
        AssertPitch(expectedHertz, RisingCrossingsPerSecond(voice, 2000, 15000));
    }

    [Fact]
    public void A_fixed_oscillator_ignores_the_note()
    {
        var parameters = new Dictionary<string, double> { ["aFixed"] = 1, ["aFreq"] = 300 };
        AssertPitch(300, RisingCrossingsPerSecond(Render(parameters, 40), 2000, 15000));
        AssertPitch(300, RisingCrossingsPerSecond(Render(parameters, 80), 2000, 15000));
    }

    [Fact]
    public void Transpose_moves_the_note_in_semitones()
    {
        var voice = Render(new Dictionary<string, double> { ["transpose"] = -12 });
        AssertPitch(220, RisingCrossingsPerSecond(voice, 2000, 15000));
    }

    [Fact]
    public void A_modulator_brightens_a_serial_algorithm_and_is_heard_itself_in_a_parallel_one()
    {
        var serial = new Dictionary<string, double> { ["algorithm"] = 9, ["bOn"] = 1, ["bCoarse"] = 3 };
        var carrierAlone = Render(new Dictionary<string, double> { ["algorithm"] = 9 });
        Assert.True(Brightness(Render(serial), 2000, 15000) > Brightness(carrierAlone, 2000, 15000) * 1.5);

        var parallel = new Dictionary<string, double> { ["algorithm"] = 10, ["bOn"] = 1 };
        Assert.True(Rms(Render(parallel), 2000, 15000) > Rms(carrierAlone, 2000, 15000) * 1.5);
    }

    [Fact]
    public void Every_algorithm_only_modulates_from_later_oscillators_and_hears_something()
    {
        Assert.Equal(OperatorPatch.AlgorithmCount, OperatorPatch.AlgorithmModulators.Length);
        foreach (var modulators in OperatorPatch.AlgorithmModulators)
        {
            Assert.Equal(OperatorPatch.OscillatorCount, modulators.Length);
            for (var oscillator = 0; oscillator < modulators.Length; oscillator++)
            {
                Assert.All(modulators[oscillator], source => Assert.True(source > oscillator));
            }
        }
        for (var algorithm = 0; algorithm < OperatorPatch.AlgorithmCount; algorithm++)
        {
            var voice = Render(new Dictionary<string, double> { ["algorithm"] = algorithm, ["aOn"] = 1, ["bOn"] = 1, ["cOn"] = 1, ["dOn"] = 1 });
            Assert.True(Rms(voice, 2000, 15000) > 0.05, $"algorithm {algorithm}");
        }
    }

    [Fact]
    public void Feedback_adds_harmonics_to_an_unmodulated_oscillator()
    {
        var plain = Render(new Dictionary<string, double> { ["aOn"] = 1 });
        var fed = Render(new Dictionary<string, double> { ["aOn"] = 1, ["aFeedback"] = 80 });
        Assert.True(Brightness(fed, 2000, 15000) > Brightness(plain, 2000, 15000) * 1.5);
    }

    [Fact]
    public void A_percussive_envelope_decays_during_the_gate_and_a_held_one_does_not()
    {
        var plucked = Render(new Dictionary<string, double> { ["aDecay"] = 40, ["aSustain"] = 0 });
        Assert.True(Rms(plucked, 100, 500) > Rms(plucked, 10000, 500) * 20);

        var held = Render(new Dictionary<string, double> { ["aSustain"] = 1 });
        Assert.Equal(Rms(held, 2000, 1000), Rms(held, 15000, 1000), 1);
    }

    [Fact]
    public void An_envelope_can_start_at_full_level_and_a_sub_survives_the_filter_it_was_saved_with()
    {
        var startsFull = Render(new Dictionary<string, double> { ["aInitial"] = 1, ["aAttack"] = 400 });
        Assert.True(Rms(startsFull, 0, 200) > 0.3);

        var oscillator = $"""<IsOn><Manual Value="true" /></IsOn><Tune>{Saved("Coarse", "0", "1")}</Tune><Volume><Manual Value="1" /></Volume>""";
        var filter = $"""<Filter>{Saved("OnOff", "true", "false")}{Saved("Type", "3")}{Saved("Frequency", "170", "500")}</Filter>""";
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Sub", OperatorDevice(oscillator, filter))));
        var sub = InstrumentVoiceSynthesizer.RenderVoice("operator", new[] { 36 }, 0.5, SampleRate, new Random(1), rack.InstrumentParameters);
        Assert.True(Rms(sub, 4000, 10000) > 0.2);
    }

    [Fact]
    public void A_slow_attack_is_not_mistaken_for_silence()
    {
        var voice = Render(new Dictionary<string, double> { ["aAttack"] = 400 });
        Assert.True(voice.Length >= 0.5 * SampleRate);
        Assert.True(Rms(voice, 16000, 1000) > Rms(voice, 200, 1000) * 5);
    }

    [Fact]
    public void The_release_rings_past_the_gate_and_the_voice_ends_once_silent()
    {
        var gateFrames = (int)(0.2 * SampleRate);
        var voice = Render(new Dictionary<string, double> { ["aRelease"] = 300 }, gateSeconds: 0.2);
        Assert.True(voice.Length > gateFrames + 0.2 * SampleRate);
        Assert.True(Rms(voice, gateFrames + 1000, 500) > 0.01);
        Assert.True(voice.Length <= gateFrames + 0.31 * SampleRate);
    }

    [Theory]
    [InlineData(OperatorPatch.FilterLowpass12, true)]
    [InlineData(OperatorPatch.FilterLowpass24, true)]
    [InlineData(OperatorPatch.FilterHighpass12, false)]
    [InlineData(OperatorPatch.FilterHighpass24, false)]
    public void The_filter_keeps_its_side_of_the_cutoff(int filterType, bool isLowpass)
    {
        var dry = new Dictionary<string, double> { ["aWave"] = 10 };
        var filtered = new Dictionary<string, double>(dry) { ["filteron"] = 1, ["filtertype"] = filterType, ["filterfreq"] = 1200, ["filterres"] = 0 };
        var dryBrightness = Brightness(Render(dry, 45), 3000, 15000);
        var filteredBrightness = Brightness(Render(filtered, 45), 3000, 15000);
        Assert.Equal(isLowpass, filteredBrightness < dryBrightness);
    }

    [Theory]
    [InlineData(OperatorPatch.FilterLowpass24)]
    [InlineData(OperatorPatch.FilterHighpass24)]
    [InlineData(OperatorPatch.FilterBandpass)]
    [InlineData(OperatorPatch.FilterNotch)]
    public void The_filter_stays_stable_at_full_resonance_near_nyquist(int filterType)
    {
        var voice = Render(new Dictionary<string, double>
        {
            ["aWave"] = 22,
            ["filteron"] = 1,
            ["filtertype"] = filterType,
            ["filterfreq"] = 20000,
            ["filterres"] = 1,
            ["filterenv"] = 72,
            ["filtersustain"] = 1,
        });
        Assert.All(voice, sample => Assert.True(float.IsFinite(sample) && Math.Abs(sample) < 100));
    }

    [Fact]
    public void A_pitch_envelope_starts_high_and_falls_to_the_note()
    {
        var voice = Render(new Dictionary<string, double>
        {
            ["pitchon"] = 1,
            ["pitchpeak"] = 24,
            ["pitchdecay"] = 60,
            ["pitchsustain"] = 0,
        }, midiNote: 45);
        Assert.True(RisingCrossingsPerSecond(voice, 0, 500) > 200);
        AssertPitch(110, RisingCrossingsPerSecond(voice, 6000, 15000));
    }

    [Fact]
    public void An_lfo_moves_the_pitch_of_the_oscillators_that_follow_it()
    {
        var parameters = new Dictionary<string, double> { ["lfoon"] = 1, ["lforate"] = 5, ["lfoamount"] = 12 };
        var moving = Render(parameters, gateSeconds: 1);
        var still = Render(new Dictionary<string, double>(parameters) { ["aLfo"] = 0 }, gateSeconds: 1);
        Assert.NotEqual(RisingCrossingsPerSecond(moving, 1000, 2000), RisingCrossingsPerSecond(moving, 3500, 2000));
        AssertPitch(440, RisingCrossingsPerSecond(still, 2000, 15000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(22)]
    public void Every_waveform_sounds_and_stays_in_range(int waveform)
    {
        var voice = Render(new Dictionary<string, double> { ["aWave"] = waveform });
        Assert.True(Rms(voice, 2000, 15000) > 0.1);
        Assert.All(voice, sample => Assert.True(Math.Abs(sample) <= 1.2));
    }

    [Fact]
    public void The_same_parameters_render_the_same_samples()
    {
        var parameters = new Dictionary<string, double> { ["aWave"] = 22, ["bOn"] = 1, ["lfoon"] = 1, ["lfoshape"] = 5, ["lfoamount"] = 2, ["lforate"] = 20 };
        Assert.Equal(Render(parameters), Render(parameters));
    }

    [Fact]
    public void Chords_and_a_spread_render_without_going_quiet()
    {
        var chord = InstrumentVoiceSynthesizer.RenderVoice(OperatorPatch.InstrumentName, new[] { 60, 64, 67 }, 0.3, SampleRate, new Random(7), new Dictionary<string, double>());
        Assert.True(Rms(chord, 2000, 8000) > 0.2);
        var spread = Render(new Dictionary<string, double> { ["spread"] = 50 });
        Assert.True(Rms(spread, 2000, 15000) > 0.2);
    }

    [Theory]
    [InlineData("fm", "operator")]
    [InlineData("FM synth", "operator")]
    [InlineData("Ableton Operator", "operator")]
    [InlineData("operator", "operator")]
    public void Names_models_invent_land_on_the_instrument(string requested, string expected) =>
        Assert.Equal(expected, ProjectAlignmentRemediator.ResolveInstrument(requested));

    [Fact]
    public void The_catalog_stays_short_for_small_models()
    {
        var catalog = Workflows.PromptLibrary.DescribeDeviceCatalog();
        Assert.Contains("instrument \"operator\": algorithm 0-10", catalog);
        Assert.DoesNotContain("cFixed", catalog);
    }

    // ── Reading presets ───────────────────────────────────────────────────────

    /// <summary>A parameter the way Live saves it: the real value in an automation event, a stale default in Manual.</summary>
    private static string Saved(string name, string value, string stale = "0") =>
        $"""<{name}><ArrangerAutomation><Events><FloatEvent Time="-63072000" Value="{value}" /></Events></ArrangerAutomation><Manual Value="{stale}" /></{name}>""";

    private static string OperatorDevice(string oscillatorA, string extras = "") => $"""
        <Operator>
            <On><Manual Value="true" /></On>
            <Operator.0>{oscillatorA}</Operator.0>
            {extras}
        </Operator>
        """;

    private static string WritePreset(string directory, string name, string instrumentXml, string effectXml = "")
    {
        var effectPreset = effectXml.Length > 0 ? $"<AbletonDevicePreset><Device>{effectXml}</Device></AbletonDevicePreset>" : string.Empty;
        var xml = $"""
            <Ableton>
              <GroupDevicePreset>
                <Device><InstrumentGroupDevice /></Device>
                <BranchPresets>
                  <InstrumentBranchPreset>
                    <DevicePresets>
                      <AbletonDevicePreset><Device>{instrumentXml}</Device></AbletonDevicePreset>
                      {effectPreset}
                    </DevicePresets>
                  </InstrumentBranchPreset>
                </BranchPresets>
              </GroupDevicePreset>
            </Ableton>
            """;
        var path = Path.Combine(directory, $"{name}.adg");
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes(xml));
        return path;
    }

    private static T WithTemporaryFolder<T>(Func<string, T> work)
    {
        var folder = Directory.CreateTempSubdirectory("slaw-operator").FullName;
        try
        {
            return work(folder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private const string PlainOscillator = """<IsOn><Manual Value="true" /></IsOn><Volume><Manual Value="1" /></Volume>""";

    [Fact]
    public void Saved_values_win_over_the_stale_manual_defaults()
    {
        var oscillator = $"""
            <IsOn><Manual Value="true" /></IsOn>
            {Saved("WaveForm", "10")}
            {Saved("Volume", "0.5", "1")}
            <Tune>{Saved("Coarse", "3", "1")}{Saved("Fine", "250")}</Tune>
            <Envelope>{Saved("DecayTime", "123", "600")}{Saved("SustainLevel", "0.25", "1")}</Envelope>
            """;
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Saved", OperatorDevice(oscillator))));

        Assert.Equal("operator", rack.Instrument);
        Assert.Equal(10, rack.InstrumentParameters["aWave"]);
        Assert.Equal(0.5, rack.InstrumentParameters["aLevel"]);
        Assert.Equal(3, rack.InstrumentParameters["aCoarse"]);
        Assert.Equal(250, rack.InstrumentParameters["aFine"]);
        Assert.Equal(123, rack.InstrumentParameters["aDecay"]);
        Assert.Equal(0.25, rack.InstrumentParameters["aSustain"]);
    }

    [Fact]
    public void A_preset_with_no_events_still_reads_its_manual_values()
    {
        var oscillator = """<IsOn><Manual Value="true" /></IsOn><WaveForm><Manual Value="18" /></WaveForm><Volume><Manual Value="0.75" /></Volume>""";
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Old", OperatorDevice(oscillator))));
        Assert.Equal(18, rack.InstrumentParameters["aWave"]);
        Assert.Equal(0.75, rack.InstrumentParameters["aLevel"]);
    }

    [Fact]
    public void A_switched_off_oscillator_stays_off()
    {
        var extras = """<Operator.1><IsOn><Manual Value="false" /></IsOn></Operator.1>""";
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Off", OperatorDevice(PlainOscillator, extras))));
        Assert.Equal(1, rack.InstrumentParameters["aOn"]);
        Assert.Equal(0, rack.InstrumentParameters["bOn"]);
    }

    [Theory]
    [InlineData(0, OperatorPatch.FilterLowpass12)]
    [InlineData(1, OperatorPatch.FilterLowpass24)]
    [InlineData(3, OperatorPatch.FilterLowpass24)]
    [InlineData(10, OperatorPatch.FilterHighpass12)]
    [InlineData(11, OperatorPatch.FilterHighpass24)]
    [InlineData(4, OperatorPatch.FilterBandpass)]
    [InlineData(8, OperatorPatch.FilterNotch)]
    public void Filter_settings_arrive(int storedType, int expectedType)
    {
        var filter = $"""<Filter>{Saved("OnOff", "true", "false")}{Saved("Type", storedType.ToString())}{Saved("Frequency", "700", "500")}{Saved("Resonance", "1.15")}{Saved("EnvelopeAmount", "50")}</Filter>""";
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Filter", OperatorDevice(PlainOscillator, filter))));
        Assert.Equal(1, rack.InstrumentParameters["filteron"]);
        Assert.Equal(expectedType, rack.InstrumentParameters["filtertype"]);
        Assert.Equal(700, rack.InstrumentParameters["filterfreq"]);
        Assert.Equal(0.315, rack.InstrumentParameters["filterres"], 3);
        Assert.Equal(36, rack.InstrumentParameters["filterenv"]);
    }

    [Fact]
    public void The_level_is_brought_up_to_a_usable_peak()
    {
        var quiet = """<IsOn><Manual Value="true" /></IsOn><Volume><Manual Value="0.05" /></Volume>""";
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Quiet", OperatorDevice(quiet))));
        var note = InstrumentVoiceSynthesizer.RenderVoice("operator", new[] { 60 }, 0.5, SampleRate, new Random(60), rack.InstrumentParameters);
        Assert.Equal(0.8, note.Max(sample => Math.Abs(sample)), 2);
    }

    [Theory]
    [InlineData("<Overdrive><On><Manual Value=\"true\" /></On><Drive><Manual Value=\"50\" /></Drive><DryWet><Manual Value=\"100\" /></DryWet></Overdrive>", "drive")]
    [InlineData("<Saturator><On><Manual Value=\"true\" /></On><PreDrive><Manual Value=\"6\" /></PreDrive><DryWet><Manual Value=\"1\" /></DryWet></Saturator>", "drive")]
    [InlineData("<PingPongDelay><On><Manual Value=\"true\" /></On><DelayModeSwitch><Manual Value=\"true\" /></DelayModeSwitch><BeatDelayTime><Manual Value=\"2\" /></BeatDelayTime><DryWet><Manual Value=\"0.4\" /></DryWet></PingPongDelay>", "delay")]
    [InlineData("<CrossDelay><On><Manual Value=\"true\" /></On><SyncModeLeft><Manual Value=\"true\" /></SyncModeLeft><BeatDelayEnumL><Manual Value=\"3\" /></BeatDelayEnumL><DryWet><Manual Value=\"0.4\" /></DryWet></CrossDelay>", "delay")]
    [InlineData("<CrossDelay><On><Manual Value=\"true\" /></On><MsDelayLeft><Manual Value=\"1\" /></MsDelayLeft><MsDelayRight><Manual Value=\"13\" /></MsDelayRight><DryWet><Manual Value=\"0.5\" /></DryWet></CrossDelay>", "widen")]
    [InlineData("<Redux><On><Manual Value=\"true\" /></On><SampleResMode><Manual Value=\"false\" /></SampleResMode><SampleResRough><Manual Value=\"12\" /></SampleResRough></Redux>", "bitcrush")]
    [InlineData("<AutoFilter><On><Manual Value=\"true\" /></On><FilterType><Manual Value=\"0\" /></FilterType><Cutoff><Manual Value=\"80\" /></Cutoff></AutoFilter>", "lowpass")]
    public void Effects_with_a_nearest_relative_come_along(string effectXml, string expectedType)
    {
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Effect", OperatorDevice(PlainOscillator), effectXml)));
        Assert.Equal(expectedType, Assert.Single(rack.Effects).Type);
        var effect = rack.Effects[0];
        foreach (var specification in DeviceCatalog.EffectParametersFor(effect.Type))
        {
            if (effect.Parameters.TryGetValue(specification.Key, out var value))
            {
                Assert.InRange(value, specification.Minimum, specification.Maximum);
            }
        }
    }

    [Fact]
    public void An_effect_switched_off_in_an_event_is_skipped()
    {
        var overdrive = $"""<Overdrive>{Saved("On", "false", "true")}<DryWet><Manual Value="100" /></DryWet></Overdrive>""";
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Off", OperatorDevice(PlainOscillator), overdrive)));
        Assert.Empty(rack.Effects);
        Assert.Empty(rack.LeftOut);
    }

    [Fact]
    public void What_has_no_relative_is_named_and_a_silent_analyzer_is_not()
    {
        var eq = """<Eq8><On><Manual Value="true" /></On></Eq8>""";
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Eq", OperatorDevice(PlainOscillator), eq)));
        Assert.Contains("EQ Eight", rack.LeftOut);

        var analyzer = """<SpectrumAnalyzer><On><Manual Value="true" /></On></SpectrumAnalyzer>""";
        var quiet = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Analyzer", OperatorDevice(PlainOscillator), analyzer)));
        Assert.Empty(quiet.LeftOut);
    }

    [Fact]
    public void An_imported_preset_becomes_a_track_that_survives_a_save_and_plays()
    {
        var oscillator = $"""<IsOn><Manual Value="true" /></IsOn>{Saved("WaveForm", "10")}<Volume><Manual Value="1" /></Volume>""";
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Track", OperatorDevice(oscillator))));
        var project = ProjectSerializer.CreateBlankProject("rack", 120);
        var track = SoundImportPlanner.CreateRackTrack(project, rack, 0);
        Assert.Equal("operator", track.Instrument);
        Assert.Null(track.SampleZones);
        project.Tracks.Add(track);
        project.Timeline.Add(new TimelineClip { TrackId = track.Id, StartStep = 0, LengthSteps = 16, Clip = new SlawClip { Name = "riff", Pattern = CompactPatternJsonConverter.SplitStepString("C3:4 . . . E3:4 . . . G3:4 . . . C4:4 . . .") } });

        var reloaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));

        Assert.Equal("operator", reloaded.Tracks[0].Instrument);
        Assert.Equal(10, reloaded.Tracks[0].InstrumentParameters["aWave"]);
        var rendered = OfflineArrangementRenderer.Render(reloaded);
        Assert.Contains(rendered.Left, sample => Math.Abs(sample) > 0.05);
    }

    [Fact]
    public void Synth_presets_preview_from_a_rendered_note()
    {
        var rack = WithTemporaryFolder(folder => AbletonRackImporter.Read(WritePreset(folder, "Preview", OperatorDevice(PlainOscillator))));
        var previewPath = AbletonRackImporter.WriteSynthPreview(rack);
        Assert.True(File.Exists(previewPath));
        Assert.True(SampleLibrary.Load(previewPath).Samples.Length > 0.4 * 44100);
    }
}
