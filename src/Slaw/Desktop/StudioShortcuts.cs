using Avalonia.Input;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>Everything the studio can be told to do from the keyboard.</summary>
internal enum StudioCommand
{
    TogglePlayback,
    PlayFromSelection,
    SwitchView,
    ToggleBrowser,
    ToggleDetailPanel,
    ToggleAiPanel,
    ToggleSessionMixer,
    ToggleSecondWindow,
    ToggleDetailInSecondWindow,
    ToggleFullScreen,
    ShowCommandPalette,
    ImportSounds,
    LaunchSelection,
    LaunchSelectedScene,
    ToggleRecord,
    ToggleComputerKeyboard,
    SelectPreviousScene,
    SelectNextScene,
    SelectPreviousTrack,
    SelectNextTrack,
    CreateClip,
    DuplicateClip,
    DuplicateScene,
    DeleteClip,
    CopyClip,
    CutClip,
    PasteClip,
    AddTrack,
    AddScene,
    Undo,
    Redo,
    New,
    Open,
    Save,
    SaveAs,
    Bounce,
    BounceAs,
    Settings,
    ShowShortcuts,
    NewChat,
    Dismiss,
}

/// <summary>Where a shortcut still fires when a control has keyboard focus.</summary>
internal enum ShortcutReach
{
    /// <summary>Only when no control owns the keyboard (letters, arrows, Delete, Enter, clipboard).</summary>
    Studio,

    /// <summary>Also over controls with their own keys (sample editor, lists), but not while typing.</summary>
    Global,

    /// <summary>Even while typing in a text field: the file and history commands.</summary>
    Everywhere,
}

internal sealed record StudioShortcut(
    StudioCommand Command,
    Key Key,
    KeyModifiers Modifiers,
    string Description,
    ShortcutReach Reach = ShortcutReach.Studio,
    bool Repeats = false);

/// <summary>
/// The studio's key map, modelled on the one 90% of producers already have in
/// their fingers. One table drives dispatch and the cheat sheet, so the two can
/// never disagree, unlike most product documentation.
/// </summary>
internal static class StudioShortcutMap
{
    private const KeyModifiers Command = KeyModifiers.Control;
    private const KeyModifiers CommandShift = KeyModifiers.Control | KeyModifiers.Shift;
    private const KeyModifiers CommandAlt = KeyModifiers.Control | KeyModifiers.Alt;

