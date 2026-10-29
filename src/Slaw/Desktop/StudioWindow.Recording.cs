using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// Audio recording, Ableton-style: arm a sampler track (●), press ● Rec, and
/// sing. Stopped, Rec launches the scene and the take starts with it; playing,
/// the take punches in on the next step. Stopping the transport (or Rec) ends
/// the take: it becomes a WAV in recordings/, loaded into the armed track and
/// triggered on the step it was sung on.
///
/// No input monitoring: slaw's output runs through a pipe with tens of
/// milliseconds of buffering, so hearing yourself through it would be a slapback
/// delay with a feedback risk. Use your interface's or PipeWire's direct monitoring.
/// </summary>
internal sealed partial class StudioWindow
{
    private const double MinimumTakeSeconds = 0.1;
    private const double InputMeterWidth = 72;

    private string? recordArmedTrackId;
    private IAudioInputSource? audioInputSource;
    private string? audioInputConfiguration;
    private AudioTakeInProgress? activeAudioTake;
    private readonly Border inputMeterFill = new() { Height = 8, Width = 0, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
    private Control? inputMeter;
    private long lastRecordingStatusTimestamp;

    /// <summary>A take being captured, and how to find where it started once the clip's playhead is known.</summary>
    private sealed class AudioTakeInProgress
    {
        public required string TrackId { get; init; }
        public required int SceneIndex { get; init; }
        public required double StepSeconds { get; init; }
        public required int ClipLength { get; init; }

        /// <summary>The clip was already looping when capture began, so positions wrap around it.</summary>
        public required bool ClipWasRunning { get; init; }

        public required string UndoLabel { get; init; }
        public required long StartedTimestamp { get; init; }

        /// <summary>The clip position the performer heard at <see cref="AnchorTimestamp"/>, unwrapped.</summary>
        public double? AnchorPosition { get; set; }
        public long AnchorTimestamp { get; set; }
        public bool TransportSeenRunning { get; set; }
    }

    // ── IStudioHost ───────────────────────────────────────────────────────────

    public bool IsArmedForRecording(string trackIdentifier) => recordArmedTrackId == trackIdentifier;

    /// <summary>Arms a track for audio recording (one at a time: there is one microphone), or disarms it.</summary>
    public void ToggleRecordArm(string trackIdentifier)
    {
        if (recordArmedTrackId == trackIdentifier)
        {
            DisarmRecording();
        }
        else
        {
            if (activeAudioTake is not null)
            {
                FinishAudioTake();
            }
            recordArmedTrackId = trackIdentifier;
            if (!OpenAudioInput())
            {
                recordArmedTrackId = null;
            }
            else
            {
                var armedTrackName = document.Project.Tracks.FirstOrDefault(track => track.Id == trackIdentifier)?.Name ?? "track";
                SetStatus(StatusSeverity.Informational, $"🎙 {armedTrackName} armed via {audioInputSource?.SubstrateName} — press ● Rec (F9) to record a take");
            }
        }
        RefreshInputMeterVisibility();
        sessionView.Rebuild();
    }

    /// <summary>Adds an empty sampler track to record into, and arms it.</summary>
    public void AddAudioTrack()
    {
        if (document.Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
        {
            SetStatus(StatusSeverity.Warning, "the track budget is exhausted. consolidate headcount before onboarding vocals.");
            return;
        }
        var audioTrack = AudioTakeAssembler.CreateTakeTrack(document.Project, template: null);
        var sceneIndex = document.SelectedSceneIndex;
        document.Edit($"added the audio track {audioTrack.Name}", project =>
        {
            audioTrack.Clips[sceneIndex] = SlawClip.Empty(project.Scenes[sceneIndex], project.SceneLengthInSteps(sceneIndex));
            project.Tracks.Add(audioTrack);
        }, EditScope.Structure);
        document.Select(document.Project.Tracks.Count - 1, sceneIndex);
        ToggleRecordArm(audioTrack.Id);
    }

    // ── Input lifecycle ───────────────────────────────────────────────────────

    private bool OpenAudioInput()
    {
        var requestedConfiguration = $"{preferences.RecordingSubstrate}|{preferences.RecordingInputDevice}";
        if (audioInputSource is not null && audioInputConfiguration == requestedConfiguration)
        {
            return true;
        }
        CloseAudioInput();
        var createdSource = AudioInputFactory.TryCreate(LiveAudioSession.LiveSampleRate, preferences.RecordingSubstrate, preferences.RecordingInputDevice, out var unavailabilityReason);
        if (createdSource is null)
        {
            SetStatus(StatusSeverity.Failure, $"cannot record: {unavailabilityReason}");
            return false;
        }
        try
        {
            createdSource.Start();
        }
        catch (InvalidOperationException inputRemediationOpportunity)
        {
            createdSource.Dispose();
            SetStatus(StatusSeverity.Failure, $"cannot record: {inputRemediationOpportunity.Message}");
            return false;
        }
        createdSource.Faulted += faultDescription => Dispatcher.UIThread.Post(() =>
        {
            if (audioInputSource != createdSource)
            {
                return;
            }
            FinishAudioTake();
            DisarmRecording();
            SetStatus(StatusSeverity.Failure, $"recording input lost: {faultDescription}");
        });
        audioInputSource = createdSource;
        audioInputConfiguration = requestedConfiguration;
        return true;
    }

    private void CloseAudioInput()
    {
        audioInputSource?.Dispose();
        audioInputSource = null;
        audioInputConfiguration = null;
    }

    private void DisarmRecording()
    {
        FinishAudioTake();
        recordArmedTrackId = null;
        CloseAudioInput();
        RefreshInputMeterVisibility();
        sessionView.Rebuild();
    }

    /// <summary>Re-opens the input after Settings changed the tool or device.</summary>
    private void ApplyRecordingPreference()
    {
        if (recordArmedTrackId is not null && activeAudioTake is null && !OpenAudioInput())
        {
            DisarmRecording();
        }
    }

    // ── Takes ─────────────────────────────────────────────────────────────────

    /// <summary>Called when ● Rec changes. Armed audio tracks start (or end) a take with it.</summary>
    private void HandleAudioRecordToggle(bool recordEngaged)
    {
        if (recordEngaged)
        {
            BeginAudioTake(launchIfStopped: true);
        }
        else
        {
            FinishAudioTake();
        }
    }

    private void BeginAudioTake(bool launchIfStopped)
    {
        if (activeAudioTake is not null || recordArmedTrackId is null || !OpenAudioInput())
        {
            return;
        }
        var project = document.Project;
        if (project.Tracks.FirstOrDefault(track => track.Id == recordArmedTrackId) is not { } armedTrack)
        {
            DisarmRecording();
            return;
        }
        var transportSnapshot = liveAudioSession.Engine.Transport;
        var transportPlaying = PlaybackToggle.IsAudiblyPlaying(transportSnapshot);
        if (!transportPlaying && !launchIfStopped)
        {
            return;
        }

        // Record into whatever scene is playing; from a stop, into what is about to play.
        int sceneIndex;
        if (transportPlaying && transportSnapshot.Tracks.TryGetValue(armedTrack.Id, out var armedTrackState) && armedTrackState.PlayingScene is { } armedTrackScene)
        {
            sceneIndex = armedTrackScene;
        }
        else if (transportPlaying && transportSnapshot.Mode == TransportMode.Arrangement && transportSnapshot.ArrangementSection is { } playingSection && playingSection < project.Arrangement.Count)
        {
            sceneIndex = project.Arrangement[playingSection].Scene;
        }
        else if (transportPlaying)
        {
            sceneIndex = transportSnapshot.Tracks.Values.Select(trackState => trackState.PlayingScene).FirstOrDefault(scene => scene is not null) ?? document.SelectedSceneIndex;
        }
        else
        {
            sceneIndex = arrangementViewActive && project.Arrangement.Count > 0 ? project.Arrangement[0].Scene : document.SelectedSceneIndex;
        }
        sceneIndex = Math.Clamp(sceneIndex, 0, Math.Max(0, project.Scenes.Count - 1));

        var takeTrack = armedTrack;
        var undoLabel = $"record audio into {armedTrack.Name} ({Stopwatch.GetTimestamp()})";
        document.BeginEdit(undoLabel);
        if (AudioTakeAssembler.NeedsFreshTrack(armedTrack, sceneIndex) && project.Tracks.Count < ProjectAlignmentRemediator.MaximumTrackCount)
        {
            takeTrack = AudioTakeAssembler.CreateTakeTrack(project, armedTrack);
            project.Tracks.Insert(project.Tracks.IndexOf(armedTrack) + 1, takeTrack);
            recordArmedTrackId = takeTrack.Id;
        }
        while (takeTrack.Clips.Count <= sceneIndex)
        {
            takeTrack.Clips.Add(null);
        }
        // The take needs a clip to play in, so its playhead says where the take starts.
        var clipExisted = takeTrack.Clips[sceneIndex] is not null;
        var takeClip = takeTrack.Clips[sceneIndex] ??= SlawClip.Empty(project.Scenes[sceneIndex], project.SceneLengthInSteps(sceneIndex));
        document.CommitEdit($"armed a take on {takeTrack.Name}", EditScope.Structure, notifyCopilot: false);

        audioInputSource!.BeginTake();
        var clipAlreadyLooping = transportPlaying && (transportSnapshot.Mode == TransportMode.Arrangement || clipExisted &&
            transportSnapshot.Tracks.TryGetValue(takeTrack.Id, out var takeTrackState) && takeTrackState.PlayingScene == sceneIndex);
        activeAudioTake = new AudioTakeInProgress
        {
            TrackId = takeTrack.Id,
            SceneIndex = sceneIndex,
            StepSeconds = project.StepDurationSeconds,
            ClipLength = takeClip.Pattern.Count,
            ClipWasRunning = clipAlreadyLooping,
            UndoLabel = undoLabel,
            StartedTimestamp = Stopwatch.GetTimestamp(),
        };

        if (!transportPlaying)
        {
            if (arrangementViewActive && project.Arrangement.Count > 0)
            {
                PlayArrangementFrom(0);
            }
            else
            {
                LaunchScene(sceneIndex);
            }
        }
        else if (!clipAlreadyLooping && transportSnapshot.Mode == TransportMode.Session)
        {
            // Session clips launch on the bar; the take is pre-roll until then.
            liveAudioSession.Engine.LaunchClip(takeTrack.Id, sceneIndex);
        }
        SetStatus(StatusSeverity.Failure, $"● recording {takeTrack.Name} into \"{project.Scenes[sceneIndex]}\" — stop the transport or ● Rec to end the take");
    }

    private void FinishAudioTake()
    {
        if (activeAudioTake is not { } finishedTake)
        {
            return;
        }
        activeAudioTake = null;
        var capturedTake = audioInputSource?.EndTake();
        var project = document.Project;
        var takeTrack = project.Tracks.FirstOrDefault(track => track.Id == finishedTake.TrackId);
        if (capturedTake is null || takeTrack is null || finishedTake.AnchorPosition is not { } anchorPosition ||
            capturedTake.FirstFrameTimestamp == 0 || capturedTake.Samples.Length < MinimumTakeSeconds * capturedTake.SampleRate)
        {
            SetStatus(StatusSeverity.Warning, "take discarded: nothing was captured while the clip played");
            return;
        }

        // Where the first captured frame sat in the clip, as the performer heard it.
        var anchorSecondsAfterCapture = (finishedTake.AnchorTimestamp - capturedTake.FirstFrameTimestamp) / (double)Stopwatch.Frequency;
        var captureStartPosition = anchorPosition - anchorSecondsAfterCapture / finishedTake.StepSeconds;
        var takePlacement = AudioTakeAssembler.Place(captureStartPosition, finishedTake.ClipWasRunning, finishedTake.ClipLength,
            finishedTake.StepSeconds, preferences.RecordingInputLatencyMilliseconds / 1000.0, capturedTake.SampleRate);
        var takeSamples = AudioTakeAssembler.TrimHead(capturedTake.Samples, takePlacement.TrimFrames);
        if (takeSamples.Length < MinimumTakeSeconds * capturedTake.SampleRate)
        {
            SetStatus(StatusSeverity.Warning, "take discarded: it ended before its first step");
            return;
        }

        var recordingsFolder = Path.Combine(
            document.FilePath is { } projectFilePath ? Path.GetDirectoryName(Path.GetFullPath(projectFilePath))! : Path.GetDirectoryName(StudioPreferences.PreferencesFilePath)!,
            AudioTakeAssembler.RecordingsFolderName);
        var takePath = Path.Combine(recordingsFolder, AudioTakeAssembler.TakeFileName(takeTrack.Name, DateTime.Now));
        try
        {
            AudioTakeAssembler.WriteTakeWave(takePath, takeSamples, capturedTake.SampleRate);
        }
        catch (Exception writeRemediationOpportunity) when (writeRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"take lost: could not write {takePath}: {writeRemediationOpportunity.Message}");
            return;
        }

        // One undo step for the whole take, unless something else was edited while it ran.
        if (document.UndoLabel != finishedTake.UndoLabel)
        {
            document.BeginEdit($"record audio into {takeTrack.Name}");
        }
        AudioTakeAssembler.ApplyTake(project, takeTrack, finishedTake.SceneIndex, takePath, takePlacement.StartStep, takeSamples.Length, capturedTake.SampleRate);
        var takeSeconds = takeSamples.Length / (double)capturedTake.SampleRate;
        document.CommitEdit(string.Create(CultureInfo.InvariantCulture, $"recorded a {takeSeconds:0.0} s take on {takeTrack.Name}"), EditScope.Structure);
        SetStatus(StatusSeverity.Success, string.Create(CultureInfo.InvariantCulture,
            $"take recorded: {takeSeconds:0.0} s from step {takePlacement.StartStep + 1} → {Path.GetFileName(takePath)} — Ctrl+Z removes it"));
    }

    /// <summary>
    /// The armed clip's position as heard right now, without wrapping, or null
    /// until its playhead runs. Same latency model as <see cref="AudiblePlayheadPosition"/>.
    /// </summary>
    private double? UnwrappedAudibleClipPosition(string trackIdentifier, int sceneIndex)
    {
        var transportSnapshot = liveAudioSession.Engine.Transport;
        if (transportSnapshot.Mode == TransportMode.Stopped || transportSnapshot.StepDurationSeconds <= 0 ||
            !transportSnapshot.Tracks.TryGetValue(trackIdentifier, out var trackState) ||
            trackState.PlayingScene != sceneIndex || trackState.ClipStep is not { } renderedStep)
        {
            return null;
        }
        var secondsSincePublished = Stopwatch.GetElapsedTime(transportSnapshot.PublishedTimestamp).TotalSeconds;
        var outputLatencySeconds = (preferences.AudioLatencyMilliseconds + 30) / 1000.0;
        return renderedStep + (secondsSincePublished - outputLatencySeconds) / transportSnapshot.StepDurationSeconds;
    }

    /// <summary>Per display frame: meter, take anchoring, auto start/stop with the transport, status.</summary>
    private void AdvanceRecordingDisplay()
    {
        if (recordArmedTrackId is null)
        {
            return;
        }
        if (document.Project.Tracks.All(track => track.Id != recordArmedTrackId))
        {
            DisarmRecording();
            return;
        }
        var meterLevel = audioInputSource?.PeakLevel ?? 0f;
        inputMeterFill.Width = Math.Sqrt(meterLevel) * InputMeterWidth;
        inputMeterFill.Background = meterLevel >= 0.9f ? StudioTheme.Failure : StudioTheme.Playhead;

        var transportSnapshot = liveAudioSession.Engine.Transport;
        var transportPlaying = PlaybackToggle.IsAudiblyPlaying(transportSnapshot);
        if (activeAudioTake is null)
        {
            // Rec stays engaged across stops, so pressing play again records the next take.
            if (recordToggle?.IsChecked == true && transportPlaying)
            {
                BeginAudioTake(launchIfStopped: false);
            }
            return;
        }

        var takeInProgress = activeAudioTake;
        if (takeInProgress.AnchorPosition is null && UnwrappedAudibleClipPosition(takeInProgress.TrackId, takeInProgress.SceneIndex) is { } heardPosition)
        {
            takeInProgress.AnchorPosition = heardPosition;
            takeInProgress.AnchorTimestamp = Stopwatch.GetTimestamp();
        }
        takeInProgress.TransportSeenRunning |= transportPlaying;
        if (takeInProgress.TransportSeenRunning && transportSnapshot.Mode == TransportMode.Stopped)
        {
            FinishAudioTake();
            return;
        }
        if (Stopwatch.GetElapsedTime(lastRecordingStatusTimestamp).TotalMilliseconds >= 250)
        {
            lastRecordingStatusTimestamp = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(takeInProgress.StartedTimestamp);
            var takeTrackName = document.Project.Tracks.FirstOrDefault(track => track.Id == takeInProgress.TrackId)?.Name ?? "track";
            SetStatus(StatusSeverity.Failure, takeInProgress.AnchorPosition is null
                ? $"● {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00} · {takeTrackName} · waiting for the clip to come around…"
                : $"● {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00} · recording {takeTrackName}");
        }
    }

    // ── Meter ─────────────────────────────────────────────────────────────────

    private Control ComposeInputMeter()
    {
        inputMeter = StudioUi.Row(4,
            StudioUi.Caption("🎙", 11),
            new Border
            {
                Width = InputMeterWidth,
                Height = 8,
                CornerRadius = new CornerRadius(2),
                Background = StudioTheme.SurfaceRaised,
                VerticalAlignment = VerticalAlignment.Center,
                Child = inputMeterFill,
            });
        ToolTip.SetTip(inputMeter, "Input level of the armed track's microphone. Red means clipping: back off, or turn the gain down at the source.");
        inputMeter.IsVisible = false;
        return inputMeter;
    }

    private void RefreshInputMeterVisibility()
    {
        if (inputMeter is not null)
        {
            inputMeter.IsVisible = recordArmedTrackId is not null;
        }
        inputMeterFill.Width = 0;
    }
}
