using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The sample editor proper: waveform, non-destructive edit toolbar, sustain loop,
/// slicing and export. All state lives in the project; this view merely holds
/// opinions about it, which is the healthiest relationship a view can have.
/// </summary>
internal sealed class SampleEditorView : UserControl
{
    private const double MinimumRegionSeconds = 1e-6;
    private const double SliceMergeToleranceSeconds = 0.001;

    private readonly StudioDocument document;
    private readonly IStudioHost host;
    private readonly string trackIdentifier;
    private readonly bool hostedInWindow;

    private readonly StackPanel messagePanel = new() { Spacing = 8, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock messageText = new() { TextWrapping = TextWrapping.Wrap, Foreground = StudioTheme.TextMuted, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button messageLoadButton;
    private readonly DockPanel editorPanel = new();
    private readonly WaveformEditorControl waveform = new();

    private readonly TextBlock fileNameText = new() { FontWeight = FontWeight.Bold, Foreground = StudioTheme.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 };
    private readonly TextBlock fileDetailText = new() { Foreground = StudioTheme.TextMuted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock statusText = new() { Foreground = StudioTheme.Failure, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock infoText = new() { Foreground = StudioTheme.TextMuted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock sliceHintText = new() { Foreground = StudioTheme.TextMuted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button pitchedButton;
    private readonly Button sliceButton;
    private readonly CheckBox loopCheckBox;
    private readonly WrapPanel sliceRow;
    private readonly Slider sensitivitySlider = new() { Minimum = 0, Maximum = 1, Value = 0.5, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(Button Button, bool NeedsSelection)> editButtons = new();
    private readonly Border selectionToolbar = new()
    {
        IsVisible = false,
        Background = StudioTheme.SurfaceRaised,
        BorderBrush = StudioTheme.Accent,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(5),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Bottom,
        BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, OffsetY = 3, Color = Color.FromArgb(150, 0, 0, 0) }),
    };
    private Button revertButton = null!;
    private Button openLargeButton = null!;

    private SampleData? editedSample;
    private string? loadedSamplePath;
    private string? loadedFingerprint;
    private string? loadFailureMessage;
    private (double Start, double End)? selection;
    private double? cursorSeconds;
    private (double Start, double End)? previewRegion;
    private bool synchronizingControls;
    private bool markerDragInProgress;

    public SampleEditorView(StudioDocument document, IStudioHost host, string trackIdentifier, bool hostedInWindow = false)
    {
        this.document = document;
        this.host = host;
        this.trackIdentifier = trackIdentifier;
        this.hostedInWindow = hostedInWindow;

        messageLoadButton = StudioUi.AccentButton("Load sample…", () =>
        {
            if (FindTrack() is var (_, trackIndex))
            {
                host.ChooseSampleFileForTrack(trackIndex);
            }
        });
        pitchedButton = StudioUi.Button("Pitched", () => SetPlaybackMode(SampleEditSettings.PitchedMode), "Repitch the sample across the keyboard", compact: true);
        sliceButton = StudioUi.Button("Slice", () => SetPlaybackMode(SampleEditSettings.SliceMode), "Each note plays its own slice", compact: true);
        loopCheckBox = new CheckBox { Content = "Sustain loop", Foreground = StudioTheme.Text, VerticalAlignment = VerticalAlignment.Center };
        loopCheckBox.IsCheckedChanged += (_, _) =>
        {
            if (!synchronizingControls)
            {
                SetLoopEnabled(loopCheckBox.IsChecked == true);
            }
        };
        sliceRow = BuildSliceRow();

        messagePanel.Children.Add(messageText);
        messagePanel.Children.Add(messageLoadButton);
        BuildEditorPanel();

        var rootPanel = new Grid();
        rootPanel.Children.Add(editorPanel);
        rootPanel.Children.Add(messagePanel);
        Content = rootPanel;
        WireWaveform();
        Refresh();
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    private (SlawTrack Track, int Index)? FindTrack()
    {
        var tracks = document.Project.Tracks;
        for (var trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            if (tracks[trackIndex].Id == trackIdentifier)
            {
                return (tracks[trackIndex], trackIndex);
            }
        }
        return null;
    }

    private double EditedDuration => editedSample?.DurationSeconds ?? 0;

    // ── Construction ──────────────────────────────────────────────────────────

    private static WrapPanel NewWrapRow() => new() { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4, Margin = new Thickness(8, 4) };

    private void BuildEditorPanel()
    {
        var headerRow = NewWrapRow();
        headerRow.Children.Add(fileNameText);
        headerRow.Children.Add(fileDetailText);
        headerRow.Children.Add(StudioUi.Button("Load…", () => { if (FindTrack() is var (_, trackIndex)) { host.ChooseSampleFileForTrack(trackIndex); } }, "Replace the sample", compact: true));
        headerRow.Children.Add(StudioUi.Button("▶ Selection", PreviewSelection, "Audition the selection (or the start–end region)", compact: true));
        headerRow.Children.Add(StudioUi.Button("▶ All", PreviewAll, "Audition the whole edited sample", compact: true));
        headerRow.Children.Add(StudioUi.Button("■", StopPreview, "Stop the audition", compact: true));
        openLargeButton = StudioUi.Button("⤢ Open large", () => host.OpenSampleEditorWindow(trackIdentifier), "Open in its own window", compact: true);
        openLargeButton.IsVisible = !hostedInWindow;
        headerRow.Children.Add(openLargeButton);
        headerRow.Children.Add(StudioUi.Button("Save as WAV…", () => _ = ExportAsync(bake: false), "Export the edited sample", compact: true));
        headerRow.Children.Add(StudioUi.Button("Bake…", () => _ = ExportAsync(bake: true), "Export, then use the export as the source and clear the edit list", compact: true));

        var editRow = NewWrapRow();
        AddEditButton(editRow, "Trim", true, () => ApplyOperation("trim", 0, "trim sample"), "Keep only the selection");
        AddEditButton(editRow, "Delete", true, () => ApplyOperation("delete", 0, "delete selection"), "Remove the selection and close the gap");
        AddEditButton(editRow, "Silence", true, () => ApplyOperation("silence", 0, "silence selection"), "Mute the selection");
        AddEditButton(editRow, "Reverse", false, () => ApplyOperation("reverse", 0, "reverse sample"), "Reverse the selection (or everything)");
        AddEditButton(editRow, "Normalize", false, () => ApplyOperation("normalize", -0.3, "normalize sample"), "Peak-normalize to -0.3 dBFS");
        AddEditButton(editRow, "−3 dB", true, () => ApplyOperation("gain", -3, "reduce gain"), "Quieter selection");
        AddEditButton(editRow, "+3 dB", true, () => ApplyOperation("gain", 3, "increase gain"), "Louder selection");
        AddEditButton(editRow, "Fade in", false, () => ApplyOperation("fadeIn", 0, "fade in"), "Fade in across the selection (or everything)");
        AddEditButton(editRow, "Fade out", false, () => ApplyOperation("fadeOut", 0, "fade out"), "Fade out across the selection (or everything)");
        AddEditButton(editRow, "Duplicate", true, () => ApplyOperation("duplicate", 0, "duplicate selection"), "Repeat the selection right after itself");
        revertButton = StudioUi.Button("Revert to original", RevertToOriginal, "Throw away every edit", compact: true);
        editRow.Children.Add(revertButton);

        var modeRow = NewWrapRow();
        modeRow.Children.Add(StudioUi.Row(0, pitchedButton, sliceButton));
        modeRow.Children.Add(StudioUi.VerticalSeparator());
        modeRow.Children.Add(loopCheckBox);
        modeRow.Children.Add(StudioUi.Caption("(loops while a note is held — turn on Note Length on the device)", 11));

        var topPanel = new StackPanel { Spacing = 0 };
        topPanel.Children.Add(headerRow);
        topPanel.Children.Add(statusText);
        topPanel.Children.Add(editRow);
        topPanel.Children.Add(modeRow);
        topPanel.Children.Add(sliceRow);
        DockPanel.SetDock(topPanel, Dock.Top);

        var footerRow = NewWrapRow();
        footerRow.Children.Add(infoText);
        footerRow.Children.Add(StudioUi.Button("−", () => waveform.ZoomBy(2.0, CurrentZoomAnchor()), "Zoom out", compact: true));
        footerRow.Children.Add(StudioUi.Button("+", () => waveform.ZoomBy(0.5, CurrentZoomAnchor()), "Zoom in", compact: true));
        footerRow.Children.Add(StudioUi.Button("Fit", () => waveform.ZoomToFit(), "Show the whole sample", compact: true));
        footerRow.Children.Add(StudioUi.Button("Zoom to selection", () => waveform.ZoomToSelection(), "Fill the view with the selection", compact: true));
        DockPanel.SetDock(footerRow, Dock.Bottom);

        selectionToolbar.Child = StudioUi.Row(4,
            StudioUi.Button("▶ Listen", PreviewSelection, "Hear the selection", compact: true),
            StudioUi.AccentButton("🎹 Play on keys", PlaySelectionOnKeys, "Put the selection on a new track and play it on the keyboard, pitched. The original stays as it is.", compact: true),
            StudioUi.Button("Trim", () => ApplyOperation("trim", 0, "trim sample"), "Keep only the selection on this track", compact: true));

        waveform.MinHeight = 120;
        editorPanel.Children.Add(topPanel);
        editorPanel.Children.Add(footerRow);
        editorPanel.Children.Add(new Panel { Children = { waveform, selectionToolbar } });
    }

    /// <summary>Keeps the floating toolbar centred under the selection, clear of the marker handles along the top, while either one moves.</summary>
    private void PositionSelectionToolbar()
    {
        var hasSelection = selection is { } shownSelection && shownSelection.End - shownSelection.Start > MinimumRegionSeconds;
        selectionToolbar.IsVisible = hasSelection;
        if (selection is not { } activeSelection || !hasSelection)
        {
            return;
        }
        selectionToolbar.Measure(Size.Infinity);
        var toolbarWidth = selectionToolbar.DesiredSize.Width;
        var selectionCentre = (waveform.ViewXOf(activeSelection.Start) + waveform.ViewXOf(activeSelection.End)) / 2;
        var toolbarLeft = Math.Clamp(selectionCentre - toolbarWidth / 2, 4, Math.Max(4, waveform.Bounds.Width - toolbarWidth - 4));
        selectionToolbar.Margin = new Thickness(toolbarLeft, 0, 0, WaveformEditorControl.BodyBottomInset + 6);
    }

    private void PlaySelectionOnKeys()
    {
        if (selection is { } activeSelection && activeSelection.End - activeSelection.Start > MinimumRegionSeconds)
        {
            host.PlaySliceOnKeys(trackIdentifier, activeSelection.Start, activeSelection.End);
        }
    }

    /// <summary>Right-click in Slice mode: what can be done with the slice under the pointer.</summary>
    private void ShowSliceMenu(double positionSeconds, int? sliceLineIndex)
    {
        if (FindTrack() is not var (track, _) || track.SampleEditing is not { PlaybackMode: SampleEditSettings.SliceMode, SlicePointsSeconds.Count: > 0 } settings ||
            SliceTrackAssembler.SliceAt(settings.SlicePointsSeconds, EditedDuration, positionSeconds) is not { } slice)
        {
            return;
        }
        MenuItem MenuEntry(string header, Action onClick)
        {
            var menuEntry = new MenuItem { Header = header };
            menuEntry.Click += (_, _) => onClick();
            return menuEntry;
        }
        var sliceMenu = new MenuFlyout();
        sliceMenu.Items.Add(MenuEntry("🎹 Play this slice on keys", () => host.PlaySliceOnKeys(trackIdentifier, slice.Start, slice.End)));
        sliceMenu.Items.Add(MenuEntry("▶ Listen to this slice", () => PreviewRegion(slice.Start, slice.End)));
        if (sliceLineIndex is { } lineIndex)
        {
            sliceMenu.Items.Add(new Separator());
            sliceMenu.Items.Add(MenuEntry("Remove this slice line", () => RemoveSliceLine(lineIndex)));
        }
        sliceMenu.ShowAt(waveform, showAtPointer: true);
    }

    private void RemoveSliceLine(int sliceIndex) => document.Edit("remove slice", project =>
    {
        if (FindTrack() is var (track, _) && track.SampleEditing is { } settings && sliceIndex >= 0 && sliceIndex < settings.SlicePointsSeconds.Count)
        {
            settings.SlicePointsSeconds.RemoveAt(sliceIndex);
        }
    });

    private double CurrentZoomAnchor() => waveform.VisibleStartSeconds + waveform.VisibleDurationSeconds / 2;

    private void AddEditButton(Panel row, string caption, bool needsSelection, Action onClick, string toolTip)
    {
        var editButton = StudioUi.Button(caption, onClick, toolTip, compact: true);
        editButtons.Add((editButton, needsSelection));
        row.Children.Add(editButton);
    }

    private WrapPanel BuildSliceRow()
    {
        var row = NewWrapRow();
        row.Children.Add(StudioUi.Button("÷ 4", () => SliceEqually(4), "Four equal slices across the start–end region", compact: true));
        row.Children.Add(StudioUi.Button("÷ 8", () => SliceEqually(8), "Eight equal slices", compact: true));
        row.Children.Add(StudioUi.Button("÷ 16", () => SliceEqually(16), "Sixteen equal slices", compact: true));
        row.Children.Add(StudioUi.VerticalSeparator());
        row.Children.Add(StudioUi.Button("Detect transients", DetectSlices, "Slice at detected onsets", compact: true));
        row.Children.Add(StudioUi.Caption("sensitivity", 11));
        row.Children.Add(sensitivitySlider);
        row.Children.Add(StudioUi.Button("Clear slices", ClearSlices, "Remove every slice point", compact: true));
        if (host.AiFeaturesEnabled)
        {
            row.Children.Add(StudioUi.AiButton("✨ Auto-chop", () => { if (FindTrack() is var (_, trackIndex)) { host.AutoChopWithAi(trackIndex); } }, "Let the model choose slice points"));
        }
        row.Children.Add(sliceHintText);
        return row;
    }

    private void WireWaveform()
    {
        waveform.MarkerDragStarted += () =>
        {
            markerDragInProgress = true;
            document.BeginEdit("move marker");
        };
        waveform.MarkerMoved += OnMarkerMoved;
        waveform.MarkerDragCompleted += () => markerDragInProgress = false;
        waveform.SliceAddRequested += slicePositionSeconds => document.Edit("add slice", project =>
        {
            if (FindTrack() is not var (track, _) || EditedDuration <= 0)
            {
                return;
            }
            var settings = track.SampleEditing ??= new SampleEditSettings();
            if (settings.SlicePointsSeconds.Any(existingPoint => Math.Abs(existingPoint - slicePositionSeconds) < SliceMergeToleranceSeconds))
            {
                return;
            }
            settings.SlicePointsSeconds.Add(Math.Clamp(slicePositionSeconds, 0, EditedDuration));
            settings.SlicePointsSeconds.Sort();
        });
        waveform.SliceRemoveRequested += RemoveSliceLine;
        waveform.WaveformRightClicked += ShowSliceMenu;
        waveform.ViewportChanged += PositionSelectionToolbar;
        waveform.SizeChanged += (_, _) => PositionSelectionToolbar();
        waveform.SelectionChanged += newSelection =>
        {
            selection = newSelection;
            UpdateInfoLine();
            UpdateToolbarEnabledState();
        };
        waveform.CursorPlaced += placedCursorSeconds =>
        {
            cursorSeconds = placedCursorSeconds;
            UpdateInfoLine();
        };
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    /// <summary>Re-reads the track and brings every control into agreement with it. Cheap unless the audio changed.</summary>
    public void Refresh()
    {
        var located = FindTrack();
        if (located is not var (track, _) || track.Instrument != "sampler")
        {
            ShowMessage("Select a sampler track to edit its sample.", showLoadButton: false, failure: false);
            return;
        }
        if (string.IsNullOrEmpty(track.Sample))
        {
            editedSample = null;
            loadedSamplePath = null;
            loadedFingerprint = null;
            ShowMessage("This sampler has no sample yet.", showLoadButton: true, failure: false);
            return;
        }

        var currentFingerprint = SampleEditProcessor.Fingerprint(track.SampleEditing);
        if (track.Sample != loadedSamplePath || currentFingerprint != loadedFingerprint)
        {
            ReloadSample(track, currentFingerprint);
        }
        if (editedSample is null)
        {
            ShowMessage(loadFailureMessage ?? "The sample could not be loaded.", showLoadButton: true, failure: true);
            return;
        }

        messagePanel.IsVisible = false;
        editorPanel.IsVisible = true;
        SynchronizeFromModel(track);
    }

    private void ReloadSample(SlawTrack track, string currentFingerprint)
    {
        var samplePathChanged = track.Sample != loadedSamplePath;
        var previousDuration = EditedDuration;
        loadedSamplePath = track.Sample;
        loadedFingerprint = currentFingerprint;
        previewRegion = null;
        try
        {
            editedSample = SampleEditProcessor.LoadProcessedNative(track.Sample!, track.SampleEditing);
            loadFailureMessage = null;
            waveform.SetWaveform(editedSample.Samples, editedSample.SampleRate);
            if (samplePathChanged || Math.Abs(previousDuration - editedSample.DurationSeconds) > 1e-6)
            {
                waveform.ZoomToFit();
            }
        }
        catch (Exception remediationOpportunity) when (remediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            editedSample = null;
            loadFailureMessage = remediationOpportunity.Message;
        }
    }

    private void ShowMessage(string message, bool showLoadButton, bool failure)
    {
        messageText.Text = message;
        messageText.Foreground = failure ? StudioTheme.Failure : StudioTheme.TextMuted;
        messageLoadButton.IsVisible = showLoadButton;
        messagePanel.IsVisible = true;
        editorPanel.IsVisible = false;
    }

    private void SynchronizeFromModel(SlawTrack track)
    {
        var duration = EditedDuration;
        var settings = track.SampleEditing;
        synchronizingControls = true;
        try
        {
            fileNameText.Text = Path.GetFileName(track.Sample);
            fileDetailText.Text = string.Create(CultureInfo.InvariantCulture,
                $"{duration:0.000} s · {editedSample!.SampleRate} Hz · {settings?.Operations.Count ?? 0} edit(s)");

            var startFraction = track.InstrumentParameters.GetValueOrDefault("start", 0);
            var endFraction = track.InstrumentParameters.GetValueOrDefault("end", 1);
            waveform.StartMarkerSeconds = startFraction * duration;
            waveform.EndMarkerSeconds = endFraction * duration;

            var loopEnabled = settings?.LoopEnabled == true;
            waveform.LoopVisible = loopEnabled;
            waveform.LoopStartSeconds = settings?.LoopStartSeconds ?? 0;
            waveform.LoopEndSeconds = settings?.LoopEndSeconds ?? 0;
            loopCheckBox.IsChecked = loopEnabled;

            var sliceMode = settings?.PlaybackMode == SampleEditSettings.SliceMode;
            waveform.SlicesVisible = sliceMode;
            waveform.SlicePoints = settings is null ? Array.Empty<double>() : settings.SlicePointsSeconds.ToArray();
            sliceRow.IsVisible = sliceMode;
            SetToggleLook(pitchedButton, !sliceMode);
            SetToggleLook(sliceButton, sliceMode);

            var rootNote = (int)Math.Round(track.InstrumentParameters.GetValueOrDefault("root", NotationResolver.DefaultTriggerMidiNote));
            sliceHintText.Text = $"Slice 1 plays on {NotationResolver.FormatMidiNote(rootNote)}, slice 2 on {NotationResolver.FormatMidiNote(rootNote + 1)}, …";

            if (selection is { } currentSelection && (currentSelection.End > duration + 1e-9 || currentSelection.Start >= duration))
            {
                selection = null;
                waveform.Selection = null;
            }
        }
        finally
        {
            synchronizingControls = false;
        }
        UpdateInfoLine();
        UpdateToolbarEnabledState();
    }

    private static void SetToggleLook(Button toggleButton, bool active)
    {
        if (active && !toggleButton.Classes.Contains("accent"))
        {
            toggleButton.Classes.Add("accent");
        }
        else if (!active)
        {
            toggleButton.Classes.Remove("accent");
        }
    }

    private void UpdateInfoLine()
    {
        var text = selection is { } activeSelection
            ? string.Create(CultureInfo.InvariantCulture, $"selection {activeSelection.Start:0.000} – {activeSelection.End:0.000} s ({activeSelection.End - activeSelection.Start:0.000} s)")
            : "no selection — drag on the waveform to select, then play it on the keys · ctrl+wheel zoom · double-click to add a slice";
        if (cursorSeconds is { } placedCursor)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · cursor {placedCursor:0.000} s");
        }
        infoText.Text = text;
    }

    private void UpdateToolbarEnabledState()
    {
        var hasSelection = selection is { } activeSelection && activeSelection.End - activeSelection.Start > MinimumRegionSeconds;
        foreach (var (editButton, needsSelection) in editButtons)
        {
            editButton.IsEnabled = !needsSelection || hasSelection;
        }
        revertButton.IsEnabled = FindTrack() is var (track, _) && track.SampleEditing is { Operations.Count: > 0 };
        PositionSelectionToolbar();
    }

    // ── Playback ──────────────────────────────────────────────────────────────

    /// <summary>Called by the host about 30 times a second: follows the audition, if this view started it.</summary>
    public void UpdatePreviewPlayhead()
    {
        if (previewRegion is { } region && host.PreviewProgress is { } progress)
        {
            waveform.PlayheadSeconds = region.Start + Math.Clamp(progress, 0, 1) * (region.End - region.Start);
            return;
        }
        previewRegion = null;
        if (waveform.PlayheadSeconds is not null)
        {
            waveform.PlayheadSeconds = null;
        }
    }

    private void PreviewSelection()
    {
        if (selection is { } activeSelection && activeSelection.End - activeSelection.Start > MinimumRegionSeconds)
        {
            PreviewRegion(activeSelection.Start, activeSelection.End);
            return;
        }
        if (waveform.EndMarkerSeconds - waveform.StartMarkerSeconds > MinimumRegionSeconds)
        {
            PreviewRegion(waveform.StartMarkerSeconds, waveform.EndMarkerSeconds);
            return;
        }
        PreviewAll();
    }

    private void PreviewAll() => PreviewRegion(0, EditedDuration);

    private void PreviewRegion(double startSeconds, double endSeconds)
    {
        if (editedSample is null)
        {
            return;
        }
        var sampleRate = editedSample.SampleRate;
        var startFrame = Math.Clamp((int)Math.Round(startSeconds * sampleRate), 0, editedSample.Samples.Length);
        var endFrame = Math.Clamp((int)Math.Round(endSeconds * sampleRate), 0, editedSample.Samples.Length);
        if (endFrame <= startFrame)
        {
            return;
        }
        var regionSamples = editedSample.Samples[startFrame..endFrame];
        previewRegion = ((double)startFrame / sampleRate, (double)endFrame / sampleRate);
        host.PreviewSampleBuffer(SampleLibrary.ToStereoBuffer(new SampleData(regionSamples, sampleRate, editedSample.SourcePath)));
    }

    private void StopPreview()
    {
        previewRegion = null;
        waveform.PlayheadSeconds = null;
        host.PreviewSampleBuffer(new StereoBuffer(1, editedSample?.SampleRate ?? 44100));
    }

    // ── Marker, slice and loop edits ──────────────────────────────────────────

    private void OnMarkerMoved(WaveformMarkerKind markerKind, int markerIndex, double positionSeconds)
    {
        if (!markerDragInProgress || FindTrack() is not var (track, _) || EditedDuration <= 0)
        {
            return;
        }
        var duration = EditedDuration;
        var settings = track.SampleEditing ??= new SampleEditSettings();
        var fraction = Math.Clamp(positionSeconds / duration, 0, 1);
        switch (markerKind)
        {
            case WaveformMarkerKind.Start:
                track.InstrumentParameters["start"] = Math.Min(fraction, track.InstrumentParameters.GetValueOrDefault("end", 1) - 0.001);
                break;
            case WaveformMarkerKind.End:
                track.InstrumentParameters["end"] = Math.Max(fraction, track.InstrumentParameters.GetValueOrDefault("start", 0) + 0.001);
                break;
            case WaveformMarkerKind.LoopStart:
                settings.LoopStartSeconds = Math.Clamp(positionSeconds, 0, duration);
                break;
            case WaveformMarkerKind.LoopEnd:
                settings.LoopEndSeconds = Math.Clamp(positionSeconds, 0, duration);
                break;
            case WaveformMarkerKind.Slice:
                if (markerIndex >= 0 && markerIndex < settings.SlicePointsSeconds.Count)
                {
                    settings.SlicePointsSeconds[markerIndex] = Math.Clamp(positionSeconds, 0, duration);
                }
                break;
        }
        document.CommitEdit("move marker", EditScope.Content, notifyCopilot: false);
    }

    private void SetPlaybackMode(string playbackMode)
    {
        if (synchronizingControls || FindTrack() is not var (track, _) || (track.SampleEditing?.PlaybackMode ?? SampleEditSettings.PitchedMode) == playbackMode)
        {
            return;
        }
        document.Edit($"{playbackMode} playback mode", _ => (track.SampleEditing ??= new SampleEditSettings()).PlaybackMode = playbackMode);
    }

    private void SetLoopEnabled(bool loopEnabled)
    {
        if (FindTrack() is not var (track, _) || EditedDuration <= 0)
        {
            return;
        }
        document.Edit(loopEnabled ? "enable sustain loop" : "disable sustain loop", _ =>
        {
            var settings = track.SampleEditing ??= new SampleEditSettings();
            settings.LoopEnabled = loopEnabled;
            if (loopEnabled && settings.LoopEndSeconds - settings.LoopStartSeconds <= MinimumRegionSeconds)
            {
                var regionStart = track.InstrumentParameters.GetValueOrDefault("start", 0) * EditedDuration;
                var regionEnd = track.InstrumentParameters.GetValueOrDefault("end", 1) * EditedDuration;
                var regionThird = (regionEnd - regionStart) / 3;
                settings.LoopStartSeconds = regionStart + regionThird;
                settings.LoopEndSeconds = regionStart + 2 * regionThird;
            }
        });
    }

    private void SliceEqually(int sliceCount)
    {
        if (FindTrack() is not var (track, _) || EditedDuration <= 0)
        {
            return;
        }
        var regionStart = track.InstrumentParameters.GetValueOrDefault("start", 0) * EditedDuration;
        var regionEnd = track.InstrumentParameters.GetValueOrDefault("end", 1) * EditedDuration;
        document.Edit($"slice into {sliceCount}", _ =>
        {
            var settings = track.SampleEditing ??= new SampleEditSettings();
            settings.SlicePointsSeconds = Enumerable.Range(1, sliceCount - 1)
                .Select(sliceNumber => regionStart + (regionEnd - regionStart) * sliceNumber / sliceCount)
                .ToList();
        });
    }

    private void DetectSlices()
    {
        if (FindTrack() is not var (track, _) || editedSample is null)
        {
            return;
        }
        var sampleRate = editedSample.SampleRate;
        var regionStartFrame = Math.Clamp((int)Math.Round(track.InstrumentParameters.GetValueOrDefault("start", 0) * editedSample.Samples.Length), 0, editedSample.Samples.Length);
        var regionEndFrame = Math.Clamp((int)Math.Round(track.InstrumentParameters.GetValueOrDefault("end", 1) * editedSample.Samples.Length), regionStartFrame, editedSample.Samples.Length);
        var regionOffsetSeconds = (double)regionStartFrame / sampleRate;
        var detected = SampleEditProcessor.DetectTransients(editedSample.Samples[regionStartFrame..regionEndFrame], sampleRate, sensitivitySlider.Value)
            .Select(onsetSeconds => onsetSeconds + regionOffsetSeconds)
            .Where(onsetSeconds => onsetSeconds > regionOffsetSeconds + SliceMergeToleranceSeconds)
            .OrderBy(onsetSeconds => onsetSeconds)
            .ToList();
        document.Edit("detect transients", _ => (track.SampleEditing ??= new SampleEditSettings()).SlicePointsSeconds = detected);
    }

    private void ClearSlices()
    {
        if (FindTrack() is var (track, _) && track.SampleEditing is { SlicePointsSeconds.Count: > 0 })
        {
            document.Edit("clear slices", _ => track.SampleEditing!.SlicePointsSeconds.Clear());
        }
    }

    // ── Destructive-looking (but not) edits ───────────────────────────────────

    private void RevertToOriginal()
    {
        if (FindTrack() is var (track, _) && track.SampleEditing is { Operations.Count: > 0 })
        {
            selection = null;
            waveform.Selection = null;
            document.Edit("revert to original", _ => track.SampleEditing!.Operations.Clear());
        }
    }

    private void ApplyOperation(string operationType, double amount, string label)
    {
        if (FindTrack() is not var (track, _) || editedSample is null)
        {
            return;
        }
        var oldDuration = EditedDuration;
        var needsSelection = operationType is "trim" or "delete" or "duplicate";
        var regionStart = selection?.Start ?? 0;
        var regionEnd = selection?.End ?? oldDuration;
        regionStart = Math.Clamp(regionStart, 0, oldDuration);
        regionEnd = Math.Clamp(regionEnd, 0, oldDuration);
        var regionLength = regionEnd - regionStart;
        if (regionLength <= MinimumRegionSeconds || (needsSelection && selection is null)
            || (operationType == "delete" && regionLength >= oldDuration - MinimumRegionSeconds))
        {
            return;
        }

        var newDuration = operationType switch
        {
            "trim" => regionLength,
            "delete" => oldDuration - regionLength,
            "duplicate" => oldDuration + regionLength,
            _ => oldDuration,
        };
        Func<double, double> mapPosition = operationType switch
        {
            "trim" => position => Math.Clamp(position - regionStart, 0, newDuration),
            "delete" => position => position <= regionStart ? position : position >= regionEnd ? position - regionLength : regionStart,
            "duplicate" => position => position >= regionEnd ? position + regionLength : position,
            _ => position => position,
        };
        Func<double, bool> keepSlice = operationType switch
        {
            "trim" => position => position > regionStart && position < regionEnd,
            "delete" => position => position <= regionStart || position >= regionEnd,
            _ => _ => true,
        };

        document.BeginEdit(label);
        var settings = track.SampleEditing ??= new SampleEditSettings();
        settings.Operations.Add(new SampleEditOperation { Type = operationType, StartSeconds = regionStart, EndSeconds = regionEnd, Amount = amount });
        if (operationType is "trim" or "delete" or "duplicate")
        {
            RemapPositions(track, settings, oldDuration, newDuration, mapPosition, keepSlice);
            if (operationType != "duplicate")
            {
                selection = null;
                waveform.Selection = null;
            }
        }
        document.CommitEdit(label, EditScope.Content, notifyCopilot: true);
    }

    private static void RemapPositions(SlawTrack track, SampleEditSettings settings, double oldDuration, double newDuration,
        Func<double, double> mapPosition, Func<double, bool> keepSlice)
    {
        settings.SlicePointsSeconds = settings.SlicePointsSeconds
            .Where(keepSlice)
            .Select(mapPosition)
            .Where(position => position > SliceMergeToleranceSeconds && position < newDuration - SliceMergeToleranceSeconds)
            .Distinct()
            .OrderBy(position => position)
            .ToList();

        settings.LoopStartSeconds = mapPosition(settings.LoopStartSeconds);
        settings.LoopEndSeconds = mapPosition(settings.LoopEndSeconds);
        if (settings.LoopEndSeconds - settings.LoopStartSeconds <= MinimumRegionSeconds)
        {
            settings.LoopEnabled = false;
            settings.LoopStartSeconds = 0;
            settings.LoopEndSeconds = 0;
        }

        var startFraction = Math.Clamp(mapPosition(track.InstrumentParameters.GetValueOrDefault("start", 0) * oldDuration) / newDuration, 0, 1);
        var endFraction = Math.Clamp(mapPosition(track.InstrumentParameters.GetValueOrDefault("end", 1) * oldDuration) / newDuration, 0, 1);
        if (endFraction - startFraction < 0.001)
        {
            startFraction = 0;
            endFraction = 1;
        }
        startFraction = Math.Min(startFraction, 0.99);
        endFraction = Math.Max(endFraction, 0.01);
        if (track.InstrumentParameters.ContainsKey("start") || startFraction > 1e-9)
        {
            track.InstrumentParameters["start"] = startFraction;
        }
        if (track.InstrumentParameters.ContainsKey("end") || endFraction < 1 - 1e-9)
        {
            track.InstrumentParameters["end"] = endFraction;
        }
    }

    // ── Export ────────────────────────────────────────────────────────────────

    private async Task ExportAsync(bool bake)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || editedSample is null || FindTrack() is not var (track, _))
        {
            return;
        }
        var exportedSample = editedSample;
        var suggestedStem = Path.GetFileNameWithoutExtension(track.Sample) ?? "sample";
        try
        {
            var chosenFile = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = bake ? "Bake the edited sample to a new WAV" : "Save the edited sample as WAV",
                SuggestedFileName = bake ? $"{suggestedStem}-baked.wav" : $"{suggestedStem}-edit.wav",
                DefaultExtension = "wav",
                FileTypeChoices = new[] { new FilePickerFileType("WAV audio") { Patterns = new[] { "*.wav" } } },
            });
            var exportPath = chosenFile?.TryGetLocalPath();
            if (exportPath is null)
            {
                return;
            }
            WaveFileMaterializer.Write(exportPath, SampleLibrary.ToStereoBuffer(exportedSample));
            statusText.IsVisible = false;
            if (bake)
            {
                var bakedPath = Path.GetFullPath(exportPath);
                document.Edit("bake sample edits", _ =>
                {
                    track.Sample = bakedPath;
                    track.SampleEditing?.Operations.Clear();
                });
            }
        }
        catch (Exception remediationOpportunity) when (remediationOpportunity is IOException or UnauthorizedAccessException)
        {
            statusText.Text = $"Export failed: {remediationOpportunity.Message}";
            statusText.IsVisible = true;
        }
    }
}
