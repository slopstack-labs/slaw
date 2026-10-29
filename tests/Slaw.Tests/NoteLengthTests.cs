using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Notes struck together no longer have to let go together.</summary>
public class NoteLengthTests
{
    private static List<string> EmptyClip(int stepCount = 16) => Enumerable.Repeat(".", stepCount).ToList();

    [Fact]
    public void ChordNotesKeepTheirOwnLengths()
    {
        var pattern = EmptyClip();
        ClipNoteEditor.AddNote(pattern, 0, 60, length: 8);
        ClipNoteEditor.AddNote(pattern, 0, 64, length: 2);

        Assert.Equal("C4:8+E4:2", pattern[0]);
        Assert.Equal(new[] { (60, 8), (64, 2) }, ClipNoteEditor.NotesStartingAt(pattern, 0).Select(note => (note.MidiNote, note.Length)));
        Assert.Equal(0, ClipNoteEditor.FindNoteCovering(pattern, 6, 60)?.StartStep);
        Assert.Null(ClipNoteEditor.FindNoteCovering(pattern, 6, 64));
    }

    [Fact]
    public void LongNotesRingOverLaterNotesAndUniformChordsStayClassic()
    {
        var pattern = EmptyClip();
        ClipNoteEditor.AddNote(pattern, 0, 48, length: 1);
        ClipNoteEditor.AddNote(pattern, 2, 67, length: 1);
        ClipNoteEditor.SetNoteLength(pattern, 0, 48, 8);

        Assert.Equal("C3:8", pattern[0]);
        Assert.Equal("G4", pattern[2]);
        Assert.Equal(0, ClipNoteEditor.FindNoteCovering(pattern, 5, 48)?.StartStep);

        var uniform = EmptyClip();
        ClipNoteEditor.AddNote(uniform, 4, 60, length: 4);
        ClipNoteEditor.AddNote(uniform, 4, 64, length: 4);
        Assert.Equal(new[] { "C4+E4", "_", "_", "_", "." }, uniform.Skip(4).Take(5));

        ClipNoteEditor.RemoveNote(uniform, 4, 60);
        ClipNoteEditor.RemoveNote(uniform, 4, 64);
        Assert.All(uniform, step => Assert.Equal(".", step));
    }

    [Fact]
    public void EngineRendersEachNoteForItsOwnLength()
    {
        var project = new SlawProject
        {
            Bpm = 120,
            Scenes = { "A" },
            Tracks =
            {
                new SlawTrack
                {
                    Name = "Pad",
                    Instrument = "sine",
                    InstrumentParameters = { ["release"] = 5 },
                    Clips = { new SlawClip { Name = "A", Pattern = "C4:8+C6:1 . . . . . . . . . . . . . . .".Split(' ').ToList() } },
                },
            },
            Arrangement = { new ArrangementSection { Scene = 0, Repeats = 1 } },
        };
        ProjectAlignmentRemediator.Remediate(project);
        Assert.Equal("C4:8+C6", project.Tracks[0].Clips[0]!.Pattern[0]);

        var rendered = OfflineArrangementRenderer.Render(project, sampleRate: 8000);
        // One step at 120 BPM is 0.125 s. Step 4 (0.5–0.625 s) must still hold C4 but not C6.
        var midStep = rendered.Left.AsSpan((int)(0.52 * 8000), 600).ToArray();
        var late = rendered.Left.AsSpan((int)(1.3 * 8000), 600).ToArray();
        Assert.True(midStep.Max(Math.Abs) > 0.01f, "the long note stopped early");
        Assert.True(late.Max(Math.Abs) < 0.001f, "something rang past its length");
    }
}
