using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The keyboard in the studio: the shortcut map, the computer MIDI keyboard,
/// and the focus rules that keep Space meaning "play" instead of "type a space
/// into the track name you clicked four minutes ago".
/// </summary>
internal sealed partial class StudioWindow
{
    private const string ComputerKeyboardSourceName = "computer keyboard";

    private readonly ComputerKeyboardPiano computerKeyboardPiano = new();

    /// <summary>
    /// Keys currently down. Avalonia asks X11 for detectable auto-repeat, so a held
    /// key arrives as a stream of KeyDowns with a single KeyUp at the end.
    /// </summary>
    private readonly HashSet<Key> heldKeys = new();

    private readonly TextBlock computerKeyboardStatusText = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTheme.TextMuted, IsVisible = false };
    private ToggleButton? computerKeyboardToggle;
    private string? copiedClipPayload;
    private Window? shortcutCheatSheet;

    private bool ComputerKeyboardActive => computerKeyboardToggle?.IsChecked == true;

    private void WireKeyboard()
    {
        AddHandler(KeyDownEvent, HandleWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, HandleWindowKeyUp, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, HandleWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => ReleaseHeldKeys();

        // A clicked button keeps focus and re-fires on Enter; in a DAW the keyboard
        // belongs to the transport, so buttons are mouse-only.
        Styles.Add(new Style(selector => selector.Is<Button>())
        {
            Setters = { new Setter(InputElement.FocusableProperty, false) },
        });
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private Control ComposeComputerKeyboardControls()
    {
        computerKeyboardToggle = new ToggleButton
        {
            Content = "⌨ Keys",
            Padding = new Thickness(10, 4),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(computerKeyboardToggle,
            "Computer MIDI keyboard (M): play the selected track from your keyboard, recording included.\n" +
            "A S D F G H J K L ; are the white keys, W E T Y U O P the black ones.\n" +
            "Z / X: octave down / up · C / V: velocity down / up");
        computerKeyboardToggle.IsCheckedChanged += (_, _) =>
        {
            computerKeyboardToggle.Foreground = ComputerKeyboardActive ? StudioTheme.Accent : StudioTheme.Text;
            if (!ComputerKeyboardActive)
            {
                ReleaseComputerKeyboardNotes();
            }
            RefreshComputerKeyboardStatus();
            SetStatus(StatusSeverity.Informational, ComputerKeyboardActive
                ? "computer MIDI keyboard on: A–; play the selected track, Z/X octave, C/V velocity (M to switch off)"
                : "computer MIDI keyboard off: your letters are letters again");
        };
        return StudioUi.Row(6, computerKeyboardToggle, computerKeyboardStatusText);
    }

    private void RefreshComputerKeyboardStatus()
    {
        computerKeyboardStatusText.IsVisible = ComputerKeyboardActive;
        computerKeyboardStatusText.Text = $"{computerKeyboardPiano.OctaveLabel} · vel {computerKeyboardPiano.Velocity}";
    }

    // ── Routing ───────────────────────────────────────────────────────────────

    private void HandleWindowKeyDown(object? sender, KeyEventArgs keyEvent)
    {
        var focusedElement = FocusManager?.GetFocusedElement();
        var typingInTextField = focusedElement is TextBox;
        var controlOwnsKeys = typingInTextField ||
                              focusedElement is SelectingItemsControl or RangeBase or NumericUpDown or SelectableTextBlock or WaveformEditorControl;

        if (typingInTextField && keyEvent.Key == Key.Enter && focusedElement is TextBox { AcceptsReturn: false } singleLineInput && singleLineInput != aiInstructionInput)
        {
            // Enter commits a name or a tempo and hands the keyboard back to the transport.
            Dispatcher.UIThread.Post(() => FocusManager?.ClearFocus(), DispatcherPriority.Input);
            return;
        }

        var commandModifiersHeld = (keyEvent.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) != 0;
        if (ComputerKeyboardActive && !controlOwnsKeys && !commandModifiersHeld && ComputerKeyboardPiano.ClaimsKey(keyEvent.PhysicalKey))
        {
            if (heldKeys.Add(keyEvent.Key))
            {
                PressComputerKeyboardKey(keyEvent.PhysicalKey);
            }
            keyEvent.Handled = true;
            return;
        }

        if (StudioShortcutMap.Resolve(keyEvent.Key, keyEvent.KeyModifiers) is not { } shortcut ||
            !StudioShortcutMap.Reaches(shortcut, typingInTextField, controlOwnsKeys))
        {
            return;
        }
        keyEvent.Handled = true;
        var autoRepeat = !heldKeys.Add(keyEvent.Key);
        if (autoRepeat && !shortcut.Repeats)
        {
            return;
        }
        ExecuteStudioCommand(shortcut.Command);
    }

    private void HandleWindowKeyUp(object? sender, KeyEventArgs keyEvent)
    {
        heldKeys.Remove(keyEvent.Key);
        if (computerKeyboardPiano.Release(keyEvent.PhysicalKey) is { } releasedNote)
        {
            HandleMidiMessage(new MidiMessage(MidiMessageKind.NoteOff, 0, releasedNote, 0, ComputerKeyboardSourceName));
            keyEvent.Handled = true;
        }
    }

    /// <summary>
    /// Clicking anything that cannot take focus itself (the grid, a knob, a button)
    /// takes it away from whatever text field had it, which also commits that edit.
    /// </summary>
    private void HandleWindowPointerPressed(object? sender, PointerPressedEventArgs pointerEvent)
    {
        if (FocusManager?.GetFocusedElement() is null)
        {
            return;
        }
        var pressedElement = pointerEvent.Source as Visual;
        var landsOnFocusableControl = pressedElement?.GetSelfAndVisualAncestors()
            .TakeWhile(visual => visual is not TopLevel)
            .Any(visual => visual is InputElement { Focusable: true, IsEffectivelyEnabled: true }) == true;
        if (!landsOnFocusableControl)
        {
            FocusManager?.ClearFocus();
        }
    }

    /// <summary>Focus went elsewhere (another window, a dialog): nothing may stay stuck down.</summary>
    private void ReleaseHeldKeys()
    {
        heldKeys.Clear();
        ReleaseComputerKeyboardNotes();
    }

    // ── Computer MIDI keyboard ────────────────────────────────────────────────

    private void PressComputerKeyboardKey(PhysicalKey physicalKey)
    {
        switch (physicalKey)
        {
            case PhysicalKey.Z:
                computerKeyboardPiano.ShiftOctave(-1);
                break;
            case PhysicalKey.X:
                computerKeyboardPiano.ShiftOctave(1);
                break;
            case PhysicalKey.C:
                computerKeyboardPiano.ShiftVelocity(-1);
                break;
            case PhysicalKey.V:
                computerKeyboardPiano.ShiftVelocity(1);
                break;
            default:
                if (computerKeyboardPiano.Press(physicalKey) is { } struckNote)
                {
                    // The same path as a hardware keyboard, so recording and step input behave identically.
                    HandleMidiMessage(new MidiMessage(MidiMessageKind.NoteOn, 0, struckNote, computerKeyboardPiano.Velocity, ComputerKeyboardSourceName));
                }
                return;
        }
        RefreshComputerKeyboardStatus();
        SetStatus(StatusSeverity.Informational, $"computer keyboard: octave {computerKeyboardPiano.OctaveLabel}, velocity {computerKeyboardPiano.Velocity}");
    }

    private void ReleaseComputerKeyboardNotes()
    {
        foreach (var releasedNote in computerKeyboardPiano.ReleaseAll())
        {
            HandleMidiMessage(new MidiMessage(MidiMessageKind.NoteOff, 0, releasedNote, 0, ComputerKeyboardSourceName));
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private void ExecuteStudioCommand(StudioCommand studioCommand)
    {
        switch (studioCommand)
        {
            case StudioCommand.TogglePlayback:
                TogglePlayback();
                break;
            case StudioCommand.PlayFromSelection:
                PlayFromSelectedScene();
                break;
            case StudioCommand.SwitchView:
                SelectMainView(!arrangementViewActive);
                break;
            case StudioCommand.LaunchSelection:
                if (arrangementViewActive)
                {
                    PlayFromSelectedScene();
                }
                else
                {
                    LaunchClip(document.SelectedTrackIndex, document.SelectedSceneIndex);
                }
                break;
            case StudioCommand.LaunchSelectedScene:
                LaunchScene(document.SelectedSceneIndex);
                break;
            case StudioCommand.ToggleRecord:
                if (recordToggle is not null)
                {
                    recordToggle.IsChecked = recordToggle.IsChecked != true;
                }
                break;
            case StudioCommand.ToggleComputerKeyboard:
                if (computerKeyboardToggle is not null)
                {
                    computerKeyboardToggle.IsChecked = !ComputerKeyboardActive;
                }
                break;
            case StudioCommand.SelectPreviousScene:
                MoveSelection(-1, vertical: true);
                break;
            case StudioCommand.SelectNextScene:
                MoveSelection(1, vertical: true);
                break;
            case StudioCommand.SelectPreviousTrack:
                MoveSelection(-1, vertical: false);
                break;
            case StudioCommand.SelectNextTrack:
                MoveSelection(1, vertical: false);
                break;
            case StudioCommand.CreateClip:
                if (document.SelectedTrack is not null && document.SelectedClip is null && document.Project.Scenes.Count > 0)
                {
                    document.CreateClip(document.SelectedTrackIndex, document.SelectedSceneIndex);
                }
                break;
            case StudioCommand.DuplicateClip:
                if (document.SelectedClip is not null)
                {
                    document.DuplicateClipDown(document.SelectedTrackIndex, document.SelectedSceneIndex);
                }
                break;
            case StudioCommand.DuplicateScene:
                if (document.Project.Scenes.Count > 0)
                {
                    document.DuplicateScene(document.SelectedSceneIndex);
                }
                break;
            case StudioCommand.DeleteClip:
                if (document.SelectedClip is not null)
                {
                    document.DeleteClip(document.SelectedTrackIndex, document.SelectedSceneIndex);
                }
                break;
            case StudioCommand.CopyClip:
                CopySelectedClip();
                break;
            case StudioCommand.CutClip:
                if (CopySelectedClip())
                {
                    document.DeleteClip(document.SelectedTrackIndex, document.SelectedSceneIndex);
                }
                break;
            case StudioCommand.PasteClip:
                PasteClipIntoSelection();
                break;
            case StudioCommand.AddTrack:
                document.AddTrack("sine");
                break;
            case StudioCommand.AddScene:
                document.AddScene();
                break;
            case StudioCommand.Undo:
                PerformUndo();
                break;
            case StudioCommand.Redo:
                PerformRedo();
                break;
            case StudioCommand.New:
                _ = NewProjectAsync();
                break;
            case StudioCommand.NewChat:
                StartNewChat();
                break;
            case StudioCommand.Open:
                _ = OpenProjectAsync();
                break;
            case StudioCommand.Save:
                _ = SaveProjectAsync(saveAs: false);
                break;
            case StudioCommand.SaveAs:
                _ = SaveProjectAsync(saveAs: true);
                break;
            case StudioCommand.Bounce:
                _ = BounceProjectAsync();
                break;
            case StudioCommand.BounceAs:
                _ = BounceAsAsync();
                break;
            case StudioCommand.Settings:
                _ = OpenSettingsAsync();
                break;
            case StudioCommand.ShowShortcuts:
                ShowShortcutCheatSheet();
                break;
            case StudioCommand.Dismiss:
                liveAudioSession.Engine.StopNoteAudition();
                ReleaseComputerKeyboardNotes();
                FocusManager?.ClearFocus();
                break;
        }
    }

    /// <summary>
    /// Shift+Space: Arrangement view plays from the first section that uses the
    /// selected scene; Session view relaunches that scene right away.
    /// </summary>
    private void PlayFromSelectedScene()
    {
        if (arrangementViewActive)
        {
            var sectionIndex = document.Project.Arrangement.FindIndex(section => section.Scene == document.SelectedSceneIndex);
            PlayArrangementFrom(Math.Max(0, sectionIndex));
            return;
        }
        liveAudioSession.Engine.Stop();
        LaunchScene(document.SelectedSceneIndex);
    }

    /// <summary>Arrows walk the grid the way it is drawn: scenes are rows in Session view, tracks are rows in Arrangement view.</summary>
    private void MoveSelection(int delta, bool vertical)
    {
        var movesScenes = vertical != arrangementViewActive;
        var trackIndex = document.SelectedTrackIndex + (movesScenes ? 0 : delta);
        var sceneIndex = document.SelectedSceneIndex + (movesScenes ? delta : 0);
        document.Select(
            Math.Clamp(trackIndex, 0, Math.Max(0, document.Project.Tracks.Count - 1)),
            Math.Clamp(sceneIndex, 0, Math.Max(0, document.Project.Scenes.Count - 1)));
    }

    private bool CopySelectedClip()
    {
        if (document.SelectedClip is not { } selectedClip)
        {
            return false;
        }
        // Serialized, so the copy carries every field a clip has now or grows later.
        copiedClipPayload = JsonSerializer.Serialize(selectedClip, ProjectSerializer.LenientSerializationOptions);
        SetStatus(StatusSeverity.Informational, $"copied \"{selectedClip.Name}\"");
        return true;
    }

    private void PasteClipIntoSelection()
    {
        if (copiedClipPayload is null || document.SelectedTrack is not { } targetTrack || document.Project.Scenes.Count == 0 ||
            JsonSerializer.Deserialize<SlawClip>(copiedClipPayload, ProjectSerializer.LenientSerializationOptions) is not { } pastedClip)
        {
            return;
        }
        var sceneIndex = document.SelectedSceneIndex;
        document.BeginEdit("paste clip");
        while (targetTrack.Clips.Count <= sceneIndex)
        {
            targetTrack.Clips.Add(null);
        }
        targetTrack.Clips[sceneIndex] = pastedClip;
        document.CommitEdit($"pasted a clip on {targetTrack.Name}", EditScope.Structure);
    }

    // ── Cheat sheet ───────────────────────────────────────────────────────────

    private void ShowShortcutCheatSheet()
    {
        if (shortcutCheatSheet is not null)
        {
            shortcutCheatSheet.Activate();
            return;
        }

        var shortcutRows = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4) };
        var rowIndex = 0;
        void AddRow(string gesture, string description)
        {
            shortcutRows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var gestureCell = new TextBlock { Text = gesture, FontFamily = StudioTheme.MonospaceFont, FontSize = 12, Foreground = StudioTheme.Accent, Margin = new Thickness(0, 3, 18, 3) };
            var descriptionCell = new TextBlock { Text = description, FontSize = 12, Foreground = StudioTheme.Text, Margin = new Thickness(0, 3), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(gestureCell, rowIndex);
            Grid.SetRow(descriptionCell, rowIndex);
            Grid.SetColumn(descriptionCell, 1);
            shortcutRows.Children.Add(gestureCell);
            shortcutRows.Children.Add(descriptionCell);
            rowIndex++;
        }

        // Shortcuts sharing a description share a row: "Ctrl+Shift+Z / Ctrl+Y  Redo".
        foreach (var descriptionGroup in StudioShortcutMap.All.GroupBy(shortcut => shortcut.Description))
        {
            AddRow(string.Join(" / ", descriptionGroup.Select(StudioShortcutMap.DescribeGesture)), descriptionGroup.Key);
        }
        AddRow(string.Empty, string.Empty);
        AddRow("A S D F G H J K L ;", "Computer keyboard (M on): white keys, C to E an octave up");
        AddRow("W E T Y U O P", "Computer keyboard: black keys");
        AddRow("Z / X", "Computer keyboard: octave down / up");
        AddRow("C / V", "Computer keyboard: velocity down / up");

        shortcutCheatSheet = new Window
        {
            Title = "slaw — keyboard shortcuts",
            Width = 560,
            Height = 720,
            Background = StudioTheme.Background,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Padding = new Thickness(20, 16),
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        StudioUi.SectionHeading("Keyboard shortcuts"),
                        StudioUi.Caption("Letters, arrows, Delete and Enter wait politely while you type in a text field. Esc or Enter hands the keyboard back.", 11, StudioTheme.TextMuted),
                        shortcutRows,
                    },
                },
            },
        };
        shortcutCheatSheet.KeyDown += (_, keyEvent) =>
        {
            if (keyEvent.Key is Key.Escape or Key.F1)
            {
                shortcutCheatSheet?.Close();
                keyEvent.Handled = true;
            }
        };
        shortcutCheatSheet.Closed += (_, _) => shortcutCheatSheet = null;
        shortcutCheatSheet.Show(this);
    }
}
