using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// Drum and bass enablement: ratchets ("x*4", several strikes inside one step) and unison
/// spread (a detuned oscillator pair, i.e. a Reese bass), validated on behalf of regulators.
/// </summary>
public class RatchetAndUnisonTests
{
    private const int TestSampleRate = 22050;

    // ── Notation ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("x*2", new[] { 60 }, 2, 1.0)]
    [InlineData("x*3@0.5", new[] { 60 }, 3, 0.5)]
    [InlineData("x@0.5*3", new[] { 60 }, 3, 0.5)]
    [InlineData("**2", new[] { 60 }, 2, 1.0)]
    [InlineData("*", new[] { 60 }, 1, 1.0)]
    [InlineData("*@0.4", new[] { 60 }, 1, 0.4)]
    [InlineData("C2*2", new[] { 36 }, 2, 1.0)]
    [InlineData("A3+C4*2", new[] { 57, 60 }, 2, 1.0)]
    [InlineData("A3@0.6+C4@0.6*4", new[] { 57, 60 }, 4, 0.6)]
    [InlineData("x*8", new[] { 60 }, 8, 1.0)]
    [InlineData("x*16", new[] { 60 }, NotationResolver.MaximumRatchetCount, 1.0)]
    [InlineData("x*1", new[] { 60 }, 1, 1.0)]
    public void ParsesRatchets(string stepNotation, int[] expectedNotes, int expectedRatchet, double expectedVelocity)
    {
        var stepEvent = NotationResolver.TryResolve(stepNotation)!.Value;
        Assert.Equal(StepEventKind.Trigger, stepEvent.Kind);
        Assert.Equal(expectedNotes, stepEvent.MidiNotes);
        Assert.Equal(expectedRatchet, stepEvent.StrikeCount);
        Assert.Equal(expectedVelocity, stepEvent.NoteVelocities[0], 6);
    }

    [Fact]
    public void RatchetsKeepPerNoteLengthsAndVelocities()
    {
        var stepEvent = NotationResolver.ResolveOrRest("C4:8@0.5*2+E4:2");
        Assert.Equal(new[] { 60, 64 }, stepEvent.MidiNotes);
        Assert.Equal(new[] { 8, 2 }, stepEvent.NoteLengths);
        Assert.Equal(new[] { 0.5, 1.0 }, stepEvent.NoteVelocities);
        Assert.Equal(2, stepEvent.StrikeCount);
    }

    [Theory]
    [InlineData("x*")]
    [InlineData("x*0")]
    [InlineData("x*2*3")]
    [InlineData("x*1.5")]
    [InlineData("x*two")]
    [InlineData(".*2")]
    [InlineData("_*2")]
    [InlineData("C4*")]
    public void MalformedRatchetsAreUnreadable(string stepNotation) =>
        Assert.Null(NotationResolver.TryResolve(stepNotation));

    [Theory]
    [InlineData("x*3@0.5", true, "x@0.5*3")]
    [InlineData("x@0.5*3", true, "x@0.5*3")]
    [InlineData("x*2", true, "x*2")]
    [InlineData("x*1", true, "x")]
    [InlineData("x*12", true, "x*8")]
    [InlineData("C2*2", true, "x*2")]
    [InlineData("C2*2", false, "C2*2")]
    [InlineData("x*2", false, "C4*2")]
    [InlineData("C4*2@0.5", false, "C4@0.5*2")]
    [InlineData("C4+A3*2", false, "A3+C4*2")]
    [InlineData("A3*2+C4", false, "A3+C4*2")]
    [InlineData("A3@0.6+C4:8@0.6*4", false, "A3@0.6+C4:8@0.6*4")]
    public void CanonicalizesRatchetsLast(string stepNotation, bool percussive, string expected)
    {
        var canonical = NotationResolver.Canonicalize(stepNotation, percussive);
        Assert.Equal(expected, canonical);
        Assert.Equal(canonical, NotationResolver.Canonicalize(canonical, percussive));
    }

