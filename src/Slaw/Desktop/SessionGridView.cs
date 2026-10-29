using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The session view: tracks are columns, scenes are rows, each cell is a
/// launchable clip slot, and every column ends in a mixer strip. The right-most
/// column launches whole scenes and holds the master fader.
/// </summary>
internal sealed class SessionGridView : UserControl
{
    private const double TrackColumnWidth = 128;
    private const double SceneColumnWidth = 150;

    private readonly StudioDocument document;
    private readonly IStudioHost host;
    private readonly Dictionary<(int TrackIndex, int SceneIndex), ClipSlotControl> clipSlots = new();
    private readonly List<Button> sceneLaunchButtons = new();

    public SessionGridView(StudioDocument document, IStudioHost host)
    {
        this.document = document;
        this.host = host;
        Rebuild();
    }

    public void Rebuild()
    {
        clipSlots.Clear();
        sceneLaunchButtons.Clear();
        var project = document.Project;
        var trackCount = project.Tracks.Count;
        var sceneCount = project.Scenes.Count;

        var sessionGrid = new Grid { ColumnSpacing = 4, RowSpacing = 3, Margin = new Thickness(12) };
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            sessionGrid.ColumnDefinitions.Add(new ColumnDefinition(TrackColumnWidth, GridUnitType.Pixel));
        }
        sessionGrid.ColumnDefinitions.Add(new ColumnDefinition(SceneColumnWidth, GridUnitType.Pixel));
        sessionGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        // Rows: header, one per scene, add-scene, mixer.
        sessionGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
        {
            sessionGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        sessionGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        sessionGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var mixerRowIndex = sceneCount + 2;

        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            var track = project.Tracks[trackIndex];
            Place(sessionGrid, ComposeTrackHeader(track, trackIndex), 0, trackIndex);
            for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
            {
                Place(sessionGrid, ComposeClipSlot(track, trackIndex, sceneIndex), sceneIndex + 1, trackIndex);
            }
            Place(sessionGrid, ComposeMixerStrip(track, trackIndex), mixerRowIndex, trackIndex);
        }

        var masterHeader = StudioUi.Panel(StudioUi.Caption("Scenes", 12, StudioTheme.Text, FontWeight.Bold), new Thickness(10, 8));
        Place(sessionGrid, masterHeader, 0, trackCount);
        for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
        {
            Place(sessionGrid, ComposeSceneLauncher(sceneIndex), sceneIndex + 1, trackCount);
        }
        Place(sessionGrid, StudioUi.Button("+ Scene", document.AddScene, "Add an empty scene row", compact: true), sceneCount + 1, trackCount);
        Place(sessionGrid, ComposeMasterStrip(), mixerRowIndex, trackCount);

        var addTrackButton = StudioUi.Button("+", () => document.AddTrack("sine"), "Add a track (or pick an instrument in the browser)");
        addTrackButton.VerticalAlignment = VerticalAlignment.Top;
        Place(sessionGrid, addTrackButton, 0, trackCount + 1);

        Content = new ScrollViewer
        {
            Content = sessionGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        RefreshSelection();
    }

    private static void Place(Grid targetGrid, Control placedControl, int rowIndex, int columnIndex)
    {
        Grid.SetRow(placedControl, rowIndex);
        Grid.SetColumn(placedControl, columnIndex);
        targetGrid.Children.Add(placedControl);
    }

