using System.Diagnostics;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Live audio recording: everything after the microphone, verified without one.</summary>
public class AudioRecordingTests
{
    private const int TakeSampleRate = 48000;

    private sealed class ScriptedInput : AudioInputSourceBase
    {
        public ScriptedInput()
            : base(TakeSampleRate)
        {
        }

        public override string SubstrateName => "scripted";

        public override void Start()
        {
        }

        public void Deliver(float[] block) => OnBlockCaptured(block);

        public override void Dispose()
        {
        }
    }

    private static SlawProject OneSceneProject() => new()
    {
        Bpm = 120,
        Scenes = new List<string> { "Verse" },
        Arrangement = new List<ArrangementSection> { new() { Scene = 0, Repeats = 1 } },
        Tracks = new List<SlawTrack>(),
    };

    [Fact]
    public void PunchInWhileLoopingLandsOnTheNextStepAndTrimsTheGap()
    {
        // Captured from step 5.25 of a running 16-step clip at 0.125 s per step, 20 ms input latency.
        var placement = AudioTakeAssembler.Place(5.25, clipWasRunning: true, clipLength: 16, stepSeconds: 0.125, inputLatencySeconds: 0.02, TakeSampleRate);
        Assert.Equal(6, placement.StartStep);
        Assert.Equal((int)Math.Round((0.75 * 0.125 + 0.02) * TakeSampleRate), placement.TrimFrames);

        // Just before the loop point the take belongs to the next lap's step 0.
        Assert.Equal(0, AudioTakeAssembler.Place(15.9, true, 16, 0.125, 0, TakeSampleRate).StartStep);
        // A position from before the wrap is folded back into the clip.
        Assert.Equal(3, AudioTakeAssembler.Place(-13.5, true, 16, 0.125, 0, TakeSampleRate).StartStep);
    }

    [Fact]
    public void PreRollBeforeTheTransportStartedIsTrimmedToStepZero()
    {
        // Capture began 1.5 steps before the clip started (launched from a stop, or on the next bar).
        var placement = AudioTakeAssembler.Place(-1.5, clipWasRunning: false, clipLength: 16, stepSeconds: 0.125, inputLatencySeconds: 0.03, TakeSampleRate);
        Assert.Equal(0, placement.StartStep);
        Assert.Equal((int)Math.Round((1.5 * 0.125 + 0.03) * TakeSampleRate), placement.TrimFrames);

        var takeSamples = Enumerable.Range(0, 100).Select(index => (float)index).ToArray();
        Assert.Equal(40f, AudioTakeAssembler.TrimHead(takeSamples, 40)[0]);
        Assert.Empty(AudioTakeAssembler.TrimHead(takeSamples, 500));
    }

