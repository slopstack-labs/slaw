using Avalonia.Platform.Storage;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.SoundFonts;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>SoundFonts in the studio: dropping and loading .sf2 files, and importing MIDI voiced with one.</summary>
internal sealed partial class StudioWindow
{
    public void ReportStatus(string message) => SetStatus(StatusSeverity.Warning, message);

    private static List<string> DroppedSoundFonts(DragPayload dragPayload) =>
        dragPayload.Kind == DragPayloadKind.Files
            ? dragPayload.FilePaths!.Where(droppedPath => File.Exists(droppedPath) && SoundFontBank.IsSoundFontFile(droppedPath)).ToList()
            : new List<string>();

    /// <summary>A .sf2 dropped on a track loads into it; dropped anywhere else it becomes a new track.</summary>
    private void LoadDroppedSoundFont(string soundFontPath, DropTarget dropTarget, int targetTrackIndex)
    {
        var loadsIntoTrack = dropTarget.Kind is DropTargetKind.Track or DropTargetKind.ClipSlot or DropTargetKind.DeviceChain && IsValidTrack(targetTrackIndex);
        var keptProgram = loadsIntoTrack ? document.Project.Tracks[targetTrackIndex].SoundFont : null;
        SoundFontReference soundFontReference;
        try
        {
            soundFontReference = SoundFontDeviceOperations.Resolve(soundFontPath, keptProgram?.Bank ?? 0, keptProgram?.Program ?? 0);
        }
        catch (InvalidDataException loadRemediationOpportunity)
        {
            SetStatus(StatusSeverity.Failure, loadRemediationOpportunity.Message);
            return;
        }
        if (loadsIntoTrack)
        {
            var targetTrack = document.Project.Tracks[targetTrackIndex];
            document.Edit($"loaded {SoundFontDeviceOperations.Describe(soundFontReference)} on {targetTrack.Name}",
                _ => SoundFontDeviceOperations.LoadIntoTrack(targetTrack, soundFontReference), EditScope.Structure);
            document.Select(targetTrackIndex, document.SelectedSceneIndex);
        }
        else if (!AddSoundFontTrack(soundFontReference))
        {
            return;
        }
        SetStatus(StatusSeverity.Success, $"loaded {SoundFontDeviceOperations.Describe(soundFontReference)} — pick another preset in the device");
    }

    private bool AddSoundFontTrack(SoundFontReference soundFontReference)
    {
        if (document.Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
        {
            SetStatus(StatusSeverity.Warning, "the track budget is exhausted. consolidate headcount before onboarding General MIDI.");
            return false;
        }
        var sceneIndex = document.SelectedSceneIndex;
        document.Edit($"added a {soundFontReference.PresetName} track", project =>
        {
            var soundFontTrack = SoundFontDeviceOperations.CreateTrack(project, soundFontReference);
            if (sceneIndex < soundFontTrack.Clips.Count)
            {
                soundFontTrack.Clips[sceneIndex] = SlawClip.Empty(project.Scenes[sceneIndex], project.Steps);
            }
            project.Tracks.Add(soundFontTrack);
        }, EditScope.Structure);
        document.Select(document.Project.Tracks.Count - 1, sceneIndex);
        return true;
    }

    private async Task<string?> PickSoundFontAsync(string title)
    {
        var chosenFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { DetailView.SoundFontFileType },
        });
        return chosenFiles.Count > 0 ? chosenFiles[0].TryGetLocalPath() : null;
    }

    /// <summary>Browser → SoundFont…: a new track playing the chosen font's first preset.</summary>
    private async Task AddSoundFontTrackFromPickerAsync()
    {
        if (await PickSoundFontAsync("Add a SoundFont track") is { } soundFontPath)
        {
            LoadDroppedSoundFont(soundFontPath, new DropTarget(DropTargetKind.EmptyArea), -1);
        }
    }

    /// <summary>File → Import MIDI with SoundFont…: pick the .mid, then the .sf2 to voice it with.</summary>
    private async Task ImportMidiWithSoundFontFromPickerAsync()
    {
        var chosenMidiFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import MIDI file (then choose its SoundFont)",
            AllowMultiple = false,
            FileTypeFilter = new[] { MidiFileType },
        });
        if (chosenMidiFiles.Count == 0 || chosenMidiFiles[0].TryGetLocalPath() is not { } midiFilePath)
        {
            return;
        }
        if (await PickSoundFontAsync($"SoundFont for {Path.GetFileName(midiFilePath)}") is { } soundFontPath)
        {
            ImportMidiFile(midiFilePath, soundFontPath);
        }
    }

    /// <summary>
    /// The font a MIDI import should use: the one asked for, else the .sf2 that came
    /// with the file. Null (built-in instruments) when there is none or it will not load.
    /// </summary>
    private MidiSoundFontChoice? SoundFontForMidiImport(string midiFilePath, string? requestedSoundFontPath)
    {
        var soundFontPath = requestedSoundFontPath ?? MidiFileImporter.FindCompanionSoundFont(midiFilePath);
        if (soundFontPath is null)
        {
            return null;
        }
        try
        {
            return new MidiSoundFontChoice(Path.GetFullPath(soundFontPath), SoundFontLibrary.Load(soundFontPath));
        }
        catch (Exception loadRemediationOpportunity) when (loadRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Warning, $"SoundFont {Path.GetFileName(soundFontPath)} would not load ({loadRemediationOpportunity.Message}); importing with built-in instruments");
            return null;
        }
    }
}
