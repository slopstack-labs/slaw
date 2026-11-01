using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Whether stakeholders can find what they just onboarded: the browser's search and tracks that announce themselves.</summary>
public class BrowserFindabilityTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("dex", true)]
    [InlineData("DEXED", true)]
    [InlineData("vst3 dex", true)]
    [InlineData("dex vst3", true)]
    [InlineData("dex effect", false)]
    [InlineData("serum", false)]
    public void SearchWantsEveryWordInAnyOrder(string query, bool expected) =>
        Assert.Equal(expected, BrowserSearch.Matches(query, "Dexed Digital Suburban vst3 plugin instrument"));

    private static StudioDocument DocumentWithOneTrack()
    {
        var project = new SlawProject();
        project.Tracks.Clear();
        project.Tracks.Add(new SlawTrack { Name = "Kick", Instrument = "kick" });
        return new StudioDocument(project, null);
    }

    [Fact]
    public void TrackAddedThroughAnEditIsSelectedAndAnnounced()
    {
        var document = DocumentWithOneTrack();
        var announced = new List<int>();
        document.TrackArrived += announced.Add;

        document.Edit("added two", project =>
        {
            project.Tracks.Add(new SlawTrack { Name = "Pad", Instrument = "saw" });
            project.Tracks.Add(new SlawTrack { Name = "Keys", Instrument = "epiano" });
        }, EditScope.Structure);

        Assert.Equal(new[] { 2 }, announced);
        Assert.Equal(2, document.SelectedTrackIndex);
    }

    [Fact]
    public void TrackAddedByReplacingTheProjectIsAnnounced()
    {
        var document = DocumentWithOneTrack();
        var announced = new List<int>();
        document.TrackArrived += announced.Add;

        var patched = document.Project.DeepClone();
        patched.Tracks.Add(new SlawTrack { Name = "Pad", Instrument = "saw" });
        document.ReplaceProject(patched, "agent: patch", undoable: true);

        Assert.Equal(new[] { 1 }, announced);
        Assert.Equal(1, document.SelectedTrackIndex);
    }

    [Fact]
    public void EditThatAddsNoTrackLeavesTheSelectionAlone()
    {
        var document = DocumentWithOneTrack();
        document.Edit("added one", project => project.Tracks.Add(new SlawTrack { Name = "Pad", Instrument = "saw" }), EditScope.Structure);
        document.Select(0, 0);
        var announced = new List<int>();
        document.TrackArrived += announced.Add;

        document.Edit("tempo", project => project.Bpm = 122);

        Assert.Empty(announced);
        Assert.Equal(0, document.SelectedTrackIndex);
    }

    [Fact]
    public void AnotherSongIsNotAnArrival()
    {
        var document = DocumentWithOneTrack();
        var announced = new List<int>();
        document.TrackArrived += announced.Add;

        var otherSong = new SlawProject();
        otherSong.Tracks.Clear();
        otherSong.Tracks.Add(new SlawTrack { Name = "Lead", Instrument = "saw" });
        otherSong.Tracks.Add(new SlawTrack { Name = "Bass", Instrument = "saw" });
        document.ReplaceProject(otherSong, "agent: import MIDI", undoable: true);

        Assert.Empty(announced);
        Assert.Equal(0, document.SelectedTrackIndex);
    }
}
