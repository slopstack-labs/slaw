using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Surfaces;

/// <summary>
/// Novation Launchpad MK2 as a clip launcher and a set of note pads. Runs inside the studio
/// because MIDI needs no helper app and notes want the short path to the recorder. All numbers
/// come from Novation's "Launchpad MK2 Programmer's Reference": Session layout, where a pad is
/// 10 × row + column counted from the bottom left, the right column is column 9, and the top
/// buttons are controllers 104–111. Call everything on the document's thread.
/// </summary>
internal sealed class LaunchpadSurface : IDisposable
{
    private const string PortNameFragment = "Launchpad MK2";

    private const int ArrowUpController = 104;
    private const int ArrowDownController = 105;
    private const int ArrowLeftController = 106;
    private const int ArrowRightController = 107;
    private const int SessionModeController = 108;
    private const int NotesModeController = 109;
    private const int RecordController = 110;
    private const int PlayController = 111;

    private const int NoteOnStatus = 0x90;
    private const int ControllerStatus = 0xB0;
    private const int FlashChannelOffset = 1;

    private const int GridSize = 8;
    private const int SceneColumn = 9;
    private const int LowestBaseNote = 0;
    private const int HighestBaseNote = 120;
    private const int DefaultBaseNote = 36;

    private const int ColorOff = 0;
    private const int ColorDim = 1;
    private const int ColorWhite = 3;
    private const int ColorRed = 5;
    private const int ColorYellow = 13;
    private const int ColorGreen = 21;
    private const int ColorBlue = 45;

    private static readonly byte[] SelectSessionLayout = { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x18, 0x22, 0x00, 0xF7 };

    private readonly IMidiOutputPort outputPort;
    private readonly IControlSurfaceHost surfaceHost;
    private readonly Dictionary<int, Led> litLeds = new();
    private readonly Dictionary<int, int> heldNotesByPad = new();

    private bool notesMode;
    private int pageColumn;
    private int pageRow;
    private int baseNote = DefaultBaseNote;
    private string scaleName = "chromatic";

    /// <summary>What one LED should show. A flashing LED alternates between <see cref="Color"/> and off.</summary>
    private readonly record struct Led(int Color, bool Flashing = false);

    public LaunchpadSurface(IMidiOutputPort outputPort, IControlSurfaceHost surfaceHost)
    {
        this.outputPort = outputPort;
        this.surfaceHost = surfaceHost;
    }

    public string PortName => outputPort.Name;

    public static bool IsLaunchpadPort(string portName) => portName.Contains(PortNameFragment, StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        outputPort.SendSysex(SelectSessionLayout);
        Refresh();
    }

    public void HandleMessage(MidiMessage midiMessage)
    {
        switch (midiMessage.Kind)
        {
            case MidiMessageKind.ControlChange when midiMessage.Data2 > 0:
                PressTopButton(midiMessage.Data1);
                break;
            case MidiMessageKind.NoteOn:
                PressPad(midiMessage.Data1 / 10, midiMessage.Data1 % 10, midiMessage.Data2);
                break;
            case MidiMessageKind.NoteOff:
                ReleasePad(midiMessage.Data1);
                break;
        }
        Refresh();
    }

    /// <summary>Sends only the LEDs that changed, so calling this often is cheap.</summary>
    public void Refresh()
    {
        foreach (var (address, desiredLed) in ComposeLeds())
        {
            if (litLeds.TryGetValue(address, out var litLed) ? litLed == desiredLed : desiredLed.Color == ColorOff)
            {
                continue;
            }
            litLeds[address] = desiredLed;
            var status = address >= ArrowUpController ? ControllerStatus : NoteOnStatus;
            // A flash alternates with the LED's static colour, so that is cleared first.
            outputPort.SendShort(status, address, desiredLed.Flashing ? ColorOff : desiredLed.Color);
            if (desiredLed.Flashing)
            {
                outputPort.SendShort(status + FlashChannelOffset, address, desiredLed.Color);
            }
        }
    }

