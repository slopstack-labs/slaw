using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// Groovebox mode: the session seen the way a drum machine sees it. Scenes are
/// patterns on a row of pads, every track is one row of sixteen steps, and the
/// pattern being edited is the selected scene. A pad launches its pattern on the
/// next bar while the music plays (shift+click only edits it), a track's name pad
/// plays its sound, and each row has a groovebox's pattern tools: shift, length,
/// Euclidean fill, dice and clear. It edits the same clips the Session view shows.
/// </summary>
internal sealed class GrooveboxView : UserControl
{
    private const double TrackPadWidth = 136;
    private const double StepRowHeight = 38;
    private const int MaximumEuclideanHitChoices = 16;

    private static readonly IBrush PlayingPatternFill = new SolidColorBrush(Color.Parse("#14532d"));
    private static readonly (string Label, double Density)[] DiceDensities =
    {
        ("Sparse (25% of steps)", 0.25),
        ("Medium (40% of steps)", 0.4),
        ("Busy (60% of steps)", 0.6),
    };

    /// <summary>One track's row. A track with no clip in the pattern edits a stand-in that becomes its clip on the first edit.</summary>
    private sealed record TrackRow(SlawTrack Track, int TrackIndex, SlawClip Clip, bool IsStandIn, StepRowControl Steps, Border Pad, ToggleButton MuteToggle, ToggleButton SoloToggle);

    private readonly StudioDocument document;
    private readonly IStudioHost host;
    private readonly List<TrackRow> trackRows = new();
    private readonly List<(Border Pad, int SceneIndex)> patternPads = new();
    private readonly List<ToggleButton> pageToggles = new();
    private readonly Random patternDice = new();
    private StepRowControl? stepRuler;
    private ToggleButton? followToggle;
    private int shownSceneIndex = -1;
    private int pageIndex;
    private int pageCount = 1;
    private bool followPlayhead = true;
    private bool auditioning;
    private TransportSnapshot? lastTransport;

    public GrooveboxView(StudioDocument document, IStudioHost host)
    {
        this.document = document;
        this.host = host;
        Rebuild();
    }

    /// <summary>The scene this view edits as its pattern.</summary>
    public int PatternIndex => shownSceneIndex;

    public void Rebuild()
    {
        trackRows.Clear();
        patternPads.Clear();
        pageToggles.Clear();
        var project = document.Project;
        shownSceneIndex = Math.Clamp(document.SelectedSceneIndex, 0, Math.Max(0, project.Scenes.Count - 1));
        pageCount = project.Tracks.Count == 0
            ? 1
            : project.Tracks.Max(track => GrooveboxPatternOperations.PageCount(track.ClipAt(shownSceneIndex)?.Pattern.Count ?? project.Steps));
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);