    [Fact]
    public void RatchetsSurviveSerializationAndRemediation()
    {
        var project = new SlawProject
        {
            Scenes = new List<string> { "A" },
            Tracks = new List<SlawTrack>
            {
                new() { Name = "Hats", Instrument = "hat", Clips = new List<SlawClip?> { new() { Name = "h", Pattern = new List<string> { "x*2", "x*3@0.5", "*", "**4", "x*0" } } } },
                new() { Name = "Reese", Instrument = "saw", Clips = new List<SlawClip?> { new() { Name = "r", Pattern = new List<string> { "C2*2", "_", "A3+C4*2", "C4:4@0.5*3" } } } },
            },
        };
        var reloaded = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Equal(new[] { "x*2", "x*3@0.5", "*", "**4", "x*0" }, reloaded.Tracks[0].Clips[0]!.Pattern);

        var ledger = ProjectAlignmentRemediator.Remediate(reloaded);
        Assert.Equal(new[] { "x*2", "x@0.5*3", "x", "x*4", "." }, reloaded.Tracks[0].Clips[0]!.Pattern);
        Assert.Equal(new[] { "C2*2", "_", "A3+C4*2", "C4:4@0.5*3" }, reloaded.Tracks[1].Clips[0]!.Pattern);
        Assert.Contains(ledger, entry => entry.Contains("1 unreadable step", StringComparison.Ordinal));
        Assert.Equal(reloaded.Tracks[1].Clips[0]!.Pattern, ProjectSerializer.Deserialize(ProjectSerializer.Serialize(reloaded)).Tracks[1].Clips[0]!.Pattern);
    }

    [Fact]
    public void EditorOperationsKeepTheRatchet()
    {
        var pattern = Enumerable.Repeat(".", 8).ToList();
        pattern[0] = "A3*2";
        ClipNoteEditor.AddNote(pattern, 0, 60, length: 1);
        Assert.Equal("A3+C4*2", pattern[0]);
        ClipNoteEditor.SetNoteVelocity(pattern, 0, 57, 0.5);
        Assert.Equal("A3@0.5+C4*2", pattern[0]);
        ClipNoteEditor.SetNoteLength(pattern, 0, 60, 4);
        Assert.Equal("A3@0.5+C4:4*2", pattern[0]);
        Assert.All(ClipNoteEditor.NotesStartingAt(pattern, 0), placedNote => Assert.Equal(2, placedNote.RatchetCount));
        ClipNoteEditor.RemoveNote(pattern, 0, 57);
        Assert.Equal(new[] { "C4*2", "_", "_", "_", "." }, pattern.Take(5));
        ClipNoteEditor.RemoveNote(pattern, 0, 60);
        Assert.Equal(".", pattern[0]);

        var transposed = new SlawClip { Pattern = new List<string> { "C2@0.7*3" } };
        PatternEditingOperations.Transpose(new SlawTrack { Instrument = "saw" }, transposed, 0, 12);
        Assert.Equal("C3@0.7*3", transposed.Pattern[0]);

        var drums = new List<string> { "x*4", "." };
        ClipNoteEditor.SetDrumHitVelocity(drums, 0, 0.3);
        Assert.Equal("x@0.3*4", drums[0]);
    }

    [Fact]
    public void LintWarnsAboutRatchetsThatLookPerNote()
    {
        Assert.Null(PatternLint.DescribeRatchetProblem("x*2"));
        Assert.Null(PatternLint.DescribeRatchetProblem("A3+C4*2"));
        Assert.Null(PatternLint.DescribeRatchetProblem("A3@0.6+C4@0.6*2"));
        Assert.Null(PatternLint.DescribeRatchetProblem("*"));
        Assert.Null(PatternLint.DescribeRatchetProblem("x*3@0.5"));
        Assert.Contains("\"A3+C4*2\"", PatternLint.DescribeRatchetProblem("A3*2+C4"));
        Assert.Contains("WHOLE step", PatternLint.DescribeRatchetProblem("A3*2+C4*3"));
        Assert.Contains("\"A3+C4*3\"", PatternLint.DescribeRatchetProblem("A3*2+C4*3"));
        Assert.Contains("\"x*8\"", PatternLint.DescribeRatchetProblem("x*16"));

        // A trailing velocity is still flagged, and the suggestion keeps the ratchet at the end.
        Assert.Equal("A3@0.7+C4@0.7+E4@0.7*2", PatternLint.DescribeTrailingSuffix("A3+C4+E4@0.7*2")?.Suggestion);
        Assert.Null(PatternLint.DescribeTrailingSuffix("A3+C4*2"));

        var warnings = PatternLint.ChordSuffixWarnings(new[] { "A3*2+C4", "x*2", "x*20", "." }, "Chords, ");
        Assert.Equal(2, warnings.Count);
        Assert.StartsWith("Chords, step 0: \"A3*2+C4\" repeats the WHOLE step 2 times", warnings[0]);
        Assert.StartsWith("Chords, step 2: \"x*20\" plays as \"x*8\"", warnings[1]);
    }

