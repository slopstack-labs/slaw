using SlopstackLabs.Slaw.Inference;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;
using SlopstackLabs.Slaw.Workflows;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// The verification substrate. Deliberately thin: slaw's output is
/// non-deterministic by design, so we only test the parts that are not.
/// </summary>
public class SmokeTests
{
    [Theory]
    [InlineData("A4", 69)]
    [InlineData("C4", 60)]
    [InlineData("C#4", 61)]
    [InlineData("Bb2", 46)]
    [InlineData("72", 72)]
    public void ResolvesScientificPitchNotation(string stepNotation, int expectedMidiNote)
    {
        var resolvedStepEvent = NotationResolver.TryResolve(stepNotation);
        Assert.Equal(StepEventKind.Trigger, resolvedStepEvent?.Kind);
        Assert.Equal(expectedMidiNote, resolvedStepEvent?.MidiNote);
    }

    [Fact]
    public void RemediatesHallucinatedArrangement()
    {
        const string modelOutput = """
            Sure! Here's your arrangement:
            ```json
            {"name":"Q3","bpm":900,"steps":16,"loops":2,"tracks":[{"name":"Bass","instrument":"warm analog bass","pattern":["A1","_","banana"]}],}
            ```
            Let me know if you need anything else!
            """;

        var arrangementDeliverable = ProductionWorkflows.ParseArrangement(modelOutput, inferenceProvider: null);

        var remediatedTrack = Assert.Single(arrangementDeliverable.Project.Tracks);
        Assert.Equal(300, arrangementDeliverable.Project.Bpm);
        Assert.Equal("saw", remediatedTrack.Instrument);
        var remediatedPattern = remediatedTrack.Clips[0]!.Pattern;
        Assert.Equal(3, remediatedPattern.Count);
        Assert.Equal(new[] { "A1", "_", "." }, remediatedPattern.Take(3));
        // "loops": 2 becomes two plays of the scene on the arrangement timeline.
        var composedProject = arrangementDeliverable.Project;
        Assert.Empty(composedProject.Arrangement);
        var arrangementClip = Assert.Single(composedProject.Timeline);
        Assert.Equal(2 * composedProject.SceneLengthInSteps(0), arrangementClip.LengthSteps);
        Assert.NotEmpty(arrangementDeliverable.RemediationLedger);
    }

    [Fact]
    public void RejectsArrangementWithoutAnyJson() =>
        Assert.Throws<ArrangementMisalignmentException>(() =>
            ProductionWorkflows.ParseArrangement("I'm sorry, I can't compose music.", inferenceProvider: null));

    [Fact]
    public void RenderingIsDeterministicAndWithinHearingProtectionCeiling()
    {
        var starterProject = ProjectSerializer.CreateStarterProject("smoke");

        var firstRender = OfflineArrangementRenderer.Render(starterProject);
        var secondRender = OfflineArrangementRenderer.Render(starterProject);

        Assert.Equal(firstRender.Left, secondRender.Left);
        Assert.InRange(firstRender.PeakAmplitude(), 0.01f, MasterChainProcessor.HearingProtectionCeiling + 1e-6f);
    }

