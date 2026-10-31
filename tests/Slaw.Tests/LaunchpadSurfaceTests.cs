using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Surfaces;
using SlopstackLabs.Slaw.Synthesis;
using Xunit;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>No hardware: a recording port checks the bytes against Novation's programmer's reference.</summary>
public class LaunchpadSurfaceTests
{
    private const int Off = 0;
    private const int Dim = 1;
    private const int White = 3;
    private const int Red = 5;
    private const int Yellow = 13;
    private const int Green = 21;
    private const int Blue = 45;

    private sealed class RecordingPort : IMidiOutputPort
    {
        public string Name => "Launchpad MK2";

        public List<(int Status, int Address, int Color)> Sent { get; } = new();

        public List<byte[]> Sysex { get; } = new();

        public bool Disposed { get; private set; }

        public void SendShort(int status, int data1, int data2) => Sent.Add((status, data1, data2));

        public void SendSysex(byte[] sysexMessage) => Sysex.Add(sysexMessage);

        public void Dispose() => Disposed = true;

        /// <summary>The colour an address was last told to show on a channel.</summary>
        public int? LastColor(int status, int address)
        {
            var matching = Sent.Where(sent => sent.Status == status && sent.Address == address).ToList();
            return matching.Count == 0 ? null : matching[^1].Color;
        }
    }

    private sealed class FakeHost : IControlSurfaceHost
    {
        public FakeHost()
        {
            var project = ProjectSerializer.CreateStarterProject("launchpad");
            project.Tracks = project.Tracks.Take(2).ToList();
            project.Scenes = new List<string> { "Verse", "Drop", "Empty" };
            foreach (var track in project.Tracks)
            {
                track.Clips = new List<SlawClip?> { SlawClip.Empty("Verse", 16), SlawClip.Empty("Drop", 16), null };
            }
            Document = new StudioDocument(project, filePath: null);
        }

        public StudioDocument Document { get; }

        public TransportSnapshot Transport { get; set; } = TransportSnapshot.Idle;

        public bool RecordEngaged { get; set; }

        public bool ArrangementViewActive => false;

        public List<string> Calls { get; } = new();

        public void RunCommand(StudioCommand studioCommand) => Calls.Add($"command {studioCommand}");

        public void LaunchClip(int trackIndex, int sceneIndex) => Calls.Add($"clip {trackIndex} {sceneIndex}");

        public void LaunchScene(int sceneIndex) => Calls.Add($"scene {sceneIndex}");

        public void StopTrack(int trackIndex) => Calls.Add($"stop {trackIndex}");

        public void BackToArrangement() => Calls.Add("back");

        public void PlayNote(int midiNote, int velocity) => Calls.Add($"on {midiNote} {velocity}");

        public void ReleaseNote(int midiNote) => Calls.Add($"off {midiNote}");
    }

    private static (LaunchpadSurface Surface, RecordingPort Port, FakeHost Host) StartSurface()
    {
        var recordingPort = new RecordingPort();
        var fakeHost = new FakeHost();
        var surface = new LaunchpadSurface(recordingPort, fakeHost);
        surface.Start();
        return (surface, recordingPort, fakeHost);
    }

    private static MidiMessage Press(int padAddress) => new(MidiMessageKind.NoteOn, 0, padAddress, 127, "Launchpad MK2");

    private static MidiMessage Release(int padAddress) => new(MidiMessageKind.NoteOff, 0, padAddress, 0, "Launchpad MK2");

    private static MidiMessage Top(int controllerNumber) => new(MidiMessageKind.ControlChange, 0, controllerNumber, 127, "Launchpad MK2");

