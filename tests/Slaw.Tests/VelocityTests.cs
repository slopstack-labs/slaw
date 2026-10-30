using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Per-note velocity: some notes are struck, some are merely suggested.</summary>
public class VelocityTests
{
    private static List<string> EmptyClip(int stepCount = 16) => Enumerable.Repeat(".", stepCount).ToList();

    [Theory]
    [InlineData("C4@0.5", 60, 0, 0.5)]
    [InlineData("C4:8@0.25", 60, 8, 0.25)]
    [InlineData("C4@0.25:8", 60, 8, 0.25)]
    [InlineData("C4@64", 60, 0, 64 / 127.0)]
    [InlineData("C4@7", 60, 0, 7 / 127.0)]
    [InlineData("C4@banana", 60, 0, 1)]
    [InlineData("C4@-3", 60, 0, NotationResolver.MinimumVelocity)]
    [InlineData("x@0.4", NotationResolver.DefaultTriggerMidiNote, 0, 0.4)]
    public void ParsesVelocities(string stepNotation, int expectedMidiNote, int expectedLength, double expectedVelocity)
    {
        var stepEvent = NotationResolver.TryResolve(stepNotation)!.Value;
        Assert.Equal(StepEventKind.Trigger, stepEvent.Kind);
        Assert.Equal(expectedMidiNote, stepEvent.MidiNote);
        Assert.Equal(expectedLength, stepEvent.NoteLengths[0]);
        Assert.Equal(expectedVelocity, stepEvent.NoteVelocities[0], 6);
    }

    [Theory]
    [InlineData("C4@0.5", false, "C4@0.5")]
    [InlineData("C4:8@0.5+E4", false, "C4:8@0.5+E4")]
    [InlineData("E4+C4@0.333", false, "C4@0.33+E4")]
    [InlineData("C4@1", false, "C4")]
    [InlineData("C4@127", false, "C4")]
    [InlineData("C4@0.6", true, "x@0.6")]
    [InlineData("x@0.6", true, "x@0.6")]
    [InlineData("x@0.6", false, "C4@0.6")]
    [InlineData("x", true, "x")]
    public void CanonicalizesWithoutLosingVelocity(string stepNotation, bool percussive, string expected) =>
        Assert.Equal(expected, NotationResolver.Canonicalize(stepNotation, percussive));

    [Fact]
    public void ChordNotesKeepTheirOwnVelocities()
    {
        var stepEvent = NotationResolver.ResolveOrRest("A3@0.3+C4+E4@0.8");
        Assert.Equal(new[] { 57, 60, 64 }, stepEvent.MidiNotes);
        Assert.Equal(new[] { 0.3, 1.0, 0.8 }, stepEvent.NoteVelocities);
        Assert.Equal(1.0, stepEvent.PeakVelocity);
    }

    [Fact]
    public void EditorOperationsPreserveVelocities()
    {
        var pattern = EmptyClip();
        ClipNoteEditor.AddNote(pattern, 0, 60, length: 4);
        ClipNoteEditor.AddNote(pattern, 0, 64, length: 4);
        ClipNoteEditor.SetNoteVelocity(pattern, 0, 60, 0.5);
        Assert.Equal(new[] { "C4@0.5+E4", "_", "_", "_" }, pattern.Take(4));

        // Lengthening the third, adding a fifth, removing the third: the root stays soft throughout.
        ClipNoteEditor.SetNoteLength(pattern, 0, 64, 8);
        Assert.Equal("C4:4@0.5+E4:8", pattern[0]);
        ClipNoteEditor.AddNote(pattern, 0, 67, length: 4, velocity: 0.75);
        Assert.Equal("C4:4@0.5+E4:8+G4:4@0.75", pattern[0]);
        ClipNoteEditor.RemoveNote(pattern, 0, 64);
        Assert.Equal(new[] { "C4@0.5+G4@0.75", "_", "_", "_", "." }, pattern.Take(5));
        Assert.Equal(new[] { 0.5, 0.75 }, ClipNoteEditor.NotesStartingAt(pattern, 0).Select(note => note.Velocity));

        var transposeTrack = new SlawTrack { Instrument = "saw" };
        var transposeClip = new SlawClip { Pattern = pattern };
        PatternEditingOperations.Transpose(transposeTrack, transposeClip, 0, 2);
        Assert.Equal("D4@0.5+A4@0.75", pattern[0]);

        var drums = EmptyClip(4);
        drums[1] = "x";
        ClipNoteEditor.SetDrumHitVelocity(drums, 1, 0.3);
        ClipNoteEditor.SetDrumHitVelocity(drums, 2, 0.3);
        Assert.Equal(new[] { ".", "x@0.3", ".", "." }, drums);
    }

    [Fact]
    public void SofterNotesRenderQuieter()
    {
        static float PeakOf(string pattern, string instrument)
        {
            var project = new SlawProject
            {
                Bpm = 120,
                Scenes = new List<string> { "A" },
                Arrangement = new List<ArrangementSection> { new() { Scene = 0, Repeats = 1 } },
                Tracks = new List<SlawTrack>
                {
                    new() { Instrument = instrument, Gain = 1, Clips = new List<SlawClip?> { new() { Pattern = CompactPatternJsonConverter.SplitStepString(pattern) } } },
                },
            };
            return OfflineArrangementRenderer.Render(project, 22050).Left.Max(Math.Abs);
        }

        // Half velocity is 0.5^1.5 ≈ 0.35 of full amplitude (≈ -9 dB).
        var melodicRatio = PeakOf("A3@0.5 _ _ _", "sine") / PeakOf("A3 _ _ _", "sine");
        Assert.InRange(melodicRatio, 0.30, 0.40);
        var drumRatio = PeakOf("x@0.5 . . .", "kick") / PeakOf("x . . .", "kick");
        Assert.InRange(drumRatio, 0.30, 0.40);
    }

    [Fact]
    public void RemediatorKeepsValidVelocities()
    {
        var project = new SlawProject
        {
            Scenes = new List<string> { "A" },
            Arrangement = new List<ArrangementSection> { new() { Scene = 0, Repeats = 1 } },
            Tracks = new List<SlawTrack>
            {
                new() { Name = "Keys", Instrument = "triangle", Clips = new List<SlawClip?> { new() { Name = "k", Pattern = new List<string> { "C4@0.5+E4", "_", "G4@banana", "x@0.25" } } } },
                new() { Name = "Kick", Instrument = "kick", Clips = new List<SlawClip?> { new() { Name = "d", Pattern = new List<string> { "x@0.4", "x@300", ".", "x" } } } },
            },
        };
        ProjectAlignmentRemediator.Remediate(project);
        Assert.Equal(new[] { "C4@0.5+E4", "_", "G4", "C4@0.25" }, project.Tracks[0].Clips[0]!.Pattern);
        Assert.Equal(new[] { "x@0.4", "x", ".", "x" }, project.Tracks[1].Clips[0]!.Pattern);
    }
}