    // ── Engine ────────────────────────────────────────────────────────────────

    private static SlawProject RatchetProject(string pattern, string instrument = "hat", double bpm = 60)
    {
        var track = new SlawTrack
        {
            Name = "Roll",
            Instrument = instrument,
            Gain = 1,
            // The shortest hat (15 ms) leaves silence between strikes a quarter-step apart, so every onset is visible.
            InstrumentParameters = instrument == "hat" ? new Dictionary<string, double> { ["decay"] = 0.25 } : new Dictionary<string, double>(),
            Clips = new List<SlawClip?> { new() { Name = "roll", Pattern = CompactPatternJsonConverter.SplitStepString(pattern) } },
        };
        var project = ProjectSerializer.CreateBlankProject("ratchet probe", bpm);
        project.Scenes = new List<string> { "A" };
        project.Tracks.Add(track);
        project.Timeline.Add(new TimelineClip { TrackId = track.Id, StartStep = 0, LengthSteps = 4, Clip = new SlawClip { Name = "roll", Pattern = track.Clips[0]!.Pattern.ToList() } });
        return project;
    }

    /// <summary>Frames where sound starts after at least <paramref name="minimumSilence"/> frames of (near) silence.</summary>
    private static List<int> Onsets(float[] samples, int minimumSilence = 200)
    {
        var onsets = new List<int>();
        var silentRun = minimumSilence;
        for (var frameIndex = 0; frameIndex < samples.Length; frameIndex++)
        {
            if (Math.Abs(samples[frameIndex]) > 1e-4f)
            {
                if (silentRun >= minimumSilence)
                {
                    onsets.Add(frameIndex);
                }
                silentRun = 0;
            }
            else
            {
                silentRun++;
            }
        }
        return onsets;
    }

    /// <summary>Runs the live engine block by block (odd block size on purpose) with the given transport start.</summary>
    private static float[] RenderLive(SlawProject project, Action<SequencerEngine> start, int frameCount, int blockFrames = 333)
    {
        var engine = new SequencerEngine(TestSampleRate);
        engine.PublishProject(project);
        start(engine);
        var rendered = new float[frameCount];
        var right = new float[blockFrames];
        for (var blockStart = 0; blockStart < frameCount; blockStart += blockFrames)
        {
            var blockLength = Math.Min(blockFrames, frameCount - blockStart);
            engine.RenderBlock(rendered.AsSpan(blockStart, blockLength), right.AsSpan(0, blockLength));
        }
        return rendered;
    }

    [Fact]
    public void FourStrikesLandEvenlyInsideOneStep()
    {
        // 60 BPM at 22050 Hz: a step is 5512.5 frames, so the second step starts on frame 5513
        // (the engine starts a step on the first whole frame at or after its boundary).
        var project = RatchetProject(". x*4 . .");
        var stepFrames = project.StepDurationSeconds * TestSampleRate;
        var rendered = OfflineArrangementRenderer.Render(project, TestSampleRate).Left;
        var onsets = Onsets(rendered);
        Assert.Equal(4, onsets.Count);
        var stepStart = (int)Math.Ceiling(stepFrames);
        for (var strikeIndex = 0; strikeIndex < 4; strikeIndex++)
        {
            Assert.InRange(onsets[strikeIndex] - stepStart - strikeIndex * stepFrames / 4, -1, 1);
        }

        // The same step without a ratchet strikes once, and each strike is that same hit.
        var single = OfflineArrangementRenderer.Render(RatchetProject(". x . ."), TestSampleRate).Left;
        Assert.Equal(new[] { onsets[0] }, Onsets(single));
        var hitFrames = 300;
        for (var strikeIndex = 0; strikeIndex < 4; strikeIndex++)
        {
            for (var frameOffset = 0; frameOffset < hitFrames; frameOffset++)
            {
                Assert.Equal(single[onsets[0] + frameOffset], rendered[onsets[strikeIndex] + frameOffset], 4);
            }
        }
    }