    [Fact]
    public void WritesCanonicalRiffHeader()
    {
        using var materializedStream = new MemoryStream();
        WaveFileMaterializer.Write(materializedStream, new StereoBuffer(100, 44100));
        var materializedBytes = materializedStream.ToArray();

        Assert.Equal("RIFF"u8.ToArray(), materializedBytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), materializedBytes[8..12]);
        Assert.Equal(44 + 100 * 4, materializedBytes.Length);
    }

    [Fact]
    public void DiscardsHallucinatedHeaderAndUpscalesPayload()
    {
        var hallucinatedFile = Convert.FromHexString("52494646" + "00000000" + "57415645" + "64617461" + "00000000" + "80FF0080");

        var decodedBuffer = HallucinatedWaveformDecoder.Decode(hallucinatedFile);

        Assert.Equal(new byte[] { 0x80, 0xFF, 0x00, 0x80 }, HallucinatedWaveformDecoder.ExtractPcmPayload(hallucinatedFile));
        Assert.True(decodedBuffer.DurationSeconds >= HallucinatedWaveformDecoder.MinimumDeliverableDurationSeconds);
    }

    [Fact]
    public void ChordNotationRoundTrips()
    {
        var resolvedChord = NotationResolver.TryResolve("E4+C4+G4");

        Assert.Equal(new[] { 60, 64, 67 }, resolvedChord?.MidiNotes);
        Assert.Equal(60, resolvedChord?.MidiNote);
        Assert.Equal("C4+E4+G4", NotationResolver.FormatChord(resolvedChord!.Value.MidiNotes));
        Assert.Equal("C4+E4+G4", NotationResolver.Canonicalize("E4+C4+G4", percussiveInstrument: false));
    }

    [Fact]
    public void SessionLaunchIsLiveAndEditsApplyWithoutRestart()
    {
        var sequencerEngine = new SequencerEngine(44100);
        var starterProject = ProjectSerializer.CreateStarterProject("t");
        sequencerEngine.PublishProject(starterProject);
        sequencerEngine.LaunchScene(1);

        var leftBlock = new float[512];
        var rightBlock = new float[512];
        var peakAmplitude = 0f;
        for (var blockIndex = 0; blockIndex < 40; blockIndex++)
        {
            sequencerEngine.RenderBlock(leftBlock, rightBlock);
            peakAmplitude = Math.Max(peakAmplitude, leftBlock.Max(Math.Abs));
        }
        Assert.True(peakAmplitude > 0.001f);
        Assert.Equal(TransportMode.Session, sequencerEngine.Transport.Mode);

        var mutedProject = starterProject.DeepClone();
        mutedProject.Tracks.ForEach(track => track.Muted = true);
        sequencerEngine.PublishProject(mutedProject);
        for (var blockIndex = 0; blockIndex < 44100 * 2 / 512; blockIndex++)
        {
            sequencerEngine.RenderBlock(leftBlock, rightBlock);
        }
        Assert.True(Math.Max(leftBlock.Max(Math.Abs), rightBlock.Max(Math.Abs)) < 0.001f);
    }

    [Fact]
    public async Task AutopilotRestoresAiLockedTracks()
    {
        var originalProject = ProjectSerializer.CreateStarterProject("t");
        originalProject.Tracks[0].AiLocked = true;
        var lockedTrackName = originalProject.Tracks[0].Name;
        var tamperedProject = originalProject.DeepClone();
        tamperedProject.Tracks[0].Name = "tampered by ai";
        var fakeProvider = new FixedResponseInferenceProvider(ProjectSerializer.Serialize(tamperedProject));

        var autopilotDeliverable = await ProductionWorkflows.AutopilotAsync(fakeProvider, originalProject, "x", 0);

        Assert.Equal(lockedTrackName, autopilotDeliverable.Project.Tracks[0].Name);
        Assert.Contains(autopilotDeliverable.ChangeSummary, line => line.Contains("AI-locked"));
    }

    [Fact]
    public void KeepsMoreThanSixteenTracksThroughRemediation()
    {
        var crowdedProject = new SlawProject { Name = "all hands", Scenes = ["Kickoff"] };
        for (var trackIndex = 0; trackIndex < 40; trackIndex++)
        {
            crowdedProject.Tracks.Add(new SlawTrack { Name = $"Stakeholder {trackIndex + 1}", Instrument = "sine", Clips = [SlawClip.Empty("Kickoff", 16)] });
        }

        var remediationLedger = ProjectAlignmentRemediator.Remediate(crowdedProject);

        Assert.Equal(40, crowdedProject.Tracks.Count);
        Assert.DoesNotContain(remediationLedger, entry => entry.Contains("tracks (the limit"));
    }

    [Fact]
    public void MigratesV1ProjectFilesToV2()
    {
        const string legacyProjectFile = """{"name":"old","bpm":120,"steps":16,"loops":3,"tracks":[{"name":"K","instrument":"kick","pattern":["x",".",".","."]}]}""";
        var migratedProject = ProjectSerializer.Deserialize(legacyProjectFile);

        ProjectAlignmentRemediator.Remediate(migratedProject);

        Assert.Equal("slaw-project/v2", migratedProject.Format);
        Assert.Single(migratedProject.Scenes);
        Assert.Empty(migratedProject.Arrangement);
        var arrangementClip = Assert.Single(migratedProject.Timeline);
        Assert.Equal(0, arrangementClip.StartStep);
        Assert.Equal(3 * migratedProject.SceneLengthInSteps(0), arrangementClip.LengthSteps);
        Assert.False(string.IsNullOrEmpty(migratedProject.Tracks[0].Id));
    }

    private sealed class FixedResponseInferenceProvider : IInferenceSubstrateProvider
    {
        private readonly string cannedResponse;

        public FixedResponseInferenceProvider(string cannedResponse) => this.cannedResponse = cannedResponse;

        public string ProviderDisplayName => "Fake";
        public string ModelIdentifier => "fake-1";

        public Task<string> StreamCompletionAsync(
            string prompt, IReadOnlyList<string> progressSteps, CancellationToken cancellationToken = default, InferenceRequestOptions? requestOptions = null) =>
            Task.FromResult(cannedResponse);
    }
}