        var rowsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{TrackPadWidth.ToString(CultureInfo.InvariantCulture)},Auto,*,Auto"),
            ColumnSpacing = 8,
            RowSpacing = 5,
            Margin = new Thickness(12, 4, 12, 12),
        };
        rowsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        stepRuler = new StepRowControl(null, null, GrooveboxPatternOperations.PageLength);
        Place(rowsGrid, StudioUi.Caption("STEP", 10, StudioTheme.TextDim, FontWeight.Bold), 0, 0);
        Place(rowsGrid, stepRuler, 0, 2);

        if (project.Scenes.Count > 0)
        {
            for (var trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
            {
                rowsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                ComposeTrackRow(rowsGrid, project.Tracks[trackIndex], trackIndex, trackIndex + 1);
            }
        }

        var rowsScroller = new ScrollViewer
        {
            Content = rowsGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        StudioDragDrop.MakeDropTarget(rowsScroller, host, () => new DropTarget(DropTargetKind.EmptyArea));
        var patternBar = ComposePatternBar();
        DockPanel.SetDock(patternBar, Dock.Top);
        Content = new DockPanel { Children = { patternBar, rowsScroller } };
        ApplyPage();
        RefreshSelection();
        RefreshPatternPads();
    }

    private static void Place(Grid targetGrid, Control placedControl, int rowIndex, int columnIndex)
    {
        Grid.SetRow(placedControl, rowIndex);
        Grid.SetColumn(placedControl, columnIndex);
        targetGrid.Children.Add(placedControl);
    }

    // ── Pattern pads and pages ────────────────────────────────────────────────

    private Control ComposePatternBar()
    {
        var project = document.Project;
        var padsPanel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        for (var sceneIndex = 0; sceneIndex < project.Scenes.Count; sceneIndex++)
        {
            padsPanel.Children.Add(ComposePatternPad(sceneIndex));
        }
        if (project.Scenes.Count < ProjectAlignmentRemediator.MaximumSceneCount)
        {
            var addPatternButton = StudioUi.Button("+", AddPattern, "Add an empty pattern (a new scene) and edit it", compact: false);
            addPatternButton.Height = 46;
            addPatternButton.Width = 40;
            padsPanel.Children.Add(addPatternButton);
        }

        var editedClipCount = project.Tracks.Count(track => track.ClipAt(shownSceneIndex) is not null);
        var patternName = project.Scenes.Count > 0 ? project.Scenes[shownSceneIndex] : "none";
        var editingCaption = StudioUi.Caption(
            string.Create(CultureInfo.InvariantCulture, $"editing pattern {shownSceneIndex + 1}: {patternName} · {editedClipCount} of {project.Tracks.Count} tracks have a clip here"),
            11, StudioTheme.TextMuted);
        editingCaption.TextTrimming = TextTrimming.CharacterEllipsis;

        var pageControls = StudioUi.Row(4, StudioUi.Caption("PAGE", 10, StudioTheme.TextDim, FontWeight.Bold));
        for (var candidatePage = 0; candidatePage < pageCount; candidatePage++)
        {
            var capturedPage = candidatePage;
            var pageToggle = new ToggleButton
            {
                Content = (candidatePage + 1).ToString(CultureInfo.InvariantCulture),
                Padding = new Thickness(9, 2),
                FontSize = 11,
                IsChecked = candidatePage == pageIndex,
            };
            ToolTip.SetTip(pageToggle, string.Create(CultureInfo.InvariantCulture, $"Show steps {candidatePage * GrooveboxPatternOperations.PageLength + 1} to {(candidatePage + 1) * GrooveboxPatternOperations.PageLength} (stops following the playhead)"));
            pageToggle.Click += (_, _) => ShowPage(capturedPage, keepFollowing: false);
            pageToggles.Add(pageToggle);
            pageControls.Children.Add(pageToggle);
        }
        followToggle = new ToggleButton { Content = "Follow", Padding = new Thickness(9, 2), FontSize = 11, IsChecked = followPlayhead };
        ToolTip.SetTip(followToggle, "Turn the page with the playhead while the pattern plays");
        followToggle.IsCheckedChanged += (_, _) => followPlayhead = followToggle.IsChecked == true;
        pageControls.Children.Add(followToggle);
        pageControls.IsVisible = pageCount > 1;

        var infoRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(pageControls, Dock.Right);
        infoRow.Children.Add(pageControls);
        infoRow.Children.Add(editingCaption);

        return new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    StudioUi.Row(8,
                        StudioUi.Caption("Groovebox", 12, StudioTheme.Text, FontWeight.Bold),
                        StudioUi.Caption("PATTERNS", 10, StudioTheme.TextDim, FontWeight.Bold)),
                    padsPanel,
                    infoRow,
                },
            },
        };
    }

    private Control ComposePatternPad(int sceneIndex)
    {
        var patternName = document.Project.Scenes[sceneIndex];
        var nameCaption = StudioUi.Caption(patternName, 9, StudioTheme.TextMuted);
        nameCaption.TextTrimming = TextTrimming.CharacterEllipsis;
        nameCaption.HorizontalAlignment = HorizontalAlignment.Center;
        var numberCaption = StudioUi.Caption((sceneIndex + 1).ToString(CultureInfo.InvariantCulture), 15, StudioTheme.Text, FontWeight.Bold);
        numberCaption.HorizontalAlignment = HorizontalAlignment.Center;
        var patternPad = new Border
        {
            Width = 66,
            Height = 46,
            CornerRadius = new CornerRadius(6),
            Background = StudioTheme.SurfaceRaised,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(3, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { numberCaption, nameCaption } },
        };
        ToolTip.SetTip(patternPad, $"Pattern {sceneIndex + 1}: {patternName}\nclick: edit it, and while music plays, play it from the next bar · shift+click: edit without launching · right-click: more");
        // On release, not press: selecting another pattern rebuilds this view, and the pad
        // has to outlive the press for a drag onto the arrangement to start from it.
        var padPressed = false;
        patternPad.PointerPressed += (_, pointerEvent) =>
        {
            padPressed = pointerEvent.GetCurrentPoint(patternPad).Properties.IsLeftButtonPressed;
            pointerEvent.Handled = padPressed;
        };
        patternPad.PointerReleased += (_, pointerEvent) =>
        {
            if (!padPressed || pointerEvent.InitialPressMouseButton != MouseButton.Left)
            {
                return;
            }
            padPressed = false;
            pointerEvent.Handled = true;
            if (new Rect(patternPad.Bounds.Size).Contains(pointerEvent.GetPosition(patternPad)))
            {
                SelectPattern(sceneIndex, launchIfPlaying: !pointerEvent.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
        };
        patternPad.PointerCaptureLost += (_, _) => padPressed = false;

        var patternMenu = new ContextMenu();
        AddMenuItem(patternMenu, "Play this pattern", () =>
        {
            SelectPattern(sceneIndex, launchIfPlaying: false);
            host.LaunchScene(sceneIndex);
        });
        AddMenuItem(patternMenu, "Edit without launching", () => SelectPattern(sceneIndex, launchIfPlaying: false));
        if (document.Project.Scenes.Count < ProjectAlignmentRemediator.MaximumSceneCount)
        {
            AddMenuItem(patternMenu, "Copy to a new pattern after this one", () =>
            {
                document.DuplicateScene(sceneIndex);
                SelectPattern(sceneIndex + 1, launchIfPlaying: false);
            });
        }
        AddMenuItem(patternMenu, "Place in arrangement at cursor", () => host.PlaceSceneInArrangement(sceneIndex));
        if (document.Project.Scenes.Count > 1)
        {
            patternMenu.Items.Add(new Separator());
            AddMenuItem(patternMenu, "Delete pattern (scene)", () => document.RemoveScene(sceneIndex));
        }
        patternPad.ContextMenu = patternMenu;
        StudioDragDrop.MakeDragSource(patternPad, () => new DragPayload(DragPayloadKind.Scene, SceneIndex: sceneIndex));
        patternPads.Add((patternPad, sceneIndex));
        return patternPad;
    }

    private void SelectPattern(int sceneIndex, bool launchIfPlaying)
    {
        var musicPlaying = lastTransport is { } transportSnapshot && PlaybackToggle.IsAudiblyPlaying(transportSnapshot);
        document.Select(document.SelectedTrackIndex, sceneIndex);
        if (launchIfPlaying && musicPlaying)
        {
            host.LaunchScene(sceneIndex);
        }
    }

    private void AddPattern()
    {
        var sceneCountBefore = document.Project.Scenes.Count;
        document.AddScene();
        if (document.Project.Scenes.Count > sceneCountBefore)
        {
            SelectPattern(document.Project.Scenes.Count - 1, launchIfPlaying: false);
        }
    }

    private void ShowPage(int requestedPage, bool keepFollowing)
    {
        if (!keepFollowing && followToggle is not null)
        {
            followToggle.IsChecked = false;
        }
        pageIndex = Math.Clamp(requestedPage, 0, pageCount - 1);
        ApplyPage();
    }

    /// <summary>
    /// Every row shows its own page of the one in view: a 16-step loop under a 64-step
    /// bass line shows its only page throughout, which is also what is playing.
    /// </summary>
    private void ApplyPage()
    {
        foreach (var trackRow in trackRows)
        {
            var rowPageCount = GrooveboxPatternOperations.PageCount(trackRow.Clip.Pattern.Count);
            trackRow.Steps.FirstStep = pageIndex % rowPageCount * GrooveboxPatternOperations.PageLength;
        }
        if (stepRuler is not null)
        {
            stepRuler.FirstStep = pageIndex * GrooveboxPatternOperations.PageLength;
        }
        for (var toggleIndex = 0; toggleIndex < pageToggles.Count; toggleIndex++)
        {
            pageToggles[toggleIndex].IsChecked = toggleIndex == pageIndex;
        }
    }

    // ── Track rows ────────────────────────────────────────────────────────────

    private void ComposeTrackRow(Grid rowsGrid, SlawTrack track, int trackIndex, int gridRowIndex)
    {
        var project = document.Project;
        var existingClip = track.ClipAt(shownSceneIndex);
        var shownClip = existingClip ?? SlawClip.Empty(project.Scenes[shownSceneIndex], project.Steps);
        var isStandIn = existingClip is null;

        var steps = new StepRowControl(track, shownClip, GrooveboxPatternOperations.PageLength)
        {
            Height = StepRowHeight,
            SelectsHits = true,
            Opacity = isStandIn ? 0.55 : 1,
        };
        ToolTip.SetTip(steps, isStandIn
            ? "No clip in this pattern yet: click a step to start one"
            : "click: toggle · drag a hit up/down: velocity · right-click: clear · ctrl+click: sustain · wheel: pitch (shift: octave)");

        var trackPad = ComposeTrackPad(track, trackIndex, shownClip);
        var muteToggle = new ToggleButton { Content = "M", IsChecked = track.Muted, Padding = new Thickness(7, 2), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(muteToggle, "Mute (takes effect at once)");
        muteToggle.IsCheckedChanged += (_, _) =>
        {
            if (track.Muted != (muteToggle.IsChecked == true))
            {
                document.Edit($"{(muteToggle.IsChecked == true ? "muted" : "unmuted")} {track.Name}", _ => track.Muted = muteToggle.IsChecked == true);
            }
        };
        var soloToggle = new ToggleButton { Content = "S", IsChecked = track.Solo, Padding = new Thickness(7, 2), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(soloToggle, "Solo");
        soloToggle.IsCheckedChanged += (_, _) =>
        {
            if (track.Solo != (soloToggle.IsChecked == true))
            {
                document.Edit($"{(soloToggle.IsChecked == true ? "soloed" : "unsoloed")} {track.Name}", _ => track.Solo = soloToggle.IsChecked == true);
            }
        };

        var trackRow = new TrackRow(track, trackIndex, shownClip, isStandIn, steps, trackPad, muteToggle, soloToggle);
        steps.EditStarted += () => document.BeginEdit("groovebox step");
        steps.PatternEdited += editDescription => CommitStepEdit(trackRow, editDescription);

        var lengthButton = new Button
        {
            Content = StudioUi.Row(4,
                new TextBlock { Text = string.Create(CultureInfo.InvariantCulture, $"{shownClip.Pattern.Count} st"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                new StudioIconGlyph { Icon = StudioIcon.ChevronDown, Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center }),
            Padding = new Thickness(7, 3),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(lengthButton, "Pattern length for this track: shorter or longer loops than the others make a polymeter");
        var lengthMenu = new MenuFlyout();
        foreach (var lengthChoice in GrooveboxPatternOperations.LengthChoices)
        {
            AddMenuItem(lengthMenu.Items, string.Create(CultureInfo.InvariantCulture, $"{lengthChoice} steps"),
                () => ApplyToRow(trackRow, (rowTrack, rowClip) => GrooveboxPatternOperations.SetLength(rowTrack, rowClip, lengthChoice)));
        }
        lengthButton.Flyout = lengthMenu;

        var moreButton = new Button { Content = "…", Padding = new Thickness(8, 3), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(moreButton, "Pattern tools: Euclidean rhythm, dice, double, halve, clear");
        var moreMenu = new MenuFlyout();
        moreMenu.Opening += (_, _) =>
        {
            moreMenu.Items.Clear();
            foreach (var toolItem in ComposePatternTools(trackRow))
            {
                moreMenu.Items.Add(toolItem);
            }
        };
        moreButton.Flyout = moreMenu;

        var rowTools = StudioUi.Row(3,
            StudioUi.Button("◀", () => ApplyToRow(trackRow, (rowTrack, rowClip) => GrooveboxPatternOperations.Rotate(rowTrack, rowClip, -1)), "Shift the pattern one step earlier (wraps around)", compact: true),
            StudioUi.Button("▶", () => ApplyToRow(trackRow, (rowTrack, rowClip) => GrooveboxPatternOperations.Rotate(rowTrack, rowClip, 1)), "Shift the pattern one step later (wraps around)", compact: true),
            lengthButton,
            moreButton);

        var trackMenu = new ContextMenu();
        trackMenu.Opening += (_, _) =>
        {
            trackMenu.Items.Clear();
            foreach (var toolItem in ComposePatternTools(trackRow))
            {
                trackMenu.Items.Add(toolItem);
            }
        };
        trackPad.ContextMenu = trackMenu;

        Place(rowsGrid, trackPad, gridRowIndex, 0);
        Place(rowsGrid, StudioUi.Row(3, muteToggle, soloToggle), gridRowIndex, 1);
        Place(rowsGrid, steps, gridRowIndex, 2);
        Place(rowsGrid, rowTools, gridRowIndex, 3);
        trackRows.Add(trackRow);
    }

    /// <summary>The track's name pad: press to hear its sound, and it selects the track for the devices panel.</summary>
    private Border ComposeTrackPad(SlawTrack track, int trackIndex, SlawClip shownClip)
    {
        var nameCaption = StudioUi.Caption(track.Name, 12, StudioTheme.Text, FontWeight.SemiBold);
        nameCaption.TextTrimming = TextTrimming.CharacterEllipsis;
        var instrumentCaption = StudioUi.Caption(DeviceCatalog.InstrumentDisplayName(track.Instrument), 9, StudioTheme.TextMuted);
        instrumentCaption.TextTrimming = TextTrimming.CharacterEllipsis;
        var trackPad = new Border
        {
            Height = StepRowHeight,
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.TrackBrush(trackIndex),
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { nameCaption, instrumentCaption } },
        };
        ToolTip.SetTip(trackPad, $"{track.Name}\npress: play its sound and select it (devices below) · right-click: pattern tools");
        trackPad.PointerPressed += (_, pointerEvent) =>
        {
            if (!pointerEvent.GetCurrentPoint(trackPad).Properties.IsLeftButtonPressed)
            {
                return;
            }
            pointerEvent.Handled = true;
            pointerEvent.Pointer.Capture(trackPad);
            auditioning = true;
            host.StartNoteAudition(track.Id, AuditionNoteFor(shownClip));
            document.Select(trackIndex, shownSceneIndex);
        };
        trackPad.PointerReleased += (_, _) => StopAudition();
        trackPad.PointerCaptureLost += (_, _) => StopAudition();
        StudioDragDrop.MakeDropTarget(trackPad, host, () => new DropTarget(DropTargetKind.Track, trackIndex));
        return trackPad;
    }

    private void StopAudition()
    {
        if (auditioning)
        {
            auditioning = false;
            host.StopNoteAudition();
        }
    }

    /// <summary>The clip's first note, so a bass pad plays the bass in its register; drums and empty clips use the default note.</summary>
    private static int AuditionNoteFor(SlawClip clip) =>
        clip.Pattern.Select(NotationResolver.ResolveOrRest).FirstOrDefault(stepEvent => stepEvent.Kind == StepEventKind.Trigger) is { Kind: StepEventKind.Trigger } firstTrigger
            ? firstTrigger.MidiNote
            : NotationResolver.DefaultTriggerMidiNote;

    private List<Control> ComposePatternTools(TrackRow trackRow)
    {
        var stepCount = trackRow.Clip.Pattern.Count;
        var tools = new List<Control>();

        var euclideanItem = new MenuItem { Header = "Euclidean rhythm" };
        for (var hitCount = 1; hitCount <= Math.Min(stepCount, MaximumEuclideanHitChoices); hitCount++)
        {
            var capturedHits = hitCount;
            AddMenuItem(euclideanItem.Items, string.Create(CultureInfo.InvariantCulture, $"{hitCount} {(hitCount == 1 ? "hit" : "hits")} over {stepCount} steps"),
                () => ApplyToRow(trackRow, (rowTrack, rowClip) => GrooveboxPatternOperations.EuclideanFill(rowTrack, rowClip, capturedHits)));
        }
        tools.Add(euclideanItem);

        var diceItem = new MenuItem { Header = "Roll the dice" };
        foreach (var (densityLabel, density) in DiceDensities)
        {
            AddMenuItem(diceItem.Items, densityLabel,
                () => ApplyToRow(trackRow, (rowTrack, rowClip) => GrooveboxPatternOperations.Randomize(rowTrack, rowClip, density, patternDice)));
        }
        tools.Add(diceItem);

        tools.Add(new Separator());
        AddMenuItem(tools, "Shift one step earlier", () => ApplyToRow(trackRow, (rowTrack, rowClip) => GrooveboxPatternOperations.Rotate(rowTrack, rowClip, -1)));
        AddMenuItem(tools, "Shift one step later", () => ApplyToRow(trackRow, (rowTrack, rowClip) => GrooveboxPatternOperations.Rotate(rowTrack, rowClip, 1)));
        if (stepCount * 2 <= ProjectAlignmentRemediator.MaximumStepCount)
        {
            AddMenuItem(tools, string.Create(CultureInfo.InvariantCulture, $"Double length ({stepCount * 2} steps, repeats)"),
                () => ApplyToRow(trackRow, (rowTrack, rowClip) => GrooveboxPatternOperations.SetLength(rowTrack, rowClip, stepCount * 2)));
        }
        if (stepCount >= 2)
        {
            AddMenuItem(tools, string.Create(CultureInfo.InvariantCulture, $"Halve length ({stepCount / 2} steps)"),
                () => ApplyToRow(trackRow, (rowTrack, rowClip) => GrooveboxPatternOperations.SetLength(rowTrack, rowClip, stepCount / 2)));
        }
        AddMenuItem(tools, "Clear pattern", () => ApplyToRow(trackRow, GrooveboxPatternOperations.Clear));
        if (!trackRow.IsStandIn)
        {
            tools.Add(new Separator());
            AddMenuItem(tools, "Launch only this track's clip", () => host.LaunchClip(trackRow.TrackIndex, shownSceneIndex));
            AddMenuItem(tools, "Delete this clip", () => document.DeleteClip(trackRow.TrackIndex, shownSceneIndex));
        }
        return tools;
    }

    private static void AddMenuItem(ContextMenu targetMenu, string header, Action onClick) => AddMenuItem(targetMenu.Items, header, onClick);

    private static void AddMenuItem(System.Collections.IList targetItems, string header, Action onClick)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += (_, _) => onClick();
        targetItems.Add(menuItem);
    }

    private static void AddMenuItem(List<Control> targetItems, string header, Action onClick)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += (_, _) => onClick();
        targetItems.Add(menuItem);
    }

    // ── Editing ───────────────────────────────────────────────────────────────

    /// <summary>A pointer edit on a row. A stand-in becomes the track's clip in this pattern, in the same undo step.</summary>
    private void CommitStepEdit(TrackRow trackRow, string editDescription)
    {
        if (trackRow.IsStandIn && trackRow.Track.ClipAt(shownSceneIndex) is null)
        {
            PlaceClip(trackRow.Track, shownSceneIndex, trackRow.Clip);
            document.CommitEdit($"{editDescription} (new clip in pattern {document.Project.Scenes[shownSceneIndex]})", EditScope.Structure);
            return;
        }
        document.CommitEdit(editDescription);
    }

    private void ApplyToRow(TrackRow trackRow, Func<SlawTrack, SlawClip, string?> patternOperation) =>
        ApplyPatternOperation(document, trackRow.Track, trackRow.IsStandIn ? null : trackRow.Clip, shownSceneIndex, sessionClip: true,
            (operatedTrack, operatedClip, _) => patternOperation(operatedTrack, operatedClip));

    /// <summary>
    /// Runs a groovebox pattern tool on the selected clip: the arrangement clip if one is selected,
    /// otherwise the selected track's clip in the selected scene (created if the slot is empty).
    /// Returns what changed, or null when nothing did.
    /// </summary>
    public string? ApplyToSelection(Func<SlawTrack, SlawClip, bool, string?> patternOperation)
    {
        if (document.SelectedTrack is not { } selectedTrack)
        {
            return null;
        }
        if (document.SelectedTimelineClip is { } timelineClip)
        {
            return ApplyPatternOperation(document, selectedTrack, timelineClip.Clip, -1, sessionClip: false, patternOperation);
        }
        if (document.Project.Scenes.Count == 0)
        {
            return null;
        }
        var sceneIndex = document.SelectedSceneIndex;
        return ApplyPatternOperation(document, selectedTrack, selectedTrack.ClipAt(sceneIndex), sceneIndex, sessionClip: true, patternOperation);
    }

    /// <summary>
    /// Tries the tool on a copy first, so a tool that would change nothing leaves no undo step,
    /// then applies the result as one edit. A null <paramref name="targetClip"/> is an empty
    /// session slot, which gets the result as its new clip.
    /// </summary>
    private static string? ApplyPatternOperation(StudioDocument document, SlawTrack track, SlawClip? targetClip, int sceneIndex, bool sessionClip,
        Func<SlawTrack, SlawClip, bool, string?> patternOperation)
    {
        var workingClip = targetClip is null
            ? SlawClip.Empty(document.Project.Scenes[sceneIndex], document.Project.Steps)
            : FreeTimeline.CopyClip(targetClip);
        var description = patternOperation(track, workingClip, sessionClip);
        if (description is null)
        {
            return null;
        }
        document.Edit(description, _ =>
        {
            if (targetClip is null)
            {
                PlaceClip(track, sceneIndex, workingClip);
            }
            else
            {
                targetClip.Pattern = workingClip.Pattern;
                targetClip.Automation = workingClip.Automation;
            }
        }, EditScope.Structure);
        return description;
    }

    private static void PlaceClip(SlawTrack track, int sceneIndex, SlawClip clip)
    {
        while (track.Clips.Count <= sceneIndex)
        {
            track.Clips.Add(null);
        }
        track.Clips[sceneIndex] = clip;
    }

    // ── Live state ────────────────────────────────────────────────────────────

    /// <summary>Whether the rows still show the project's tracks and clips (an edit elsewhere may have swapped them).</summary>
    private bool RowsAreCurrent()
    {
        var project = document.Project;
        if (trackRows.Count != (project.Scenes.Count > 0 ? project.Tracks.Count : 0) || shownSceneIndex != Math.Clamp(document.SelectedSceneIndex, 0, Math.Max(0, project.Scenes.Count - 1)))
        {
            return false;
        }
        foreach (var trackRow in trackRows)
        {
            if (project.Tracks[trackRow.TrackIndex] != trackRow.Track ||
                (trackRow.IsStandIn ? trackRow.Track.ClipAt(shownSceneIndex) is not null : trackRow.Track.ClipAt(shownSceneIndex) != trackRow.Clip))
            {
                return false;
            }
        }
        return true;
    }

    public void RefreshContent()
    {
        if (!RowsAreCurrent())
        {
            Rebuild();
            return;
        }
        foreach (var trackRow in trackRows)
        {
            trackRow.MuteToggle.IsChecked = trackRow.Track.Muted;
            trackRow.SoloToggle.IsChecked = trackRow.Track.Solo;
            trackRow.Steps.InvalidateVisual();
        }
    }

    public void RefreshSelection()
    {
        if (!RowsAreCurrent())
        {
            Rebuild();
            return;
        }
        foreach (var trackRow in trackRows)
        {
            trackRow.Pad.Background = trackRow.TrackIndex == document.SelectedTrackIndex ? StudioTheme.Border : StudioTheme.Surface;
        }
        RefreshPatternPads();
    }

    /// <summary>Scrolls a track's row into sight, once the rebuilt view has been laid out.</summary>
    public void RevealTrack(int trackIndex) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (trackRows.FirstOrDefault(trackRow => trackRow.TrackIndex == trackIndex) is { } revealedRow)
            {
                revealedRow.Pad.BringIntoView();
            }
        }, DispatcherPriority.Background);

    public void UpdateTransport(TransportSnapshot transportSnapshot)
    {
        lastTransport = transportSnapshot;
        int? leadStep = null;
        var leadLength = 0;
        foreach (var trackRow in trackRows)
        {
            transportSnapshot.Tracks.TryGetValue(trackRow.Track.Id, out var trackState);
            var playingHere = transportSnapshot.Mode != TransportMode.Stopped && !trackRow.IsStandIn &&
                              trackState.PlayingScene == shownSceneIndex && trackState.ClipStep is not null;
            trackRow.Steps.PlayheadStep = playingHere ? trackState.ClipStep : null;
            if (playingHere && trackRow.Clip.Pattern.Count > leadLength)
            {
                leadLength = trackRow.Clip.Pattern.Count;
                leadStep = trackState.ClipStep;
            }
        }
        if (followPlayhead && leadStep is { } followedStep && pageCount > 1)
        {
            var playingPage = Math.Clamp(followedStep / GrooveboxPatternOperations.PageLength, 0, pageCount - 1);
            if (playingPage != pageIndex)
            {
                ShowPage(playingPage, keepFollowing: true);
            }
        }
        if (stepRuler is not null)
        {
            stepRuler.PlayheadStep = leadStep;
        }
        RefreshPatternPads();
    }

    private void RefreshPatternPads()
    {
        var transportSnapshot = lastTransport;
        foreach (var (patternPad, sceneIndex) in patternPads)
        {
            var playing = transportSnapshot is { Mode: not TransportMode.Stopped } &&
                          transportSnapshot.Tracks.Values.Any(trackState => trackState.PlayingScene == sceneIndex && trackState.ClipStep is not null);
            var queued = transportSnapshot is not null && transportSnapshot.Tracks.Values.Any(trackState => trackState.QueuedScene == sceneIndex);
            patternPad.Background = playing ? PlayingPatternFill : StudioTheme.SurfaceRaised;
            patternPad.BorderBrush = queued ? StudioTheme.Warning
                : sceneIndex == shownSceneIndex ? StudioTheme.Accent
                : Brushes.Transparent;
        }
    }
}