    [Fact]
    public void TakesBecomeAOneNoteClipOnAnUnalteredSamplerAndPlayBackOnTheirStep()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-take-").FullName;
        try
        {
            // A 3-second take: half a second of silence, then a 440 Hz tone at -6 dBFS.
            var takeSamples = Enumerable.Range(0, 3 * TakeSampleRate)
                .Select(frame => frame < TakeSampleRate / 2 ? 0f : (float)(0.5 * Math.Sin(2 * Math.PI * 440 * frame / TakeSampleRate)))
                .ToArray();
            var takePath = Path.Combine(workingFolder, AudioTakeAssembler.RecordingsFolderName, AudioTakeAssembler.TakeFileName("Vocal Stakeholder 1", new DateTime(2026, 10, 30, 14, 3, 7)));
            Assert.EndsWith("vocal-stakeholder-1-20261030-140307.wav", takePath);
            AudioTakeAssembler.WriteTakeWave(takePath, takeSamples, TakeSampleRate);

            var project = OneSceneProject();
            var vocalTrack = AudioTakeAssembler.CreateTakeTrack(project, template: null);
            vocalTrack.Gain = 1;
            project.Tracks.Add(vocalTrack);
            Assert.Equal("Vocal Stakeholder 1", vocalTrack.Name);

            var takeClip = AudioTakeAssembler.ApplyTake(project, vocalTrack, sceneIndex: 0, takePath, startStep: 4, takeSamples.Length, TakeSampleRate);

            // 3 s at 0.125 s per step is 24 steps from step 4: the clip grows to two bars and holds the note to its end.
            Assert.Equal(32, takeClip.Pattern.Count);
            Assert.Equal(new[] { (AudioTakeAssembler.TakeRootNote, 28) }, ClipNoteEditor.NotesStartingAt(takeClip.Pattern, 4).Select(note => (note.MidiNote, note.Length)));
            Assert.Equal("sampler", vocalTrack.Instrument);
            Assert.Equal(Path.GetFullPath(takePath), vocalTrack.Sample);
            Assert.Equal(1, vocalTrack.InstrumentParameters["hold"]);

            var render = OfflineArrangementRenderer.Render(project, TakeSampleRate).Left;
            var stepFrames = (int)(project.StepDurationSeconds * TakeSampleRate);
            var toneOnsetFrame = 4 * stepFrames + TakeSampleRate / 2;
            Assert.True(render.Skip(4 * stepFrames).Take(TakeSampleRate / 2 - 200).Max(Math.Abs) < 0.01, "the take's leading silence stays silent");
            var tonePeak = render.Skip(toneOnsetFrame + 2000).Take(TakeSampleRate / 2).Max(Math.Abs);
            Assert.InRange(tonePeak, 0.25, 0.75);
            Assert.True(render.Take(4 * stepFrames - 10).Max(Math.Abs) < 0.001, "nothing plays before the take's step");
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    [Fact]
    public void LongTimelineTakesAreSlicedIntoConsecutiveClipsThatPlayWithoutGaps()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-take-").FullName;
        try
        {
            // 18 s of 220 Hz at 120 BPM (0.125 s steps) is 144 steps: slices of 64 + 64 + 16, from bar 2.
            var takeSamples = Enumerable.Range(0, 18 * TakeSampleRate)
                .Select(frame => (float)(0.5 * Math.Sin(2 * Math.PI * 220 * frame / TakeSampleRate)))
                .ToArray();
            var takePath = Path.Combine(workingFolder, "timeline-take.wav");
            AudioTakeAssembler.WriteTakeWave(takePath, takeSamples, TakeSampleRate);

            var project = OneSceneProject();
            var vocalTrack = AudioTakeAssembler.CreateTakeTrack(project, template: null);
            vocalTrack.Gain = 1;
            project.Tracks.Add(vocalTrack);
            var takeClips = AudioTakeAssembler.ApplyTimelineTake(project, vocalTrack, takePath, startStep: 16, takeSamples.Length, TakeSampleRate);

            Assert.Equal(new[] { (16, 64), (80, 64), (144, 16) }, takeClips.Select(clip => (clip.StartStep, clip.LengthSteps)));
            Assert.Equal(new[] { 24, 25, 26 }, takeClips.Select(clip => ClipNoteEditor.NotesStartingAt(clip.Clip.Pattern, 0).Single().MidiNote));
            Assert.Equal(SampleEditSettings.SliceMode, vocalTrack.SampleEditing!.PlaybackMode);
            Assert.Equal(new[] { 8.0, 16.0 }, vocalTrack.SampleEditing.SlicePointsSeconds);
            Assert.Equal(0, vocalTrack.InstrumentParameters["hold"]);
            Assert.True(AudioTakeAssembler.NeedsFreshTimelineTrack(project, vocalTrack));

            var render = OfflineArrangementRenderer.Render(project, TakeSampleRate).Left;
            var stepFrames = (int)(project.StepDurationSeconds * TakeSampleRate);
            Assert.True(render.Take(16 * stepFrames - 10).Max(Math.Abs) < 0.001, "nothing plays before the take's bar");
            // Around each slice boundary the tone carries on: no gate gap, no declick hole longer than a few milliseconds.
            foreach (var boundaryStep in new[] { 80, 144 })
            {
                var windowStart = boundaryStep * stepFrames - TakeSampleRate / 20;
                var quietestTenMilliseconds = Enumerable.Range(0, 10)
                    .Select(windowIndex => render.Skip(windowStart + windowIndex * TakeSampleRate / 100).Take(TakeSampleRate / 100).Max(Math.Abs))
                    .Min();
                Assert.True(quietestTenMilliseconds > 0.2, $"gap at step {boundaryStep}: {quietestTenMilliseconds}");
            }
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    [Fact]
    public void TakesOfTakesCountUpAndKeepTheirPluginEffects()
    {
        var project = OneSceneProject();
        var leadTrack = new SlawTrack
        {
            Name = "Lead",
            Instrument = "sampler",
            Effects = new List<EffectDevice>
            {
                new() { Type = "vst3", Plugin = new PluginReference { Path = "/usr/lib/vst3/MVerb.vst3", ClassId = new string('A', 32), Name = "MVerb" }, Parameters = { ["p10"] = 0.2 } },
            },
        };
        project.Tracks.Add(leadTrack);
        var secondTake = AudioTakeAssembler.CreateTakeTrack(project, leadTrack);
        project.Tracks.Add(secondTake);
        var thirdTake = AudioTakeAssembler.CreateTakeTrack(project, secondTake);

        Assert.Equal("Lead · take 2", secondTake.Name);
        Assert.Equal("Lead · take 3", thirdTake.Name);
        Assert.Equal("MVerb", thirdTake.Effects.Single().Plugin?.Name);
        Assert.NotSame(leadTrack.Effects[0].Plugin, secondTake.Effects[0].Plugin);
    }

    [Fact]
    public void TakeWavesRoundTripThroughTheSampleLibrary()
    {
        var workingFolder = Directory.CreateTempSubdirectory("slaw-wave-").FullName;
        try
        {
            var takeSamples = new[] { 0f, 0.25f, -0.5f, 0.999f, -1f };
            var takePath = Path.Combine(workingFolder, "nested", "take.wav");
            AudioTakeAssembler.WriteTakeWave(takePath, takeSamples, TakeSampleRate);
            var reloaded = SampleLibrary.Load(takePath);
            Assert.Equal(TakeSampleRate, reloaded.SampleRate);
            Assert.Equal(takeSamples.Length, reloaded.Samples.Length);
            for (var sampleIndex = 0; sampleIndex < takeSamples.Length; sampleIndex++)
            {
                Assert.InRange(reloaded.Samples[sampleIndex], takeSamples[sampleIndex] - 1e-4f, takeSamples[sampleIndex] + 1e-4f);
            }
        }
        finally
        {
            Directory.Delete(workingFolder, recursive: true);
        }
    }

    [Fact]
    public void ANewTakeNeverOverwritesASampleOtherScenesStillPlay()
    {
        var project = OneSceneProject();
        project.Scenes.Add("Chorus");
        var vocalTrack = AudioTakeAssembler.CreateTakeTrack(project, template: null);
        project.Tracks.Add(vocalTrack);
        Assert.False(AudioTakeAssembler.NeedsFreshTrack(vocalTrack, 0));

        vocalTrack.Sample = "/takes/verse.wav";
        vocalTrack.Clips[0] = new SlawClip { Pattern = new List<string> { "C4", ".", ".", "." } };
        Assert.False(AudioTakeAssembler.NeedsFreshTrack(vocalTrack, 0));
        Assert.True(AudioTakeAssembler.NeedsFreshTrack(vocalTrack, 1));

        vocalTrack.Effects.Add(new EffectDevice { Type = "reverb", Parameters = { ["mix"] = 0.3 } });
        var secondTakeTrack = AudioTakeAssembler.CreateTakeTrack(project, vocalTrack);
        Assert.Equal("Vocal Stakeholder 1 · take 2", secondTakeTrack.Name);
        Assert.Equal(0.3, secondTakeTrack.Effects.Single().Parameters["mix"]);
        Assert.Null(secondTakeTrack.Sample);
    }

    [Fact]
    public void CaptureCommandsFollowPreferenceAndSkipMissingTools()
    {
        var everythingInstalled = PipedProcessAudioInput.PlanCaptureCommands(48000, "auto", null, _ => true);
        Assert.Equal(new[] { "pw-record", "parec", "arecord" }, everythingInstalled.Select(command => command.ExecutableName));
        Assert.Equal(new[] { "--raw", "--format", "f32", "--rate", "48000", "--channels", "1", "--latency", "20ms", "-" }, everythingInstalled[0].Arguments);
        Assert.Contains("--latency-msec=20", everythingInstalled[1].Arguments);
        Assert.Equal(CaptureSampleEncoding.Signed16LittleEndian, everythingInstalled[2].Encoding);

        var preferArecord = PipedProcessAudioInput.PlanCaptureCommands(44100, "arecord", null, _ => true);
        Assert.Equal("arecord", preferArecord[0].ExecutableName);
        Assert.Contains("44100", preferArecord[0].Arguments);

        var pulseOnly = PipedProcessAudioInput.PlanCaptureCommands(48000, "pw-record", "alsa_input.usb-mic", tool => tool == "parec");
        Assert.Equal("parec", Assert.Single(pulseOnly).ExecutableName);
        Assert.Contains("--device=alsa_input.usb-mic", pulseOnly[0].Arguments);

        var pipeWireTargeted = PipedProcessAudioInput.PlanCaptureCommands(48000, "auto", "alsa_input.usb-mic", tool => tool != "parec");
        Assert.Equal(new[] { "--target", "alsa_input.usb-mic", "-" }, pipeWireTargeted[0].Arguments.TakeLast(3));
        Assert.DoesNotContain("alsa_input.usb-mic", pipeWireTargeted[1].Arguments);

        Assert.Empty(PipedProcessAudioInput.PlanCaptureCommands(48000, "auto", null, _ => false));
    }

    [Fact]
    public void DecodesRawSamplesAndKeepsPartialOnesForLater()
    {
        var decoded = new float[8];
        var floatBytes = BitConverter.GetBytes(0.5f).Concat(BitConverter.GetBytes(-2f)).Concat(new byte[] { 1, 2 }).ToArray();
        Assert.Equal(2, PipedProcessAudioInput.DecodeSamples(floatBytes, CaptureSampleEncoding.Float32LittleEndian, decoded));
        Assert.Equal(new[] { 0.5f, -1f }, decoded.Take(2));

        var shortBytes = new byte[] { 0x00, 0x40, 0x00, 0x80, 0xFF };
        Assert.Equal(2, PipedProcessAudioInput.DecodeSamples(shortBytes, CaptureSampleEncoding.Signed16LittleEndian, decoded));
        Assert.Equal(new[] { 0.5f, -1f }, decoded.Take(2));
    }

    [Fact]
    public void SourceListingSkipsMonitorsOfOurOwnOutput()
    {
        const string listing = "58\talsa_input.usb-Kingston_HyperX_SoloCast-00.analog-stereo\tPipeWire\ts16le 2ch 48000Hz\tRUNNING\n" +
                               "60\talsa_output.pci-0000_09_00.4.analog-stereo.monitor\tPipeWire\ts32le 2ch 48000Hz\tRUNNING\n";
        Assert.Equal(new[] { "alsa_input.usb-Kingston_HyperX_SoloCast-00.analog-stereo" }, PipedProcessAudioInput.ParseSourceListing(listing));
    }

    [Fact]
    public void InputsMeterAlwaysButKeepAudioOnlyDuringATake()
    {
        using var scriptedInput = new ScriptedInput();
        scriptedInput.Deliver(new[] { 0.1f, -0.8f });
        Assert.Equal(0.8f, scriptedInput.PeakLevel);
        Assert.False(scriptedInput.IsTakeOpen);

        scriptedInput.BeginTake();
        var beforeDelivery = Stopwatch.GetTimestamp();
        var block = Enumerable.Repeat(0.25f, TakeSampleRate / 100).ToArray();
        scriptedInput.Deliver(block);
        scriptedInput.Deliver(block);
        var take = scriptedInput.EndTake();

        Assert.Equal(2 * block.Length, take.Samples.Length);
        // The first frame is dated one block (10 ms) before the block arrived.
        var blockTicks = (long)(0.01 * Stopwatch.Frequency);
        Assert.InRange(take.FirstFrameTimestamp, beforeDelivery - blockTicks - Stopwatch.Frequency / 100, Stopwatch.GetTimestamp());
        Assert.False(scriptedInput.IsTakeOpen);
        Assert.Empty(scriptedInput.EndTake().Samples);
    }
}