    // ── Cells ─────────────────────────────────────────────────────────────────

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
            MinHeight = 22,
        };
        nameInput.LostFocus += (_, _) =>
        {
            var revisedName = nameInput.Text?.Trim();
            if (!string.IsNullOrEmpty(revisedName) && revisedName != track.Name)
            {
                document.Edit($"renamed a track to {revisedName}", _ => track.Name = revisedName);
            }
        };
        nameInput.GotFocus += (_, _) => document.Select(trackIndex, document.SelectedSceneIndex);

        var headerContent = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                nameInput,
                StudioUi.Caption(DeviceCatalog.InstrumentDisplayName(track.Instrument), 10),
            },
        };
        return new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.TrackBrush(trackIndex),
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4),
            Child = headerContent,
        };
    }

    private Control ComposeClipSlot(SlawTrack track, int trackIndex, int sceneIndex)
    {
        var clipSlot = new ClipSlotControl(track, trackIndex, sceneIndex);
        clipSlots[(trackIndex, sceneIndex)] = clipSlot;
        clipSlot.Selected += () => document.Select(trackIndex, sceneIndex);
        clipSlot.Activated += () =>
        {
            if (track.ClipAt(sceneIndex) is null)
            {
                document.CreateClip(trackIndex, sceneIndex);
            }
            else
            {
                document.Select(trackIndex, sceneIndex);
            }
        };
        clipSlot.LaunchRequested += () =>
        {
            document.Select(trackIndex, sceneIndex);
            if (track.ClipAt(sceneIndex) is null)
            {
                host.StopTrack(trackIndex);
            }
            else
            {
                host.LaunchClip(trackIndex, sceneIndex);
            }
        };

        var slotMenu = new ContextMenu();
        slotMenu.Opening += (_, _) =>
        {
            slotMenu.Items.Clear();
            var clipExists = track.ClipAt(sceneIndex) is not null;
            if (!clipExists)
            {
                AddMenuItem(slotMenu, "Create clip", () => document.CreateClip(trackIndex, sceneIndex));
            }
            else
            {
                AddMenuItem(slotMenu, "Launch clip", () => host.LaunchClip(trackIndex, sceneIndex));
                AddMenuItem(slotMenu, "Duplicate to next scene", () => document.DuplicateClipDown(trackIndex, sceneIndex));
                AddMenuItem(slotMenu, "Delete clip", () => document.DeleteClip(trackIndex, sceneIndex));
            }
            if (host.AiFeaturesEnabled)
            {
                AddMenuItem(slotMenu, clipExists ? "✨ Rewrite clip with AI" : "✨ Generate clip with AI", () => host.GenerateClipWithAi(trackIndex, sceneIndex));
            }
        };
        clipSlot.ContextMenu = slotMenu;
        return clipSlot;
    }

    private Control ComposeSceneLauncher(int sceneIndex)
    {
        var launchButton = StudioUi.Button("▶", () => host.LaunchScene(sceneIndex), "Launch every clip in this scene", compact: true);
        sceneLaunchButtons.Add(launchButton);
        var sceneNameInput = new TextBox
        {
            Text = document.Project.Scenes[sceneIndex],
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0),
            MinHeight = 22,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sceneNameInput.LostFocus += (_, _) =>
        {
            var revisedName = sceneNameInput.Text?.Trim();
            if (!string.IsNullOrEmpty(revisedName) && revisedName != document.Project.Scenes[sceneIndex])
            {
                document.Edit($"renamed a scene to {revisedName}", project => project.Scenes[sceneIndex] = revisedName);
            }
        };

        var sceneMenu = new ContextMenu();
        AddMenuItem(sceneMenu, "Launch scene", () => host.LaunchScene(sceneIndex));
        AddMenuItem(sceneMenu, "Append to arrangement", () => document.Edit("appended a scene to the arrangement",
            project => project.Arrangement.Add(new ArrangementSection { Scene = sceneIndex, Repeats = 2 }), EditScope.Structure));
        AddMenuItem(sceneMenu, "Duplicate scene", () => document.DuplicateScene(sceneIndex));
        AddMenuItem(sceneMenu, "Delete scene", () => document.RemoveScene(sceneIndex));

        var launcherLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
        Grid.SetColumn(sceneNameInput, 1);
        launcherLayout.Children.Add(launchButton);
        launcherLayout.Children.Add(sceneNameInput);
        return new Border
        {
            Background = StudioTheme.Surface,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 2),
            Height = 30,
            Child = launcherLayout,
            ContextMenu = sceneMenu,
        };
    }

    private static void AddMenuItem(ContextMenu targetMenu, string header, Action onClick)
    {
        var menuItem = new MenuItem { Header = header };
        menuItem.Click += (_, _) => onClick();
        targetMenu.Items.Add(menuItem);
    }

    private Control ComposeMixerStrip(SlawTrack track, int trackIndex)
    {
        var stopButton = StudioUi.Button("■", () => host.StopTrack(trackIndex), "Stop this track's clip", compact: true);

        var panSpecification = new ParameterSpecification("pan", "Pan", -1, 1, 0, "");
        var panKnob = StudioUi.Knob(panSpecification, track.Pan);
        panKnob.EditStarted += () => document.BeginEdit("pan");
        panKnob.ValueChanged += panValue =>
        {
            track.Pan = Math.Round(panValue, 2);
            document.CommitEdit("panned a track", notifyCopilot: false);
        };

        var gainReadout = StudioUi.Caption(StudioUi.GainToDecibels(track.Gain), 10, StudioTheme.Text);
        var volumeFader = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0,
            Maximum = 1.5,
            Value = track.Gain,
            Height = 112,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(volumeFader, "Track volume");
        var faderGestureActive = false;
        volumeFader.ValueChanged += (_, _) =>
        {
            if (Math.Abs(volumeFader.Value - track.Gain) < 0.0005)
            {
                return;
            }
            if (!faderGestureActive)
            {
                document.BeginEdit("volume");
                faderGestureActive = true;
            }
            track.Gain = Math.Round(volumeFader.Value, 3);
            gainReadout.Text = StudioUi.GainToDecibels(track.Gain);
            document.CommitEdit("changed a track volume", notifyCopilot: false);
        };
        volumeFader.AddHandler(PointerReleasedEvent, (_, _) => faderGestureActive = false, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        var muteToggle = new ToggleButton { Content = "M", IsChecked = track.Muted, Padding = new Thickness(8, 3), FontSize = 12 };
        ToolTip.SetTip(muteToggle, "Mute");
        muteToggle.IsCheckedChanged += (_, _) =>
        {
            if (track.Muted != (muteToggle.IsChecked == true))
            {
                document.Edit($"{(muteToggle.IsChecked == true ? "muted" : "unmuted")} {track.Name}", _ => track.Muted = muteToggle.IsChecked == true);
            }
        };
        var soloToggle = new ToggleButton { Content = "S", IsChecked = track.Solo, Padding = new Thickness(8, 3), FontSize = 12 };
        ToolTip.SetTip(soloToggle, "Solo");
        soloToggle.IsCheckedChanged += (_, _) =>
        {
            if (track.Solo != (soloToggle.IsChecked == true))
            {
                document.Edit($"{(soloToggle.IsChecked == true ? "soloed" : "unsoloed")} {track.Name}", _ => track.Solo = soloToggle.IsChecked == true);
            }
        };
        var toggleRow = StudioUi.Row(4, muteToggle, soloToggle);

        if (host.AiFeaturesEnabled)
        {
            var aiLockToggle = new ToggleButton { Content = "🔒", IsChecked = track.AiLocked, Padding = new Thickness(6, 3), FontSize = 11 };
            ToolTip.SetTip(aiLockToggle, "Protect this track from AI edits. The autopilot will leave it exactly as it is.");
            aiLockToggle.IsCheckedChanged += (_, _) =>
            {
                if (track.AiLocked != (aiLockToggle.IsChecked == true))
                {
                    document.Edit($"{(aiLockToggle.IsChecked == true ? "protected" : "unprotected")} {track.Name} from the AI", _ => track.AiLocked = aiLockToggle.IsChecked == true);
                }
            };
            toggleRow.Children.Add(aiLockToggle);
        }

        var stripFooter = StudioUi.Row(4,
            StudioUi.Button("✕", () => document.RemoveTrack(trackIndex), "Delete track", compact: true));
        if (host.AiFeaturesEnabled)
        {
            stripFooter.Children.Add(StudioUi.AiButton("✨ Name", () => host.RenameTrackWithAi(trackIndex), "Let the AI rebrand this track"));
        }

        var stripContent = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                stopButton,
                panKnob,
                volumeFader,
                gainReadout,
                toggleRow,
                stripFooter,
            },
        };
        foreach (var stripChild in stripContent.Children)
        {
            stripChild.HorizontalAlignment = HorizontalAlignment.Center;
        }
        return new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.TrackBrush(trackIndex),
            BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 8),
            Child = stripContent,
        };
    }

    private Control ComposeMasterStrip()
    {
        var masterChain = document.Project.Master;
        var gainReadout = StudioUi.Caption(StudioUi.GainToDecibels(masterChain.Gain), 10, StudioTheme.Text);
        var masterFader = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0,
            Maximum = 2,
            Value = masterChain.Gain,
            Height = 112,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var faderGestureActive = false;
        masterFader.ValueChanged += (_, _) =>
        {
            if (Math.Abs(masterFader.Value - document.Project.Master.Gain) < 0.0005)
            {
                return;
            }
            if (!faderGestureActive)
            {
                document.BeginEdit("master volume");
                faderGestureActive = true;
            }
            document.Project.Master.Gain = Math.Round(masterFader.Value, 3);
            gainReadout.Text = StudioUi.GainToDecibels(document.Project.Master.Gain);
            document.CommitEdit("changed the master volume", notifyCopilot: false);
        };
        masterFader.AddHandler(PointerReleasedEvent, (_, _) => faderGestureActive = false, Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        var stripContent = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                StudioUi.Button("■ Stop all clips", host.StopAll, "Stop every clip", compact: true),
                StudioUi.Caption("Master", 12, StudioTheme.Text, FontWeight.Bold),
                masterFader,
                gainReadout,
                StudioUi.Caption("hearing protection: on", 9, StudioTheme.TextDim),
            },
        };
        foreach (var stripChild in stripContent.Children)
        {
            stripChild.HorizontalAlignment = HorizontalAlignment.Center;
        }
        return new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.Accent,
            BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 8),
            Child = stripContent,
        };
    }

    // ── Live state ────────────────────────────────────────────────────────────

    public void RefreshSelection()
    {
        foreach (var ((trackIndex, sceneIndex), clipSlot) in clipSlots)
        {
            var nowSelected = trackIndex == document.SelectedTrackIndex && sceneIndex == document.SelectedSceneIndex;
            if (clipSlot.IsSelected != nowSelected)
            {
                clipSlot.IsSelected = nowSelected;
                clipSlot.InvalidateVisual();
            }
        }
    }

    public void RefreshContent()
    {
        foreach (var clipSlot in clipSlots.Values)
        {
            clipSlot.InvalidateVisual();
        }
    }

    public void UpdateTransport(TransportSnapshot transportSnapshot)
    {
        var project = document.Project;
        foreach (var ((trackIndex, sceneIndex), clipSlot) in clipSlots)
        {
            if (trackIndex >= project.Tracks.Count)
            {
                continue;
            }
            var track = project.Tracks[trackIndex];
            transportSnapshot.Tracks.TryGetValue(track.Id, out var trackState);
            var nowPlaying = transportSnapshot.Mode != TransportMode.Stopped && trackState.PlayingScene == sceneIndex && trackState.ClipStep is not null;
            var nowQueued = trackState.QueuedScene == sceneIndex;
            double? playbackFraction = nowPlaying && track.ClipAt(sceneIndex) is { Pattern.Count: > 0 } playingClip
                ? (trackState.ClipStep!.Value + 0.5) / playingClip.Pattern.Count
                : null;
            if (clipSlot.IsPlaying != nowPlaying || clipSlot.IsQueued != nowQueued || clipSlot.PlaybackFraction != playbackFraction)
            {
                clipSlot.IsPlaying = nowPlaying;
                clipSlot.IsQueued = nowQueued;
                clipSlot.PlaybackFraction = playbackFraction;
                clipSlot.InvalidateVisual();
            }
        }
    }
}