    [Fact]
    public void Starting_selects_session_layout_with_the_documented_bytes()
    {
        var (_, port, _) = StartSurface();
        Assert.Equal(new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x18, 0x22, 0x00, 0xF7 }, port.Sysex.Single());
    }

    [Theory]
    [InlineData("Launchpad MK2", true)]
    [InlineData("2- Launchpad MK2", true)]
    [InlineData("launchpad mk2", true)]
    [InlineData("Launchpad Mini MK3", false)]
    [InlineData("Launchpad X", false)]
    [InlineData("Microsoft GS Wavetable Synth", false)]
    public void Only_the_MK2_is_claimed(string portName, bool expected) =>
        Assert.Equal(expected, LaunchpadSurface.IsLaunchpadPort(portName));

    [Fact]
    public void The_top_left_pad_is_track_zero_scene_zero_and_lights_by_state()
    {
        var (surface, port, host) = StartSurface();
        Assert.Equal(Dim, port.LastColor(0x90, 81));
        Assert.Null(port.LastColor(0x90, 61));

        var firstTrack = host.Document.Project.Tracks[0];
        host.Transport = TransportSnapshot.Idle with
        {
            Mode = TransportMode.Session,
            Tracks = new Dictionary<string, TrackTransportState> { [firstTrack.Id] = new(PlayingScene: 0, ClipStep: 0, QueuedScene: 1) },
        };
        surface.Refresh();

        Assert.Equal(Green, port.LastColor(0x90, 81));
        Assert.Equal(Off, port.LastColor(0x90, 71));
        Assert.Equal(Yellow, port.LastColor(0x91, 71));
        Assert.Equal(Green, port.LastColor(0x90, 89));
        Assert.Equal(Yellow, port.LastColor(0x91, 79));
    }

    [Fact]
    public void An_unchanged_refresh_sends_nothing()
    {
        var (surface, port, _) = StartSurface();
        port.Sent.Clear();
        surface.Refresh();
        Assert.Empty(port.Sent);
    }

    [Fact]
    public void Pads_launch_stop_and_ignore_what_does_not_exist()
    {
        var (surface, _, host) = StartSurface();
        surface.HandleMessage(Press(81));
        surface.HandleMessage(Press(72));
        surface.HandleMessage(Press(61));
        surface.HandleMessage(Press(88));
        surface.HandleMessage(Press(11));
        surface.HandleMessage(Press(89));
        surface.HandleMessage(Press(19));
        Assert.Equal(new[] { "clip 0 0", "clip 1 1", "stop 0", "scene 0" }, host.Calls);
    }

    [Fact]
    public void Top_buttons_run_the_transport()
    {
        var (surface, port, host) = StartSurface();
        surface.HandleMessage(Top(110));
        surface.HandleMessage(Top(111));
        Assert.Equal(new[] { "command ToggleRecord", "command TogglePlayback" }, host.Calls);

        host.RecordEngaged = true;
        surface.Refresh();
        Assert.Equal(Red, port.LastColor(0xB0, 110));

        host.Calls.Clear();
        surface.HandleMessage(new MidiMessage(MidiMessageKind.ControlChange, 0, 111, 0, "Launchpad MK2"));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Arrows_page_the_grid_and_stop_at_the_ends()
    {
        var (surface, _, host) = StartSurface();
        surface.HandleMessage(Top(107));
        surface.HandleMessage(Top(107));
        surface.HandleMessage(Top(107));
        surface.HandleMessage(Top(105));
        surface.HandleMessage(Press(81));
        Assert.Equal("clip 1 1", host.Calls.Single());

        surface.HandleMessage(Top(106));
        surface.HandleMessage(Top(106));
        surface.HandleMessage(Top(104));
        surface.HandleMessage(Top(104));
        surface.HandleMessage(Press(81));
        Assert.Equal("clip 0 0", host.Calls[^1]);
    }

    [Fact]
    public void Notes_mode_plays_pads_from_the_bottom_left_and_releases_what_it_pressed()
    {
        var (surface, port, host) = StartSurface();
        surface.HandleMessage(Top(109));
        Assert.Equal(Blue, port.LastColor(0xB0, 109));
        Assert.Equal(Blue, port.LastColor(0x90, 11));

        surface.HandleMessage(Press(11));
        Assert.Equal(White, port.LastColor(0x90, 11));
        surface.HandleMessage(Press(21));
        surface.HandleMessage(Release(11));
        surface.HandleMessage(Release(21));
        Assert.Equal(new[] { "on 36 127", "on 44 127", "off 36", "off 44" }, host.Calls);
        Assert.Equal(Blue, port.LastColor(0x90, 11));
    }

    [Fact]
    public void A_held_note_ends_on_the_note_it_started_on_even_after_an_octave_change()
    {
        var (surface, _, host) = StartSurface();
        surface.HandleMessage(Top(109));
        surface.HandleMessage(Press(11));
        surface.HandleMessage(Top(104));
        surface.HandleMessage(Release(11));
        surface.HandleMessage(Press(11));
        Assert.Equal(new[] { "on 36 127", "off 36", "on 48 127" }, host.Calls);
    }

    [Fact]
    public void Leaving_notes_mode_or_closing_releases_held_notes_and_darkens_the_pads()
    {
        var (surface, port, host) = StartSurface();
        surface.HandleMessage(Top(109));
        surface.HandleMessage(Press(11));
        surface.HandleMessage(Top(108));
        Assert.Equal("off 36", host.Calls[^1]);

        surface.HandleMessage(Top(109));
        surface.HandleMessage(Press(12));
        surface.Dispose();
        Assert.Equal("off 37", host.Calls[^1]);
        Assert.Equal(Off, port.LastColor(0x90, 11));
        Assert.Equal(Off, port.LastColor(0xB0, 111));
        Assert.True(port.Disposed);
    }

    [Fact]
    public void The_side_buttons_in_notes_mode_change_scale_and_run_commands()
    {
        var (surface, port, host) = StartSurface();
        surface.HandleMessage(Top(109));
        surface.HandleMessage(Press(79));
        surface.HandleMessage(Press(69));
        surface.HandleMessage(Press(59));
        Assert.Equal(new[] { "command Undo", "command SelectPreviousScene", "command SelectNextScene" }, host.Calls);

        host.Calls.Clear();
        surface.HandleMessage(Press(89));
        Assert.Equal(Blue, port.LastColor(0x90, 89));
        surface.HandleMessage(Press(12));
        Assert.Equal("on 38 127", host.Calls.Single());
    }

    [Theory]
    [InlineData(0, 60, "chromatic", 60)]
    [InlineData(1, 60, "chromatic", 61)]
    [InlineData(1, 60, "major", 62)]
    [InlineData(2, 60, "major", 64)]
    [InlineData(2, 60, "minor", 63)]
    [InlineData(7, 60, "major", 72)]
    [InlineData(8, 60, "minor", 74)]
    public void Pads_map_to_notes(int padIndex, int baseNote, string scaleName, int expectedNote) =>
        Assert.Equal(expectedNote, NotePadLayout.NoteAt(padIndex, baseNote, scaleName));

    [Fact]
    public void Pads_above_the_top_of_midi_are_silent() =>
        Assert.Null(NotePadLayout.NoteAt(63, 120, "chromatic"));

    [Fact]
    public void Opening_something_with_no_owning_app_reports_it_instead_of_throwing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"slaw-no-such-file-{Guid.NewGuid():N}.streamDeckPlugin");
        var opened = ControllerDirectory.TryOpenInOwningApp(missingPath, out var failureReason);
        // Elsewhere the desktop's opener takes the path and complains in its own time.
        if (OperatingSystem.IsWindows())
        {
            Assert.False(opened);
            Assert.False(string.IsNullOrEmpty(failureReason));
        }
    }

    [Fact]
    public void With_no_controllers_every_line_says_so_without_failing()
    {
        var statusLines = ControllerDirectory.DescribeStatus(new ControllerSnapshot(false, 47116, 0, null, null, Array.Empty<string>()), pluginsFolder: null);
        Assert.All(statusLines, line => Assert.False(line.Ready));
        Assert.Contains(statusLines, line => line.Name == "Launchpad MK2" && line.Detail == "not connected");
    }

    [Fact]
    public void Controllers_stay_out_of_the_way_by_default()
    {
        var defaults = new StudioPreferences();
        Assert.False(defaults.ControlSurfaceEnabled);
    }

    [Fact]
    public void Plugin_packages_are_found_up_the_folders_of_a_checkout()
    {
        var checkout = Directory.CreateTempSubdirectory("slaw-controllers").FullName;
        try
        {
            var plugin = ControllerDirectory.DeckPlugins[0];
            var distFolder = Directory.CreateDirectory(Path.Combine(checkout, "integrations", "dist")).FullName;
            var appFolder = Directory.CreateDirectory(Path.Combine(checkout, "out")).FullName;
            Assert.Null(ControllerDirectory.FindPluginPackage(plugin, appFolder));

            var packagePath = Path.Combine(distFolder, plugin.PackageFileName);
            File.WriteAllBytes(packagePath, new byte[] { 1 });
            Assert.Equal(packagePath, ControllerDirectory.FindPluginPackage(plugin, appFolder));
        }
        finally
        {
            Directory.Delete(checkout, recursive: true);
        }
    }

    [Fact]
    public void Status_lines_say_what_is_connected_and_why_not()
    {
        var pluginsFolder = Directory.CreateTempSubdirectory("slaw-plugins").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(pluginsFolder, $"{ControllerDirectory.DeckPlugins[0].Identifier}.sdPlugin"));
            var statusLines = ControllerDirectory.DescribeStatus(new ControllerSnapshot(true, 47116, 2, null, "busy", Array.Empty<string>()), pluginsFolder);

            Assert.Contains(statusLines, line => line.Name == "Deck control" && line.Ready && line.Detail.Contains("2 plugin connection(s)"));
            Assert.Contains(statusLines, line => line.Name == "slaw Keys" && line.Ready);
            Assert.Contains(statusLines, line => line.Name == "slaw Dials" && !line.Ready && line.Detail == "not installed");
            Assert.Contains(statusLines, line => line.Name == "Launchpad MK2" && !line.Ready && line.Detail == "busy");
        }
        finally
        {
            Directory.Delete(pluginsFolder, recursive: true);
        }
    }
}
