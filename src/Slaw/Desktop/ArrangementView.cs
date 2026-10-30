using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The Arrangement view: the song as one timeline where clips sit at any step on
/// any track. Track headers on the left, a custom-drawn canvas (ruler, lanes,
/// clips) on the right, a horizontal scroll bar below. Selecting a clip opens it
/// in the detail view, envelopes and all. Session clips launched while it plays
/// take their track over (dimmed lane, orange ▶) until Back to Arrangement.
/// </summary>
internal sealed class ArrangementView : UserControl
{
    private readonly StudioDocument document;
    private readonly IStudioHost host;
    private readonly ArrangementCanvas timelineCanvas;
    private readonly StackPanel trackHeaderColumn = new() { Spacing = ArrangementCanvas.LaneSpacing };
    private readonly TextBlock timelineSummary = StudioUi.Caption(string.Empty, 10);
    private readonly ScrollBar horizontalScrollBar = new() { Orientation = Orientation.Horizontal, Minimum = 0, Margin = new Thickness(ArrangementCanvas.TrackHeaderWidth + 12, 2, 12, 4) };
    private readonly Button loopButton;
    private readonly Dictionary<string, Control> sessionOverrideIndicators = new();
    private bool synchronizingScrollBar;

    /// <summary>
    /// True when a track header was the last thing clicked here (and no arrangement clip
    /// is selected since): Delete then removes that track instead of a clip.
    /// </summary>
    public bool TrackHeaderSelected { get; private set; }

    public ArrangementView(StudioDocument document, IStudioHost host)
    {
        this.document = document;
        this.host = host;
        timelineCanvas = new ArrangementCanvas(document, host);
        timelineCanvas.ViewportChanged += SynchronizeScrollBar;
        timelineCanvas.AddHandler(PointerPressedEvent, (_, _) => TrackHeaderSelected = false, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        horizontalScrollBar.ValueChanged += (_, _) =>
        {
            if (!synchronizingScrollBar)
            {
                timelineCanvas.ScrollStep = horizontalScrollBar.Value;
            }
        };

        loopButton = StudioUi.Button("⟲ Loop", ToggleLoop, "Loop brace on/off: drag across the ruler to set it", compact: true);
        var placeSceneButton = new Button { Content = "＋ Scene at cursor ▾", Padding = new Thickness(8, 2), FontSize = 12 };
        ToolTip.SetTip(placeSceneButton, "Place a session scene's clips in the arrangement at the ruler cursor");
        var placeSceneMenu = new MenuFlyout();
        placeSceneMenu.Opening += (_, _) =>
        {
            placeSceneMenu.Items.Clear();
            for (var sceneIndex = 0; sceneIndex < document.Project.Scenes.Count; sceneIndex++)
            {
                var capturedSceneIndex = sceneIndex;
                var sceneItem = new MenuItem { Header = document.Project.Scenes[sceneIndex] };
                sceneItem.Click += (_, _) => host.PlaceSceneInArrangement(capturedSceneIndex);
                placeSceneMenu.Items.Add(sceneItem);
            }
        };
        placeSceneButton.Flyout = placeSceneMenu;
        var toolbar = StudioUi.Row(6,
            StudioUi.Caption("Arrangement", 12, StudioTheme.Text, FontWeight.Bold),
            timelineSummary,
            new Border { Width = 12 },
            StudioUi.Button("▶ From cursor", () => host.PlayTimelineFrom(timelineCanvas.CursorStep), "Play the arrangement from the ruler cursor (Space)", compact: true),
            StudioUi.Button("⇤", () => MoveCursor(0), "Cursor to the start", compact: true),
            loopButton,
            StudioUi.Button("−", () => timelineCanvas.Zoom(1 / 1.5, null), "Zoom out (Ctrl+wheel)", compact: true),
            StudioUi.Button("+", () => timelineCanvas.Zoom(1.5, null), "Zoom in (Ctrl+wheel)", compact: true),
            new Border { Width = 12 },
            placeSceneButton);
        toolbar.Margin = new Thickness(12, 8, 12, 4);

        var laneGrid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{ArrangementCanvas.TrackHeaderWidth},*"), ColumnSpacing = 0, Margin = new Thickness(12, 0, 12, 0) };
        var headerStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                new Border { Height = ArrangementCanvas.RulerHeight + ArrangementCanvas.LaneSpacing },
                trackHeaderColumn,
            },
        };
        laneGrid.Children.Add(headerStack);
        Grid.SetColumn(timelineCanvas, 1);
        timelineCanvas.VerticalAlignment = VerticalAlignment.Top;
        laneGrid.Children.Add(timelineCanvas);

