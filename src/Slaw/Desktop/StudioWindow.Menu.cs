using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The menu bar: File, Edit, Create, View, Help, where every DAW keeps them.
/// Items run the same <see cref="StudioCommand"/>s as the keyboard and show the
/// gesture from <see cref="StudioShortcutMap"/>, so menus, keys and the F1 sheet
/// cannot drift apart.
/// </summary>
internal sealed partial class StudioWindow
{
    private string? lastBouncePath;

    private Control ComposeMenuBar()
    {
        MenuItem CommandItem(string header, StudioCommand studioCommand)
        {
            var commandItem = new MenuItem { Header = header };
            if (StudioShortcutMap.All.FirstOrDefault(shortcut => shortcut.Command == studioCommand) is { } shortcut)
            {
                // Display only: the window's key handler owns dispatch, so nothing fires twice.
                commandItem.InputGesture = new KeyGesture(shortcut.Key, shortcut.Modifiers);
            }
            commandItem.Click += (_, _) => ExecuteStudioCommand(studioCommand);
            return commandItem;
        }

        MenuItem ActionItem(string header, Action onClick, KeyGesture? displayedGesture = null)
        {
            var actionItem = new MenuItem { Header = header, InputGesture = displayedGesture };
            actionItem.Click += (_, _) => onClick();
            return actionItem;
        }

        var bounceItem = CommandItem("_Bounce Arrangement…", StudioCommand.Bounce);
        var revealBounceItem = ActionItem("Show Last Bounce in Folder", () => _ = RevealLastBounceAsync());
        var fileMenu = new MenuItem
        {
            Header = "_File",
            Items =
            {
                CommandItem("_New Project", StudioCommand.New),
                CommandItem("_Open…", StudioCommand.Open),
                new Separator(),
                CommandItem("_Save", StudioCommand.Save),
                CommandItem("Save _As…", StudioCommand.SaveAs),
                new Separator(),
                CommandItem("Import _Sounds…", StudioCommand.ImportSounds),
                ActionItem("_Import MIDI…", () => _ = ImportMidiFromPickerAsync()),
                ActionItem("Import MIDI with Sound_Font…", () => _ = ImportMidiWithSoundFontFromPickerAsync()),
                new Separator(),
                bounceItem,
                CommandItem("Bounce _Options… (scene, stems, format)", StudioCommand.BounceAs),
                revealBounceItem,
                new Separator(),
                CommandItem("Se_ttings…", StudioCommand.Settings),
                new Separator(),
                ActionItem("_Quit", Close, new KeyGesture(Key.Q, KeyModifiers.Control)),
            },
        };
        fileMenu.SubmenuOpened += (_, _) =>
        {
            // Say where a bounce will land before anyone asks for one.
            bounceItem.Header = document.FilePath is { } projectPath
                ? $"_Bounce Arrangement… → {Path.GetFileNameWithoutExtension(projectPath)}.wav"
                : "_Bounce Arrangement…";
            ToolTip.SetTip(bounceItem, document.FilePath is { } savedPath
                ? $"Opens the bounce dialog, pointed at {Path.ChangeExtension(savedPath, ".wav")}"
                : "Opens the bounce dialog; the project is not saved yet, so pick a destination there");
            revealBounceItem.IsEnabled = lastBouncePath is not null && File.Exists(lastBouncePath);
            ToolTip.SetTip(revealBounceItem, lastBouncePath ?? "Nothing bounced yet this session");
        };

        var undoItem = CommandItem("_Undo", StudioCommand.Undo);
        var redoItem = CommandItem("_Redo", StudioCommand.Redo);
        var editMenu = new MenuItem
        {
            Header = "_Edit",
            Items =
            {
                undoItem,
                redoItem,
                new Separator(),
                CommandItem("Cu_t Clip", StudioCommand.CutClip),
                CommandItem("_Copy Clip", StudioCommand.CopyClip),
                CommandItem("_Paste Clip", StudioCommand.PasteClip),
                CommandItem("_Duplicate", StudioCommand.DuplicateClip),
                CommandItem("Duplicate _Scene", StudioCommand.DuplicateScene),
                CommandItem("De_lete", StudioCommand.DeleteClip),
            },
        };
        editMenu.SubmenuOpened += (_, _) =>
        {
            undoItem.Header = document.UndoLabel is { } undoLabel ? $"_Undo {undoLabel}" : "_Undo";
            undoItem.IsEnabled = document.CanUndo;
            redoItem.IsEnabled = document.CanRedo;
        };

        var createMenu = new MenuItem
        {
            Header = "_Create",
            Items =
            {
                CommandItem("Insert _Track", StudioCommand.AddTrack),
                ActionItem("Insert _Audio Track (mic)", AddAudioTrack),
                CommandItem("Insert _Scene", StudioCommand.AddScene),
                CommandItem("Insert Empty _Clip", StudioCommand.CreateClip),
                new Separator(),
                CommandItem("New AI C_hat", StudioCommand.NewChat),
            },
        };

        var checkedViewItems = new List<(MenuItem Item, Func<bool> IsOn)>();
        MenuItem CheckedCommandItem(string header, StudioCommand studioCommand, Func<bool> isOn)
        {
            var checkedItem = CommandItem(header, studioCommand);
            checkedItem.ToggleType = MenuItemToggleType.CheckBox;
            checkedViewItems.Add((checkedItem, isOn));
            return checkedItem;
        }

        var viewMenu = new MenuItem
        {
            Header = "_View",
            Items =
            {
                ActionItem("_Session", () => SelectMainView(arrangementView: false)),
                ActionItem("_Arrangement", () => SelectMainView(arrangementView: true)),
                CommandItem("Toggle Session / Arrangement", StudioCommand.SwitchView),
                new Separator(),
                CommandItem("Computer MIDI _Keyboard", StudioCommand.ToggleComputerKeyboard),
                new Separator(),
                CheckedCommandItem("_Browser", StudioCommand.ToggleBrowser, () => !preferences.BrowserHidden),
                CheckedCommandItem("Clip && _Devices Panel", StudioCommand.ToggleDetailPanel, () => !preferences.DetailPanelHidden),
                CheckedCommandItem("Session _Mixer", StudioCommand.ToggleSessionMixer, () => !preferences.SessionMixerHidden),
                CheckedCommandItem("AI _Co-Pilot Panel", StudioCommand.ToggleAiPanel, () => !preferences.AiPanelHidden),
                new Separator(),
                CheckedCommandItem("Second _Window", StudioCommand.ToggleSecondWindow, () => secondWindow is not null),
                CheckedCommandItem("Clip && Devices in Second Window", StudioCommand.ToggleDetailInSecondWindow, () => secondWindow is not null && preferences.DetailInSecondWindow),
                CheckedCommandItem("_Full Screen", StudioCommand.ToggleFullScreen, () => FocusedStudioWindow.WindowState == WindowState.FullScreen),
            },
        };
        viewMenu.SubmenuOpened += (_, _) =>
        {
            foreach (var (checkedItem, isOn) in checkedViewItems)
            {
                checkedItem.IsChecked = isOn();
            }
        };

        var helpMenu = new MenuItem
        {
            Header = "_Help",
            Items =
            {
                CommandItem("_Command Palette…", StudioCommand.ShowCommandPalette),
                CommandItem("_Keyboard Shortcuts", StudioCommand.ShowShortcuts),
            },
        };

        return new Menu
        {
            Background = StudioTheme.Surface,
            Padding = new Thickness(6, 0),
            Items = { fileMenu, editMenu, createMenu, viewMenu, helpMenu },
        };
    }

    private async Task ImportMidiFromPickerAsync()
    {
        var selectedFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import MIDI file",
            AllowMultiple = false,
            FileTypeFilter = new[] { MidiFileType },
        });
        if (selectedFiles.Count > 0 && selectedFiles[0].TryGetLocalPath() is { } selectedPath)
        {
            ImportMidiFile(selectedPath);
        }
    }

    private async Task RevealLastBounceAsync()
    {
        if (lastBouncePath is null || Path.GetDirectoryName(lastBouncePath) is not { } bounceFolder || !Directory.Exists(bounceFolder))
        {
            SetStatus(StatusSeverity.Warning, "nothing bounced yet this session. the deliverable pipeline is empty.");
            return;
        }
        if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(bounceFolder)))
        {
            SetStatus(StatusSeverity.Warning, $"could not open {bounceFolder}; the deliverable is there anyway");
        }
    }
}
