using Avalonia.Input;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The key map and the computer MIDI keyboard: muscle memory, verified.</summary>
public class KeyboardShortcutTests
{
    [Theory]
    [InlineData(Key.Space, KeyModifiers.None, nameof(StudioCommand.TogglePlayback))]
    [InlineData(Key.Space, KeyModifiers.Shift, nameof(StudioCommand.PlayFromSelection))]
    [InlineData(Key.Tab, KeyModifiers.None, nameof(StudioCommand.SwitchView))]
    [InlineData(Key.Z, KeyModifiers.Control, nameof(StudioCommand.Undo))]
    [InlineData(Key.Z, KeyModifiers.Control | KeyModifiers.Shift, nameof(StudioCommand.Redo))]
    [InlineData(Key.Z, KeyModifiers.Meta, nameof(StudioCommand.Undo))]
    [InlineData(Key.D, KeyModifiers.Control, nameof(StudioCommand.DuplicateClip))]
    [InlineData(Key.D, KeyModifiers.Control | KeyModifiers.Shift, nameof(StudioCommand.DuplicateScene))]
    [InlineData(Key.M, KeyModifiers.None, nameof(StudioCommand.ToggleComputerKeyboard))]
    [InlineData(Key.M, KeyModifiers.Control | KeyModifiers.Shift, nameof(StudioCommand.CreateClip))]
    [InlineData(Key.Enter, KeyModifiers.Shift, nameof(StudioCommand.LaunchSelectedScene))]
    public void ResolvesAbletonShapedShortcuts(Key key, KeyModifiers modifiers, string expectedCommand) =>
        Assert.Equal(expectedCommand, StudioShortcutMap.Resolve(key, modifiers)?.Command.ToString());

    [Fact]
    public void UnboundKeysResolveToNothing()
    {
        Assert.Null(StudioShortcutMap.Resolve(Key.Q, KeyModifiers.None));
        Assert.Null(StudioShortcutMap.Resolve(Key.Space, KeyModifiers.Alt));
    }

    [Fact]
    public void EveryGestureIsBoundOnce()
    {
        var duplicateGestures = StudioShortcutMap.All
            .GroupBy(shortcut => (shortcut.Key, shortcut.Modifiers))
            .Where(gestureGroup => gestureGroup.Count() > 1)
            .Select(gestureGroup => StudioShortcutMap.DescribeGesture(gestureGroup.First()));
        Assert.Empty(duplicateGestures);
    }

    [Fact]
    public void TypingKeepsLettersAndSpaceButNotSave()
    {
        var space = StudioShortcutMap.Resolve(Key.Space, KeyModifiers.None)!;
        var save = StudioShortcutMap.Resolve(Key.S, KeyModifiers.Control)!;
        var delete = StudioShortcutMap.Resolve(Key.Delete, KeyModifiers.None)!;

        Assert.False(StudioShortcutMap.Reaches(space, typingInTextField: true, controlOwnsKeys: true));
        Assert.True(StudioShortcutMap.Reaches(save, typingInTextField: true, controlOwnsKeys: true));
        Assert.True(StudioShortcutMap.Reaches(space, typingInTextField: false, controlOwnsKeys: true));
        Assert.False(StudioShortcutMap.Reaches(delete, typingInTextField: false, controlOwnsKeys: true));
        Assert.True(StudioShortcutMap.Reaches(delete, typingInTextField: false, controlOwnsKeys: false));
    }

    [Fact]
    public void ComputerKeyboardPlaysAbletonLayoutByPosition()
    {
        var piano = new ComputerKeyboardPiano();
        Assert.Equal(60, piano.Press(PhysicalKey.A));
        Assert.Equal(61, piano.Press(PhysicalKey.W));
        Assert.Equal(72, piano.Press(PhysicalKey.K));
        Assert.Equal(76, piano.Press(PhysicalKey.Semicolon));
        Assert.Null(piano.Press(PhysicalKey.Q));
        Assert.False(ComputerKeyboardPiano.ClaimsKey(PhysicalKey.M));
        Assert.True(ComputerKeyboardPiano.ClaimsKey(PhysicalKey.Z));
    }

    [Fact]
    public void ComputerKeyboardIgnoresAutoRepeatAndReleasesWhatItStruck()
    {
        var piano = new ComputerKeyboardPiano();
        Assert.Equal(60, piano.Press(PhysicalKey.A));
        Assert.Null(piano.Press(PhysicalKey.A));
        piano.ShiftOctave(1);
        Assert.Equal(60, piano.Release(PhysicalKey.A));
        Assert.Null(piano.Release(PhysicalKey.A));
        Assert.Equal(72, piano.Press(PhysicalKey.A));
        Assert.Equal(new[] { 72 }, piano.ReleaseAll());
        Assert.Empty(piano.ReleaseAll());
    }

    [Fact]
    public void ComputerKeyboardClampsOctaveAndVelocity()
    {
        var piano = new ComputerKeyboardPiano();
        piano.ShiftOctave(-20);
        Assert.Equal(ComputerKeyboardPiano.LowestBaseNote, piano.BaseNote);
        piano.ShiftOctave(20);
        Assert.Equal(ComputerKeyboardPiano.HighestBaseNote, piano.BaseNote);
        Assert.Equal(124, piano.Press(PhysicalKey.Semicolon));

        Assert.Equal(100, piano.Velocity);
        piano.ShiftVelocity(1);
        Assert.Equal(120, piano.Velocity);
        piano.ShiftVelocity(5);
        Assert.Equal(127, piano.Velocity);
        piano.ShiftVelocity(-20);
        Assert.Equal(1, piano.Velocity);
    }

    [Fact]
    public void SilentSessionClockDoesNotCountAsPlaying()
    {
        var silentSession = TransportSnapshot.Idle with
        {
            Mode = TransportMode.Session,
            Tracks = new Dictionary<string, TrackTransportState> { ["a"] = new(null, null, null) },
        };
        var queuedSession = silentSession with
        {
            Tracks = new Dictionary<string, TrackTransportState> { ["a"] = new(null, null, 2) },
        };
        var queuedStopOnly = silentSession with
        {
            Tracks = new Dictionary<string, TrackTransportState> { ["a"] = new(null, null, -1) },
        };

        Assert.False(PlaybackToggle.IsAudiblyPlaying(TransportSnapshot.Idle));
        Assert.False(PlaybackToggle.IsAudiblyPlaying(silentSession));
        Assert.False(PlaybackToggle.IsAudiblyPlaying(queuedStopOnly));
        Assert.True(PlaybackToggle.IsAudiblyPlaying(queuedSession));
        Assert.True(PlaybackToggle.IsAudiblyPlaying(TransportSnapshot.Idle with { Mode = TransportMode.Arrangement }));
    }
}
