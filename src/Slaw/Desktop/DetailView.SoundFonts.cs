using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.SoundFonts;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>The SoundFont Player device: which .sf2 it plays, a preset picker, and Load….</summary>
internal sealed partial class DetailView
{
    internal static readonly FilePickerFileType SoundFontFileType = new("SoundFont") { Patterns = new[] { "*.sf2" } };

    private Control ComposeSoundFontSection(SlawTrack track)
    {
        var loadButton = StudioUi.Button("Load…", () => _ = ChooseSoundFontAsync(track), "Load a SoundFont (.sf2) into this track; you can also drop one onto the track", compact: true);
        if (track.SoundFont is not { Path.Length: > 0 } soundFontReference)
        {
            return new StackPanel
            {
                Spacing = 4,
                Children = { StudioUi.Caption("No SoundFont loaded — drop a .sf2 here or use Load…", 10, StudioTheme.Warning), loadButton },
            };
        }

        var fontCaption = StudioUi.Caption(Path.GetFileName(soundFontReference.Path), 10);
        ToolTip.SetTip(fontCaption, soundFontReference.Path);
        Control presetControl;
        if (SoundFontLibrary.TryGet(soundFontReference.Path) is { } bank)
        {
            var presetIndex = bank.Presets.ToList().FindIndex(preset => preset.Bank == soundFontReference.Bank && preset.Program == soundFontReference.Program);
            var presetSelector = new ComboBox
            {
                ItemsSource = bank.Presets.Select(preset => preset.Label).ToList(),
                SelectedIndex = presetIndex,
                PlaceholderText = $"{soundFontReference.Bank}:{soundFontReference.Program} {soundFontReference.PresetName}",
                FontSize = 12,
                Width = 210,
                MaxDropDownHeight = 360,
            };
            ToolTip.SetTip(presetSelector, "Preset (bank:program name). Bank 128 is the drum kit: every MIDI note plays a different drum.");
            presetSelector.SelectionChanged += (_, _) =>
            {
                if (presetSelector.SelectedIndex < 0 || presetSelector.SelectedIndex == presetIndex)
                {
                    return;
                }
                var pickedPreset = bank.Presets[presetSelector.SelectedIndex];
                document.Edit($"switched {track.Name} to {pickedPreset.Label}", _ => track.SoundFont = new SoundFontReference
                {
                    Path = soundFontReference.Path,
                    Bank = pickedPreset.Bank,
                    Program = pickedPreset.Program,
                    PresetName = pickedPreset.Name,
                }, EditScope.Structure);
            };
            presetControl = presetSelector;
        }
        else if (SoundFontLibrary.LoadProblem(soundFontReference.Path) is { } loadProblem)
        {
            presetControl = StudioUi.Caption($"⚠ {loadProblem}", 10, StudioTheme.Failure);
        }
        else
        {
            presetControl = StudioUi.Caption("reading presets…", 10);
            _ = Task.Run(() => SoundFontLibrary.LoadPreset(soundFontReference)).ContinueWith(_ => Dispatcher.UIThread.Post(Rebuild), TaskScheduler.Default);
        }
        return new StackPanel { Spacing = 4, Children = { presetControl, StudioUi.Row(6, fontCaption, loadButton) } };
    }

    private async Task ChooseSoundFontAsync(SlawTrack track)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }
        var chosenFiles = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Load a SoundFont into {track.Name}",
            AllowMultiple = false,
            FileTypeFilter = new[] { SoundFontFileType },
        });
        if (chosenFiles.Count == 0 || chosenFiles[0].TryGetLocalPath() is not { } chosenPath)
        {
            return;
        }
        SoundFontReference loadedReference;
        try
        {
            // Keep the track's program when the new font has it, so swapping fonts keeps the part's instrument.
            loadedReference = await Task.Run(() => SoundFontDeviceOperations.Resolve(chosenPath, track.SoundFont?.Bank ?? 0, track.SoundFont?.Program ?? 0));
        }
        catch (InvalidDataException loadRemediationOpportunity)
        {
            host.ReportStatus(loadRemediationOpportunity.Message);
            return;
        }
        document.Edit($"loaded {SoundFontDeviceOperations.Describe(loadedReference)} on {track.Name}",
            _ => SoundFontDeviceOperations.LoadIntoTrack(track, loadedReference), EditScope.Structure);
    }
}