        var hint = new TextBlock
        {
            Text = "ruler: click sets the cursor · double-click plays · drag sets the loop · lanes: double-click adds a bar · drag moves (snaps to beats; Shift: bars, Alt: steps) · drag the right edge to loop the pattern longer · Ctrl+drag copies · right-click for more · Ctrl+wheel zooms · launch session clips while it plays to take a track over",
            FontSize = 10,
            Foreground = StudioTheme.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 2, 14, 8),
        };
        var verticalScroller = new ScrollViewer
        {
            Content = laneGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        DockPanel.SetDock(horizontalScrollBar, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(hint);
        layout.Children.Add(horizontalScrollBar);
        layout.Children.Add(verticalScroller);
        Content = layout;
        Rebuild();
    }

    /// <summary>Where Space starts playback in this view.</summary>
    public long CursorStep => timelineCanvas.CursorStep;

    public void Rebuild()
    {
        trackHeaderColumn.Children.Clear();
        sessionOverrideIndicators.Clear();
        for (var trackIndex = 0; trackIndex < document.Project.Tracks.Count; trackIndex++)
        {
            trackHeaderColumn.Children.Add(ComposeTrackHeader(document.Project.Tracks[trackIndex], trackIndex));
        }
        RefreshContent();
    }

    public void RefreshContent()
    {
        var project = document.Project;
        var timelineSteps = FreeTimeline.LengthInSteps(project);
        var timelineSeconds = timelineSteps * project.StepDurationSeconds;
        timelineSummary.Text = project.Timeline.Count == 0
            ? "empty — double-click a lane, drop a session clip or scene here, or place a scene at the cursor"
            : FormattableString.Invariant($"{project.Timeline.Count} clips · {timelineSteps / 16.0:0.#} bars · {(int)timelineSeconds / 60}:{(int)timelineSeconds % 60:00}");
        loopButton.Content = project.TimelineLoop is { IsValid: true } loopRegion
            ? $"⟲ Loop {FreeTimeline.FormatPosition(loopRegion.StartStep)}–{FreeTimeline.FormatPosition(loopRegion.EndStep)}"
            : "⟲ Loop off";
        timelineCanvas.Height = ArrangementCanvas.RulerHeight + ArrangementCanvas.LaneSpacing +
                                project.Tracks.Count * (ArrangementCanvas.LaneHeight + ArrangementCanvas.LaneSpacing);
        timelineCanvas.InvalidateVisual();
        SynchronizeScrollBar();
    }

    public void RefreshSelection()
    {
        if (document.SelectedTimelineClipId is not null)
        {
            TrackHeaderSelected = false;
        }
        timelineCanvas.InvalidateVisual();
    }

    public void UpdateTransport(TransportSnapshot transportSnapshot)
    {
        timelineCanvas.PlayheadStep = transportSnapshot.Mode == TransportMode.Timeline && transportSnapshot.TimelineStep >= 0 ? transportSnapshot.TimelineStep : null;
        var overriddenTrackIds = transportSnapshot.Mode == TransportMode.Timeline
            ? transportSnapshot.Tracks.Where(pair => pair.Value.OverridesArrangement).Select(pair => pair.Key).ToHashSet()
            : new HashSet<string>();
        if (!overriddenTrackIds.SetEquals(timelineCanvas.OverriddenTrackIds))
        {
            timelineCanvas.OverriddenTrackIds = overriddenTrackIds;
        }
        foreach (var (trackId, indicator) in sessionOverrideIndicators)
        {
            indicator.IsVisible = overriddenTrackIds.Contains(trackId);
        }
    }

    private void SynchronizeScrollBar()
    {
        synchronizingScrollBar = true;
        horizontalScrollBar.Maximum = Math.Max(0, timelineCanvas.ContentSteps - timelineCanvas.VisibleSteps);
        horizontalScrollBar.ViewportSize = timelineCanvas.VisibleSteps;
        horizontalScrollBar.Value = Math.Clamp(timelineCanvas.ScrollStep, 0, horizontalScrollBar.Maximum);
        synchronizingScrollBar = false;
    }

    private void MoveCursor(long cursorStep)
    {
        timelineCanvas.CursorStep = cursorStep;
        timelineCanvas.ScrollStep = Math.Max(0, cursorStep - 16);
    }

    private void ToggleLoop()
    {
        var project = document.Project;
        if (project.TimelineLoop is not null)
        {
            document.Edit("cleared the timeline loop", editedProject => editedProject.TimelineLoop = null, EditScope.Structure);
            return;
        }
        // Default brace: the selected clip, or four bars from the cursor.
        var loopRegion = document.SelectedTimelineClip is { } selectedClip
            ? new TimelineLoopRegion { StartStep = selectedClip.StartStep, EndStep = selectedClip.EndStep }
            : new TimelineLoopRegion { StartStep = (int)(timelineCanvas.CursorStep / 16 * 16), EndStep = (int)(timelineCanvas.CursorStep / 16 * 16) + 64 };
        document.Edit("set the timeline loop", editedProject => editedProject.TimelineLoop = loopRegion, EditScope.Structure);
    }

    private Control ComposeTrackHeader(SlawTrack track, int trackIndex)
    {
        Control TrackToggle(string caption, string toolTip, Func<bool> readState, Action<bool> writeState, string editVerb)
        {
            var trackToggle = new ToggleButton { Content = caption, IsChecked = readState(), Padding = new Thickness(6, 1), FontSize = 11, MinHeight = 20 };
            ToolTip.SetTip(trackToggle, toolTip);
            trackToggle.IsCheckedChanged += (_, _) =>
            {
                if (readState() != (trackToggle.IsChecked == true))
                {
                    document.Edit($"{editVerb} {track.Name}", _ => writeState(trackToggle.IsChecked == true));
                }
            };
            return trackToggle;
        }

        var toggleRow = StudioUi.Row(3,
            TrackToggle("M", "Mute", () => track.Muted, muted => track.Muted = muted, "toggled mute on"),
            TrackToggle("S", "Solo", () => track.Solo, soloed => track.Solo = soloed, "toggled solo on"));
        if (track.Instrument == "sampler")
        {
            // In the Arrangement view, ● Rec puts the armed track's take straight onto the timeline.
            var recordArmToggle = new ToggleButton { Content = "●", IsChecked = host.IsArmedForRecording(track.Id), Padding = new Thickness(6, 1), FontSize = 11, MinHeight = 20 };
            recordArmToggle.Foreground = recordArmToggle.IsChecked == true ? StudioTheme.Failure : StudioTheme.TextMuted;
            ToolTip.SetTip(recordArmToggle, "Arm for audio recording: ● Rec (F9) then records the microphone into the arrangement from the cursor");
            recordArmToggle.Click += (_, _) => host.ToggleRecordArm(track.Id);
            toggleRow.Children.Add(recordArmToggle);
        }
        toggleRow.Children.Add(StudioUi.Caption(DeviceCatalog.InstrumentDisplayName(track.Instrument), 10));

        // Shown while a launched session clip (or a stop) has this track instead of the arrangement.
        var sessionOverrideIndicator = StudioUi.Caption("▶ session", 10, StudioTheme.Warning, FontWeight.SemiBold);
        ToolTip.SetTip(sessionOverrideIndicator, "This track is playing a session clip over the arrangement. ⤺ Back to Arrangement returns it on the next bar.");
        sessionOverrideIndicator.IsVisible = false;
        sessionOverrideIndicators[track.Id] = sessionOverrideIndicator;

        // The name: trimmed with the full name as a tooltip; double-click (or Rename…) edits it in place.
        var nameLabel = new TextBlock
        {
            Text = track.Name,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = StudioTheme.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(nameLabel, track.Name);
        var nameInput = new TextBox { Text = track.Name, FontSize = 12, Padding = new Thickness(2, 0), MinHeight = 20, IsVisible = false };
        void BeginRename()
        {
            nameLabel.IsVisible = false;
            nameInput.IsVisible = true;
            nameInput.Text = track.Name;
            nameInput.Focus();
            nameInput.SelectAll();
        }
        void EndRename(bool commit)
        {
            if (!nameInput.IsVisible)
            {
                return;
            }
            nameInput.IsVisible = false;
            nameLabel.IsVisible = true;
            if (commit)
            {
                document.RenameTrack(document.Project.Tracks.IndexOf(track), nameInput.Text ?? string.Empty);
            }
        }
        nameInput.KeyDown += (_, keyEvent) =>
        {
            if (keyEvent.Key is Key.Enter or Key.Escape)
            {
                EndRename(commit: keyEvent.Key == Key.Enter);
                keyEvent.Handled = true;
            }
        };
        nameInput.LostFocus += (_, _) => EndRename(commit: true);
        nameLabel.DoubleTapped += (_, _) => BeginRename();

        var deleteButton = StudioUi.Button("✕", () => host.DeleteTrack(document.Project.Tracks.IndexOf(track)), "Delete this track and its arrangement clips (Del after clicking the header; Ctrl+Z restores it)", compact: true);
        deleteButton.IsVisible = false;
        deleteButton.VerticalAlignment = VerticalAlignment.Top;
        var nameRow = new DockPanel { Children = { Docked(deleteButton, Dock.Right), Docked(sessionOverrideIndicator, Dock.Right), nameLabel, nameInput } };
        sessionOverrideIndicator.Margin = new Thickness(4, 0);

        var trackHeader = new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.TrackBrush(trackIndex),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(8, 3),
            Height = ArrangementCanvas.LaneHeight,
            Margin = new Thickness(0, 0, 6, 0),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    nameRow,
                    toggleRow,
                },
            },
        };
        // The ✕ only shows while the pointer is over the header, so the column stays calm.
        trackHeader.PointerEntered += (_, _) => deleteButton.IsVisible = true;
        trackHeader.PointerExited += (_, _) => deleteButton.IsVisible = false;
        trackHeader.AddHandler(PointerPressedEvent, (_, _) =>
        {
            document.Select(document.Project.Tracks.IndexOf(track), document.SelectedSceneIndex);
            TrackHeaderSelected = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        trackHeader.ContextMenu = ComposeTrackMenu(track, BeginRename);
        StudioDragDrop.MakeDropTarget(trackHeader, host, () => new DropTarget(DropTargetKind.Track, trackIndex));
        return trackHeader;
    }

    private ContextMenu ComposeTrackMenu(SlawTrack track, Action beginRename)
    {
        var trackMenu = new ContextMenu();
        trackMenu.Opening += (_, _) =>
        {
            trackMenu.Items.Clear();
            var trackIndex = document.Project.Tracks.IndexOf(track);
            void AddTrackMenuItem(string header, Action onClick, bool enabled = true)
            {
                var menuItem = new MenuItem { Header = header, IsEnabled = enabled };
                menuItem.Click += (_, _) => onClick();
                trackMenu.Items.Add(menuItem);
            }
            AddTrackMenuItem("Rename…", beginRename);
            AddTrackMenuItem("Duplicate track", () => document.DuplicateTrack(trackIndex),
                document.Project.Tracks.Count < ProjectAlignmentRemediator.MaximumTrackCount);
            if (track.Instrument == "sampler")
            {
                AddTrackMenuItem(host.IsArmedForRecording(track.Id) ? "Disarm recording" : "Arm for recording", () => host.ToggleRecordArm(track.Id));
            }
            trackMenu.Items.Add(new Separator());
            AddTrackMenuItem("Delete track", () => host.DeleteTrack(trackIndex), document.Project.Tracks.Count > 1);
        };
        return trackMenu;
    }

    private static Control Docked(Control dockedControl, Dock dockPosition)
    {
        DockPanel.SetDock(dockedControl, dockPosition);
        return dockedControl;
    }
}

