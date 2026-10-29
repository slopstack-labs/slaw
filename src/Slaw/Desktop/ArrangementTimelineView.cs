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
    private readonly List<Border> sectionHeaders = new();

    // A section is selected by clicking its header; clicking a clip block selects the clip instead.
    private int? selectedSectionIndex;

    public ArrangementTimelineView(StudioDocument document, IStudioHost host)
    {
        this.document = document;
        this.host = host;
        Rebuild();
    }

    public void Rebuild()
    {
        lanesBySection.Clear();
        sectionHeaders.Clear();
        var project = document.Project;
        if (selectedSectionIndex >= project.Arrangement.Count)
        {
            selectedSectionIndex = null;
        }
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
                    SelectSection(null);
                    document.Select(capturedTrackIndex, capturedSceneIndex);
                    if (pointerEvent.ClickCount >= 2)
                    {
                        host.PlayArrangementFrom(capturedSectionIndex);
                    }
                };
                laneControl.ContextMenu = ComposeLaneMenu(capturedTrackIndex, capturedSceneIndex, capturedSectionIndex);
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
                        Text = "Click a block to edit that track's clip below · right-click for clip and section actions · double-click to play from there · click a section header, then Del to remove it or Ctrl+D to duplicate it · drag ⠿ to reorder sections · drop instruments, effects or samples onto tracks",
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

    private ContextMenu ComposeLaneMenu(int trackIndex, int sceneIndex, int sectionIndex)
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
            laneMenu.Items.Add(new Separator());
            foreach (var sectionMenuItem in ComposeSectionMenuItems(sectionIndex))
            {
                laneMenu.Items.Add(sectionMenuItem);
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
        // The remove button shares the first row with the scene name, so a narrow section can never push it out of view.
        var removeButton = StudioUi.Button("✕", () => RemoveSection(sectionIndex), "Remove this section (Del)", compact: true);
        if (project.Arrangement.Count <= 1)
        {
            removeButton.IsEnabled = false;
            ToolTip.SetTip(removeButton, "The arrangement needs at least one section. Add another before removing this one.");
        }
        removeButton.VerticalAlignment = VerticalAlignment.Center;
        sectionGrip.VerticalAlignment = VerticalAlignment.Center;
        var titleRow = new DockPanel
        {
            Children =
            {
                Docked(sectionGrip, Dock.Left),
                Docked(removeButton, Dock.Right),
                sceneSelector,
            },
        };
        sceneSelector.Margin = new Thickness(2, 0, 4, 0);
        var controlRow = StudioUi.Row(2,
            repeatsRow,
            new Border { Width = 6 },
            StudioUi.Button("▶", () => host.PlayArrangementFrom(sectionIndex), "Play the arrangement from this section", compact: true),
            StudioUi.Button("◀", () => MoveSection(sectionIndex, -1), "Move earlier", compact: true),
            StudioUi.Button("▶▶", () => MoveSection(sectionIndex, 1), "Move later", compact: true));

        var sectionHeader = StudioUi.Panel(new StackPanel
        {
            Spacing = 4,
            Children = { titleRow, controlRow },
        }, new Thickness(6));
        sectionHeader.BorderThickness = new Thickness(1.5);
        sectionHeader.BorderBrush = sectionIndex == selectedSectionIndex ? StudioTheme.Accent : Brushes.Transparent;
        // Tunnel, so a press on the scene picker or a button still selects the section.
        sectionHeader.AddHandler(PointerPressedEvent, (_, _) => SelectSection(sectionIndex), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var sectionMenu = new ContextMenu();
        sectionMenu.Opening += (_, _) =>
        {
            SelectSection(sectionIndex);
            sectionMenu.Items.Clear();
            foreach (var sectionMenuItem in ComposeSectionMenuItems(sectionIndex))
            {
                sectionMenu.Items.Add(sectionMenuItem);
            }
        };
        sectionHeader.ContextMenu = sectionMenu;
        StudioDragDrop.MakeDropTarget(sectionHeader, host, () => new DropTarget(DropTargetKind.ArrangementSection, Index: sectionIndex));
        sectionHeaders.Add(sectionHeader);
        return sectionHeader;
    }

    private static Control Docked(Control dockedControl, Dock dockPosition)
    {
        DockPanel.SetDock(dockedControl, dockPosition);
        return dockedControl;
    }

    private IEnumerable<MenuItem> ComposeSectionMenuItems(int sectionIndex)
    {
        MenuItem SectionMenuItem(string header, Action onClick, bool enabled = true)
        {
            var menuItem = new MenuItem { Header = header, IsEnabled = enabled };
            menuItem.Click += (_, _) => onClick();
            return menuItem;
        }
        var sectionCount = document.Project.Arrangement.Count;
        yield return SectionMenuItem("Play from this section", () => host.PlayArrangementFrom(sectionIndex));
        yield return SectionMenuItem("Duplicate section", () => DuplicateSection(sectionIndex));
        yield return SectionMenuItem("Move section earlier", () => MoveSection(sectionIndex, -1), sectionIndex > 0);
        yield return SectionMenuItem("Move section later", () => MoveSection(sectionIndex, 1), sectionIndex < sectionCount - 1);
        yield return SectionMenuItem(sectionCount > 1 ? "Delete section" : "Delete section (the last one stays)", () => RemoveSection(sectionIndex), sectionCount > 1);
    }

    private void SelectSection(int? sectionIndex)
    {
        selectedSectionIndex = sectionIndex;
        for (var headerIndex = 0; headerIndex < sectionHeaders.Count; headerIndex++)
        {
            sectionHeaders[headerIndex].BorderBrush = headerIndex == sectionIndex ? StudioTheme.Accent : Brushes.Transparent;
        }
    }

    /// <summary>Deletes the section selected by its header (Del in Arrangement view). False if no section is selected.</summary>
    public bool RemoveSelectedSection()
    {
        if (selectedSectionIndex is not { } sectionIndex || sectionIndex >= document.Project.Arrangement.Count)
        {
            return false;
        }
        RemoveSection(sectionIndex);
        return true;
    }

    /// <summary>Duplicates the selected section right after itself (Ctrl+D in Arrangement view). False if none is selected.</summary>
    public bool DuplicateSelectedSection()
    {
        if (selectedSectionIndex is not { } sectionIndex || sectionIndex >= document.Project.Arrangement.Count)
        {
            return false;
        }
        DuplicateSection(sectionIndex);
        return true;
    }

    private void DuplicateSection(int sectionIndex)
    {
        selectedSectionIndex = sectionIndex + 1;
        document.Edit("duplicated an arrangement section", project =>
        {
            var duplicatedSection = project.Arrangement[sectionIndex];
            project.Arrangement.Insert(sectionIndex + 1, new ArrangementSection { Scene = duplicatedSection.Scene, Repeats = duplicatedSection.Repeats });
        }, EditScope.Structure);
    }

    private void MoveSection(int sectionIndex, int offset)
    {
        var destinationIndex = sectionIndex + offset;
        if (destinationIndex < 0 || destinationIndex >= document.Project.Arrangement.Count)
        {
            return;
        }
        selectedSectionIndex = selectedSectionIndex == sectionIndex ? destinationIndex : selectedSectionIndex;
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
        // Keep a section selected, so Del can clear out a run of them.
        selectedSectionIndex = Math.Min(sectionIndex, document.Project.Arrangement.Count - 2);
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