    public static readonly IReadOnlyList<StudioShortcut> All = new StudioShortcut[]
    {
        new(StudioCommand.TogglePlayback, Key.Space, KeyModifiers.None, "Play / stop", ShortcutReach.Global),
        new(StudioCommand.PlayFromSelection, Key.Space, KeyModifiers.Shift, "Play from the selected scene", ShortcutReach.Global),
        new(StudioCommand.SwitchView, Key.Tab, KeyModifiers.None, "Switch Session / Arrangement view (with two windows: swap them)", ShortcutReach.Global),
        // Global, not Everywhere: Ctrl+Alt is AltGr on many keyboards, and a text field may want that µ.
        new(StudioCommand.ToggleBrowser, Key.B, CommandAlt, "Show / hide the browser", ShortcutReach.Global),
        new(StudioCommand.ToggleDetailPanel, Key.L, CommandAlt, "Show / hide the clip and devices panel", ShortcutReach.Global),
        new(StudioCommand.ToggleSessionMixer, Key.M, CommandAlt, "Show / hide the Session view's mixer strips", ShortcutReach.Global),
        new(StudioCommand.ToggleAiPanel, Key.I, CommandAlt, "Show / hide the AI co-pilot panel", ShortcutReach.Global),
        new(StudioCommand.ToggleSecondWindow, Key.W, CommandShift, "Open / close the second window", ShortcutReach.Global),
        new(StudioCommand.ToggleFullScreen, Key.F11, KeyModifiers.None, "Full screen for the focused window", ShortcutReach.Global),
        new(StudioCommand.ShowCommandPalette, Key.P, CommandShift, "Command palette: search everything the studio can do", ShortcutReach.Everywhere),
        new(StudioCommand.ImportSounds, Key.I, CommandShift, "Import sounds: a folder or files as a drum kit, tracks or library", ShortcutReach.Everywhere),
        new(StudioCommand.LaunchSelection, Key.Enter, KeyModifiers.None, "Launch the selected clip (Arrangement: play from it)"),
        new(StudioCommand.LaunchSelectedScene, Key.Enter, KeyModifiers.Shift, "Launch the selected scene"),
        new(StudioCommand.Dismiss, Key.Escape, KeyModifiers.None, "Stop notes, leave text fields, close overlays", ShortcutReach.Everywhere),
        new(StudioCommand.ToggleRecord, Key.F9, KeyModifiers.None, "Arm / disarm record (MIDI, and audio on an armed track)", ShortcutReach.Global),
        new(StudioCommand.ToggleComputerKeyboard, Key.M, KeyModifiers.None, "Computer MIDI keyboard on / off"),
        new(StudioCommand.SelectPreviousScene, Key.Up, KeyModifiers.None, "Move the selection up", Repeats: true),
        new(StudioCommand.SelectNextScene, Key.Down, KeyModifiers.None, "Move the selection down", Repeats: true),
        new(StudioCommand.SelectPreviousTrack, Key.Left, KeyModifiers.None, "Move the selection left", Repeats: true),
        new(StudioCommand.SelectNextTrack, Key.Right, KeyModifiers.None, "Move the selection right", Repeats: true),
        new(StudioCommand.CreateClip, Key.M, CommandShift, "Create an empty clip in the selected slot"),
        new(StudioCommand.DuplicateClip, Key.D, Command, "Duplicate the selected clip (Arrangement: right after itself; Session: into the next scene)"),
        new(StudioCommand.DuplicateScene, Key.D, CommandShift, "Duplicate the selected scene"),
        new(StudioCommand.DeleteClip, Key.Delete, KeyModifiers.None, "Delete the selected clip (Arrangement: the selected clip, or the track whose header you clicked last)"),
        new(StudioCommand.DeleteClip, Key.Back, KeyModifiers.None, "Delete the selected clip (Arrangement: the selected clip, or the track whose header you clicked last)"),
        new(StudioCommand.CopyClip, Key.C, Command, "Copy the selected clip"),
        new(StudioCommand.CutClip, Key.X, Command, "Cut the selected clip"),
        new(StudioCommand.PasteClip, Key.V, Command, "Paste into the selected slot"),
        new(StudioCommand.AddTrack, Key.T, Command, "Add a track"),
        new(StudioCommand.AddTrack, Key.T, CommandShift, "Add a track"),
        new(StudioCommand.AddScene, Key.I, Command, "Add a scene"),
        new(StudioCommand.Undo, Key.Z, Command, "Undo", ShortcutReach.Everywhere, Repeats: true),
        new(StudioCommand.Redo, Key.Z, CommandShift, "Redo", ShortcutReach.Everywhere, Repeats: true),
        new(StudioCommand.Redo, Key.Y, Command, "Redo", ShortcutReach.Everywhere, Repeats: true),
        new(StudioCommand.New, Key.N, Command, "New project", ShortcutReach.Everywhere),
        new(StudioCommand.NewChat, Key.N, CommandShift, "New AI chat (the co-pilot forgets this one)", ShortcutReach.Everywhere),
        new(StudioCommand.Open, Key.O, Command, "Open a project", ShortcutReach.Everywhere),
        new(StudioCommand.Save, Key.S, Command, "Save", ShortcutReach.Everywhere),
        new(StudioCommand.SaveAs, Key.S, CommandShift, "Save As…", ShortcutReach.Everywhere),
        new(StudioCommand.Bounce, Key.B, Command, "Bounce the arrangement (shows where the WAV goes first)", ShortcutReach.Everywhere),
        new(StudioCommand.BounceAs, Key.B, CommandShift, "Bounce with your last options (scene, stems, format)", ShortcutReach.Everywhere),
        new(StudioCommand.Settings, Key.OemComma, Command, "Settings", ShortcutReach.Everywhere),
        new(StudioCommand.ShowShortcuts, Key.F1, KeyModifiers.None, "This cheat sheet", ShortcutReach.Everywhere),
        new(StudioCommand.ShowShortcuts, Key.OemQuestion, Command, "This cheat sheet", ShortcutReach.Everywhere),
    };

    /// <summary>Finds the shortcut for a key press. Cmd (Meta) counts as Ctrl, so the map works on a Mac too.</summary>
    public static StudioShortcut? Resolve(Key key, KeyModifiers modifiers)
    {
        var normalizedModifiers = modifiers.HasFlag(KeyModifiers.Meta)
            ? (modifiers & ~KeyModifiers.Meta) | KeyModifiers.Control
            : modifiers;
        return All.FirstOrDefault(shortcut => shortcut.Key == key && shortcut.Modifiers == normalizedModifiers);
    }