/// <summary>
/// The Arrangement view's drawing surface: a bar ruler with cursor and loop brace, one
/// lane per track, and the timeline clips with miniature previews. Handles every
/// pointer gesture itself; edits go through the document as single undo steps.
/// </summary>
internal sealed class ArrangementCanvas : Control
{
    public const double TrackHeaderWidth = 236;
    public const double LaneHeight = 52;
    public const double LaneSpacing = 3;
    public const double RulerHeight = 26;

    private const double ResizeHandleWidth = 7;
    private const double DragThreshold = 4;
    private const double MinimumPixelsPerStep = 1.2;
    private const double MaximumPixelsPerStep = 48;

    private static readonly Typeface LabelTypeface = new(StudioTheme.MonospaceFont, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IPen BarLinePen = new Pen(new SolidColorBrush(Color.Parse("#4a4a5e"), 0.9), 1);
    private static readonly IPen BeatLinePen = new Pen(new SolidColorBrush(Color.Parse("#2a2a3a"), 0.8), 1);
    private static readonly IPen RepetitionPen = new Pen(new SolidColorBrush(Colors.White, 0.25), 1, new DashStyle(new double[] { 2, 3 }, 0));
    private static readonly IPen CursorPen = new Pen(StudioTheme.Accent, 1.5);
    private static readonly IPen PlayheadPen = new Pen(StudioTheme.Playhead, 2);
    private static readonly IPen SelectedClipPen = new Pen(StudioTheme.Text, 2);
    private static readonly IBrush LoopBraceBrush = new SolidColorBrush(Color.Parse("#f59e0b"), 0.55);
    private static readonly IBrush LoopShadeBrush = new SolidColorBrush(Color.Parse("#f59e0b"), 0.06);

    private readonly StudioDocument document;
    private readonly IStudioHost host;

    private double pixelsPerStep = 6;
    private double scrollStep;
    private long cursorStep;
    private long? playheadStep;

    private enum GestureKind { None, Move, Resize, LoopBrace }

    private GestureKind gestureKind;
    private Point gestureOrigin;
    private bool gestureStarted;
    private bool gestureCopies;
    private TimelineClip? gestureClip;
    private int gestureOriginalStart;
    private int gestureOriginalLength;
    private int gestureGrabOffsetSteps;
    private long loopBraceAnchorStep;

    public ArrangementCanvas(StudioDocument document, IStudioHost host)
    {
        this.document = document;
        this.host = host;
        ClipToBounds = true;
        Focusable = false;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, HandleDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHover(false));
        AddHandler(DragDrop.DropEvent, HandleDrop);
    }