    [Fact]
    public void SessionArrangementAndBounceAgreeOnEveryStrike()
    {
        var project = RatchetProject("x*2 x*3@0.5 . x*4");
        var frameCount = (int)(4 * project.StepDurationSeconds * TestSampleRate);
        var bounced = OfflineArrangementRenderer.Render(project, TestSampleRate).Left;
        var arrangement = RenderLive(project, engine => engine.PlayTimeline(), frameCount);
        var session = RenderLive(project, engine => engine.LaunchClip(project.Tracks[0].Id, 0), frameCount);
        var expectedOnsets = Onsets(bounced.Take(frameCount).ToArray());
        Assert.Equal(2 + 3 + 4, expectedOnsets.Count);
        Assert.Equal(expectedOnsets, Onsets(arrangement));
        Assert.Equal(expectedOnsets, Onsets(session));
        Assert.Equal(bounced.Take(frameCount), arrangement);

        // Deterministic, like every bounce.
        Assert.Equal(bounced, OfflineArrangementRenderer.Render(project, TestSampleRate).Left);
    }

    [Fact]
    public void MelodicRatchetsStrikeAgainAndTheLastStrikeHoldsTheNote()
    {
        static SlawProject Bass(string pattern)
        {
            var project = RatchetProject(pattern, "sine", bpm: 60);
            project.Tracks[0].InstrumentParameters = new Dictionary<string, double> { ["attack"] = 1, ["release"] = 5, ["sustain"] = 1 };
            project.Timeline[0].LengthSteps = 8;
            return project;
        }
        var stepFrames = Bass("C3").StepDurationSeconds * TestSampleRate;
        var plain = OfflineArrangementRenderer.Render(Bass("C3 _ _ _ . . . ."), TestSampleRate).Left;
        var ratcheted = OfflineArrangementRenderer.Render(Bass("C3*2 _ _ _ . . . ."), TestSampleRate).Left;

        // Both end where the held note ends (4 steps, gated at 92%, plus a 5 ms release).
        static int LastSound(float[] samples) => Array.FindLastIndex(samples, sample => Math.Abs(sample) > 1e-3f);
        Assert.InRange(LastSound(ratcheted) - LastSound(plain), -(int)(stepFrames * 0.1), (int)(stepFrames * 0.1));

        // The first strike is released before the second starts, half a step in: a dip, then the attack again.
        var secondStrike = (int)Math.Round(stepFrames / 2);
        var dipWindow = ratcheted.Skip(secondStrike - 60).Take(60).Max(Math.Abs);
        var sustainedWindow = plain.Skip(secondStrike - 60).Take(60).Max(Math.Abs);
        Assert.True(dipWindow < sustainedWindow * 0.2, $"no gap before the second strike ({dipWindow} vs {sustainedWindow})");
        Assert.True(ratcheted.Skip(secondStrike + 200).Take(200).Max(Math.Abs) > sustainedWindow * 0.8, "second strike is missing");
    }

    // ── Unison spread ─────────────────────────────────────────────────────────

    /// <summary>
    /// The oscillator exactly as it was before unison spread existed (saw, no bend), so
    /// "spread 0 changes nothing" is checked against the original arithmetic, sample for sample.
    /// </summary>
    private static float[] ReferenceSaw(double frequencyHz, double gateDurationSeconds, int sampleRate, double attackMs, double decayMs, double sustainLevel, double releaseMs)
    {
        var attackSeconds = attackMs / 1000.0;
        var decaySeconds = decayMs / 1000.0;
        var releaseSeconds = releaseMs / 1000.0;
        var gateSampleCount = (int)(gateDurationSeconds * sampleRate);
        var renderedVoice = new float[gateSampleCount + (int)(releaseSeconds * sampleRate)];
        var phaseIncrementPerSample = frequencyHz / sampleRate;
        var oscillatorPhase = 0.0;
        var levelAtGateRelease = 0.0;
        for (var sampleIndex = 0; sampleIndex < renderedVoice.Length; sampleIndex++)
        {
            var elapsedSeconds = (double)sampleIndex / sampleRate;
            double envelopeLevel;
            if (sampleIndex < gateSampleCount)
            {
                envelopeLevel = elapsedSeconds < attackSeconds
                    ? elapsedSeconds / attackSeconds
                    : elapsedSeconds < attackSeconds + decaySeconds
                        ? 1 - (1 - sustainLevel) * (elapsedSeconds - attackSeconds) / decaySeconds
                        : sustainLevel;
                levelAtGateRelease = envelopeLevel;
            }
            else
            {
                var releaseProgress = (double)(sampleIndex - gateSampleCount) / (releaseSeconds * sampleRate);
                envelopeLevel = levelAtGateRelease * (1 - releaseProgress);
            }
            renderedVoice[sampleIndex] = (float)((2 * oscillatorPhase - 1) * envelopeLevel * 0.4);
            oscillatorPhase += phaseIncrementPerSample;
            oscillatorPhase -= Math.Floor(oscillatorPhase);
        }
        return renderedVoice;
    }

