using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// MIDI in the studio: play the selected track from any connected keyboard,
/// record what you play into the selected clip (quantized to the playhead while
/// playing, step input while stopped), and steer the mix with knobs.
/// </summary>
internal sealed partial class StudioWindow
{
    private const int ModulationWheelController = 1;
    private const int VolumeController = 7;
    private const int PanController = 10;

    /// <summary>Notes struck within this window land on the same step in step input (a chord).</summary>
    private static readonly TimeSpan StepInputChordWindow = TimeSpan.FromMilliseconds(60);

    private IMidiInputSource? midiInputSource;
    private readonly TextBlock midiStatusText = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTheme.TextMuted };
    private ToggleButton? recordToggle;
    private DispatcherTimer? midiActivityTimer;

    private bool recordingTakeOpen;
    private readonly Dictionary<int, RecordedNote> heldRecordedNotes = new();
    private int stepInputCursor;
    private (int TrackIndex, int SceneIndex) stepInputTarget = (-1, -1);
    private DateTime lastStepInputNoteTime = DateTime.MinValue;
    private DateTime lastControllerEditTime = DateTime.MinValue;
    private DispatcherTimer? controllerSettleTimer;

    /// <summary>Where a held, recorded note started, so its release can write the sustain.</summary>
    private sealed record RecordedNote(SlawClip Clip, int StartStep, int MidiNote, double StartPosition, bool Percussive);

    // ── Toolbar controls ──────────────────────────────────────────────────────

    private Control ComposeRecordControls()
    {
        recordToggle = new ToggleButton
        {
            Content = "● Rec",
            Padding = new Thickness(10, 4),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(recordToggle,
            "Record MIDI into the selected clip. While playing: notes land on the playhead (quantized to sixteenths). " +
            "While stopped: step input — each note (or chord) fills the next step.\n" +
            "With a track armed for audio (● on its mixer strip): records a take from the microphone until the transport stops.");
        recordToggle.IsCheckedChanged += (_, _) =>
        {
            var armed = recordToggle.IsChecked == true;
            recordToggle.Foreground = armed ? StudioTheme.Failure : StudioTheme.Text;
            HandleAudioRecordToggle(armed);
            if (!armed)
            {
                CloseRecordingTake();
            }
            else
            {
                stepInputTarget = (-1, -1);
                SetStatus(StatusSeverity.Informational, midiInputSource is null
                    ? "record armed — but no MIDI input is running (Settings → Integrations)"
                    : "record armed: play to record into the selected clip (step input while stopped)");
            }
        };
        midiActivityTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            midiStatusText.Foreground = StudioTheme.TextMuted;
            midiActivityTimer!.Stop();
        });
        return StudioUi.Row(6, recordToggle, ComposeInputMeter(), midiStatusText);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Starts or stops the MIDI backend to match the preferences.</summary>
    private void ApplyMidiInputPreference()
    {
        if (!preferences.MidiInputEnabled)
        {
            midiInputSource?.Dispose();
            midiInputSource = null;
            midiStatusText.Text = string.Empty;
            return;
        }
        if (midiInputSource is not null)
        {
            return;
        }
        var createdSource = MidiInputFactory.TryCreate(out var unavailabilityReason);
        if (createdSource is null)
        {
            midiStatusText.Text = "🎹 MIDI n/a";
            ToolTip.SetTip(midiStatusText, unavailabilityReason);
            return;
        }
        createdSource.MessageReceived += midiMessage => Dispatcher.UIThread.Post(() => HandleMidiMessage(midiMessage));
        createdSource.SourcesChanged += () => Dispatcher.UIThread.Post(RefreshMidiStatus);
        try
        {
            createdSource.Start();
            midiInputSource = createdSource;
        }
        catch (InvalidOperationException midiRemediationOpportunity)
        {
            createdSource.Dispose();
            midiStatusText.Text = "🎹 MIDI off";
            ToolTip.SetTip(midiStatusText, midiRemediationOpportunity.Message);
            return;
        }
        RefreshMidiStatus();
    }

    private void RefreshMidiStatus()
    {
        if (midiInputSource is null)
        {
            return;
        }
        var sourceNames = midiInputSource.ConnectedSourceNames;
        midiStatusText.Text = sourceNames.Count switch
        {
            0 => "🎹 no MIDI devices",
            1 => $"🎹 {sourceNames[0]}",
            _ => $"🎹 {sourceNames.Count} MIDI sources",
        };
        var portHint = OperatingSystem.IsLinux() && midiInputSource is AlsaSequencerMidiInput alsaInput
            ? $"\nOther programs can send to slaw at ALSA sequencer port {alsaInput.InputAddress} (e.g. aconnect, aplaymidi -p {alsaInput.InputAddress})."
            : string.Empty;
        ToolTip.SetTip(midiStatusText,
            $"MIDI input via {midiInputSource.BackendName}: {(sourceNames.Count == 0 ? "nothing connected yet — plug in a keyboard" : string.Join(", ", sourceNames))}." +
            $"\nPlays the selected track. Mod wheel → filter, CC7 → volume, CC10 → pan.{portHint}");
    }

    // ── Message handling ──────────────────────────────────────────────────────

    private void HandleMidiMessage(MidiMessage midiMessage)
    {
        midiStatusText.Foreground = StudioTheme.Playhead;
        midiActivityTimer?.Stop();
        midiActivityTimer?.Start();

        if (document.SelectedTrack is not { } targetTrack)
        {
            return;
        }
        switch (midiMessage.Kind)
        {
            case MidiMessageKind.NoteOn:
                liveAudioSession.Engine.StartNoteAudition(targetTrack.Id, midiMessage.Data1, midiMessage.Data2 / 127.0);
                if (recordToggle?.IsChecked == true)
                {
                    RecordNoteOn(targetTrack, midiMessage.Data1);
                }
                break;
            case MidiMessageKind.NoteOff:
                liveAudioSession.Engine.StopNoteAudition(midiMessage.Data1);
                if (heldRecordedNotes.Remove(midiMessage.Data1, out var recordedNote))
                {
                    RecordNoteOff(recordedNote);
                }
                break;
            case MidiMessageKind.ControlChange:
                HandleControllerChange(targetTrack, midiMessage.Data1, midiMessage.Data2);
                break;
        }
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    private void OpenRecordingTake(string takeLabel)
    {
        if (recordingTakeOpen)
        {
            return;
        }
        document.BeginEdit(takeLabel);
        recordingTakeOpen = true;
    }

    private void CloseRecordingTake()
    {
        heldRecordedNotes.Clear();
        if (!recordingTakeOpen)
        {
            return;
        }
        recordingTakeOpen = false;
        document.CommitEdit("recorded a MIDI take", EditScope.Structure);
        SetStatus(StatusSeverity.Success, "take recorded — Ctrl+Z removes the whole take");
    }

    /// <summary>
    /// The clip step being heard right now, as a fractional position. The engine
    /// publishes the step it is rendering; what the player hears lags by the
    /// output latency, so the position is pulled back by that much.
    /// </summary>
    private double? AudiblePlayheadPosition(SlawTrack targetTrack, SlawClip targetClip)
    {
        var transportSnapshot = liveAudioSession.Engine.Transport;
        if (transportSnapshot.Mode == TransportMode.Stopped || transportSnapshot.StepDurationSeconds <= 0 ||
            !transportSnapshot.Tracks.TryGetValue(targetTrack.Id, out var trackState) || trackState.ClipStep is not { } renderedStep)
        {
            return null;
        }
        var secondsSincePublished = Stopwatch.GetElapsedTime(transportSnapshot.PublishedTimestamp).TotalSeconds;
        var outputLatencySeconds = (preferences.AudioLatencyMilliseconds + 30) / 1000.0;
        var position = renderedStep + (secondsSincePublished - outputLatencySeconds) / transportSnapshot.StepDurationSeconds;
        var clipLength = targetClip.Pattern.Count;
        return ((position % clipLength) + clipLength) % clipLength;
    }

    private void RecordNoteOn(SlawTrack targetTrack, int midiNote)
    {
        var trackIndex = document.Project.Tracks.IndexOf(targetTrack);
        var transportSnapshot = liveAudioSession.Engine.Transport;
        var playingScene = transportSnapshot.Mode != TransportMode.Stopped &&
                           transportSnapshot.Tracks.TryGetValue(targetTrack.Id, out var trackState)
            ? trackState.PlayingScene
            : null;
        var sceneIndex = playingScene ?? document.SelectedSceneIndex;
        var percussive = InstrumentVoiceSynthesizer.IsPercussive(targetTrack.Instrument);

        OpenRecordingTake($"record into {targetTrack.Name}");
        while (targetTrack.Clips.Count <= sceneIndex)
        {
            targetTrack.Clips.Add(null);
        }
        var targetClip = targetTrack.Clips[sceneIndex] ??= SlawClip.Empty(document.Project.Scenes[sceneIndex], document.Project.Steps);

        int targetStep;
        double startPosition;
        bool appendsToChord;
        if (AudiblePlayheadPosition(targetTrack, targetClip) is { } audiblePosition)
        {
            targetStep = (int)Math.Round(audiblePosition) % targetClip.Pattern.Count;
            startPosition = audiblePosition;
            // Real-time takes overdub: the loop comes around, you add to what is there.
            appendsToChord = true;
        }
        else
        {
            // Step input: a new note advances the cursor, unless it is part of a chord being struck.
            if (stepInputTarget != (trackIndex, sceneIndex))
            {
                stepInputTarget = (trackIndex, sceneIndex);
                stepInputCursor = 0;
                lastStepInputNoteTime = DateTime.MinValue;
            }
            var joinsChord = DateTime.UtcNow - lastStepInputNoteTime < StepInputChordWindow;
            if (!joinsChord && lastStepInputNoteTime != DateTime.MinValue)
            {
                stepInputCursor = (stepInputCursor + 1) % targetClip.Pattern.Count;
            }
            lastStepInputNoteTime = DateTime.UtcNow;
            appendsToChord = joinsChord;
            targetStep = stepInputCursor % targetClip.Pattern.Count;
            startPosition = targetStep;
            SetStatus(StatusSeverity.Informational, $"step input: step {targetStep + 1}/{targetClip.Pattern.Count} on {targetTrack.Name}");
        }

        if (percussive)
        {
            targetClip.Pattern[targetStep] = NotationResolver.TriggerSymbol;
        }
        else if (appendsToChord)
        {
            ClipNoteEditor.AddNote(targetClip.Pattern, targetStep, midiNote, length: 1);
        }
        else
        {
            ClipNoteEditor.WriteChord(targetClip.Pattern, targetStep, new[] { (midiNote, 1) });
        }
        heldRecordedNotes[midiNote] = new RecordedNote(targetClip, targetStep, midiNote, startPosition, percussive);
        document.CommitEdit($"recorded {NotationResolver.FormatMidiNote(midiNote)} on {targetTrack.Name}", EditScope.Content, notifyCopilot: false);
    }

    private void RecordNoteOff(RecordedNote recordedNote)
    {
        if (recordedNote.Percussive || document.SelectedTrack is not { } targetTrack)
        {
            return;
        }
        // Only real-time takes know how long the note was held.
        if (AudiblePlayheadPosition(targetTrack, recordedNote.Clip) is not { } releasePosition)
        {
            return;
        }
        var clipLength = recordedNote.Clip.Pattern.Count;
        var heldSteps = (int)Math.Round(((releasePosition - recordedNote.StartPosition) % clipLength + clipLength) % clipLength);
        // Only this key's note gets the length: the rest of a recorded chord keeps its own.
        ClipNoteEditor.SetNoteLength(recordedNote.Clip.Pattern, recordedNote.StartStep, recordedNote.MidiNote, Math.Max(1, heldSteps));
        document.CommitEdit("held a recorded note", EditScope.Content, notifyCopilot: false);
    }

    // ── Controllers ───────────────────────────────────────────────────────────

    private void HandleControllerChange(SlawTrack targetTrack, int controllerNumber, int controllerValue)
    {
        var normalizedValue = Math.Clamp(controllerValue, 0, 127) / 127.0;
        Action? controllerEdit = controllerNumber switch
        {
            ModulationWheelController when DeviceCatalog.InstrumentParametersFor(targetTrack.Instrument).Any(specification => specification.Key == "cutoff") =>
                () => targetTrack.InstrumentParameters["cutoff"] = 200 * Math.Pow(20000.0 / 200, normalizedValue),
            VolumeController => () => targetTrack.Gain = Math.Round(normalizedValue * 1.2, 3),
            PanController => () => targetTrack.Pan = Math.Round((controllerValue - 64) / 63.0, 2),
            _ => null,
        };
        if (controllerEdit is null)
        {
            return;
        }
        // One undo step per knob gesture, not one per message.
        if (DateTime.UtcNow - lastControllerEditTime > TimeSpan.FromSeconds(1))
        {
            document.BeginEdit($"MIDI controller {controllerNumber}");
        }
        lastControllerEditTime = DateTime.UtcNow;
        controllerEdit();
        document.CommitEdit($"MIDI CC{controllerNumber} on {targetTrack.Name}", EditScope.Content, notifyCopilot: false);

        // Knobs and faders catch up once the gesture settles.
        controllerSettleTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            controllerSettleTimer!.Stop();
            sessionView.Rebuild();
            detailView.Rebuild();
        });
        controllerSettleTimer.Stop();
        controllerSettleTimer.Start();
    }
}
