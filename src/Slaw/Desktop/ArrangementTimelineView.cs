using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The arrangement view: the song as an ordered list of scenes, drawn as a
/// timeline with one lane per track. Sections can be re-pointed at any scene,
/// repeated, reordered, removed, and played from.
/// </summary>
internal sealed class ArrangementTimelineView : UserControl
{
    private const double TrackLabelWidth = 210;
    private const double PixelsPerStep = 5;
    private const double MinimumSectionWidth = 236;

    private readonly StudioDocument document;
    private readonly IStudioHost host;
    private readonly List<List<ArrangementLaneControl>> lanesBySection = new();

    public ArrangementTimelineView(StudioDocument document, IStudioHost host)
    {
        this.document = document;
        this.host = host;
        Rebuild();
    }

    public void Rebuild()
    {
        lanesBySection.Clear();
        var project = document.Project;
        var timelineGrid = new Grid { ColumnSpacing = 3, RowSpacing = 3, Margin = new Thickness(12) };
        timelineGrid.ColumnDefinitions.Add(new ColumnDefinition(TrackLabelWidth, GridUnitType.Pixel));
        for (var sectionIndex = 0; sectionIndex < project.Arrangement.Count; sectionIndex++)
        {
            var section = project.Arrangement[sectionIndex];
            var sectionWidth = Math.Max(MinimumSectionWidth, project.SceneLengthInSteps(section.Scene) * section.Repeats * PixelsPerStep);
            timelineGrid.ColumnDefinitions.Add(new ColumnDefinition(sectionWidth, GridUnitType.Pixel));
        }
        timelineGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var rowIndex = 0; rowIndex <= project.Tracks.Count; rowIndex++)
        {
            timelineGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        var totalSteps = project.ArrangementLengthInSteps();
        var totalSeconds = totalSteps * project.StepDurationSeconds;
        Place(timelineGrid, StudioUi.Panel(new StackPanel
        {
            Children =
            {
                StudioUi.Caption("Arrangement", 12, StudioTheme.Text, FontWeight.Bold),
                StudioUi.Caption(FormattableString.Invariant($"{totalSteps / 16.0:0.#} bars · {(int)totalSeconds / 60}:{(int)totalSeconds % 60:00}"), 10),
            },
        }, new Thickness(8, 6)), 0, 0);

        for (var trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
        {
            Place(timelineGrid, ComposeTrackHeader(project.Tracks[trackIndex], trackIndex), trackIndex + 1, 0);
        }

        for (var sectionIndex = 0; sectionIndex < project.Arrangement.Count; sectionIndex++)
        {
            var section = project.Arrangement[sectionIndex];
            Place(timelineGrid, ComposeSectionHeader(sectionIndex), 0, sectionIndex + 1);
            var sectionLanes = new List<ArrangementLaneControl>();
            for (var trackIndex = 0; trackIndex < project.Tracks.Count; trackIndex++)
            {
                var laneControl = new ArrangementLaneControl(project.Tracks[trackIndex], trackIndex, section.Scene, section.Repeats)
                {
                    SceneLengthInSteps = project.SceneLengthInSteps(section.Scene),
                };
                var capturedSectionIndex = sectionIndex;
                var capturedTrackIndex = trackIndex;
                var capturedSceneIndex = section.Scene;
                laneControl.PointerPressed += (_, pointerEvent) =>
                {
                    document.Select(capturedTrackIndex, capturedSceneIndex);
                    if (pointerEvent.ClickCount >= 2)
                    {
                        host.PlayArrangementFrom(capturedSectionIndex);
                    }
                };
                laneControl.ContextMenu = ComposeLaneMenu(capturedTrackIndex, capturedSceneIndex);
                StudioDragDrop.MakeDropTarget(laneControl, host, () => new DropTarget(DropTargetKind.ClipSlot, capturedTrackIndex, capturedSceneIndex));
                ToolTip.SetTip(laneControl, $"{project.Tracks[trackIndex].Name} · {project.Scenes[section.Scene]} — click to edit below, double-click to play from here");
                sectionLanes.Add(laneControl);
                Place(timelineGrid, laneControl, trackIndex + 1, sectionIndex + 1);
            }
            lanesBySection.Add(sectionLanes);
        }

        var addSectionButton = new Button { Content = "+ Section", VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 5), Margin = new Thickness(6, 0, 0, 0) };
        var sceneMenu = new MenuFlyout();
        for (var sceneIndex = 0; sceneIndex < project.Scenes.Count; sceneIndex++)
        {
            var capturedSceneIndex = sceneIndex;
            var sceneMenuItem = new MenuItem { Header = project.Scenes[sceneIndex] };
            sceneMenuItem.Click += (_, _) => document.Edit("added an arrangement section",
                editedProject => editedProject.Arrangement.Add(new ArrangementSection { Scene = capturedSceneIndex, Repeats = 2 }), EditScope.Structure);
            sceneMenu.Items.Add(sceneMenuItem);
        }
        addSectionButton.Flyout = sceneMenu;
        StudioDragDrop.MakeDropTarget(addSectionButton, host, () => new DropTarget(DropTargetKind.ArrangementEnd));
        Place(timelineGrid, addSectionButton, 0, project.Arrangement.Count + 1);

        RefreshSelection();
        var timelineScroller = new ScrollViewer
        {
            Background = Brushes.Transparent,
            Content = new StackPanel
            {
                Children =
                {
                    timelineGrid,
                    new TextBlock
                    {
                        Text = "Click a block to edit that track's clip below · right-click for clip actions · double-click to play from there · drag ⠿ to reorder sections · drop instruments, effects or samples onto tracks",
                        FontSize = 10,
                        Foreground = StudioTheme.TextMuted,
                        Margin = new Thickness(14, 0, 14, 10),
                    },
                },
            },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        StudioDragDrop.MakeDropTarget(timelineScroller, host, () => new DropTarget(DropTargetKind.EmptyArea));
        Content = timelineScroller;
    }

    private Control ComposeTrackHeader(SlawTrack track, int trackIndex)
    {
        var nameInput = new TextBox
        {
            Text = track.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0),
            MinHeight = 20,
        };
        nameInput.GotFocus += (_, _) => document.Select(trackIndex, document.SelectedSceneIndex);
        nameInput.LostFocus += (_, _) =>
        {
            var revisedName = nameInput.Text?.Trim();
            if (!string.IsNullOrEmpty(revisedName) && revisedName != track.Name)
            {
                document.Edit($"renamed a track to {revisedName}", _ => track.Name = revisedName, EditScope.Structure);
            }
        };

        Control TrackToggle(string caption, string toolTip, Func<bool> readState, Action<bool> writeState, string editVerb)
        {
            var trackToggle = new Avalonia.Controls.Primitives.ToggleButton { Content = caption, IsChecked = readState(), Padding = new Thickness(6, 1), FontSize = 11, MinHeight = 20 };
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

        var volumeSlider = new Slider { Minimum = 0, Maximum = 1.5, Value = track.Gain, Width = 70, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -8) };
        ToolTip.SetTip(volumeSlider, "Track volume");
        var volumeGestureActive = false;
        volumeSlider.ValueChanged += (_, _) =>
        {
            if (Math.Abs(volumeSlider.Value - track.Gain) < 0.0005)
            {
                return;
            }
            if (!volumeGestureActive)
            {
                document.BeginEdit("volume");
                volumeGestureActive = true;
            }
            track.Gain = Math.Round(volumeSlider.Value, 3);
            document.CommitEdit("changed a track volume", notifyCopilot: false);
        };
        volumeSlider.AddHandler(PointerReleasedEvent, (_, _) => volumeGestureActive = false,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        var controlRow = StudioUi.Row(3,
            TrackToggle("M", "Mute", () => track.Muted, muted => track.Muted = muted, "toggled mute on"),
            TrackToggle("S", "Solo", () => track.Solo, soloed => track.Solo = soloed, "toggled solo on"));
        if (host.AiFeaturesEnabled)
        {
            controlRow.Children.Add(TrackToggle("🔒", "Protect this track from AI edits", () => track.AiLocked, locked => track.AiLocked = locked, "toggled AI protection on"));
        }
        controlRow.Children.Add(volumeSlider);

        var trackHeader = new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.TrackBrush(trackIndex),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 3),
            Height = 52,
            Child = new StackPanel { Spacing = 2, Children = { nameInput, controlRow } },
        };
        trackHeader.PointerPressed += (_, _) => document.Select(trackIndex, document.SelectedSceneIndex);
        StudioDragDrop.MakeDropTarget(trackHeader, host, () => new DropTarget(DropTargetKind.Track, trackIndex));
        return trackHeader;
    }

    private ContextMenu ComposeLaneMenu(int trackIndex, int sceneIndex)
    {
        var laneMenu = new ContextMenu();
        laneMenu.Opening += (_, _) =>
        {
            laneMenu.Items.Clear();
            var clipExists = document.Project.ClipAt(trackIndex, sceneIndex) is not null;
            void AddLaneMenuItem(string header, Action onClick)
            {
                var menuItem = new MenuItem { Header = header };
                menuItem.Click += (_, _) => onClick();
                laneMenu.Items.Add(menuItem);
            }
            if (clipExists)
            {
                AddLaneMenuItem("Edit clip", () => document.Select(trackIndex, sceneIndex));
                AddLaneMenuItem("Delete clip", () => document.DeleteClip(trackIndex, sceneIndex));
            }
            else
            {
                AddLaneMenuItem("Create clip", () => document.CreateClip(trackIndex, sceneIndex));
            }
            if (host.AiFeaturesEnabled)
            {
                AddLaneMenuItem(clipExists ? "✨ Rewrite clip with AI" : "✨ Generate clip with AI", () => host.GenerateClipWithAi(trackIndex, sceneIndex));
            }
        };
        return laneMenu;
    }

    /// <summary>Outlines every lane showing the clip that is open in the detail view.</summary>
    public void RefreshSelection()
    {
        foreach (var laneControl in lanesBySection.SelectMany(sectionLanes => sectionLanes))
        {
            var nowSelected = laneControl.TrackIndex == document.SelectedTrackIndex && laneControl.SceneIndex == document.SelectedSceneIndex;
            if (laneControl.IsSelected != nowSelected)
            {
                laneControl.IsSelected = nowSelected;
                laneControl.InvalidateVisual();
            }
        }
    }

    /// <summary>Redraws lanes after note or mixer edits without rebuilding the timeline.</summary>
    public void RefreshContent()
    {
        foreach (var laneControl in lanesBySection.SelectMany(sectionLanes => sectionLanes))
        {
            laneControl.InvalidateVisual();
        }
    }

    private static void Place(Grid targetGrid, Control placedControl, int rowIndex, int columnIndex)
    {
        Grid.SetRow(placedControl, rowIndex);
        Grid.SetColumn(placedControl, columnIndex);
        targetGrid.Children.Add(placedControl);
    }

    private Control ComposeSectionHeader(int sectionIndex)
    {
        var project = document.Project;
        var section = project.Arrangement[sectionIndex];

        var sceneSelector = new ComboBox
        {
            ItemsSource = project.Scenes.ToList(),
            SelectedIndex = section.Scene,
            FontSize = 12,
            MinWidth = 100,
            Padding = new Thickness(6, 2),
        };
        sceneSelector.SelectionChanged += (_, _) =>
        {
            if (sceneSelector.SelectedIndex >= 0 && sceneSelector.SelectedIndex != section.Scene)
            {
                document.Edit("re-pointed an arrangement section", _ => section.Scene = sceneSelector.SelectedIndex, EditScope.Structure);
            }
        };

        void ChangeRepeats(int repeatDelta)
        {
            var requestedRepeats = Math.Clamp(section.Repeats + repeatDelta, 1, 32);
            if (requestedRepeats != section.Repeats)
            {
                document.Edit("changed section repeats", _ => section.Repeats = requestedRepeats, EditScope.Structure);
            }
        }

        sceneSelector.HorizontalAlignment = HorizontalAlignment.Stretch;
        var repeatsRow = StudioUi.Row(2,
            StudioUi.Button("−", () => ChangeRepeats(-1), "Fewer repeats", compact: true),
            StudioUi.Caption($"{section.Repeats}×", 12, StudioTheme.Text, FontWeight.SemiBold),
            StudioUi.Button("+", () => ChangeRepeats(1), "More repeats", compact: true));
        var sectionGrip = StudioDragDrop.DragGrip("Drag to reorder this section");
        StudioDragDrop.MakeDragSource(sectionGrip, () => new DragPayload(DragPayloadKind.ArrangementSection, Index: sectionIndex));
        var controlRow = StudioUi.Row(2,
            sectionGrip,
            repeatsRow,
            new Border { Width = 6 },
            StudioUi.Button("▶", () => host.PlayArrangementFrom(sectionIndex), "Play the arrangement from this section", compact: true),
            StudioUi.Button("◀", () => MoveSection(sectionIndex, -1), "Move earlier", compact: true),
            StudioUi.Button("▶▶", () => MoveSection(sectionIndex, 1), "Move later", compact: true),
            StudioUi.Button("✕", () => RemoveSection(sectionIndex), "Remove section", compact: true));

        var sectionHeader = StudioUi.Panel(new StackPanel
        {
            Spacing = 4,
            Children = { sceneSelector, controlRow },
        }, new Thickness(6));
        StudioDragDrop.MakeDropTarget(sectionHeader, host, () => new DropTarget(DropTargetKind.ArrangementSection, Index: sectionIndex));
        return sectionHeader;
    }

    private void MoveSection(int sectionIndex, int offset)
    {
        var destinationIndex = sectionIndex + offset;
        if (destinationIndex < 0 || destinationIndex >= document.Project.Arrangement.Count)
        {
            return;
        }
        document.Edit("reordered the arrangement", project =>
        {
            var movedSection = project.Arrangement[sectionIndex];
            project.Arrangement.RemoveAt(sectionIndex);
            project.Arrangement.Insert(destinationIndex, movedSection);
        }, EditScope.Structure);
    }

    private void RemoveSection(int sectionIndex)
    {
        if (document.Project.Arrangement.Count <= 1)
        {
            return;
        }
        document.Edit("removed an arrangement section", project => project.Arrangement.RemoveAt(sectionIndex), EditScope.Structure);
    }

    public void UpdateTransport(TransportSnapshot transportSnapshot)
    {
        var project = document.Project;
        int? playingSection = null;
        double playingFraction = 0;
        if (transportSnapshot.Mode == TransportMode.Arrangement && transportSnapshot.ArrangementStep >= 0 && project.Arrangement.Count > 0)
        {
            var (sectionIndex, stepInSection) = SequencerEngine.LocateArrangementStep(project, transportSnapshot.ArrangementStep);
            var section = project.Arrangement[sectionIndex];
            playingSection = sectionIndex;
            playingFraction = (stepInSection + 0.5) / Math.Max(1, project.SceneLengthInSteps(section.Scene) * section.Repeats);
        }
        for (var sectionIndex = 0; sectionIndex < lanesBySection.Count; sectionIndex++)
        {
            foreach (var laneControl in lanesBySection[sectionIndex])
            {
                laneControl.PlayheadFraction = sectionIndex == playingSection ? playingFraction : null;
            }
        }
    }
}
