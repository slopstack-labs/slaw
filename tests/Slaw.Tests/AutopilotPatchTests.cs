using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The autopilot speaks in patches now; these keep it honest.</summary>
public class AutopilotPatchTests
{
    [Fact]
    public void PatchAddsEditsRemovesAndRespectsLocks()
    {
        var baseProject = ProjectSerializer.CreateStarterProject("patch");
        var kickTrack = baseProject.Tracks[0];
        var lockedTrack = baseProject.Tracks[1];
        lockedTrack.AiLocked = true;
        var removedTrack = baseProject.Tracks[2];

        var projectPatch = ProjectPatchApplier.ParsePatch($$$"""
            {
              "summary": "made it wobble",
              "bpm": "140",
              "tracks": [
                {"id": "{{{kickTrack.Id}}}", "clips": {"1": "x . x . x . x . x . x . x . x ."}},
                {"id": "{{{lockedTrack.Id}}}", "name": "Hijacked"},
                {"id": "{{{removedTrack.Id}}}", "remove": true},
                {"id": "new", "name": "Wobble Bass", "instrument": "warm wobble bass", "effects": [{"type": "lowpass", "parameters": {"cutoff": 500}}], "clips": {"0": "E1 _ _ . E1 . G1 _"}}
              ]
            }
            """);

        var patchOutcome = ProjectPatchApplier.Apply(baseProject, projectPatch);
        var patchedProject = patchOutcome.Project;
        ProjectAlignmentRemediator.Remediate(patchedProject);

        Assert.Equal("made it wobble", patchOutcome.ModelSummary);
        Assert.Equal(140, patchedProject.Bpm);
        Assert.Equal("x", patchedProject.Tracks.Single(track => track.Id == kickTrack.Id).Clips[1]!.Pattern[2]);
        Assert.Equal(lockedTrack.Name, patchedProject.Tracks.Single(track => track.Id == lockedTrack.Id).Name);
        Assert.Equal(1, patchOutcome.LockedTrackEditsIgnored);
        Assert.DoesNotContain(patchedProject.Tracks, track => track.Id == removedTrack.Id);

        var wobbleTrack = patchedProject.Tracks.Single(track => track.Name == "Wobble Bass");
        Assert.Equal("saw", wobbleTrack.Instrument);
        Assert.Equal("lowpass", Assert.Single(wobbleTrack.Effects).Type);
        Assert.Equal(new[] { "E1", "_", "_", ".", "E1", ".", "G1", "_" }, wobbleTrack.Clips[0]!.Pattern);
        Assert.Equal("Wobble Bass", baseProject.Tracks.Any(track => track.Name == "Wobble Bass") ? "leaked" : "Wobble Bass");
    }

    [Fact]
    public void StringPatternsDeserialize()
    {
        var parsedProject = ProjectSerializer.Deserialize("""{"tracks":[{"name":"K","instrument":"kick","clips":[{"name":"A","pattern":"x . . . x . . ."}]}]}""");
        Assert.Equal(8, parsedProject.Tracks[0].Clips[0]!.Pattern.Count);
    }
}

public class PayloadExtractionTests
{
    [Theory]
    [InlineData("Sure! {\"a\": {\"b\": \"}\"}} and also {\"c\": 1}", "{\"a\": {\"b\": \"}\"}}")]
    [InlineData("{\"tracks\": [{\"id\": \"new\", \"name\": \"Wob", "{\"tracks\": [{\"id\": \"new\", \"name\": \"Wob\"}]}")]
    [InlineData("{\"bpm\": 140, \"tracks\": [", "{\"bpm\": 140, \"tracks\": []}")]
    [InlineData("{\"bpm\": 140,", "{\"bpm\": 140}")]
    public void ExtractsFirstObjectAndRepairsTruncation(string modelOutput, string expectedPayload)
    {
        var extractedPayload = SlopstackLabs.Slaw.Workflows.LanguageModelPayloadExtractor.ExtractJsonObject(modelOutput);
        Assert.Equal(expectedPayload, extractedPayload);
        Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(extractedPayload!));
    }
}