    /// <summary>Raised when zoom, scroll or content size change, so the scroll bar can follow.</summary>
    public event Action? ViewportChanged;

    public double ScrollStep
    {
        get => scrollStep;
        set
        {
            var clampedStep = Math.Clamp(value, 0, Math.Max(0, ContentSteps - VisibleSteps));
            if (Math.Abs(clampedStep - scrollStep) > 1e-6)
            {
                scrollStep = clampedStep;
                InvalidateVisual();
                ViewportChanged?.Invoke();
            }
        }
    }

    public long CursorStep
    {
        get => cursorStep;
        set
        {
            cursorStep = Math.Clamp(value, 0, FreeTimeline.MaximumStep);
            InvalidateVisual();
        }
    }

    private static readonly IBrush SessionOverrideShade = new SolidColorBrush(StudioTheme.BackgroundColor, 0.6);
    private IReadOnlySet<string> overriddenTrackIds = new HashSet<string>();

    /// <summary>Tracks playing session material over the arrangement right now; their lanes are dimmed.</summary>
    public IReadOnlySet<string> OverriddenTrackIds
    {
        get => overriddenTrackIds;
        set
        {
            overriddenTrackIds = value;
            InvalidateVisual();
        }
    }

    public long? PlayheadStep
    {
        set
        {
            if (playheadStep == value)
            {
                return;
            }
            playheadStep = value;
            // Follow the playhead off the right edge, a page at a time.
            if (value is { } currentStep && (currentStep < scrollStep || currentStep > scrollStep + VisibleSteps * 0.9))
            {
                ScrollStep = Math.Max(0, currentStep - VisibleSteps * 0.1);
            }
            InvalidateVisual();
        }
    }

    public double VisibleSteps => Math.Max(1, Bounds.Width / pixelsPerStep);

    /// <summary>How far the timeline can scroll: past the last clip (and loop, and cursor) by sixteen bars.</summary>
    public double ContentSteps
    {
        get
        {
            var project = document.Project;
            var furthestStep = Math.Max(FreeTimeline.LengthInSteps(project), Math.Max(project.TimelineLoop?.EndStep ?? 0, (int)cursorStep));
            return Math.Max(furthestStep + 16 * 16, VisibleSteps);
        }
    }