    /// <summary>Whether a shortcut may fire given who currently owns the keyboard.</summary>
    public static bool Reaches(StudioShortcut shortcut, bool typingInTextField, bool controlOwnsKeys) =>
        shortcut.Reach switch
        {
            ShortcutReach.Everywhere => true,
            ShortcutReach.Global => !typingInTextField,
            _ => !typingInTextField && !controlOwnsKeys,
        };

    public static string DescribeGesture(StudioShortcut shortcut)
    {
        var keyName = shortcut.Key switch
        {
            Key.OemComma => ",",
            Key.OemQuestion => "/",
            Key.Back => "Backspace",
            Key.Delete => "Del",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Left => "←",
            Key.Right => "→",
            _ => shortcut.Key.ToString(),
        };
        var modifierPrefix = (shortcut.Modifiers.HasFlag(KeyModifiers.Control) ? "Ctrl+" : string.Empty) +
                             (shortcut.Modifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : string.Empty) +
                             (shortcut.Modifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : string.Empty);
        return modifierPrefix + keyName;
    }
}

/// <summary>How each command reads in the palette. A test keeps this complete, so new commands cannot go missing.</summary>
internal static class StudioCommandTitles
{
    public static readonly IReadOnlyList<(StudioCommand Command, string Title)> All = new[]
    {
        (StudioCommand.TogglePlayback, "Transport: Play / Stop"),
        (StudioCommand.PlayFromSelection, "Transport: Play from Selected Scene"),
        (StudioCommand.ToggleRecord, "Transport: Arm / Disarm Record"),
        (StudioCommand.LaunchSelection, "Launch: Selected Clip"),
        (StudioCommand.LaunchSelectedScene, "Launch: Selected Scene"),
        (StudioCommand.SwitchView, "View: Toggle Session / Arrangement"),
        (StudioCommand.ToggleBrowser, "View: Show / Hide Browser"),
        (StudioCommand.ToggleDetailPanel, "View: Show / Hide Clip & Devices Panel"),
        (StudioCommand.ToggleAiPanel, "View: Show / Hide AI Panel"),
        (StudioCommand.ToggleSessionMixer, "View: Show / Hide Session Mixer"),
        (StudioCommand.ToggleSecondWindow, "View: Open / Close Second Window"),
        (StudioCommand.ToggleDetailInSecondWindow, "View: Clip & Devices in Second Window"),
        (StudioCommand.ToggleFullScreen, "View: Full Screen"),
        (StudioCommand.ToggleComputerKeyboard, "Keys: Computer MIDI Keyboard On / Off"),
        (StudioCommand.SelectPreviousScene, "Selection: Move Up"),
        (StudioCommand.SelectNextScene, "Selection: Move Down"),
        (StudioCommand.SelectPreviousTrack, "Selection: Move Left"),
        (StudioCommand.SelectNextTrack, "Selection: Move Right"),
        (StudioCommand.CreateClip, "Create: Empty Clip"),
        (StudioCommand.AddTrack, "Create: Track"),
        (StudioCommand.AddScene, "Create: Scene"),
        (StudioCommand.NewChat, "Create: New AI Chat"),
        (StudioCommand.DuplicateClip, "Edit: Duplicate Clip"),
        (StudioCommand.DuplicateScene, "Edit: Duplicate Scene"),
        (StudioCommand.DeleteClip, "Edit: Delete"),
        (StudioCommand.CopyClip, "Edit: Copy Clip"),
        (StudioCommand.CutClip, "Edit: Cut Clip"),
        (StudioCommand.PasteClip, "Edit: Paste Clip"),
        (StudioCommand.Undo, "Edit: Undo"),
        (StudioCommand.Redo, "Edit: Redo"),
        (StudioCommand.New, "File: New Project"),
        (StudioCommand.Open, "File: Open…"),
        (StudioCommand.Save, "File: Save"),
        (StudioCommand.SaveAs, "File: Save As…"),
        (StudioCommand.ImportSounds, "File: Import Sounds…"),
        (StudioCommand.Bounce, "File: Bounce Arrangement…"),
        (StudioCommand.BounceAs, "File: Bounce Options…"),
        (StudioCommand.Settings, "File: Settings…"),
        (StudioCommand.ShowShortcuts, "Help: Keyboard Shortcuts"),
    };
}

