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
    private const double TrackLabelWidth = 150;
    private const double PixelsPerStep = 5;
    private const double MinimumSectionWidth = 190;

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
            var trackLabel = new Border
            {
                Background = StudioTheme.Surface,
                BorderBrush = StudioTheme.TrackBrush(trackIndex),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(8, 0),
                Height = 34,
                Child = StudioUi.Caption(project.Tracks[trackIndex].Name, 12, StudioTheme.Text),
            };
            Place(timelineGrid, trackLabel, trackIndex + 1, 0);
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
                laneControl.PointerPressed += (_, pointerEvent) =>
                {
                    if (pointerEvent.ClickCount >= 2)
                    {
                        host.PlayArrangementFrom(capturedSectionIndex);
                    }
                };
                ToolTip.SetTip(laneControl, "Double-click to play from here");
                sectionLanes.Add(laneControl);
                Place(timelineGrid, laneControl, trackIndex + 1, sectionIndex + 1);
            }
            lanesBySection.Add(sectionLanes);
        }

        var addSectionButton = new Button { Content = "+ Section", VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 5) };
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
        Place(timelineGrid, addSectionButton, 0, project.Arrangement.Count + 1);

        Content = new ScrollViewer
        {
            Content = timelineGrid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
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

        var repeatsInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 32,
            Value = section.Repeats,
            Increment = 1,
            FormatString = "0×",
            Width = 96,
            FontSize = 12,
        };
        repeatsInput.ValueChanged += (_, _) =>
        {
            if (repeatsInput.Value is { } requestedRepeats && (int)requestedRepeats != section.Repeats)
            {
                document.Edit("changed section repeats", _ => section.Repeats = (int)requestedRepeats, EditScope.Structure);
            }
        };

        var controlRow = StudioUi.Row(2,
            StudioUi.Button("▶", () => host.PlayArrangementFrom(sectionIndex), "Play the arrangement from this section", compact: true),
            StudioUi.Button("◀", () => MoveSection(sectionIndex, -1), "Move earlier", compact: true),
            StudioUi.Button("▶▶", () => MoveSection(sectionIndex, 1), "Move later", compact: true),
            StudioUi.Button("✕", () => RemoveSection(sectionIndex), "Remove section", compact: true));

        return StudioUi.Panel(new StackPanel
        {
            Spacing = 4,
            Children = { StudioUi.Row(4, sceneSelector, repeatsInput), controlRow },
        }, new Thickness(6));
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