    public void Zoom(double factor, double? anchorX)
    {
        var anchorPixel = anchorX ?? Bounds.Width / 2;
        var anchorStep = StepAt(anchorPixel);
        pixelsPerStep = Math.Clamp(pixelsPerStep * factor, MinimumPixelsPerStep, MaximumPixelsPerStep);
        scrollStep = Math.Max(0, anchorStep - anchorPixel / pixelsPerStep);
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs sizeChange)
    {
        base.OnSizeChanged(sizeChange);
        ViewportChanged?.Invoke();
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    private double XAt(double timelineStep) => (timelineStep - scrollStep) * pixelsPerStep;

    private double StepAt(double pointerX) => pointerX / pixelsPerStep + scrollStep;

    private static double LaneTop(int trackIndex) => RulerHeight + LaneSpacing + trackIndex * (LaneHeight + LaneSpacing);

    private int TrackIndexAt(double pointerY)
    {
        if (pointerY < RulerHeight + LaneSpacing)
        {
            return -1;
        }
        var trackIndex = (int)((pointerY - RulerHeight - LaneSpacing) / (LaneHeight + LaneSpacing));
        return trackIndex < document.Project.Tracks.Count ? trackIndex : -1;
    }

    private Rect ClipRect(TimelineClip timelineClip, int trackIndex) =>
        new(XAt(timelineClip.StartStep), LaneTop(trackIndex) + 1, Math.Max(2, timelineClip.LengthSteps * pixelsPerStep), LaneHeight - 2);

    /// <summary>Snaps to a beat by default; Shift snaps to bars, Alt frees it to single steps.</summary>
    private static int SnapResolution(KeyModifiers keyModifiers) =>
        keyModifiers.HasFlag(KeyModifiers.Alt) ? 1 : keyModifiers.HasFlag(KeyModifiers.Shift) ? 16 : 4;

    private static int Snap(double timelineStep, int resolution) => (int)Math.Max(0, Math.Round(timelineStep / resolution) * resolution);

    /// <summary>The topmost clip under the pointer: drawn last, so the later-starting one, as playback hears it.</summary>
    private TimelineClip? ClipAt(Point pointerPosition, out bool onResizeHandle)
    {
        onResizeHandle = false;
        var trackIndex = TrackIndexAt(pointerPosition.Y);
        if (trackIndex < 0)
        {
            return null;
        }
        var trackClips = FreeTimeline.ClipsForTrack(document.Project, document.Project.Tracks[trackIndex].Id);
        for (var clipIndex = trackClips.Count - 1; clipIndex >= 0; clipIndex--)
        {
            var clipRect = ClipRect(trackClips[clipIndex], trackIndex);
            if (clipRect.Contains(pointerPosition))
            {
                onResizeHandle = pointerPosition.X >= clipRect.Right - ResizeHandleWidth;
                return trackClips[clipIndex];
            }
        }
        return null;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext drawingContext)
    {
        var project = document.Project;
        var width = Bounds.Width;
        var firstVisibleStep = (int)Math.Floor(scrollStep);
        var lastVisibleStep = (int)Math.Ceiling(scrollStep + VisibleSteps);

        // Lanes and grid.
        for (var trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            drawingContext.DrawRectangle(trackIndex % 2 == 0 ? StudioTheme.CellEven : StudioTheme.CellOdd, null,
                new Rect(0, LaneTop(trackIndex), width, LaneHeight), 2, 2);
        }
        var showBeats = pixelsPerStep * 4 >= 7;
        var lanesBottom = LaneTop(project.Tracks.Count);
        for (var gridStep = firstVisibleStep - firstVisibleStep % 4; gridStep <= lastVisibleStep; gridStep += 4)
        {
            if (gridStep % 16 != 0 && !showBeats)
            {
                continue;
            }
            var gridX = Math.Round(XAt(gridStep)) + 0.5;
            drawingContext.DrawLine(gridStep % 16 == 0 ? BarLinePen : BeatLinePen, new Point(gridX, RulerHeight), new Point(gridX, lanesBottom));
        }

        // Loop brace shading under the clips.
        if (project.TimelineLoop is { IsValid: true } loopRegion)
        {
            drawingContext.DrawRectangle(LoopShadeBrush, null, new Rect(XAt(loopRegion.StartStep), RulerHeight, (loopRegion.EndStep - loopRegion.StartStep) * pixelsPerStep, lanesBottom - RulerHeight));
        }

        // Clips, earlier first so the later-starting (audible) one is drawn on top.
        for (var trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            var track = project.Tracks[trackIndex];
            foreach (var timelineClip in FreeTimeline.ClipsForTrack(project, track.Id))
            {
                if (timelineClip.EndStep < firstVisibleStep || timelineClip.StartStep > lastVisibleStep)
                {
                    continue;
                }
                DrawClip(drawingContext, track, trackIndex, timelineClip);
            }
        }

        // Lanes a session launch took over: the arrangement is still drawn, dimmed, under an orange edge.
        for (var trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            if (overriddenTrackIds.Contains(project.Tracks[trackIndex].Id))
            {
                var laneRect = new Rect(0, LaneTop(trackIndex), width, LaneHeight);
                drawingContext.DrawRectangle(SessionOverrideShade, null, laneRect, 2, 2);
                drawingContext.DrawRectangle(StudioTheme.Warning, null, new Rect(0, laneRect.Y, 3, laneRect.Height));
            }
        }

        DrawRuler(drawingContext, project, firstVisibleStep, lastVisibleStep);

        var cursorX = XAt(cursorStep);
        drawingContext.DrawLine(CursorPen, new Point(cursorX, RulerHeight - 6), new Point(cursorX, lanesBottom));
        if (playheadStep is { } currentStep)
        {
            var playheadX = XAt(currentStep + 0.5);
            drawingContext.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, lanesBottom));
        }
        if (StudioDragDrop.IsDropHovered(this))
        {
            drawingContext.DrawRectangle(null, StudioDragDrop.DropHoverPen, new Rect(Bounds.Size).Deflate(1), 3, 3);
        }
    }

    private void DrawClip(DrawingContext drawingContext, SlawTrack track, int trackIndex, TimelineClip timelineClip)
    {
        var clipRect = ClipRect(timelineClip, trackIndex);
        var patternLength = Math.Max(1, timelineClip.Clip.Pattern.Count);
        drawingContext.DrawRectangle(StudioTheme.Surface, null, clipRect, 3, 3);
        drawingContext.DrawRectangle(StudioTheme.TrackBrushDimmed(trackIndex), new Pen(StudioTheme.TrackBrush(trackIndex), 1), clipRect, 3, 3);
        using (drawingContext.PushClip(clipRect))
        {
            // Every pass of the looping pattern at true scale, the last one cut off where the clip ends.
            var repetitions = (int)Math.Ceiling(timelineClip.LengthSteps / (double)patternLength);
            var previewArea = new Rect(clipRect.X + 1, clipRect.Y + 15, repetitions * patternLength * pixelsPerStep - 2, clipRect.Height - 18);
            ClipMiniatureRenderer.Draw(drawingContext, timelineClip.Clip, InstrumentVoiceSynthesizer.IsPercussive(track.Instrument), previewArea,
                StudioTheme.TrackBrush(trackIndex), repetitions);
            for (var repetitionIndex = 1; repetitionIndex < repetitions; repetitionIndex++)
            {
                var repetitionX = Math.Round(XAt(timelineClip.StartStep + repetitionIndex * patternLength)) + 0.5;
                drawingContext.DrawLine(RepetitionPen, new Point(repetitionX, clipRect.Y + 2), new Point(repetitionX, clipRect.Bottom - 2));
            }
            if (clipRect.Width > 24)
            {
                DrawLabel(drawingContext, timelineClip.Clip.Name, new Point(clipRect.X + 5, clipRect.Y + 1), StudioTheme.Text, 10);
            }
        }
        if (timelineClip.Id == document.SelectedTimelineClipId)
        {
            drawingContext.DrawRectangle(null, SelectedClipPen, clipRect, 3, 3);
        }
        if (track.Muted)
        {
            drawingContext.DrawRectangle(new SolidColorBrush(StudioTheme.BackgroundColor, 0.55), null, clipRect, 3, 3);
        }
    }

    private void DrawRuler(DrawingContext drawingContext, SlawProject project, int firstVisibleStep, int lastVisibleStep)
    {
        drawingContext.DrawRectangle(StudioTheme.Surface, null, new Rect(0, 0, Bounds.Width, RulerHeight), 2, 2);
        if (project.TimelineLoop is { IsValid: true } loopRegion)
        {
            drawingContext.DrawRectangle(LoopBraceBrush, null, new Rect(XAt(loopRegion.StartStep), 2, (loopRegion.EndStep - loopRegion.StartStep) * pixelsPerStep, 6), 2, 2);
        }
        // Label every bar, or every 2, 4, 8… when zoomed out far enough that they would collide.
        var barsPerLabel = 1;
        while (barsPerLabel * 16 * pixelsPerStep < 34)
        {
            barsPerLabel *= 2;
        }
        var labelInterval = barsPerLabel * 16;
        for (var labelStep = firstVisibleStep - firstVisibleStep % labelInterval; labelStep <= lastVisibleStep; labelStep += labelInterval)
        {
            var labelX = Math.Round(XAt(labelStep)) + 0.5;
            drawingContext.DrawLine(BarLinePen, new Point(labelX, RulerHeight - 9), new Point(labelX, RulerHeight));
            DrawLabel(drawingContext, (labelStep / 16 + 1).ToString(CultureInfo.InvariantCulture), new Point(labelX + 3, 9), StudioTheme.TextMuted, 10);
        }
        // The cursor's flag.
        var cursorX = XAt(cursorStep);
        var cursorFlag = new StreamGeometry();
        using (var flagContext = cursorFlag.Open())
        {
            flagContext.BeginFigure(new Point(cursorX - 5, RulerHeight - 9), true);
            flagContext.LineTo(new Point(cursorX + 5, RulerHeight - 9));
            flagContext.LineTo(new Point(cursorX, RulerHeight - 2));
            flagContext.EndFigure(true);
        }
        drawingContext.DrawGeometry(StudioTheme.Accent, null, cursorFlag);
    }

    private static void DrawLabel(DrawingContext drawingContext, string labelText, Point origin, IBrush foreground, double fontSize) =>
        drawingContext.DrawText(new FormattedText(labelText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, fontSize, foreground), origin);

    // ── Pointer gestures ──────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs pointerEvent)
    {
        base.OnPointerPressed(pointerEvent);
        var pointerPosition = pointerEvent.GetPosition(this);
        var pointerProperties = pointerEvent.GetCurrentPoint(this).Properties;
        var resolution = SnapResolution(pointerEvent.KeyModifiers);
        pointerEvent.Handled = true;

        if (pointerPosition.Y < RulerHeight)
        {
            if (pointerProperties.IsRightButtonPressed)
            {
                OpenRulerMenu();
                return;
            }
            CursorStep = Snap(StepAt(pointerPosition.X), resolution);
            if (pointerEvent.ClickCount >= 2)
            {
                host.PlayTimelineFrom(cursorStep);
                return;
            }
            BeginGesture(GestureKind.LoopBrace, pointerPosition, null);
            loopBraceAnchorStep = cursorStep;
            pointerEvent.Pointer.Capture(this);
            return;
        }

        var trackIndex = TrackIndexAt(pointerPosition.Y);
        if (trackIndex < 0)
        {
            return;
        }
        var pressedClip = ClipAt(pointerPosition, out var onResizeHandle);
        if (pointerProperties.IsRightButtonPressed)
        {
            if (pressedClip is not null)
            {
                document.SelectTimelineClip(pressedClip.Id);
                OpenClipMenu(pressedClip);
            }
            return;
        }
        if (!pointerProperties.IsLeftButtonPressed)
        {
            return;
        }
        if (pressedClip is null)
        {
            if (pointerEvent.ClickCount >= 2)
            {
                CreateClip(document.Project.Tracks[trackIndex], (int)(Math.Floor(StepAt(pointerPosition.X) / 16) * 16));
            }
            else
            {
                document.SelectTimelineClip(null);
                document.Select(trackIndex, document.SelectedSceneIndex);
            }
            return;
        }

        document.SelectTimelineClip(pressedClip.Id);
        BeginGesture(onResizeHandle ? GestureKind.Resize : GestureKind.Move, pointerPosition, pressedClip);
        gestureCopies = !onResizeHandle && pointerEvent.KeyModifiers.HasFlag(KeyModifiers.Control);
        gestureGrabOffsetSteps = (int)Math.Floor(StepAt(pointerPosition.X)) - pressedClip.StartStep;
        pointerEvent.Pointer.Capture(this);
    }

    private void BeginGesture(GestureKind kind, Point origin, TimelineClip? clip)
    {
        gestureKind = kind;
        gestureOrigin = origin;
        gestureStarted = false;
        gestureCopies = false;
        gestureClip = clip;
        gestureOriginalStart = clip?.StartStep ?? 0;
        gestureOriginalLength = clip?.LengthSteps ?? 0;
    }

    protected override void OnPointerMoved(PointerEventArgs pointerEvent)
    {
        base.OnPointerMoved(pointerEvent);
        var pointerPosition = pointerEvent.GetPosition(this);
        if (gestureKind == GestureKind.None)
        {
            var onResizeHandle = false;
            var hoveredClip = pointerPosition.Y >= RulerHeight ? ClipAt(pointerPosition, out onResizeHandle) : null;
            Cursor = hoveredClip is null
                ? Cursor.Default
                : new Cursor(onResizeHandle ? StandardCursorType.SizeWestEast : StandardCursorType.Hand);
            return;
        }
        if (!gestureStarted)
        {
            if (Math.Abs(pointerPosition.X - gestureOrigin.X) + Math.Abs(pointerPosition.Y - gestureOrigin.Y) < DragThreshold)
            {
                return;
            }
            gestureStarted = true;
            if (gestureKind != GestureKind.LoopBrace)
            {
                document.BeginEdit(gestureKind == GestureKind.Resize ? "resize timeline clip" : gestureCopies ? "copy timeline clip" : "move timeline clip");
                if (gestureCopies && gestureClip is not null)
                {
                    // The original stays put; the copy follows the pointer.
                    var copiedClip = FreeTimeline.Copy(gestureClip, gestureClip.StartStep);
                    document.Project.Timeline.Add(copiedClip);
                    gestureClip = copiedClip;
                }
            }
        }

        var resolution = SnapResolution(pointerEvent.KeyModifiers);
        var pointerStep = StepAt(pointerPosition.X);
        switch (gestureKind)
        {
            case GestureKind.LoopBrace:
                var braceEdge = Snap(pointerStep, resolution);
                var braceStart = (int)Math.Min(loopBraceAnchorStep, braceEdge);
                var braceEnd = (int)Math.Max(loopBraceAnchorStep, braceEdge);
                document.Project.TimelineLoop = braceEnd > braceStart ? new TimelineLoopRegion { StartStep = braceStart, EndStep = braceEnd } : document.Project.TimelineLoop;
                break;
            case GestureKind.Move when gestureClip is not null:
                gestureClip.StartStep = Math.Min(FreeTimeline.MaximumStep, Snap(pointerStep - gestureGrabOffsetSteps, resolution));
                if (TrackIndexAt(pointerPosition.Y) is >= 0 and var hoveredTrackIndex)
                {
                    gestureClip.TrackId = document.Project.Tracks[hoveredTrackIndex].Id;
                }
                break;
            case GestureKind.Resize when gestureClip is not null:
                var resizedEnd = Math.Max(gestureClip.StartStep + 1, Snap(pointerStep, resolution));
                gestureClip.LengthSteps = Math.Clamp(resizedEnd - gestureClip.StartStep, 1, FreeTimeline.MaximumClipLength);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs pointerEvent)
    {
        base.OnPointerReleased(pointerEvent);
        if (gestureKind == GestureKind.None)
        {
            return;
        }
        pointerEvent.Pointer.Capture(null);
        var finishedKind = gestureKind;
        var finishedClip = gestureClip;
        gestureKind = GestureKind.None;
        if (!gestureStarted)
        {
            return;
        }
        switch (finishedKind)
        {
            case GestureKind.LoopBrace when document.Project.TimelineLoop is { } braceRegion:
                // The brace was drawn live for feedback; record it as one undoable edit.
                document.Project.TimelineLoop = null;
                document.Edit("set the timeline loop", project => project.TimelineLoop = braceRegion, EditScope.Structure);
                break;
            case GestureKind.Move or GestureKind.Resize when finishedClip is not null:
                var unchanged = !gestureCopies && finishedClip.StartStep == gestureOriginalStart && finishedClip.LengthSteps == gestureOriginalLength;
                document.CommitEdit(unchanged ? "touched a timeline clip" : finishedKind == GestureKind.Resize
                    ? $"stretched a timeline clip to {finishedClip.LengthSteps / 16.0:0.##} bars"
                    : $"{(gestureCopies ? "copied" : "moved")} a timeline clip to {FreeTimeline.FormatPosition(finishedClip.StartStep)}", EditScope.Structure);
                document.SelectTimelineClip(finishedClip.Id);
                break;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs wheelEvent)
    {
        base.OnPointerWheelChanged(wheelEvent);
        if (wheelEvent.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Zoom(wheelEvent.Delta.Y > 0 ? 1.25 : 1 / 1.25, wheelEvent.GetPosition(this).X);
            wheelEvent.Handled = true;
        }
        else if (wheelEvent.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(wheelEvent.Delta.X) > Math.Abs(wheelEvent.Delta.Y))
        {
            var scrollDelta = Math.Abs(wheelEvent.Delta.X) > 0 ? wheelEvent.Delta.X : wheelEvent.Delta.Y;
            ScrollStep -= scrollDelta * 40 / pixelsPerStep;
            wheelEvent.Handled = true;
        }
    }

    // ── Edits ─────────────────────────────────────────────────────────────────

    private void CreateClip(SlawTrack track, int startStep)
    {
        var createdClip = FreeTimeline.CreateEmpty(track, startStep, document.Project.Steps);
        document.Edit($"added a timeline clip to {track.Name}", project => project.Timeline.Add(createdClip), EditScope.Structure);
        document.SelectTimelineClip(createdClip.Id);
    }

    private void OpenClipMenu(TimelineClip timelineClip)
    {
        var clipMenu = new ContextMenu();
        void AddItem(string header, Action onClick)
        {
            var menuItem = new MenuItem { Header = header };
            menuItem.Click += (_, _) => onClick();
            clipMenu.Items.Add(menuItem);
        }
        AddItem("Play from here", () => host.PlayTimelineFrom(timelineClip.StartStep));
        AddItem("Duplicate after (Ctrl+D)", () => FreeTimelineCommands.DuplicateAfter(document, timelineClip.Id));
        AddItem("Rename…", () => OpenRenameFlyout(timelineClip));
        AddItem("Loop this clip", () => document.Edit("set the timeline loop",
            project => project.TimelineLoop = new TimelineLoopRegion { StartStep = timelineClip.StartStep, EndStep = timelineClip.EndStep }, EditScope.Structure));
        AddItem("Delete (Del)", () => FreeTimelineCommands.Delete(document, timelineClip.Id));
        clipMenu.Open(this);
    }

    private void OpenRulerMenu()
    {
        var rulerMenu = new ContextMenu();
        var playItem = new MenuItem { Header = "Play from the cursor" };
        playItem.Click += (_, _) => host.PlayTimelineFrom(cursorStep);
        rulerMenu.Items.Add(playItem);
        var clearLoopItem = new MenuItem { Header = "Clear the loop", IsEnabled = document.Project.TimelineLoop is not null };
        clearLoopItem.Click += (_, _) => document.Edit("cleared the timeline loop", project => project.TimelineLoop = null, EditScope.Structure);
        rulerMenu.Items.Add(clearLoopItem);
        rulerMenu.Open(this);
    }

    private void OpenRenameFlyout(TimelineClip timelineClip)
    {
        var nameInput = new TextBox { Text = timelineClip.Clip.Name, Width = 200 };
        var renameFlyout = new Flyout { Content = nameInput };
        void Commit()
        {
            var revisedName = nameInput.Text?.Trim();
            renameFlyout.Hide();
            if (!string.IsNullOrEmpty(revisedName) && revisedName != timelineClip.Clip.Name)
            {
                var timelineClipId = timelineClip.Id;
                document.Edit($"renamed a timeline clip to {revisedName}", project =>
                {
                    if (project.Timeline.FirstOrDefault(candidate => candidate.Id == timelineClipId) is { } renamedClip)
                    {
                        renamedClip.Clip.Name = revisedName;
                    }
                }, EditScope.Structure);
            }
        }
        nameInput.KeyDown += (_, keyEvent) =>
        {
            if (keyEvent.Key == Key.Enter)
            {
                Commit();
                keyEvent.Handled = true;
            }
        };
        renameFlyout.ShowAt(this, showAtPointer: true);
        nameInput.AttachedToVisualTree += (_, _) => nameInput.Focus();
    }

    // ── Drops: session clips and scenes land as copies; everything else goes to the track ──

    private void SetDropHover(bool hovered)
    {
        if (StudioDragDrop.IsDropHovered(this) != hovered)
        {
            Classes.Set(StudioDragDrop.DropHoverClass, hovered);
            InvalidateVisual();
        }
    }

    private bool CanAccept(DragPayload dragPayload, Point dropPosition)
    {
        var trackIndex = TrackIndexAt(dropPosition.Y);
        return dragPayload.Kind switch
        {
            DragPayloadKind.Clip => trackIndex >= 0 && document.Project.ClipAt(dragPayload.TrackIndex, dragPayload.SceneIndex) is not null,
            DragPayloadKind.Scene => dragPayload.SceneIndex >= 0 && dragPayload.SceneIndex < document.Project.Scenes.Count,
            _ => trackIndex >= 0 && host.CanDrop(dragPayload, new DropTarget(DropTargetKind.Track, trackIndex)),
        };
    }

    private void HandleDragOver(object? sender, DragEventArgs dragEvent)
    {
        var accepted = StudioDragDrop.ReadPayload(dragEvent) is { } dragPayload && CanAccept(dragPayload, dragEvent.GetPosition(this));
        dragEvent.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHover(accepted);
        dragEvent.Handled = true;
    }

    private void HandleDrop(object? sender, DragEventArgs dragEvent)
    {
        SetDropHover(false);
        dragEvent.Handled = true;
        var dropPosition = dragEvent.GetPosition(this);
        if (StudioDragDrop.ReadPayload(dragEvent) is not { } dragPayload || !CanAccept(dragPayload, dropPosition))
        {
            return;
        }
        var project = document.Project;
        var dropStep = Snap(StepAt(dropPosition.X), 16);
        var trackIndex = TrackIndexAt(dropPosition.Y);
        switch (dragPayload.Kind)
        {
            case DragPayloadKind.Clip:
                var sourceClip = project.ClipAt(dragPayload.TrackIndex, dragPayload.SceneIndex)!;
                var placedClip = new TimelineClip
                {
                    TrackId = project.Tracks[trackIndex].Id,
                    StartStep = dropStep,
                    LengthSteps = sourceClip.Pattern.Count,
                    Clip = FreeTimeline.CopyClip(sourceClip),
                };
                document.Edit($"placed \"{sourceClip.Name}\" in the arrangement", editedProject => editedProject.Timeline.Add(placedClip), EditScope.Structure);
                document.SelectTimelineClip(placedClip.Id);
                break;
            case DragPayloadKind.Scene:
                // A whole scene: every clip in that row, side by side, at the drop position.
                var sceneIndex = dragPayload.SceneIndex;
                var sceneLength = project.SceneLengthInSteps(sceneIndex);
                document.Edit($"placed the scene \"{project.Scenes[sceneIndex]}\" in the arrangement", editedProject =>
                {
                    foreach (var track in editedProject.Tracks)
                    {
                        if (track.ClipAt(sceneIndex) is { } sceneClip)
                        {
                            editedProject.Timeline.Add(new TimelineClip { TrackId = track.Id, StartStep = dropStep, LengthSteps = sceneLength, Clip = FreeTimeline.CopyClip(sceneClip) });
                        }
                    }
                }, EditScope.Structure);
                break;
            default:
                host.Drop(dragPayload, new DropTarget(DropTargetKind.Track, trackIndex), copyRequested: false);
                break;
        }
    }
}

/// <summary>Timeline-clip commands shared by the canvas menus and the keyboard.</summary>
internal static class FreeTimelineCommands
{
    public static void Delete(StudioDocument document, string timelineClipId)
    {
        if (document.Project.Timeline.FirstOrDefault(timelineClip => timelineClip.Id == timelineClipId) is null)
        {
            return;
        }
        document.Edit("deleted a timeline clip", project => project.Timeline.RemoveAll(timelineClip => timelineClip.Id == timelineClipId), EditScope.Structure);
        document.SelectTimelineClip(null);
    }

    /// <summary>Copies the clip to start right where it ends, like Ableton's Ctrl+D.</summary>
    public static void DuplicateAfter(StudioDocument document, string timelineClipId)
    {
        if (document.Project.Timeline.FirstOrDefault(timelineClip => timelineClip.Id == timelineClipId) is not { } sourceClip)
        {
            return;
        }
        var duplicatedClip = FreeTimeline.Copy(sourceClip, sourceClip.EndStep);
        document.Edit("duplicated a timeline clip", project => project.Timeline.Add(duplicatedClip), EditScope.Structure);
        document.SelectTimelineClip(duplicatedClip.Id);
    }

    /// <summary>Pastes a copied clip onto the selected track at <paramref name="cursorStep"/>.</summary>
    public static void Paste(StudioDocument document, SlawClip copiedClip, long cursorStep)
    {
        if (document.SelectedTrack is not { } targetTrack)
        {
            return;
        }
        var pastedClip = new TimelineClip { TrackId = targetTrack.Id, StartStep = (int)cursorStep, LengthSteps = copiedClip.Pattern.Count, Clip = FreeTimeline.CopyClip(copiedClip) };
        document.Edit($"pasted \"{copiedClip.Name}\" into the arrangement", project => project.Timeline.Add(pastedClip), EditScope.Structure);
        document.SelectTimelineClip(pastedClip.Id);
    }
}