    [Fact]
    public void SpreadZeroIsTheOriginalOscillatorBitForBit()
    {
        var reference = ReferenceSaw(NotationResolver.MidiNoteToFrequency(33), 0.5, 44100, 4, 90, 0.7, 70);
        var withoutKey = InstrumentVoiceSynthesizer.RenderVoice("saw", new[] { 33 }, 0.5, 44100, new Random(1));
        var spreadZero = InstrumentVoiceSynthesizer.RenderVoice("saw", new[] { 33 }, 0.5, 44100, new Random(1), new Dictionary<string, double> { ["spread"] = 0 });
        Assert.Equal(reference, withoutKey);
        Assert.Equal(reference, spreadZero);

        foreach (var oscillator in new[] { "sine", "square", "triangle" })
        {
            Assert.Equal(
                InstrumentVoiceSynthesizer.RenderVoice(oscillator, new[] { 45, 52 }, 0.3, 22050, new Random(1)),
                InstrumentVoiceSynthesizer.RenderVoice(oscillator, new[] { 45, 52 }, 0.3, 22050, new Random(1), new Dictionary<string, double> { ["spread"] = 0 }));
        }
    }

    [Fact]
    public void SpreadIsAnOscillatorKnobAndAnEnvelopeTarget()
    {
        foreach (var oscillator in new[] { "sine", "square", "saw", "triangle" })
        {
            var spreadSpecification = Assert.Single(DeviceCatalog.InstrumentParametersFor(oscillator), specification => specification.Key == "spread");
            Assert.Equal((0.0, 50.0, 0.0), (spreadSpecification.Minimum, spreadSpecification.Maximum, spreadSpecification.Default));
            Assert.NotEmpty(DeviceCatalog.InstrumentDescription(oscillator));
            Assert.NotNull(ClipAutomation.ResolveTarget(new SlawTrack { Instrument = oscillator }, "inst.spread"));
        }
        Assert.DoesNotContain(DeviceCatalog.InstrumentParametersFor("noise"), specification => specification.Key == "spread");
        Assert.DoesNotContain(DeviceCatalog.InstrumentParametersFor("pluck"), specification => specification.Key == "spread");
        Assert.Equal("saw", ProjectAlignmentRemediator.ResolveInstrument("Reese"));

        // An inst.spread envelope reaches the voice exactly as the knob does (sampled as each note starts).
        static float[] RenderReese(Dictionary<string, double> knobs, Dictionary<string, List<AutomationPoint>>? automation)
        {
            var project = RatchetProject("A1 _ _ _", "saw", bpm: 120);
            project.Tracks[0].InstrumentParameters = knobs;
            project.Timeline[0].Clip.Automation = automation;
            return OfflineArrangementRenderer.Render(project, TestSampleRate).Left;
        }
        var knobbed = RenderReese(new Dictionary<string, double> { ["spread"] = 30 }, null);
        var enveloped = RenderReese(new Dictionary<string, double>(), new Dictionary<string, List<AutomationPoint>>
        {
            ["inst.spread"] = new() { new AutomationPoint { Step = 0, Value = 30 }, new AutomationPoint { Step = 4, Value = 30 } },
        });
        Assert.Equal(knobbed, enveloped);
        Assert.NotEqual(knobbed, RenderReese(new Dictionary<string, double>(), null));
    }

