using Avalonia.Controls;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// Sounds arriving from elsewhere, and the command palette that also lives up here:
/// the two ways in for people who know what they want but not where it is.
/// </summary>
internal sealed partial class StudioWindow
{
    private SoundImportWindow? soundImportWindow;

    // ── Import ────────────────────────────────────────────────────────────────

    private void OpenSoundImportWindow(SoundImportMode? presetMode = null, IReadOnlyList<string>? startingPaths = null)
    {
        if (soundImportWindow is not null)
        {
            soundImportWindow.Activate();
        }
        else
        {
            soundImportWindow = new SoundImportWindow(PreviewSampleFile, request => _ = ImportSoundsAsync(request), document.Project.Bpm);
            soundImportWindow.Closed += (_, _) => soundImportWindow = null;
            soundImportWindow.Show(this);
        }
        if (presetMode is { } mode)
        {
            soundImportWindow.PresetMode(mode);
        }
        if (startingPaths is { Count: > 0 })
        {
            _ = soundImportWindow.LoadSourcesAsync(startingPaths);
        }
    }

    public void OpenSoundImport() => OpenSoundImportWindow();

    private async Task ImportSoundsAsync(SoundImportRequest request)
    {
        try
        {
            switch (request.Mode)
            {
                case SoundImportMode.DrumKit:
                    await ImportDrumKitAsync(request);
                    break;
                case SoundImportMode.SeparateTracks:
                    ImportSeparateTracks(request);
                    break;
                case SoundImportMode.AbletonPreset:
                    ImportRacks(request);
                    break;
                case SoundImportMode.SampleLibrary:
                    foreach (var folder in request.Folders)
                    {
                        RegisterSampleFolder(folder);
                    }
                    await RescanSampleLibraryAsync();
                    SetStatus(StatusSeverity.Success, $"added {request.Folders.Count} folder(s) to Browser → Samples");
                    break;
            }
        }
        catch (Exception importRemediationOpportunity) when (importRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"import failed: {importRemediationOpportunity.Message}");
        }
    }

    private async Task ImportDrumKitAsync(SoundImportRequest request)
    {
        if (document.Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
        {
            SetStatus(StatusSeverity.Warning, "the track budget is exhausted. consolidate headcount before onboarding a kit.");
            return;
        }
        SetStatus(StatusSeverity.Informational, $"building the kit {request.Name}…");
        var importsFolder = Path.Combine(
            document.FilePath is { } projectFilePath ? Path.GetDirectoryName(Path.GetFullPath(projectFilePath))! : Path.GetDirectoryName(StudioPreferences.PreferencesFilePath)!,
            SoundImportPlanner.ImportsFolderName);
        var kitPath = Path.Combine(importsFolder, AudioTakeAssembler.TakeFileName(request.Name, DateTime.Now));
        var kit = await Task.Run(() =>
        {
            var assembledKit = SoundImportPlanner.AssembleKit(request.SoundPaths.Select(SampleLibrary.Load).ToList());
            AudioTakeAssembler.WriteTakeWave(kitPath, assembledKit.Samples, assembledKit.SampleRate);
            return assembledKit;
        });

        var sceneIndex = document.SelectedSceneIndex;
        var kitTrack = SoundImportPlanner.CreateKitTrack(document.Project, request.Name, kitPath, kit, sceneIndex);
        document.Edit($"imported the kit {kitTrack.Name}", project => project.Tracks.Add(kitTrack), EditScope.Structure);
        detailView.ShowClipTab();
        document.Select(document.Project.Tracks.Count - 1, sceneIndex);
        if (computerKeyboardToggle is not null)
        {
            computerKeyboardToggle.IsChecked = true;
        }
        Activate();
        FocusManager?.ClearFocus();

        var firstKeys = request.SoundPaths.Take(4).Select((path, position) => $"{SoundImportPlanner.KeyForKitPosition(position)} {SoundImportPlanner.Classify(path).Label.ToLowerInvariant()}");
        SetStatus(StatusSeverity.Success, $"kit {kitTrack.Name}: {request.SoundPaths.Count} sounds from {SoundImportPlanner.KeyForKitPosition(0)} ({string.Join(", ", firstKeys)}{(request.SoundPaths.Count > 4 ? ", …" : string.Empty)}). Ctrl+Z removes it");
    }

    private void ImportRacks(SoundImportRequest request)
    {
        var freeTrackSlots = ProjectAlignmentRemediator.MaximumTrackCount - document.Project.Tracks.Count;
        // Playable instruments first, so the track left selected is the one the keyboard makes sense on.
        var racks = request.Racks.OrderBy(rack => rack.Kind == "Drum Rack").Take(Math.Max(0, freeTrackSlots)).ToList();
        if (racks.Count == 0)
        {
            SetStatus(StatusSeverity.Warning, "the track budget is exhausted. consolidate headcount before onboarding a rack.");
            return;
        }
        var sceneIndex = document.SelectedSceneIndex;
        document.Edit($"imported {string.Join(", ", racks.Select(rack => rack.Name))}", project =>
        {
            foreach (var rack in racks)
            {
                project.Tracks.Add(SoundImportPlanner.CreateRackTrack(project, rack, sceneIndex));
            }
        }, EditScope.Structure);
        detailView.ShowClipTab();
        document.Select(document.Project.Tracks.Count - racks.Count, sceneIndex);
        if (computerKeyboardToggle is not null)
        {
            computerKeyboardToggle.IsChecked = true;
        }
        Activate();
        FocusManager?.ClearFocus();

        var leftOut = racks.SelectMany(rack => rack.LeftOut).Distinct().ToList();
        var missing = racks.Sum(rack => rack.MissingFiles.Count);
        SetStatus(missing > 0 ? StatusSeverity.Warning : StatusSeverity.Success,
            $"imported {string.Join(" and ", racks.Select(rack => rack.Name))}. Keys A to ; play it" +
            (missing > 0 ? $", {missing} recording(s) not found" : string.Empty) +
            (leftOut.Count > 0 ? $", {leftOut.Count} thing(s) not brought over (hover to see)" : string.Empty) + ". Ctrl+Z removes it");
        ToolTip.SetTip(statusText, leftOut.Count > 0 ? $"Not brought over: {string.Join(", ", leftOut)}" : null);
    }

    private void ImportSeparateTracks(SoundImportRequest request)
    {
        var freeTrackSlots = ProjectAlignmentRemediator.MaximumTrackCount - document.Project.Tracks.Count;
        if (freeTrackSlots <= 0)
        {
            SetStatus(StatusSeverity.Warning, "the track budget is exhausted. consolidate headcount before onboarding more sounds.");
            return;
        }
        var importedPaths = request.SoundPaths.Take(freeTrackSlots).ToList();
        var sceneIndex = document.SelectedSceneIndex;
        document.Edit($"imported {importedPaths.Count} sound(s) as tracks", project =>
        {
            foreach (var soundPath in importedPaths)
            {
                project.Tracks.Add(SoundImportPlanner.CreateSoundTrack(project, soundPath, sceneIndex));
            }
        }, EditScope.Structure);
        document.Select(document.Project.Tracks.Count - importedPaths.Count, sceneIndex);
        var leftOut = request.SoundPaths.Count - importedPaths.Count;
        SetStatus(leftOut > 0 ? StatusSeverity.Warning : StatusSeverity.Success,
            $"added {importedPaths.Count} track(s)" + (leftOut > 0 ? $"; {leftOut} left out, the project is at its track limit" : string.Empty) + ". Ctrl+Z removes them");
    }

    // ── Command palette ───────────────────────────────────────────────────────

    private void ToggleCommandPalette()
    {
        if (commandPalette.IsOpen)
        {
            commandPalette.Close();
            return;
        }
        // The palette floats over the main window, so that is where the keyboard goes.
        Activate();
        commandPalette.Open(ComposePaletteEntries());
    }

    private IReadOnlyList<CommandPaletteEntry> ComposePaletteEntries()
    {
        string? GestureFor(StudioCommand studioCommand) =>
            StudioShortcutMap.All.FirstOrDefault(shortcut => shortcut.Command == studioCommand) is { } shortcut
                ? OperatingSystem.IsMacOS()
                    ? StudioShortcutMap.DescribeGesture(shortcut).Replace("Ctrl+", "⌘").Replace("Alt+", "⌥").Replace("Shift+", "⇧")
                    : StudioShortcutMap.DescribeGesture(shortcut)
                : null;

        var entries = StudioCommandTitles.All
            .Select(titled => new CommandPaletteEntry(titled.Title, GestureFor(titled.Command), () => ExecuteStudioCommand(titled.Command)))
            .ToList();
        entries.Add(new CommandPaletteEntry("View: Session", null, () => SelectMainView(arrangementView: false)));
        entries.Add(new CommandPaletteEntry("View: Arrangement", null, () => SelectMainView(arrangementView: true)));
        entries.Add(new CommandPaletteEntry("File: Import MIDI…", null, () => _ = ImportMidiFromPickerAsync()));
        entries.Add(new CommandPaletteEntry("Create: Audio Track (mic)", null, AddAudioTrack));
        entries.Add(new CommandPaletteEntry("Audio: Choose Output Device…", null, () => _ = OpenSettingsAsync(openAudioTab: true)));
        entries.Add(new CommandPaletteEntry("Audio: Choose Input Device…", null, () => _ = OpenSettingsAsync(openAudioTab: true)));
        entries.Add(new CommandPaletteEntry("Audio: Test Output or Input…", null, () => _ = OpenSettingsAsync(openAudioTab: true)));
        entries.Add(new CommandPaletteEntry("Audio: Set Latency…", null, () => _ = OpenSettingsAsync(openAudioTab: true)));
        entries.Add(new CommandPaletteEntry(preferences.ControlSurfaceEnabled ? "Controllers: Turn Off Deck Control (Stream Deck)" : "Controllers: Turn On Deck Control (Stream Deck)", null, ToggleControlSurfaceFromPalette));
        entries.Add(new CommandPaletteEntry(preferences.LaunchpadEnabled ? "Controllers: Turn Off Launchpad" : "Controllers: Turn On Launchpad", null, ToggleLaunchpadFromPalette));
        entries.Add(new CommandPaletteEntry("Controllers: Open Sample Pads on Stream Deck", null, () => OpenDeckProfile("open-pads")));
        entries.Add(new CommandPaletteEntry("Controllers: Open Clip Grid on Stream Deck", null, () => OpenDeckProfile("open-launchpad")));
        entries.Add(new CommandPaletteEntry("Controllers: Settings, Status and Plugins…", null, () => _ = OpenSettingsAsync(openControllersTab: true)));
        foreach (var deckPlugin in Surfaces.ControllerDirectory.DeckPlugins)
        {
            entries.Add(new CommandPaletteEntry($"Controllers: Install {deckPlugin.DisplayName} for Stream Deck", null, () => InstallDeckPlugin(deckPlugin)));
        }
        entries.Add(new CommandPaletteEntry("Import Sounds: Ableton Preset (.adg)…", null, () => OpenSoundImportWindow(SoundImportMode.AbletonPreset)));
        entries.Add(new CommandPaletteEntry("Import Sounds: as Drum Kit…", null, () => OpenSoundImportWindow(SoundImportMode.DrumKit)));
        entries.Add(new CommandPaletteEntry("Import Sounds: as Separate Tracks…", null, () => OpenSoundImportWindow(SoundImportMode.SeparateTracks)));
        entries.Add(new CommandPaletteEntry("Import Sounds: to Sample Library…", null, () => OpenSoundImportWindow(SoundImportMode.SampleLibrary)));
        foreach (var instrumentName in InstrumentVoiceSynthesizer.SupportedInstruments.Where(instrumentName => instrumentName is not ("soundfont" or "multisampler")))
        {
            entries.Add(new CommandPaletteEntry($"Add Track: {DeviceCatalog.InstrumentDisplayName(instrumentName)}", null, () => document.AddTrack(instrumentName)));
        }
        if (document.SelectedTrack is { } selectedTrack)
        {
            foreach (var effectType in DeviceCatalog.EffectTypes)
            {
                entries.Add(new CommandPaletteEntry($"Add Effect to {selectedTrack.Name}: {DeviceCatalog.EffectDisplayName(effectType)}", null, () => detailView.AddEffect(selectedTrack, effectType)));
            }
        }
        return entries;
    }
}
