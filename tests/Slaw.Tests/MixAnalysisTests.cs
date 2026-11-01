using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Mcp;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The numbers an agent mixes by, the note check, and the effects that act on them: measured against what they claim.</summary>
public class MixAnalysisTests
{
    private const int Rate = 44100;

    private static StereoBuffer Sine(double frequency, double amplitude, double seconds, double rightGain = 1, int rate = Rate)
    {
        var buffer = new StereoBuffer((int)(seconds * rate), rate);
        for (var frame = 0; frame < buffer.SampleFrameCount; frame++)
        {
            var sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / rate));
            buffer.Left[frame] = sample;
            buffer.Right[frame] = sample * (float)rightGain;
        }
        return buffer;
    }

    // ── Loudness and peaks ────────────────────────────────────────────────────

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void A_stereo_sine_at_minus_23_dBFS_reads_minus_23_LUFS(int rate)
    {
        // The EBU's own reference: 997 Hz at -23 dBFS in both channels is -23.0 LUFS.
        var measurement = MixAnalyzer.Measure(Sine(997, Math.Pow(10, -23.0 / 20), 20, rate: rate));
        Assert.InRange(measurement.IntegratedLufs, -23.1, -22.9);
    }

    [Fact]
    public void Silence_between_notes_does_not_make_a_song_read_quieter()
    {
        var tone = Sine(997, Math.Pow(10, -23.0 / 20), 10);
        var withGap = new StereoBuffer(tone.SampleFrameCount * 2, Rate);
        tone.Left.CopyTo(withGap.Left, 0);
        tone.Right.CopyTo(withGap.Right, 0);
        Assert.InRange(MixAnalyzer.Measure(withGap).IntegratedLufs, -23.2, -22.8);
    }

    [Fact]
    public void True_peak_finds_what_lands_between_the_samples()
    {
        // A quarter of the sample rate at 45°: every sample reads 0.707 of a peak that is really 1.
        var buffer = new StereoBuffer(8192, Rate);
        for (var frame = 0; frame < buffer.SampleFrameCount; frame++)
        {
            buffer.Left[frame] = buffer.Right[frame] = (float)Math.Sin(Math.PI / 2 * frame + Math.PI / 4);
        }
        Assert.InRange(buffer.PeakAmplitude(), 0.70, 0.71);
        Assert.InRange(MixAnalyzer.Measure(buffer).TruePeakDecibels, -0.3, 0.3);
    }

    [Fact]
    public void Mono_compatibility_is_measured_from_what_cancels()
    {
        var mono = MixAnalyzer.Measure(Sine(440, 0.5, 2));
        Assert.Equal(1, mono.Correlation, 3);
        Assert.Equal(0, mono.MonoChangeDecibels, 2);

        var opposed = MixAnalyzer.Measure(Sine(440, 0.5, 2, rightGain: -1));
        Assert.Equal(-1, opposed.Correlation, 3);
        Assert.True(opposed.MonoChangeDecibels <= -60);
        var report = MixAnalyzer.DescribeMeasurement(opposed, MixAnalyzer.ProfileBands(Sine(440, 0.5, 2, rightGain: -1)));
        Assert.Contains("loses", report);
        Assert.Contains("mono", report);
    }

    [Fact]
    public void A_wide_low_end_is_reported_and_a_wide_top_is_not()
    {
        var wideLows = new StereoBuffer(Rate * 2, Rate);
        var wideTops = new StereoBuffer(Rate * 2, Rate);
        for (var frame = 0; frame < wideLows.SampleFrameCount; frame++)
        {
            var low = (float)(0.4 * Math.Sin(2 * Math.PI * 50 * frame / Rate));
            var high = (float)(0.4 * Math.Sin(2 * Math.PI * 5000 * frame / Rate));
            wideLows.Left[frame] = low + high;
            wideLows.Right[frame] = -low + high;
            wideTops.Left[frame] = low + high;
            wideTops.Right[frame] = low - high;
        }
        Assert.True(MixAnalyzer.Measure(wideLows).LowEndCorrelation < 0);
        Assert.True(MixAnalyzer.Measure(wideTops).LowEndCorrelation > 0.9);
    }

    [Fact]
    public void A_silent_render_says_so()
    {
        var silence = new StereoBuffer(Rate, Rate);
        Assert.Equal("loudness: the render is silent.", MixAnalyzer.DescribeMeasurement(MixAnalyzer.Measure(silence), MixAnalyzer.ProfileBands(silence)));
    }

    // ── Bands and masking ─────────────────────────────────────────────────────

    private static int BandOf(double frequency) => Array.FindIndex(MixAnalyzer.Bands, band => frequency >= band.LowHz && frequency < band.HighHz);

    [Theory]
    [InlineData(40)]
    [InlineData(90)]
    [InlineData(700)]
    [InlineData(3000)]
    [InlineData(12000)]
    public void A_sine_shows_up_at_its_true_level_in_its_own_band(double frequency)
    {
        var profile = MixAnalyzer.ProfileBands(Sine(frequency, 0.5, 2));
        var expected = 20 * Math.Log10(0.5 / Math.Sqrt(2));
        var band = BandOf(frequency);
        Assert.InRange(profile.BandDecibels(band), expected - 0.5, expected + 0.5);
        for (var other = 0; other < MixAnalyzer.Bands.Length; other++)
        {
            if (Math.Abs(other - band) > 1)
            {
                Assert.True(profile.BandDecibels(other) < expected - 30, $"{MixAnalyzer.Bands[other].Label} holds {profile.BandDecibels(other):0.0} dB of a {frequency} Hz sine");
            }
        }
    }

    private static readonly (double, double)[] WholeSong = { (0, 4) };

    [Fact]
    public void Two_equal_tracks_in_one_band_are_a_likely_clash()
    {
        var clash = Assert.Single(MixAnalyzer.FindClashes(
            new[] { ("Bass", MixAnalyzer.ProfileBands(Sine(90, 0.5, 4))), ("Sub", MixAnalyzer.ProfileBands(Sine(100, 0.4, 4))) }, WholeSong));
        Assert.Equal(("Bass", "Sub", "60–120 Hz"), (clash.LouderTrack, clash.QuieterTrack, clash.BandLabel));
        Assert.True(clash.ActiveShare > 0.9);
        Assert.InRange(clash.AverageDistanceDecibels, 1, 3);
    }

    [Fact]
    public void A_clash_names_the_louder_track_first_whichever_order_they_came_in()
    {
        var clash = Assert.Single(MixAnalyzer.FindClashes(
            new[] { ("Quiet", MixAnalyzer.ProfileBands(Sine(90, 0.3, 4))), ("Loud", MixAnalyzer.ProfileBands(Sine(100, 0.5, 4))) }, WholeSong));
        Assert.Equal(("Loud", "Quiet"), (clash.LouderTrack, clash.QuieterTrack));
        Assert.Contains("\"Loud\" over \"Quiet\"", MixAnalyzer.DescribeClashes(new[] { clash }, new[] { "bars 1–8" }));
    }

    [Fact]
    public void Tracks_in_different_bands_do_not_clash()
    {
        Assert.Empty(MixAnalyzer.FindClashes(
            new[] { ("Bass", MixAnalyzer.ProfileBands(Sine(90, 0.5, 4))), ("Lead", MixAnalyzer.ProfileBands(Sine(2000, 0.5, 4))) }, WholeSong));
    }

    [Fact]
    public void A_much_quieter_track_is_not_a_clash_either()
    {
        Assert.Empty(MixAnalyzer.FindClashes(
            new[] { ("Bass", MixAnalyzer.ProfileBands(Sine(90, 0.5, 4))), ("Whisper", MixAnalyzer.ProfileBands(Sine(100, 0.05, 4))) }, WholeSong));
    }

    [Fact]
    public void Tracks_that_take_turns_do_not_clash_which_is_why_ducking_counts()
    {
        var first = Sine(90, 0.5, 4);
        var second = Sine(90, 0.5, 4);
        for (var frame = 0; frame < first.SampleFrameCount; frame++)
        {
            var firstPlays = frame / (Rate / 2) % 2 == 0;
            var silenced = firstPlays ? second : first;
            silenced.Left[frame] = silenced.Right[frame] = 0;
        }
        Assert.Empty(MixAnalyzer.FindClashes(new[] { ("One", MixAnalyzer.ProfileBands(first)), ("Other", MixAnalyzer.ProfileBands(second)) }, WholeSong));
    }

    [Fact]
    public void A_clash_is_only_reported_for_the_bars_it_happens_in()
    {
        var bass = Sine(90, 0.5, 4);
        var late = Sine(100, 0.5, 4);
        Array.Clear(late.Left, 0, Rate * 2);
        Array.Clear(late.Right, 0, Rate * 2);
        var ranges = new[] { (0.0, 2.0), (2.0, 4.0) };
        var clash = Assert.Single(MixAnalyzer.FindClashes(new[] { ("Bass", MixAnalyzer.ProfileBands(bass)), ("Late", MixAnalyzer.ProfileBands(late)) }, ranges));
        Assert.Equal(new[] { 1 }, clash.RangeIndexes);
        Assert.Contains("in bars 9–16", MixAnalyzer.DescribeClashes(new[] { clash }, new[] { "bars 1–8", "bars 9–16" }));
    }

    // ── The note check ────────────────────────────────────────────────────────

    private static SlawProject Song(params (string Instrument, string Pattern)[] parts)
    {
        var project = ProjectSerializer.CreateBlankProject("notes", 120);
        foreach (var (instrument, pattern) in parts)
        {
            var track = new SlawTrack { Name = instrument, Instrument = instrument };
            project.Tracks.Add(track);
            var clip = new TimelineClip { TrackId = track.Id, StartStep = 0, LengthSteps = 64 };
            clip.Clip.Pattern = pattern.Split(' ').ToList();
            project.Timeline.Add(clip);
        }
        return project;
    }

    private const string AMinorTune = "A3+C4+E4 _ _ _ _ _ _ _ F3+A3+C4 _ _ _ _ _ _ _ G3+B3+D4 _ _ _ _ _ _ _ E3+G3+B3 _ _ _ _ _ _ _ A4 . C5 . E5 . A5 . G5 . E5 . C5 . A4 .";

    [Fact]
    public void A_tune_in_a_key_is_found_with_nothing_outside_it()
    {
        var report = NoteKeyAnalysis.Analyze(Song(("saw", AMinorTune)));
        Assert.True(report.Key is { IsMinor: true, Tonic: 9 } or { IsMinor: false, Tonic: 0 }, report.Key?.Name);
        Assert.Empty(report.OutOfKeyNotes);
        Assert.Contains("outside the scale: none", NoteKeyAnalysis.Describe(report));
    }

    [Fact]
    public void A_wrong_note_is_named_with_its_track_and_position()
    {
        var report = NoteKeyAnalysis.Analyze(Song(("saw", AMinorTune.Replace("A5 . G5", "A5 . G#5"))), MusicalKey.Parse("A minor"));
        var wrong = Assert.Single(report.OutOfKeyNotes);
        Assert.Equal(("saw", "G#", "3.9"), (wrong.TrackName, wrong.NoteName, wrong.Position));
        Assert.Contains("\"saw\" G#: 1 note(s) at 3.9", NoteKeyAnalysis.Describe(report));
    }

    [Fact]
    public void Drums_and_recordings_are_named_not_read()
    {
        var report = NoteKeyAnalysis.Analyze(Song(("kick", "x . . . x . . ."), ("sampler", "C#4 . . . F#4 . . ."), ("saw", AMinorTune)), MusicalKey.Parse("Am"));
        Assert.Empty(report.OutOfKeyNotes);
        Assert.Equal(new[] { "kick", "sampler" }, report.UncheckedTracks);
    }

    [Fact]
    public void Only_drums_means_no_key()
    {
        var report = NoteKeyAnalysis.Analyze(Song(("kick", "x . . . x . . .")));
        Assert.Null(report.Key);
        Assert.Contains("no pitched notes", NoteKeyAnalysis.Describe(report));
    }

    [Fact]
    public void The_later_clip_wins_where_clips_overlap_so_a_hidden_note_is_not_reported()
    {
        var project = Song(("saw", "C#4 . . . . . . . . . . . . . . ."));
        var hiding = new TimelineClip { TrackId = project.Tracks[0].Id, StartStep = 0, LengthSteps = 64 };
        hiding.Clip.Pattern = AMinorTune.Split(' ').ToList();
        project.Timeline.Add(hiding);
        // Both start together, so the first one is cut to nothing.
        Assert.Empty(NoteKeyAnalysis.Analyze(project, MusicalKey.Parse("A minor")).OutOfKeyNotes);
    }

    [Theory]
    [InlineData("A minor", 9, true)]
    [InlineData("Am", 9, true)]
    [InlineData("F# major", 6, false)]
    [InlineData("bb", 10, false)]
    [InlineData("C", 0, false)]
    [InlineData("eb min", 3, true)]
    public void Keys_parse_the_way_people_write_them(string notation, int tonic, bool isMinor) =>
        Assert.Equal(new MusicalKey(tonic, isMinor), MusicalKey.Parse(notation));

    [Theory]
    [InlineData("H minor")]
    [InlineData("")]
    [InlineData("A dorian")]
    public void Anything_else_is_not_a_key(string notation) => Assert.Null(MusicalKey.Parse(notation));

    // ── The MCP tools ─────────────────────────────────────────────────────────

    private static (McpProtocolDispatcher Dispatcher, StudioDocument Document) Server()
    {
        var document = new StudioDocument(ProjectSerializer.CreateStarterProject("mcp"), filePath: null);
        return (new McpProtocolDispatcher(new SlawMcpToolset(new HeadlessMcpHost(document, new StudioPreferences { AiFeaturesEnabled = false }))), document);
    }

    private static async Task<(string Text, bool IsError)> Call(McpProtocolDispatcher dispatcher, string toolName, JsonObject arguments)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments } };
        var result = JsonNode.Parse((await dispatcher.HandleAsync(request.ToJsonString(), CancellationToken.None))!)!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task Render_reports_the_numbers_and_levels_adds_bands_and_masking()
    {
        var (dispatcher, document) = Server();
        var path = Path.Combine(Path.GetTempPath(), $"slaw-analysis-{Guid.NewGuid():N}.wav");
        try
        {
            var (plain, plainFailed) = await Call(dispatcher, "render", new JsonObject { ["path"] = path });
            Assert.False(plainFailed, plain);
            Assert.Contains("LUFS integrated", plain);
            Assert.Contains("dBTP", plain);
            Assert.Contains("correlation", plain);
            Assert.Contains("tonal balance", plain);
            Assert.DoesNotContain("likely masking", plain);
            var plainBytes = await File.ReadAllBytesAsync(path);

            var (full, fullFailed) = await Call(dispatcher, "render", new JsonObject { ["path"] = path, ["levels"] = true });
            Assert.False(fullFailed, full);
            Assert.Contains("mix bands by bars", full);
            Assert.Contains("likely masking", full);
            // Measuring must never change the bounce.
            Assert.Equal(plainBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(document.Project.Tracks.Count, document.Project.Tracks.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Check_notes_reads_the_song_and_refuses_a_key_it_cannot_parse()
    {
        var (dispatcher, _) = Server();
        var (report, failed) = await Call(dispatcher, "check_notes", new JsonObject());
        Assert.False(failed, report);
        Assert.StartsWith("key:", report);

        var (asked, askedFailed) = await Call(dispatcher, "check_notes", new JsonObject { ["key"] = "F# major" });
        Assert.False(askedFailed, asked);
        Assert.Contains("F# major (as asked", asked);

        var (bad, badFailed) = await Call(dispatcher, "check_notes", new JsonObject { ["key"] = "H dorian" });
        Assert.True(badFailed);
        Assert.Contains("is not a key", bad);
    }

    [Theory]
    [InlineData("compressor")]
    [InlineData("compression")]
    [InlineData("eq")]
    [InlineData("equalizer")]
    public async Task Agents_can_add_the_new_effects_by_name_and_synonym(string requested)
    {
        var (dispatcher, document) = Server();
        var (added, failed) = await Call(dispatcher, "add_effect", new JsonObject { ["track"] = document.Project.Tracks[0].Id, ["type"] = requested });
        Assert.False(failed, added);
        Assert.Contains(document.Project.Tracks[0].Effects[^1].Type, new[] { "compressor", "eq" });
    }

    [Theory]
    [InlineData("compressor")]
    [InlineData("eq")]
    public void The_new_effects_survive_the_remediator_and_keep_only_known_keys(string effectType)
    {
        var project = ProjectSerializer.CreateStarterProject("fx");
        project.Tracks[0].Effects.Add(new EffectDevice { Type = effectType, Parameters = new Dictionary<string, double> { ["nonsense"] = 3 } });
        ProjectAlignmentRemediator.Remediate(project);
        var effect = project.Tracks[0].Effects[^1];
        Assert.Equal(effectType, effect.Type);
        Assert.DoesNotContain("nonsense", effect.Parameters.Keys);
        Assert.NotEmpty(DeviceCatalog.EffectDescription(effectType));
    }

    // ── EQ ────────────────────────────────────────────────────────────────────

    private static double Rms(ReadOnlySpan<float> samples)
    {
        var total = 0.0;
        foreach (var sample in samples)
        {
            total += sample * sample;
        }
        return Math.Sqrt(total / samples.Length);
    }

    /// <summary>The change in level a device makes to a settled sine, in dB.</summary>
    private static double GainAt(double frequency, string effectType, Dictionary<string, double> parameters, double amplitude = 0.25)
    {
        var processor = EffectProcessor.Create(effectType, Rate);
        var input = Sine(frequency, amplitude, 1);
        var output = (float[])input.Left.Clone();
        var unused = new float[output.Length];
        var context = new EffectProcessingContext(Rate, 0.125, MonoInput: true);
        for (var start = 0; start < output.Length; start += 512)
        {
            var length = Math.Min(512, output.Length - start);
            processor.Process(output.AsSpan(start, length), unused.AsSpan(start, length), parameters, context);
        }
        var settled = Rate / 2;
        return 20 * Math.Log10(Rms(output.AsSpan(settled)) / Rms(input.Left.AsSpan(settled)));
    }

    [Theory]
    [InlineData(1000, "mid1freq", "mid1gain", 6.0, 6.0)]
    [InlineData(1000, "mid2freq", "mid2gain", -9.0, -9.0)]
    public void A_bell_makes_its_gain_at_its_frequency_and_leaves_the_rest(double frequency, string frequencyKey, string gainKey, double gain, double expected)
    {
        var parameters = new Dictionary<string, double> { [frequencyKey] = frequency, [gainKey] = gain };
        Assert.InRange(GainAt(frequency, "eq", parameters), expected - 0.2, expected + 0.2);
        Assert.InRange(GainAt(frequency * 16, "eq", parameters), -0.5, 0.5);
        Assert.InRange(GainAt(frequency / 16, "eq", parameters), -0.5, 0.5);
    }

    [Fact]
    public void Shelves_lift_their_own_end_and_leave_the_other_alone()
    {
        var low = new Dictionary<string, double> { ["lowfreq"] = 200, ["lowgain"] = 6 };
        Assert.InRange(GainAt(30, "eq", low), 5.5, 6.5);
        Assert.InRange(GainAt(5000, "eq", low), -0.3, 0.3);
        var high = new Dictionary<string, double> { ["highfreq"] = 4000, ["highgain"] = -6 };
        Assert.InRange(GainAt(18000, "eq", high), -6.5, -5.5);
        Assert.InRange(GainAt(200, "eq", high), -0.3, 0.3);
    }

    [Fact]
    public void Cuts_remove_their_end_at_twelve_dB_per_octave()
    {
        var lowCut = new Dictionary<string, double> { ["lowcut"] = 800 };
        Assert.InRange(GainAt(800, "eq", lowCut), -3.5, -2.5);
        Assert.InRange(GainAt(200, "eq", lowCut), -25, -22);
        Assert.InRange(GainAt(8000, "eq", lowCut), -0.2, 0.2);
        var highCut = new Dictionary<string, double> { ["highcut"] = 2000 };
        Assert.InRange(GainAt(8000, "eq", highCut), -30, -20);
        Assert.InRange(GainAt(200, "eq", highCut), -0.2, 0.2);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(400)]
    [InlineData(1000)]
    [InlineData(2500)]
    [InlineData(8000)]
    [InlineData(16000)]
    public void The_displayed_curve_is_what_the_audio_measures(double frequency)
    {
        var parameters = new Dictionary<string, double>
        {
            ["lowcut"] = 40,
            ["lowfreq"] = 150,
            ["lowgain"] = 4,
            ["mid1freq"] = 400,
            ["mid1gain"] = -8,
            ["mid1q"] = 2,
            ["mid2freq"] = 2500,
            ["mid2gain"] = 5,
            ["mid2q"] = 1,
            ["highfreq"] = 9000,
            ["highgain"] = -3,
            ["highcut"] = 18000,
        };
        Assert.InRange(FrequencyRealignmentEqProcessor.ResponseDecibels(parameters, frequency, Rate) - GainAt(frequency, "eq", parameters), -0.15, 0.15);
    }

    [Fact]
    public void A_flat_EQ_draws_a_flat_line() =>
        Assert.Equal(0, FrequencyRealignmentEqProcessor.ResponseDecibels(new Dictionary<string, double>(), 1000, Rate));

    private static SlawProject EqSong()
    {
        var project = ProjectSerializer.CreateBlankProject("display", 120);
        var track = new SlawTrack { Id = "keys0001", Name = "Keys", Instrument = "sine", Effects = { new EffectDevice { Type = "eq" } } };
        project.Tracks.Add(track);
        var clip = new TimelineClip { TrackId = track.Id, StartStep = 0, LengthSteps = 16 };
        clip.Clip.Pattern = "A4 _ _ _ _ _ _ _ _ _ _ _ _ _ _ _".Split(' ').ToList();
        project.Timeline.Add(clip);
        return project;
    }

    [Fact]
    public void A_display_on_screen_hears_its_own_EQ_and_only_that_one()
    {
        var ownTap = EqSpectrumTaps.Watch("keys0001", 0);
        var otherSlot = EqSpectrumTaps.Watch("keys0001", 1);
        var otherTrack = EqSpectrumTaps.Watch("elsewhere", 0);
        try
        {
            var engine = new SequencerEngine(Rate);
            engine.PublishProject(EqSong());
            engine.PlayTimeline(loop: false);
            var left = new float[2048];
            var right = new float[2048];
            for (var block = 0; block < 8; block++)
            {
                engine.RenderBlock(left, right);
            }
            engine.ReleasePlugins();
            Assert.True(ownTap.FramesWritten > 0);
            Assert.Equal(0, otherSlot.FramesWritten);
            Assert.Equal(0, otherTrack.FramesWritten);
            var latest = new float[1024];
            ownTap.CopyLatest(latest);
            Assert.Contains(latest, sample => Math.Abs(sample) > 0.01f);
        }
        finally
        {
            EqSpectrumTaps.Forget(ownTap);
            EqSpectrumTaps.Forget(otherSlot);
            EqSpectrumTaps.Forget(otherTrack);
        }
    }

    [Fact]
    public void A_bounce_neither_feeds_a_display_nor_changes_because_of_one()
    {
        var unwatched = OfflineArrangementRenderer.Render(EqSong());
        var tap = EqSpectrumTaps.Watch("keys0001", 0);
        try
        {
            var watched = OfflineArrangementRenderer.Render(EqSong());
            Assert.Equal(0, tap.FramesWritten);
            Assert.Equal(unwatched.Left, watched.Left);
        }
        finally
        {
            EqSpectrumTaps.Forget(tap);
        }
    }

    [Fact]
    public void The_tap_wraps_around_and_hands_back_the_newest_samples_in_order()
    {
        var tap = new EqSpectrumTap("t", 0);
        var block = new float[EqSpectrumTap.Capacity / 2 + 7];
        var next = 0f;
        for (var round = 0; round < 5; round++)
        {
            for (var index = 0; index < block.Length; index++)
            {
                block[index] = next++;
            }
            tap.Write(block, block, isMono: true);
        }
        var latest = new float[16];
        tap.CopyLatest(latest);
        Assert.Equal(Enumerable.Range((int)next - 16, 16).Select(value => (float)value), latest);
    }

    [Fact]
    public void A_flat_EQ_is_a_true_bypass()
    {
        var input = Sine(440, 0.5, 0.2);
        var output = (float[])input.Left.Clone();
        EffectProcessor.Create("eq", Rate).Process(output, new float[output.Length], new Dictionary<string, double>(), new EffectProcessingContext(Rate, 0.125, MonoInput: true));
        Assert.Equal(input.Left, output);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(22050)]
    public void The_EQ_stays_stable_at_every_extreme_up_to_Nyquist(int rate)
    {
        var settings = new Dictionary<string, double>
        {
            ["lowcut"] = 2000,
            ["lowfreq"] = 1000,
            ["lowgain"] = 18,
            ["mid1freq"] = 16000,
            ["mid1gain"] = 18,
            ["mid1q"] = 10,
            ["mid2freq"] = 16000,
            ["mid2gain"] = -18,
            ["mid2q"] = 0.2,
            ["highfreq"] = 20000,
            ["highgain"] = 18,
            ["highcut"] = 19999,
        };
        var processor = EffectProcessor.Create("eq", rate);
        var noise = new Random(3);
        var block = new float[1024];
        var right = new float[1024];
        for (var iteration = 0; iteration < 400; iteration++)
        {
            for (var sample = 0; sample < block.Length; sample++)
            {
                block[sample] = (float)(noise.NextDouble() * 2 - 1);
                right[sample] = (float)(noise.NextDouble() * 2 - 1);
            }
            // Moving the knobs between blocks must not wind it up either.
            settings["mid1freq"] = 40 + 15000.0 * (iteration % 7) / 6;
            processor.Process(block, right, settings, new EffectProcessingContext(rate, 0.125, MonoInput: iteration % 2 == 0));
            Assert.All(block, sample => Assert.True(float.IsFinite(sample) && Math.Abs(sample) < 200, $"{sample}"));
        }
    }

    [Fact]
    public void Mono_stays_mono_through_the_EQ_and_a_stereo_block_matches_it()
    {
        var parameters = new Dictionary<string, double> { ["mid1gain"] = 6, ["lowcut"] = 100 };
        var mono = Sine(500, 0.3, 0.2).Left;
        var left = (float[])mono.Clone();
        var right = (float[])mono.Clone();
        Assert.True(EffectProcessor.Create("eq", Rate).Process(mono, new float[mono.Length], parameters, new EffectProcessingContext(Rate, 0.125, MonoInput: true)));
        Assert.False(EffectProcessor.Create("eq", Rate).Process(left, right, parameters, new EffectProcessingContext(Rate, 0.125)));
        Assert.Equal(mono, left);
        Assert.Equal(mono, right);
    }

    // ── Compressor ────────────────────────────────────────────────────────────

    private static Dictionary<string, double> HardKnee(double threshold, double ratio) =>
        new() { ["threshold"] = threshold, ["ratio"] = ratio, ["knee"] = 0, ["attack"] = 0.1, ["release"] = 5 };

    /// <summary>A square wave has one level, so a compressor's law reads straight off it.</summary>
    private static double SquareGain(double level, Dictionary<string, double> parameters)
    {
        var block = new float[Rate];
        for (var frame = 0; frame < block.Length; frame++)
        {
            block[frame] = (float)(frame / 50 % 2 == 0 ? level : -level);
        }
        EffectProcessor.Create("compressor", Rate).Process(block, new float[block.Length], parameters, new EffectProcessingContext(Rate, 0.125, MonoInput: true));
        return 20 * Math.Log10(Math.Abs(block[^1]) / level);
    }

    [Theory]
    [InlineData(-6, -18, 4, -9)]
    [InlineData(-6, -18, 2, -6)]
    [InlineData(-30, -18, 4, 0)]
    [InlineData(-6, -18, 1, 0)]
    public void The_law_is_the_ratio_above_the_threshold(double inputDecibels, double threshold, double ratio, double expectedChange)
    {
        var change = SquareGain(Math.Pow(10, inputDecibels / 20), HardKnee(threshold, ratio));
        Assert.Equal(expectedChange, change, 1);
    }

    [Fact]
    public void Makeup_gives_the_level_back_and_mix_blends_the_dry_signal()
    {
        var level = Math.Pow(10, -6.0 / 20);
        var withMakeup = HardKnee(-18, 4);
        withMakeup["makeup"] = 9;
        Assert.Equal(0, SquareGain(level, withMakeup), 1);

        var dry = HardKnee(-18, 4);
        dry["mix"] = 0;
        Assert.Equal(0, SquareGain(level, dry), 3);
    }

    [Fact]
    public void A_soft_knee_starts_before_the_threshold_and_is_gentler_at_it()
    {
        var soft = HardKnee(-18, 4);
        soft["knee"] = 12;
        var atThreshold = Math.Pow(10, -18.0 / 20);
        Assert.InRange(SquareGain(atThreshold, soft), -2, -0.5);
        Assert.Equal(0, SquareGain(atThreshold, HardKnee(-18, 4)), 1);
    }

    [Fact]
    public void Attack_lets_the_start_of_a_hit_through_and_release_recovers()
    {
        var parameters = new Dictionary<string, double> { ["threshold"] = -30, ["ratio"] = 20, ["knee"] = 0, ["attack"] = 50, ["release"] = 50 };
        var block = new float[Rate];
        for (var frame = 0; frame < Rate / 2; frame++)
        {
            block[frame] = frame % 2 == 0 ? 0.5f : -0.5f;
        }
        for (var frame = Rate / 2; frame < Rate; frame++)
        {
            block[frame] = frame % 2 == 0 ? 0.01f : -0.01f;
        }
        EffectProcessor.Create("compressor", Rate).Process(block, new float[block.Length], parameters, new EffectProcessingContext(Rate, 0.125, MonoInput: true));
        Assert.True(Math.Abs(block[10]) > 0.45, $"the transient should pass: {block[10]}");
        Assert.True(Math.Abs(block[Rate / 2 - 1]) < 0.08, $"the sustain should be held down: {block[Rate / 2 - 1]}");
        Assert.True(Math.Abs(block[^1]) > 0.0095, $"the quiet part should have recovered: {block[^1]}");
    }

    [Fact]
    public void Both_sides_are_reduced_together_so_the_image_does_not_lean()
    {
        var left = new float[Rate / 2];
        var right = new float[Rate / 2];
        for (var frame = 0; frame < left.Length; frame++)
        {
            left[frame] = frame % 2 == 0 ? 0.5f : -0.5f;
            right[frame] = frame % 2 == 0 ? 0.05f : -0.05f;
        }
        var isMono = EffectProcessor.Create("compressor", Rate).Process(left, right, HardKnee(-30, 4), new EffectProcessingContext(Rate, 0.125));
        Assert.False(isMono);
        Assert.Equal(10, Math.Abs(left[^1] / right[^1]), 2);
        Assert.True(Math.Abs(left[^1]) < 0.3);
    }

    [Fact]
    public void A_render_with_both_effects_is_still_the_same_bytes_every_time()
    {
        var project = ProjectSerializer.CreateStarterProject("repeat");
        project.Tracks[0].Effects.Add(new EffectDevice { Type = "compressor", Parameters = new Dictionary<string, double> { ["threshold"] = -30 } });
        project.Tracks[0].Effects.Add(new EffectDevice { Type = "eq", Parameters = new Dictionary<string, double> { ["mid1gain"] = -6, ["lowcut"] = 60 } });
        var first = OfflineArrangementRenderer.Render(project);
        var second = OfflineArrangementRenderer.Render(project);
        Assert.Equal(first.Left, second.Left);
        Assert.Equal(first.Right, second.Right);
    }
}