    /// <summary>Magnitude of one frequency in a signal (a single-bin DFT).</summary>
    private static double Magnitude(float[] samples, int start, int length, double frequencyHz, int sampleRate)
    {
        double real = 0, imaginary = 0;
        for (var frameIndex = 0; frameIndex < length; frameIndex++)
        {
            // A Hann window keeps a strong neighbour from leaking into the bin.
            var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * frameIndex / (length - 1));
            var angle = 2 * Math.PI * frequencyHz * frameIndex / sampleRate;
            real += samples[start + frameIndex] * window * Math.Cos(angle);
            imaginary += samples[start + frameIndex] * window * Math.Sin(angle);
        }
        return Math.Sqrt(real * real + imaginary * imaginary) / length;
    }

    private static double Rms(float[] samples, int start, int length) =>
        Math.Sqrt(samples.Skip(start).Take(length).Sum(sample => (double)sample * sample) / length);

    [Theory]
    [InlineData("sine", 30)]
    [InlineData("saw", 25)]
    public void SpreadSplitsTheFundamentalIntoTwoBeatingPartialsAtTheSameLevel(string oscillator, double spreadCents)
    {
        const int sampleRate = 22050;
        const int midiNote = 45; // A2, 110 Hz
        var sustained = new Dictionary<string, double> { ["attack"] = 1, ["sustain"] = 1, ["decay"] = 5 };
        var spread = new Dictionary<string, double>(sustained) { ["spread"] = spreadCents };
        var single = InstrumentVoiceSynthesizer.RenderVoice(oscillator, new[] { midiNote }, 4, sampleRate, new Random(1), sustained);
        var pair = InstrumentVoiceSynthesizer.RenderVoice(oscillator, new[] { midiNote }, 4, sampleRate, new Random(1), spread);

        // Four seconds at 0.25 Hz resolution: the two partials sit ±spread/2 cents around 110 Hz.
        var fundamentalHz = NotationResolver.MidiNoteToFrequency(midiNote);
        var lowerHz = fundamentalHz * Math.Pow(2, -spreadCents / 2 / 1200);
        var upperHz = fundamentalHz * Math.Pow(2, spreadCents / 2 / 1200);
        var analysisStart = sampleRate / 10;
        var analysisLength = 3 * sampleRate;
        var lowerPartial = Magnitude(pair, analysisStart, analysisLength, lowerHz, sampleRate);
        var upperPartial = Magnitude(pair, analysisStart, analysisLength, upperHz, sampleRate);
        var between = Magnitude(pair, analysisStart, analysisLength, fundamentalHz, sampleRate);
        var singleFundamental = Magnitude(single, analysisStart, analysisLength, fundamentalHz, sampleRate);
        Assert.InRange(lowerPartial / upperPartial, 0.8, 1.25);
        Assert.True(between < lowerPartial * 0.5, $"no split around the fundamental ({between} vs {lowerPartial})");
        // Equal power: each partial carries half the single oscillator's fundamental power.
        Assert.InRange(lowerPartial / singleFundamental, 0.6, 0.8);

        // The pair beats: its short-term level swings, the single oscillator's does not.
        // Windows of five cycles of the fundamental (about 45 ms), so a steady tone measures steady.
        var windowFrames = (int)Math.Round(5 * sampleRate / fundamentalHz);
        var windowCount = analysisLength / windowFrames;
        var pairLevels = Enumerable.Range(0, windowCount).Select(windowIndex => Rms(pair, analysisStart + windowIndex * windowFrames, windowFrames)).ToList();
        var singleLevels = Enumerable.Range(0, windowCount).Select(windowIndex => Rms(single, analysisStart + windowIndex * windowFrames, windowFrames)).ToList();
        Assert.True(singleLevels.Min() / singleLevels.Max() > 0.95, $"single oscillator not steady ({singleLevels.Min()} / {singleLevels.Max()})");
        Assert.True(pairLevels.Min() / pairLevels.Max() < (oscillator == "sine" ? 0.3 : 0.8), $"no beating ({pairLevels.Min()} / {pairLevels.Max()})");

        // And the overall level stays put (within 1 dB), so the level help in get_catalog still holds.
        var levelChangeDb = 20 * Math.Log10(Rms(pair, analysisStart, analysisLength) / Rms(single, analysisStart, analysisLength));
        Assert.InRange(levelChangeDb, -1, 1);

        // Deterministic: the partner oscillator's start phase is fixed.
        Assert.Equal(pair, InstrumentVoiceSynthesizer.RenderVoice(oscillator, new[] { midiNote }, 4, sampleRate, new Random(99), spread));
    }
}