    public void Dispose()
    {
        ReleaseHeldNotes();
        foreach (var address in litLeds.Keys)
        {
            outputPort.SendShort(address >= ArrowUpController ? ControllerStatus : NoteOnStatus, address, ColorOff);
        }
        litLeds.Clear();
        outputPort.Dispose();
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    private void PressTopButton(int controllerNumber)
    {
        var project = surfaceHost.Document.Project;
        switch (controllerNumber)
        {
            case SessionModeController:
                SwitchMode(notes: false);
                break;
            case NotesModeController:
                SwitchMode(notes: true);
                break;
            case RecordController:
                surfaceHost.RunCommand(StudioCommand.ToggleRecord);
                break;
            case PlayController:
                surfaceHost.RunCommand(StudioCommand.TogglePlayback);
                break;
            case ArrowUpController when notesMode:
                baseNote = Math.Min(baseNote + 12, HighestBaseNote);
                break;
            case ArrowDownController when notesMode:
                baseNote = Math.Max(baseNote - 12, LowestBaseNote);
                break;
            case ArrowLeftController when notesMode:
                surfaceHost.RunCommand(StudioCommand.SelectPreviousTrack);
                break;
            case ArrowRightController when notesMode:
                surfaceHost.RunCommand(StudioCommand.SelectNextTrack);
                break;
            case ArrowUpController:
                pageRow = Math.Max(pageRow - 1, 0);
                break;
            case ArrowDownController:
                pageRow = Math.Min(pageRow + 1, Math.Max(0, project.Scenes.Count - 1));
                break;
            case ArrowLeftController:
                pageColumn = Math.Max(pageColumn - 1, 0);
                break;
            case ArrowRightController:
                pageColumn = Math.Min(pageColumn + 1, Math.Max(0, project.Tracks.Count - 1));
                break;
        }
    }

    private void SwitchMode(bool notes)
    {
        if (notesMode != notes)
        {
            ReleaseHeldNotes();
            notesMode = notes;
        }
    }

    private void PressPad(int gridRow, int gridColumn, int velocity)
    {
        if (gridRow is < 1 or > GridSize || gridColumn is < 1 or > SceneColumn)
        {
            return;
        }
        if (notesMode)
        {
            if (gridColumn == SceneColumn)
            {
                PressNotesModeSideButton(gridRow);
            }
            else if (NoteForPad(gridRow, gridColumn) is { } midiNote)
            {
                heldNotesByPad[10 * gridRow + gridColumn] = midiNote;
                surfaceHost.PlayNote(midiNote, velocity);
            }
            return;
        }

        var sceneIndex = pageRow + GridSize - gridRow;
        if (gridColumn == SceneColumn)
        {
            if (sceneIndex < surfaceHost.Document.Project.Scenes.Count)
            {
                surfaceHost.LaunchScene(sceneIndex);
            }
            return;
        }
        var trackIndex = pageColumn + gridColumn - 1;
        switch (ControlSurfaceProtocol.DescribeSlot(surfaceHost, trackIndex, sceneIndex))
        {
            case "stopped" or "queued":
                surfaceHost.LaunchClip(trackIndex, sceneIndex);
                break;
            case "playing" or "empty":
                surfaceHost.StopTrack(trackIndex);
                break;
        }
    }

    private void PressNotesModeSideButton(int gridRow)
    {
        switch (gridRow)
        {
            case 8:
                scaleName = NotePadLayout.NextScale(scaleName);
                break;
            case 7:
                surfaceHost.RunCommand(StudioCommand.Undo);
                break;
            case 6:
                surfaceHost.RunCommand(StudioCommand.SelectPreviousScene);
                break;
            case 5:
                surfaceHost.RunCommand(StudioCommand.SelectNextScene);
                break;
        }
    }

    private void ReleasePad(int padAddress)
    {
        if (heldNotesByPad.Remove(padAddress, out var midiNote))
        {
            surfaceHost.ReleaseNote(midiNote);
        }
    }

    private void ReleaseHeldNotes()
    {
        foreach (var midiNote in heldNotesByPad.Values)
        {
            surfaceHost.ReleaseNote(midiNote);
        }
        heldNotesByPad.Clear();
    }

    private int? NoteForPad(int gridRow, int gridColumn) =>
        NotePadLayout.NoteAt((gridRow - 1) * GridSize + gridColumn - 1, baseNote, scaleName);

    // ── LEDs ──────────────────────────────────────────────────────────────────

    private IEnumerable<(int Address, Led Led)> ComposeLeds()
    {
        for (var gridRow = 1; gridRow <= GridSize; gridRow++)
        {
            for (var gridColumn = 1; gridColumn <= SceneColumn; gridColumn++)
            {
                var address = 10 * gridRow + gridColumn;
                yield return (address, notesMode ? ComposeNotesLed(gridRow, gridColumn, address) : ComposeSessionLed(gridRow, gridColumn));
            }
        }

        var project = surfaceHost.Document.Project;
        var transportPlaying = PlaybackToggle.IsAudiblyPlaying(surfaceHost.Transport);
        yield return (ArrowUpController, new Led(notesMode ? ColorDim : pageRow > 0 ? ColorDim : ColorOff));
        yield return (ArrowDownController, new Led(notesMode || pageRow < project.Scenes.Count - 1 ? ColorDim : ColorOff));
        yield return (ArrowLeftController, new Led(notesMode || pageColumn > 0 ? ColorDim : ColorOff));
        yield return (ArrowRightController, new Led(notesMode || pageColumn < project.Tracks.Count - 1 ? ColorDim : ColorOff));
        yield return (SessionModeController, new Led(notesMode ? ColorDim : ColorBlue));
        yield return (NotesModeController, new Led(notesMode ? ColorBlue : ColorDim));
        yield return (RecordController, new Led(surfaceHost.RecordEngaged ? ColorRed : ColorDim));
        yield return (PlayController, new Led(transportPlaying ? ColorGreen : ColorDim));
    }

    private Led ComposeSessionLed(int gridRow, int gridColumn)
    {
        var sceneIndex = pageRow + GridSize - gridRow;
        if (gridColumn == SceneColumn)
        {
            return ComposeSceneLed(sceneIndex);
        }
        return ControlSurfaceProtocol.DescribeSlot(surfaceHost, pageColumn + gridColumn - 1, sceneIndex) switch
        {
            "playing" => new Led(ColorGreen),
            "queued" => new Led(ColorYellow, Flashing: true),
            "stopped" => new Led(ColorDim),
            _ => new Led(ColorOff),
        };
    }

    private Led ComposeSceneLed(int sceneIndex)
    {
        var project = surfaceHost.Document.Project;
        if (sceneIndex >= project.Scenes.Count)
        {
            return new Led(ColorOff);
        }
        var slotStates = Enumerable.Range(0, project.Tracks.Count).Select(trackIndex => ControlSurfaceProtocol.DescribeSlot(surfaceHost, trackIndex, sceneIndex)).ToList();
        if (slotStates.Contains("queued"))
        {
            return new Led(ColorYellow, Flashing: true);
        }
        return new Led(slotStates.Contains("playing") ? ColorGreen : ColorDim);
    }

    private Led ComposeNotesLed(int gridRow, int gridColumn, int address)
    {
        if (gridColumn == SceneColumn)
        {
            return gridRow switch
            {
                8 => new Led(scaleName == "chromatic" ? ColorDim : ColorBlue),
                7 or 6 or 5 => new Led(ColorDim),
                _ => new Led(ColorOff),
            };
        }
        if (heldNotesByPad.ContainsKey(address))
        {
            return new Led(ColorWhite);
        }
        return NoteForPad(gridRow, gridColumn) switch
        {
            null => new Led(ColorOff),
            var midiNote when midiNote % 12 == 0 => new Led(ColorBlue),
            _ => new Led(ColorDim),
        };
    }
}