/// <summary>What Space means right now: is anything actually audible?</summary>
internal static class PlaybackToggle
{
    /// <summary>
    /// The arrangement, or at least one clip playing or queued. A session clock
    /// ticking over silent tracks does not count: Space should start music there,
    /// not stop the silence.
    /// </summary>
    public static bool IsAudiblyPlaying(Synthesis.TransportSnapshot transportSnapshot) =>
        transportSnapshot.Mode == Synthesis.TransportMode.Timeline ||
        (transportSnapshot.Mode == Synthesis.TransportMode.Session &&
         transportSnapshot.Tracks.Values.Any(trackState => trackState.PlayingScene is not null || trackState.QueuedScene >= 0));
}

/// <summary>
/// Ableton's computer MIDI keyboard: the home row is the white keys, the row
/// above it the black ones, Z/X shift octaves and C/V change velocity. Keys are
/// matched by position, so a QWERTZ keyboard plays the same shape as a QWERTY one.
/// Pure state, no window: the studio feeds it key events and plays what it returns.
/// </summary>
internal sealed class ComputerKeyboardPiano
{
    public const int LowestBaseNote = 0;
    public const int HighestBaseNote = 108;
    public const int DefaultBaseNote = 60;
    public const int DefaultVelocity = 100;

    private static readonly int[] VelocityLevels = { 1, 20, 40, 60, 80, 100, 120, 127 };

    private static readonly IReadOnlyDictionary<PhysicalKey, int> SemitoneOffsets = new Dictionary<PhysicalKey, int>
    {
        [PhysicalKey.A] = 0,
        [PhysicalKey.W] = 1,
        [PhysicalKey.S] = 2,
        [PhysicalKey.E] = 3,
        [PhysicalKey.D] = 4,
        [PhysicalKey.F] = 5,
        [PhysicalKey.T] = 6,
        [PhysicalKey.G] = 7,
        [PhysicalKey.Y] = 8,
        [PhysicalKey.H] = 9,
        [PhysicalKey.U] = 10,
        [PhysicalKey.J] = 11,
        [PhysicalKey.K] = 12,
        [PhysicalKey.O] = 13,
        [PhysicalKey.L] = 14,
        [PhysicalKey.P] = 15,
        [PhysicalKey.Semicolon] = 16,
    };

    /// <summary>The note each held key started, so releasing it after an octave change still stops the right note.</summary>
    private readonly Dictionary<PhysicalKey, int> soundingNotes = new();

    public int BaseNote { get; private set; } = DefaultBaseNote;

    public int Velocity { get; private set; } = DefaultVelocity;

    public string OctaveLabel => NotationResolver.FormatMidiNote(BaseNote);

    public static bool IsNoteKey(PhysicalKey physicalKey) => SemitoneOffsets.ContainsKey(physicalKey);

    /// <summary>Every key the piano claims while it is on: the notes plus Z, X, C and V.</summary>
    public static bool ClaimsKey(PhysicalKey physicalKey) =>
        IsNoteKey(physicalKey) || physicalKey is PhysicalKey.Z or PhysicalKey.X or PhysicalKey.C or PhysicalKey.V;

    /// <summary>A key went down. Returns the note to start, or null for auto-repeat and non-note keys.</summary>
    public int? Press(PhysicalKey physicalKey)
    {
        if (!SemitoneOffsets.TryGetValue(physicalKey, out var semitoneOffset) || soundingNotes.ContainsKey(physicalKey))
        {
            return null;
        }
        var midiNote = Math.Clamp(BaseNote + semitoneOffset, 0, 127);
        soundingNotes[physicalKey] = midiNote;
        return midiNote;
    }

    /// <summary>A key came up. Returns the note it had started, if any.</summary>
    public int? Release(PhysicalKey physicalKey) =>
        soundingNotes.Remove(physicalKey, out var midiNote) ? midiNote : null;

    /// <summary>Lets go of everything (focus lost, mode switched off). Returns the notes to stop.</summary>
    public IReadOnlyList<int> ReleaseAll()
    {
        var releasedNotes = soundingNotes.Values.Distinct().ToList();
        soundingNotes.Clear();
        return releasedNotes;
    }

    public void ShiftOctave(int octaveDelta) =>
        BaseNote = Math.Clamp(BaseNote + 12 * octaveDelta, LowestBaseNote, HighestBaseNote);

    public void ShiftVelocity(int levelDelta)
    {
        var currentLevel = Array.FindLastIndex(VelocityLevels, level => level <= Velocity);
        Velocity = VelocityLevels[Math.Clamp(Math.Max(0, currentLevel) + levelDelta, 0, VelocityLevels.Length - 1)];
    }
}
