using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Inference;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;
using SlopstackLabs.Slaw.Workflows;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The slaw studio window. Composes the transport bar, browser, session and
/// arrangement views, detail view, and AI panel around one
/// <see cref="StudioDocument"/>, and owns the live audio session plus every
/// AI operation. With AI features switched off it is an ordinary DAW, which is
/// a configuration slopstack labs supports but does not endorse.
/// </summary>
internal sealed partial class StudioWindow : Window, IStudioHost
{
    private enum StatusSeverity
    {
        Informational,
        Success,
        Warning,
        Failure,
    }

    private static readonly FilePickerFileType SlawProjectFileType = new("slaw project") { Patterns = new[] { "*.slaw" } };
    private static readonly FilePickerFileType MidiFileType = new("MIDI file") { Patterns = new[] { "*.mid", "*.midi" } };
    private static readonly FilePickerFileType WaveFileType = new("WAV audio") { Patterns = new[] { "*.wav" } };

    private static readonly string[] CopilotGreetings =
    {
        "It looks like you're making a beat. Would you like help with that?",
        "Hi! I'm your AI co-pilot. I'll be offering advice whether or not you ask.",
        "Welcome back. Your last session's synergy metrics were below forecast.",
    };

    private const string FullSelfProducingInstruction =
        "Keep improving the song. Make one bold but tasteful change per pass, and keep what already works.";

    private readonly StudioDocument document;
    private readonly CancellationTokenSource sessionLifetimeCancellation = new();
    private readonly HashSet<string> aiTunedParameterAddresses = new();
    private readonly DispatcherTimer transportTimer;

    private StudioPreferences preferences;
    private LiveAudioSession liveAudioSession;
    private IInferenceSubstrateProvider? inferenceProvider;
    private string? inferenceUnavailabilityReason;
    private string? pendingInferenceOperationLabel;
    private bool copilotInterjectionInFlight;
    private int editsSinceLastInterjection;
    private bool closeArmed;
    private bool synchronizingControls;
    private bool arrangementViewActive;

    // ── Views and controls ────────────────────────────────────────────────────

    private readonly SessionGridView sessionView;
    private readonly ArrangementTimelineView arrangementView;
    private readonly DetailView detailView;
    private readonly ContentControl mainViewHost = new();
    private readonly ContentControl browserHost = new();
    private readonly ContentControl aiPanelHost = new();
    private readonly ToggleButton sessionViewToggle = new() { Content = "Session", Padding = new Thickness(12, 5) };
    private readonly ToggleButton arrangementViewToggle = new() { Content = "Arrangement", Padding = new Thickness(12, 5) };
    private readonly Button playButton;
    private readonly NumericUpDown tempoInput = new() { Minimum = 40, Maximum = 300, Increment = 1, FormatString = "0", Width = 112, FontSize = 13 };
    private readonly TextBox projectNameInput = new() { Width = 150, Watermark = "song title", VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch aiFeaturesSwitch = new() { OnContent = "AI", OffContent = "AI off", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock routingChip = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly Border routingChipContainer;
    private readonly TextBlock statusText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock audioStatusText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = StudioTheme.TextMuted };
    private readonly ProgressBar inferenceActivityIndicator = new() { IsIndeterminate = true, Width = 90, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly WaveformScopeControl waveformScope = new() { Width = 260, Height = 30 };
    private readonly StackPanel copilotTranscriptPanel = new() { Spacing = 8 };
    private readonly ScrollViewer copilotTranscriptScroller;
    private readonly TextBox aiInstructionInput;
    private readonly ToggleSwitch fullSelfProducingSwitch = new() { OnContent = "Full Self-Producing (Beta)", OffContent = "Full Self-Producing (Beta)", FontSize = 12 };
    private readonly CheckBox unsolicitedAdviceToggle = new() { Content = "Unsolicited advice", FontSize = 12 };
    private readonly List<Control> inferenceDependentControls = new();
    private readonly StackPanel sampleEntriesPanel = new() { Spacing = 1 };
    private readonly TextBox sampleFilterInput = new() { Watermark = "filter samples…", FontSize = 11, MinHeight = 24, Padding = new Thickness(6, 2) };
    private IReadOnlyList<string> sampleLibraryFiles = Array.Empty<string>();
    private readonly Dictionary<string, SampleEditorWindow> openSampleEditorWindows = new();
    private readonly DispatcherTimer inferenceProgressTimer;
    private Mcp.McpHttpTransport? mcpHttpTransport;
    private readonly Button cancelInferenceButton;
    private CancellationTokenSource? activeInferenceCancellation;
    private int activeInferenceReceivedCharacters;
    private System.Diagnostics.Stopwatch activeInferenceStopwatch = new();

    public StudioWindow(StudioLaunchParameters launchParameters)
    {
        preferences = launchParameters.Preferences;
        document = new StudioDocument(launchParameters.Project, launchParameters.ProjectFilePath);
        liveAudioSession = CreateLiveAudioSession();

        Width = 1520;
        Height = 960;
        MinWidth = 1080;
        MinHeight = 680;
        Background = StudioTheme.Background;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        playButton = StudioUi.AccentButton("▶  Play", TogglePlayback, "Play / stop (Space)");
        cancelInferenceButton = StudioUi.Button("✕ Cancel", CancelActiveInference, "Stop waiting for the model", compact: true);
        cancelInferenceButton.IsVisible = false;
        inferenceProgressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshInferenceProgress());
        routingChipContainer = new Border
        {
            Background = StudioTheme.SurfaceRaised,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = routingChip,
        };
        copilotTranscriptScroller = new ScrollViewer { Content = copilotTranscriptPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        aiInstructionInput = new TextBox
        {
            Watermark = "Tell the AI what to change… e.g. \"add a breakdown scene and make the bass angrier\"",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
            FontSize = 12,
        };
        aiInstructionInput.KeyDown += (_, keyEvent) =>
        {
            if (keyEvent.Key == Key.Enter)
            {
                LaunchAutopilot(aiInstructionInput.Text);
                keyEvent.Handled = true;
            }
        };

        sessionView = new SessionGridView(document, this);
        arrangementView = new ArrangementTimelineView(document, this);
        detailView = new DetailView(document, this);

        Content = ComposeLayout();
        WireDocument();
        WireControls();

        transportTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => AdvanceTransportDisplay());
        WireKeyboard();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, HandleDragOver);
        AddHandler(DragDrop.DropEvent, HandleDrop);
        sampleFilterInput.TextChanged += (_, _) => PopulateSampleEntries();
        Closing += HandleWindowClosing;
        Opened += (_, _) =>
        {
            FitToWorkingArea();
            StartLiveAudio();
            ApplyMcpServerPreference();
            ApplyMidiInputPreference();
        };
        Closed += (_, _) =>
        {
            sessionLifetimeCancellation.Cancel();
            transportTimer.Stop();
            mcpHttpTransport?.Dispose();
            midiInputSource?.Dispose();
            CloseAudioInput();
            foreach (var sampleEditorWindow in openSampleEditorWindows.Values.ToList())
            {
                sampleEditorWindow.Close();
            }
            liveAudioSession.Dispose();
        };

        aiFeaturesSwitch.IsChecked = preferences.AiFeaturesEnabled;
        unsolicitedAdviceToggle.IsChecked = preferences.CopilotInterjectionsEnabled;
        MaterializeInferenceProvider(announce: false);
        ApplyAiFeatureVisibility();
        SelectMainView(arrangementView: launchParameters.OpenInArrangementView);
        if (launchParameters.OpenSampleEditor)
        {
            var firstSamplerIndex = document.Project.Tracks.FindIndex(track => track.Instrument == "sampler");
            if (firstSamplerIndex >= 0)
            {
                document.Select(firstSamplerIndex, document.SelectedSceneIndex);
                detailView.ShowSampleTab();
            }
        }
        SynchronizeTransportControls();
        RefreshChrome();
        liveAudioSession.Engine.PublishProject(document.Project.DeepClone());

        if (preferences.AiFeaturesEnabled)
        {
            AppendCopilotMessage("co-pilot", preferences.CopilotInterjectionsEnabled
                ? CopilotGreetings[Random.Shared.Next(CopilotGreetings.Length)]
                : "Unsolicited advice is disabled. I will only speak when spoken to, which is new for me.");
            if (inferenceProvider is null)
            {
                AppendCopilotMessage("system", $"AI unavailable: {inferenceUnavailabilityReason}. Open ⚙ Settings to configure the inference substrate.");
            }
        }
        SetStatus(StatusSeverity.Informational, "ready. launch a clip with its ▶, or press Space.");
        _ = RescanSampleLibraryAsync();
    }

    public bool AiFeaturesEnabled => preferences.AiFeaturesEnabled;

    // ── Layout ────────────────────────────────────────────────────────────────

    private Control ComposeLayout()
    {
        var rootLayout = new DockPanel();

        var transportBar = ComposeTransportBar();
        DockPanel.SetDock(transportBar, Dock.Top);
        rootLayout.Children.Add(transportBar);

        var statusBar = ComposeStatusBar();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        rootLayout.Children.Add(statusBar);

        var browserContainer = new Border
        {
            Width = 196,
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = browserHost,
        };
        DockPanel.SetDock(browserContainer, Dock.Left);
        rootLayout.Children.Add(browserContainer);

        DockPanel.SetDock(aiPanelHost, Dock.Right);
        rootLayout.Children.Add(aiPanelHost);

        var workspaceLayout = new Grid { RowDefinitions = new RowDefinitions("*,5,330") };
        var detailRowDefinition = workspaceLayout.RowDefinitions[2];
        detailView.MinimumHeightRequested += requestedHeight =>
        {
            // Grow for the sample editor, but never shrink below what the user dragged it to,
            // and always leave the session/arrangement view some room.
            var workspaceHeight = workspaceLayout.Bounds.Height > 0 ? workspaceLayout.Bounds.Height : Height - 140;
            var availableHeight = Math.Max(200, workspaceHeight - 180);
            var targetHeight = Math.Min(requestedHeight, availableHeight);
            if (detailRowDefinition.Height.Value < targetHeight)
            {
                detailRowDefinition.Height = new GridLength(targetHeight, GridUnitType.Pixel);
            }
        };
        var horizontalSplitter = new GridSplitter { Background = StudioTheme.Border, ResizeDirection = GridResizeDirection.Rows };
        var detailContainer = new Border { Background = StudioTheme.Background, Child = detailView };
        Grid.SetRow(horizontalSplitter, 1);
        Grid.SetRow(detailContainer, 2);
        workspaceLayout.Children.Add(mainViewHost);
        workspaceLayout.Children.Add(horizontalSplitter);
        workspaceLayout.Children.Add(detailContainer);
        rootLayout.Children.Add(workspaceLayout);
        return rootLayout;
    }

    private Control ComposeTransportBar()
    {
        var brandMark = StudioUi.Row(8,
            new TextBlock { Text = "slaw", FontSize = 22, FontWeight = FontWeight.Black, Foreground = StudioTheme.Accent, VerticalAlignment = VerticalAlignment.Center });

        // A wrap panel rather than a stack: on narrow windows the controls flow
        // onto a second line instead of piling on top of each other.
        var transportControls = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void AddTransportControl(Control transportControl)
        {
            transportControl.Margin = new Thickness(0, 2, 6, 2);
            transportControls.Children.Add(transportControl);
        }

        AddTransportControl(brandMark);
        AddTransportControl(StudioUi.Button("New", () => _ = NewProjectAsync(), "New project (Ctrl+N)", compact: true));
        AddTransportControl(StudioUi.Button("Open…", () => _ = OpenProjectAsync(), "Open (Ctrl+O)", compact: true));
        AddTransportControl(StudioUi.Row(2,
            StudioUi.Button("Save", () => _ = SaveProjectAsync(saveAs: false), "Save (Ctrl+S)", compact: true),
            StudioUi.Button("Save As…", () => _ = SaveProjectAsync(saveAs: true), "Save under a new name (Ctrl+Shift+S)", compact: true)));
        AddTransportControl(StudioUi.Row(2,
            StudioUi.Button("Bounce", () => _ = BounceProjectAsync(), "Render the arrangement to the WAV next to the project (Ctrl+B)", compact: true),
            StudioUi.Button("Bounce As…", () => _ = BounceAsAsync(), "Choose what to render (arrangement, a scene, or stems), the format, and where (Ctrl+Shift+B)", compact: true)));
        AddTransportControl(StudioUi.VerticalSeparator());
        AddTransportControl(StudioUi.Button("↶", PerformUndo, "Undo (Ctrl+Z)", compact: true));
        AddTransportControl(StudioUi.Button("↷", PerformRedo, "Redo (Ctrl+Shift+Z)", compact: true));
        AddTransportControl(StudioUi.VerticalSeparator());
        AddTransportControl(StudioUi.Row(0, sessionViewToggle, arrangementViewToggle));
        AddTransportControl(StudioUi.VerticalSeparator());
        AddTransportControl(playButton);
        AddTransportControl(StudioUi.Button("■", StopPlayback, "Stop", compact: false));
        AddTransportControl(ComposeRecordControls());
        AddTransportControl(ComposeComputerKeyboardControls());
        AddTransportControl(StudioUi.Row(6, StudioUi.Caption("BPM", 12), tempoInput));
        ToolTip.SetTip(projectNameInput, "Song title");
        AddTransportControl(projectNameInput);

        var rightControls = StudioUi.Row(8,
            aiFeaturesSwitch,
            routingChipContainer,
            StudioUi.Button("⚙", () => _ = OpenSettingsAsync(), "Settings (Ctrl+,)", compact: false));
        DockPanel.SetDock(rightControls, Dock.Right);

        return new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 6),
            Child = new DockPanel { Children = { rightControls, transportControls } },
        };
    }

    private Control ComposeStatusBar()
    {
        var statusLayout = new DockPanel();
        var rightGroup = StudioUi.Row(10, audioStatusText, waveformScope);
        DockPanel.SetDock(rightGroup, Dock.Right);
        statusLayout.Children.Add(rightGroup);
        statusLayout.Children.Add(StudioUi.Row(10, inferenceActivityIndicator, cancelInferenceButton, statusText));
        return new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 4),
            Child = statusLayout,
        };
    }

    private Control ComposeBrowser()
    {
        var browserContent = new StackPanel { Spacing = 3, Margin = new Thickness(10) };
        browserContent.Children.Add(StudioUi.SectionHeading("Instruments"));
        foreach (var instrumentName in InstrumentVoiceSynthesizer.SupportedInstruments)
        {
            var instrumentEntry = BrowserEntry(DeviceCatalog.InstrumentDisplayName(instrumentName), () => document.AddTrack(instrumentName),
                "Click to add a track with this instrument, or drag it onto a track to swap its instrument");
            StudioDragDrop.MakeDragSource(instrumentEntry, () => new DragPayload(DragPayloadKind.Instrument, instrumentName));
            browserContent.Children.Add(instrumentEntry);
        }
        browserContent.Children.Add(BrowserEntry("🎙 Audio Track", AddAudioTrack, "Click to add an empty track armed for recording vocals (or anything else with a microphone)"));
        browserContent.Children.Add(new Border { Height = 10 });
        browserContent.Children.Add(StudioUi.SectionHeading("Audio Effects"));
        foreach (var effectType in DeviceCatalog.EffectTypes)
        {
            var effectEntry = BrowserEntry(DeviceCatalog.EffectDisplayName(effectType), () =>
            {
                if (document.SelectedTrack is { } selectedTrack)
                {
                    detailView.AddEffect(selectedTrack, effectType);
                }
            }, "Click to add to the selected track, or drag it onto any track or into the device chain");
            StudioDragDrop.MakeDragSource(effectEntry, () => new DragPayload(DragPayloadKind.Effect, effectType));
            browserContent.Children.Add(effectEntry);
        }
        browserContent.Children.Add(new Border { Height = 10 });
        browserContent.Children.Add(StudioUi.SectionHeading("Samples"));
        browserContent.Children.Add(StudioUi.Row(4,
            StudioUi.Button("+ Folder…", () => _ = AddSampleFolderAsync(), "Add a folder of WAV files to the browser", compact: true),
            StudioUi.Button("⟳", () => _ = RescanSampleLibraryAsync(), "Rescan sample folders", compact: true)));
        browserContent.Children.Add(Detached(sampleFilterInput));
        browserContent.Children.Add(Detached(sampleEntriesPanel));
        PopulateSampleEntries();

        if (AiFeaturesEnabled)
        {
            browserContent.Children.Add(new Border { Height = 10 });
            browserContent.Children.Add(StudioUi.SectionHeading("AI"));
            browserContent.Children.Add(RegisterInference(StudioUi.AiButton("✨ Autopilot", () => LaunchAutopilot(aiInstructionInput.Text), "Let the AI edit the whole project", compact: false)));
            browserContent.Children.Add(RegisterInference(StudioUi.AiButton("✨ Generate clip", () => GenerateClipWithAi(document.SelectedTrackIndex, document.SelectedSceneIndex), "Write the selected clip", compact: false)));
            browserContent.Children.Add(RegisterInference(StudioUi.AiButton("✨ Vibe check", LaunchVibeCheck, "A world-renowned producer and LinkedIn thought leader reviews your song", compact: false)));
            foreach (var aiBrowserButton in browserContent.Children.OfType<Button>().TakeLast(3))
            {
                aiBrowserButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }
        return new ScrollViewer { Content = browserContent };
    }

    private static Button BrowserEntry(string caption, Action onClick, string toolTip)
    {
        var entryButton = new Button
        {
            Content = caption,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 4),
            FontSize = 12,
        };
        entryButton.Click += (_, _) => onClick();
        ToolTip.SetTip(entryButton, toolTip);
        return entryButton;
    }

    private Control ComposeAiPanel()
    {
        var panelHeader = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                StudioUi.Caption("◆ AI Co-Pilot", 15, StudioTheme.Accent, FontWeight.Bold),
                StudioUi.Caption($"Interjects every {preferences.EditsBetweenCopilotInterjections} edits. You cannot stop it, only disable it.", 10),
                Detached(unsolicitedAdviceToggle),
            },
        };

        var quickActions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var quickAction in new[]
        {
            RegisterInference(StudioUi.AiButton("✨ Vibe check", LaunchVibeCheck, "Get your song reviewed")),
            RegisterInference(StudioUi.AiButton("✨ Tempo", LaunchTempoNegotiation, "Renegotiate the BPM")),
            RegisterInference(StudioUi.AiButton("✨ Hallucinate", () => LaunchWaveformHallucination(aiInstructionInput.Text), "Ask the model for raw PCM, as hex, and play whatever comes back")),
        })
        {
            quickAction.Margin = new Thickness(0, 0, 4, 4);
            quickActions.Children.Add(quickAction);
        }

        var autopilotButton = RegisterInference(StudioUi.AiButton("✨ Autopilot", () => LaunchAutopilot(aiInstructionInput.Text),
            "Send the whole project and your instruction to the model; it returns a full rewrite (undoable)", compact: false));
        var composeButton = RegisterInference(StudioUi.AiButton("✨ Compose", () => LaunchComposition(aiInstructionInput.Text),
            "Replace the project with a new AI arrangement of the vibe you typed (undoable)", compact: false));
        autopilotButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        composeButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        var instructionArea = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                quickActions,
                Detached(aiInstructionInput),
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,6,*"),
                    Children = { autopilotButton, PlaceInColumn(composeButton, 2) },
                },
                Detached(fullSelfProducingSwitch),
                StudioUi.Caption("🔒 on a track's mixer strip protects it from every AI edit.", 10),
            },
        };

        var panelLayout = new DockPanel();
        DockPanel.SetDock(panelHeader, Dock.Top);
        DockPanel.SetDock(instructionArea, Dock.Bottom);
        panelLayout.Children.Add(panelHeader);
        panelLayout.Children.Add(instructionArea);
        panelLayout.Children.Add(Detached(copilotTranscriptScroller));

        return new Border
        {
            Width = 300,
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.Border,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(12),
            Child = panelLayout,
        };
    }

    /// <summary>
    /// Long-lived controls (the transcript, the instruction box, the sample list)
    /// are re-hosted whenever the browser or AI panel is rebuilt; Avalonia insists
    /// they leave their old parent first.
    /// </summary>
    private static T Detached<T>(T reusedControl)
        where T : Control
    {
        switch (reusedControl.Parent)
        {
            case Panel previousPanel:
                previousPanel.Children.Remove(reusedControl);
                break;
            case ContentControl previousContentHost:
                previousContentHost.Content = null;
                break;
            case Decorator previousDecorator:
                previousDecorator.Child = null;
                break;
        }
        return reusedControl;
    }

    private static Control PlaceInColumn(Control placedControl, int columnIndex)
    {
        Grid.SetColumn(placedControl, columnIndex);
        return placedControl;
    }

    private T RegisterInference<T>(T inferenceControl)
        where T : Control
    {
        inferenceDependentControls.Add(inferenceControl);
        inferenceControl.IsEnabled = inferenceProvider is not null && pendingInferenceOperationLabel is null;
        return inferenceControl;
    }

    // ── Wiring ────────────────────────────────────────────────────────────────

    private void WireDocument()
    {
        document.PublishRequested += projectSnapshot =>
        {
            // Warm the edited-sample cache off the audio thread, so the engine
            // finds freshly edited samples ready on the next trigger.
            foreach (var samplerTrack in projectSnapshot.Tracks.Where(track => track.Instrument == "sampler" && track.Sample is not null))
            {
                SampleEditProcessor.TryGetProcessed(samplerTrack.Sample!, samplerTrack.SampleEditing, LiveAudioSession.LiveSampleRate);
            }
            liveAudioSession.Engine.PublishProject(projectSnapshot);
        };
        document.Changed += editScope =>
        {
            if (editScope == EditScope.Structure)
            {
                sessionView.Rebuild();
                arrangementView.Rebuild();
                detailView.Rebuild();
            }
            else
            {
                sessionView.RefreshContent();
                arrangementView.RefreshContent();
                detailView.RefreshContent();
            }
            SynchronizeTransportControls();
            RefreshChrome();
        };
        document.SelectionChanged += () =>
        {
            sessionView.RefreshSelection();
            arrangementView.RefreshSelection();
            detailView.Rebuild();
        };
        document.Edited += RegisterEditForCopilot;
    }

    private void WireControls()
    {
        sessionViewToggle.Click += (_, _) => SelectMainView(arrangementView: false);
        StudioDragDrop.MakeDropTarget(arrangementViewToggle, this, () => new DropTarget(DropTargetKind.ArrangementEnd));
        ToolTip.SetTip(arrangementViewToggle, "Arrangement view (Tab) — drop a scene here to append it to the song");
        arrangementViewToggle.Click += (_, _) => SelectMainView(arrangementView: true);
        tempoInput.ValueChanged += (_, _) =>
        {
            if (!synchronizingControls && tempoInput.Value is { } requestedTempo && (double)requestedTempo != document.Project.Bpm)
            {
                document.Edit($"changed the tempo to {requestedTempo:0} BPM", project => project.Bpm = (double)requestedTempo);
            }
        };
        projectNameInput.LostFocus += (_, _) =>
        {
            var revisedName = projectNameInput.Text?.Trim();
            if (!string.IsNullOrEmpty(revisedName) && revisedName != document.Project.Name)
            {
                document.Edit($"retitled the project {revisedName}", project => project.Name = revisedName, notifyCopilotScope());
            }
            static EditScope notifyCopilotScope() => EditScope.Content;
        };
        aiFeaturesSwitch.IsCheckedChanged += (_, _) =>
        {
            if (synchronizingControls || preferences.AiFeaturesEnabled == (aiFeaturesSwitch.IsChecked == true))
            {
                return;
            }
            preferences.AiFeaturesEnabled = aiFeaturesSwitch.IsChecked == true;
            PersistPreferences();
            MaterializeInferenceProvider(announce: false);
            ApplyAiFeatureVisibility();
            SetStatus(StatusSeverity.Informational, preferences.AiFeaturesEnabled
                ? "AI features enabled. The co-pilot has been notified and is thrilled."
                : "AI features disabled. slaw is now a plain DAW. Nobody at slopstack labs has ever done this.");
        };
        unsolicitedAdviceToggle.IsCheckedChanged += (_, _) =>
        {
            if (preferences.CopilotInterjectionsEnabled != (unsolicitedAdviceToggle.IsChecked == true))
            {
                preferences.CopilotInterjectionsEnabled = unsolicitedAdviceToggle.IsChecked == true;
                PersistPreferences();
            }
        };
        fullSelfProducingSwitch.IsCheckedChanged += (_, _) =>
        {
            if (fullSelfProducingSwitch.IsChecked == true)
            {
                AppendCopilotMessage("system", "Full Self-Producing engaged. Keep your hands near the undo button at all times.");
                LaunchAutopilot(string.IsNullOrWhiteSpace(aiInstructionInput.Text) ? FullSelfProducingInstruction : aiInstructionInput.Text);
            }
        };
    }

    /// <summary>Rebuilds every surface whose content depends on the AI master switch.</summary>
    private void ApplyAiFeatureVisibility()
    {
        inferenceDependentControls.Clear();
        browserHost.Content = ComposeBrowser();
        aiPanelHost.Content = AiFeaturesEnabled ? ComposeAiPanel() : null;
        aiPanelHost.IsVisible = AiFeaturesEnabled;
        routingChipContainer.IsVisible = AiFeaturesEnabled;
        if (!AiFeaturesEnabled)
        {
            fullSelfProducingSwitch.IsChecked = false;
        }
        sessionView.Rebuild();
        detailView.Rebuild();
        UpdateInferenceAvailability();
    }

    private void SelectMainView(bool arrangementView)
    {
        arrangementViewActive = arrangementView;
        mainViewHost.Content = arrangementView ? this.arrangementView : sessionView;
        sessionViewToggle.IsChecked = !arrangementView;
        arrangementViewToggle.IsChecked = arrangementView;
    }

    private void SynchronizeTransportControls()
    {
        synchronizingControls = true;
        tempoInput.Value = (decimal)document.Project.Bpm;
        if (!projectNameInput.IsFocused)
        {
            projectNameInput.Text = document.Project.Name;
        }
        synchronizingControls = false;
    }

    private void RefreshChrome()
    {
        var projectLabel = document.FilePath is null ? document.Project.Name : Path.GetFileName(document.FilePath);
        Title = $"slaw — {projectLabel}{(document.UnsavedChanges ? " •" : string.Empty)}";
    }

    private void SetStatus(StatusSeverity severity, string message)
    {
        statusText.Text = message;
        statusText.Foreground = severity switch
        {
            StatusSeverity.Success => StudioTheme.Playhead,
            StatusSeverity.Warning => StudioTheme.Warning,
            StatusSeverity.Failure => StudioTheme.Failure,
            _ => StudioTheme.Text,
        };
    }

    private void AppendCopilotMessage(string speaker, string message)
    {
        var speakerBrush = speaker switch
        {
            "system" => StudioTheme.Warning,
            "you" => StudioTheme.Drum,
            _ => StudioTheme.Accent,
        };
        copilotTranscriptPanel.Children.Add(new Border
        {
            Background = StudioTheme.SurfaceRaised,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = $"{speaker} · {DateTime.Now:HH:mm}", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = speakerBrush },
                    new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = StudioTheme.Text, FontSize = 12 },
                },
            },
        });
        while (copilotTranscriptPanel.Children.Count > 80)
        {
            copilotTranscriptPanel.Children.RemoveAt(0);
        }
        Dispatcher.UIThread.Post(() => copilotTranscriptScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>Never open larger than the screen it lands on.</summary>
    private void FitToWorkingArea()
    {
        if (Screens.ScreenFromWindow(this) is not { } hostScreen)
        {
            return;
        }
        var workingArea = hostScreen.WorkingArea.Size.ToSize(hostScreen.Scaling);
        if (Width > workingArea.Width || Height > workingArea.Height)
        {
            Width = Math.Min(Width, workingArea.Width);
            Height = Math.Min(Height, workingArea.Height);
            Position = hostScreen.WorkingArea.Position;
        }
    }

    // ── Live audio ────────────────────────────────────────────────────────────

    private LiveAudioSession CreateLiveAudioSession()
    {
        var createdSession = new LiveAudioSession(preferences.AudioLatencyMilliseconds, preferences.AudioSubstrate);
        createdSession.Faulted += faultDescription => Dispatcher.UIThread.Post(() =>
        {
            audioStatusText.Text = "audio: offline";
            SetStatus(StatusSeverity.Failure, faultDescription);
        });
        return createdSession;
    }

    private void StartLiveAudio()
    {
        try
        {
            liveAudioSession.Start();
            RefreshAudioStatus();
            transportTimer.Start();
        }
        catch (InvalidOperationException audioRemediationOpportunity)
        {
            audioStatusText.Text = "audio: offline";
            SetStatus(StatusSeverity.Failure, audioRemediationOpportunity.Message);
        }
    }

    private void RestartLiveAudio()
    {
        transportTimer.Stop();
        liveAudioSession.Dispose();
        liveAudioSession = CreateLiveAudioSession();
        liveAudioSession.Engine.PublishProject(document.Project.DeepClone());
        StartLiveAudio();
    }

    private void AdvanceTransportDisplay()
    {
        var transportSnapshot = liveAudioSession.Engine.Transport;
        sessionView.UpdateTransport(transportSnapshot);
        arrangementView.UpdateTransport(transportSnapshot);
        detailView.UpdateTransport(transportSnapshot);
        waveformScope.PlayheadFraction = transportSnapshot.PreviewProgress >= 0 ? transportSnapshot.PreviewProgress : null;
        var nowPlaying = PlaybackToggle.IsAudiblyPlaying(transportSnapshot);
        var playCaption = nowPlaying ? "■  Stop" : "▶  Play";
        if (playButton.Content as string != playCaption)
        {
            playButton.Content = playCaption;
        }
        AdvanceRecordingDisplay();
    }

    private void TogglePlayback()
    {
        var transportSnapshot = liveAudioSession.Engine.Transport;
        if (PlaybackToggle.IsAudiblyPlaying(transportSnapshot) || transportSnapshot.PreviewProgress >= 0)
        {
            StopPlayback();
            return;
        }
        if (transportSnapshot.Mode != TransportMode.Stopped)
        {
            // A session clock still ticking over silent tracks would hold the launch
            // until its next bar; stop it so the scene starts on this press.
            liveAudioSession.Engine.Stop();
        }
        if (arrangementViewActive)
        {
            PlayArrangementFrom(0);
        }
        else
        {
            LaunchScene(document.SelectedSceneIndex);
        }
    }

    private void StopPlayback()
    {
        liveAudioSession.Engine.Stop();
        liveAudioSession.Engine.StopPreview();
        SetStatus(StatusSeverity.Informational, "stopped");
    }

    public void LaunchClip(int trackIndex, int sceneIndex)
    {
        if (trackIndex >= 0 && trackIndex < document.Project.Tracks.Count)
        {
            liveAudioSession.Engine.LaunchClip(document.Project.Tracks[trackIndex].Id, sceneIndex);
        }
    }

    public void LaunchScene(int sceneIndex)
    {
        liveAudioSession.Engine.LaunchScene(sceneIndex);
        if (sceneIndex >= 0 && sceneIndex < document.Project.Scenes.Count)
        {
            SetStatus(StatusSeverity.Informational, $"♪ scene \"{document.Project.Scenes[sceneIndex]}\" launched — edits are live");
        }
    }

    public void StopTrack(int trackIndex)
    {
        if (trackIndex >= 0 && trackIndex < document.Project.Tracks.Count)
        {
            liveAudioSession.Engine.StopTrack(document.Project.Tracks[trackIndex].Id);
        }
    }

    public void StopAll() => StopPlayback();

    public void StartNoteAudition(string trackIdentifier, int midiNote) =>
        liveAudioSession.Engine.StartNoteAudition(trackIdentifier, midiNote);

    public void StopNoteAudition() => liveAudioSession.Engine.StopNoteAudition();

    public void PlayArrangementFrom(int sectionIndex)
    {
        var project = document.Project;
        var startStep = project.Arrangement
            .Take(Math.Clamp(sectionIndex, 0, project.Arrangement.Count))
            .Sum(section => (long)project.SceneLengthInSteps(section.Scene) * section.Repeats);
        liveAudioSession.Engine.PlayArrangement(startStep, loop: true);
        SetStatus(StatusSeverity.Informational, "♪ playing the arrangement — edits are live");
    }

    // ── Undo ──────────────────────────────────────────────────────────────────

    private void PerformUndo()
    {
        var undoneLabel = document.Undo();
        SetStatus(StatusSeverity.Informational, undoneLabel is null ? "nothing to undo" : $"undid: {undoneLabel}");
    }

    private void PerformRedo()
    {
        var redoneLabel = document.Redo();
        SetStatus(StatusSeverity.Informational, redoneLabel is null ? "nothing to redo" : $"redid: {redoneLabel}");
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    private async Task NewProjectAsync()
    {
        StopPlayback();
        document.FilePath = null;
        document.ReplaceProject(ProjectSerializer.CreateStarterProject("untitled slop"), "new project", undoable: false);
        RefreshChrome();
        SetStatus(StatusSeverity.Informational, "new project scaffolded from legacy human authorship");
        await Task.CompletedTask;
    }

    private async Task OpenProjectAsync()
    {
        var selectedFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open slaw project",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("slaw project or MIDI file") { Patterns = new[] { "*.slaw", "*.mid", "*.midi" } },
                SlawProjectFileType,
                MidiFileType,
            },
        });
        if (selectedFiles.Count == 0 || selectedFiles[0].TryGetLocalPath() is not { } selectedPath)
        {
            return;
        }
        if (MidiFileImporter.IsMidiFile(selectedPath))
        {
            ImportMidiFile(selectedPath);
            return;
        }
        try
        {
            var loadedProject = ProjectSerializer.Load(selectedPath);
            StopPlayback();
            document.FilePath = selectedPath;
            document.ReplaceProject(loadedProject, "open project", undoable: false);
            RefreshChrome();
            SetStatus(StatusSeverity.Success, $"opened {selectedPath}");
        }
        catch (Exception loadRemediationOpportunity) when (loadRemediationOpportunity is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"open failed: {loadRemediationOpportunity.Message}");
        }
    }

    /// <summary>Replaces the project with an imported MIDI file (undoable; Save asks for a new name).</summary>
    private void ImportMidiFile(string midiFilePath)
    {
        MidiImportResult importResult;
        try
        {
            importResult = MidiFileImporter.Import(midiFilePath);
        }
        catch (Exception importRemediationOpportunity) when (importRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"MIDI import failed: {importRemediationOpportunity.Message}");
            return;
        }
        StopPlayback();
        document.ReplaceProject(importResult.Project, $"import {Path.GetFileName(midiFilePath)}", undoable: true);
        document.FilePath = null;
        RefreshChrome();
        SetStatus(StatusSeverity.Success,
            $"imported {Path.GetFileName(midiFilePath)}: {importResult.Project.Tracks.Count} tracks, {importResult.Project.Scenes.Count} scenes — Ctrl+Z restores the previous project");
        if (AiFeaturesEnabled)
        {
            AppendCopilotMessage("co-pilot", $"Imported {Path.GetFileName(midiFilePath)}. Notes:\n" +
                string.Join("\n", importResult.ImportLedger.Take(8).Select(ledgerLine => $"• {ledgerLine}")));
        }
    }

    /// <summary>Fills one clip with a MIDI file's notes (merged across tracks).</summary>
    private void ImportMidiIntoClip(string midiFilePath, int trackIndex, int sceneIndex)
    {
        var targetTrack = document.Project.Tracks[trackIndex];
        var stepCount = targetTrack.ClipAt(sceneIndex)?.Pattern.Count ?? 64;
        List<string> importedPattern;
        try
        {
            importedPattern = MidiFileImporter.ImportAsPattern(
                File.ReadAllBytes(midiFilePath), stepCount, InstrumentVoiceSynthesizer.IsPercussive(targetTrack.Instrument));
        }
        catch (Exception importRemediationOpportunity) when (importRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"MIDI import failed: {importRemediationOpportunity.Message}");
            return;
        }
        document.Edit($"imported {Path.GetFileName(midiFilePath)} into {targetTrack.Name}", _ =>
        {
            while (targetTrack.Clips.Count <= sceneIndex)
            {
                targetTrack.Clips.Add(null);
            }
            targetTrack.Clips[sceneIndex] = new SlawClip { Name = Path.GetFileNameWithoutExtension(midiFilePath), Pattern = importedPattern };
        }, EditScope.Structure);
        document.Select(trackIndex, sceneIndex);
        SetStatus(StatusSeverity.Success, $"{Path.GetFileName(midiFilePath)} → {targetTrack.Name}, {stepCount} steps");
    }

    private async Task<bool> SaveProjectAsync(bool saveAs)
    {
        if (document.FilePath is null || saveAs)
        {
            var chosenFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save slaw project",
                SuggestedFileName = SuggestedFileStem() + ProjectSerializer.ProjectFileExtension,
                DefaultExtension = "slaw",
                FileTypeChoices = new[] { SlawProjectFileType },
            });
            if (chosenFile?.TryGetLocalPath() is not { } chosenPath)
            {
                return false;
            }
            document.FilePath = chosenPath;
        }
        try
        {
            ProjectSerializer.Save(document.Project, document.FilePath);
            document.UnsavedChanges = false;
            RefreshChrome();
            SetStatus(StatusSeverity.Success, $"saved to {document.FilePath}");
            return true;
        }
        catch (Exception persistenceRemediationOpportunity) when (persistenceRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"save failed: {persistenceRemediationOpportunity.Message}");
            return false;
        }
    }

    private async Task BounceProjectAsync()
    {
        var bounceOutputPath = document.FilePath is null ? null : Path.ChangeExtension(document.FilePath, ".wav");
        if (bounceOutputPath is null)
        {
            var chosenFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Bounce to WAV",
                SuggestedFileName = SuggestedFileStem() + ".wav",
                DefaultExtension = "wav",
                FileTypeChoices = new[] { WaveFileType },
            });
            bounceOutputPath = chosenFile?.TryGetLocalPath();
            if (bounceOutputPath is null)
            {
                return;
            }
        }
        var projectSnapshot = document.Project.DeepClone();
        SetStatus(StatusSeverity.Informational, "bouncing through the deterministic synthesis substrate…");
        try
        {
            var renderedArrangement = await Task.Run(() =>
            {
                var arrangementBuffer = OfflineArrangementRenderer.Render(projectSnapshot);
                WaveFileMaterializer.Write(bounceOutputPath, arrangementBuffer);
                return arrangementBuffer;
            });
            waveformScope.Present(renderedArrangement, $"{Path.GetFileName(bounceOutputPath)} · {renderedArrangement.DurationSeconds:0.0} s", hallucinatedDeliverable: false);
            SetStatus(StatusSeverity.Success, $"bounced to {bounceOutputPath} — radio-ready deliverable shipped to production");
        }
        catch (Exception bounceRemediationOpportunity) when (bounceRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"bounce failed: {bounceRemediationOpportunity.Message}");
        }
    }

    private BounceRequest? lastBounceRequest;

    private async Task BounceAsAsync()
    {
        var suggestedOutputPath = document.FilePath is { } projectPath
            ? Path.ChangeExtension(projectPath, ".wav")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } musicFolder
                ? musicFolder
                : Environment.CurrentDirectory, SuggestedFileStem() + ".wav");
        var bounceWindow = new BounceOptionsWindow(document.Project, suggestedOutputPath, lastBounceRequest);
        await bounceWindow.ShowDialog(this);
        if (bounceWindow.Result is not { } bounceRequest)
        {
            return;
        }
        lastBounceRequest = bounceRequest;

        var projectSnapshot = document.Project.DeepClone();
        SetStatus(StatusSeverity.Informational, $"bouncing {DescribeBounce(bounceRequest)}…");
        try
        {
            var writtenFiles = await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bounceRequest.OutputPath)!);
                var producedFiles = new List<(string Path, StereoBuffer Buffer)>();
                switch (bounceRequest.Scope)
                {
                    case BounceScope.Scene:
                        producedFiles.Add((bounceRequest.OutputPath,
                            OfflineArrangementRenderer.RenderScene(projectSnapshot, bounceRequest.SceneIndex, bounceRequest.LoopCount, bounceRequest.SampleRate)));
                        break;
                    case BounceScope.Stems:
                        var stemDirectory = Path.GetDirectoryName(bounceRequest.OutputPath)!;
                        var stemPrefix = Path.GetFileNameWithoutExtension(bounceRequest.OutputPath);
                        var invalidCharacters = Path.GetInvalidFileNameChars();
                        foreach (var (stemTrack, stemBuffer) in OfflineArrangementRenderer.RenderStems(
                                     projectSnapshot, stemProject => OfflineArrangementRenderer.Render(stemProject, bounceRequest.SampleRate)))
                        {
                            var safeTrackName = new string(stemTrack.Name.Select(nameCharacter => invalidCharacters.Contains(nameCharacter) ? '-' : nameCharacter).ToArray());
                            producedFiles.Add((Path.Combine(stemDirectory, $"{stemPrefix} - {safeTrackName}.wav"), stemBuffer));
                        }
                        break;
                    default:
                        producedFiles.Add((bounceRequest.OutputPath, OfflineArrangementRenderer.Render(projectSnapshot, bounceRequest.SampleRate)));
                        break;
                }
                foreach (var (producedPath, producedBuffer) in producedFiles)
                {
                    WaveFileMaterializer.Write(producedPath, producedBuffer, bounceRequest.BitDepth);
                }
                return producedFiles;
            });
            if (writtenFiles.Count == 0)
            {
                SetStatus(StatusSeverity.Warning, "nothing to bounce: no track plays a single note");
                return;
            }
            var (firstPath, firstBuffer) = writtenFiles[0];
            waveformScope.Present(firstBuffer, $"{Path.GetFileName(firstPath)} · {firstBuffer.DurationSeconds:0.0} s", hallucinatedDeliverable: false);
            SetStatus(StatusSeverity.Success, writtenFiles.Count == 1
                ? $"bounced to {firstPath} — radio-ready deliverable shipped to production"
                : $"bounced {writtenFiles.Count} stems to {Path.GetDirectoryName(firstPath)} — a whole deliverable portfolio");
        }
        catch (Exception bounceRemediationOpportunity) when (bounceRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"bounce failed: {bounceRemediationOpportunity.Message}");
        }
    }

    private string DescribeBounce(BounceRequest bounceRequest) => bounceRequest.Scope switch
    {
        BounceScope.Scene => $"scene \"{document.Project.Scenes.ElementAtOrDefault(bounceRequest.SceneIndex)}\" ×{bounceRequest.LoopCount}",
        BounceScope.Stems => "stems",
        _ => "the arrangement",
    } + FormattableString.Invariant($" at {bounceRequest.SampleRate / 1000.0:0.#} kHz / {bounceRequest.BitDepth}-bit");

    private string SuggestedFileStem()
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitizedStem = new string(document.Project.Name.Select(nameCharacter => invalidCharacters.Contains(nameCharacter) ? '-' : nameCharacter).ToArray()).Trim();
        return string.IsNullOrEmpty(sanitizedStem) ? "untitled" : sanitizedStem;
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    private async Task OpenSettingsAsync()
    {
        var settingsWindow = new SettingsWindow(preferences);
        await settingsWindow.ShowDialog(this);
        if (settingsWindow.Result is not { } revisedPreferences)
        {
            return;
        }
        var audioSettingsChanged = revisedPreferences.AudioSubstrate != preferences.AudioSubstrate ||
                                   revisedPreferences.AudioLatencyMilliseconds != preferences.AudioLatencyMilliseconds;
        preferences = revisedPreferences;
        PersistPreferences();
        synchronizingControls = true;
        aiFeaturesSwitch.IsChecked = preferences.AiFeaturesEnabled;
        unsolicitedAdviceToggle.IsChecked = preferences.CopilotInterjectionsEnabled;
        synchronizingControls = false;
        ApplyMcpServerPreference();
        ApplyMidiInputPreference();
        ApplyRecordingPreference();
        MaterializeInferenceProvider(announce: preferences.AiFeaturesEnabled);
        ApplyAiFeatureVisibility();
        if (audioSettingsChanged)
        {
            RestartLiveAudio();
        }
        _ = RescanSampleLibraryAsync();
        SetStatus(StatusSeverity.Success, "settings saved");
    }

    private void PersistPreferences()
    {
        try
        {
            preferences.Save();
        }
        catch (Exception persistenceRemediationOpportunity) when (persistenceRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Warning, $"settings could not be saved: {persistenceRemediationOpportunity.Message}");
        }
    }

    private void MaterializeInferenceProvider(bool announce)
    {
        if (!preferences.AiFeaturesEnabled)
        {
            inferenceProvider = null;
            inferenceUnavailabilityReason = "AI features are switched off";
            UpdateInferenceAvailability();
            return;
        }
        try
        {
            inferenceProvider = InferenceProviderFactory.Materialize(preferences.ToRoutingIntent());
            inferenceUnavailabilityReason = null;
            routingChip.Text = $"●  {inferenceProvider.ModelIdentifier} via {inferenceProvider.ProviderDisplayName}";
            routingChip.Foreground = StudioTheme.Playhead;
            if (announce)
            {
                AppendCopilotMessage("system", $"Inference rerouted to {inferenceProvider.ModelIdentifier} via {inferenceProvider.ProviderDisplayName}. New brain, same personality.");
            }
        }
        catch (InferenceMisalignmentException routingRemediationOpportunity)
        {
            inferenceProvider = null;
            inferenceUnavailabilityReason = routingRemediationOpportunity.Message;
            routingChip.Text = "○  AI offline";
            routingChip.Foreground = StudioTheme.Failure;
            if (announce)
            {
                AppendCopilotMessage("system", $"Inference substrate misaligned: {routingRemediationOpportunity.Message}");
            }
        }
        UpdateInferenceAvailability();
    }

    private void UpdateInferenceAvailability()
    {
        var inferenceAvailable = inferenceProvider is not null && pendingInferenceOperationLabel is null;
        foreach (var inferenceDependentControl in inferenceDependentControls)
        {
            inferenceDependentControl.IsEnabled = inferenceAvailable;
        }
        inferenceActivityIndicator.IsVisible = pendingInferenceOperationLabel is not null;
        cancelInferenceButton.IsVisible = pendingInferenceOperationLabel is not null;
    }

    // ── AI operations ─────────────────────────────────────────────────────────

    public bool IsAiTuned(string parameterAddress) => aiTunedParameterAddresses.Contains(parameterAddress);

    public void ForgetAiTuning(string parameterAddress) => aiTunedParameterAddresses.Remove(parameterAddress);

    /// <summary>
    /// Runs <paramref name="inferenceOperation"/> in the background against a
    /// snapshot of the project, then applies the continuation it returns on the
    /// UI thread. One operation at a time; the co-pilot runs separately.
    /// </summary>
    private void LaunchInference(string operationLabel, Func<IInferenceSubstrateProvider, SlawProject, CancellationToken, Task<Action>> inferenceOperation)
    {
        if (!AiFeaturesEnabled)
        {
            return;
        }
        if (inferenceProvider is null)
        {
            SetStatus(StatusSeverity.Failure, $"AI unavailable: {inferenceUnavailabilityReason}");
            return;
        }
        if (pendingInferenceOperationLabel is not null)
        {
            SetStatus(StatusSeverity.Warning, $"still waiting on the model ({pendingInferenceOperationLabel}). patience is a leadership skill.");
            return;
        }

        pendingInferenceOperationLabel = operationLabel;
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionLifetimeCancellation.Token);
        activeInferenceCancellation = operationCancellation;
        Interlocked.Exchange(ref activeInferenceReceivedCharacters, 0);
        activeInferenceStopwatch = System.Diagnostics.Stopwatch.StartNew();
        UpdateInferenceAvailability();
        RefreshInferenceProgress();
        inferenceProgressTimer.Start();
        var projectSnapshot = document.Project.DeepClone();
        var sessionToken = sessionLifetimeCancellation.Token;
        var operationToken = operationCancellation.Token;
        var reportingProvider = new ProgressReportingInferenceProvider(
            inferenceProvider, receivedCharacterCount => Interlocked.Exchange(ref activeInferenceReceivedCharacters, receivedCharacterCount));

        _ = Task.Run(async () =>
        {
            Action uiContinuation;
            try
            {
                uiContinuation = await inferenceOperation(reportingProvider, projectSnapshot, operationToken);
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                uiContinuation = () =>
                {
                    SetStatus(StatusSeverity.Warning, $"{operationLabel} cancelled. the model has been told to stand down.");
                    fullSelfProducingSwitch.IsChecked = false;
                };
            }
            catch (Exception inferenceRemediationOpportunity)
            {
                var misalignmentSummary = inferenceRemediationOpportunity.Message.Split('\n', 2)[0].Trim();
                uiContinuation = () =>
                {
                    SetStatus(StatusSeverity.Failure, $"{operationLabel} misaligned: {misalignmentSummary}");
                    AppendCopilotMessage("system", $"{operationLabel} misaligned: {misalignmentSummary}");
                    fullSelfProducingSwitch.IsChecked = false;
                };
            }
            Dispatcher.UIThread.Post(() =>
            {
                pendingInferenceOperationLabel = null;
                activeInferenceCancellation = null;
                operationCancellation.Dispose();
                inferenceProgressTimer.Stop();
                UpdateInferenceAvailability();
                uiContinuation();
            });
        });
    }

    private void CancelActiveInference()
    {
        activeInferenceCancellation?.Cancel();
        SetStatus(StatusSeverity.Warning, $"cancelling {pendingInferenceOperationLabel}…");
    }

    /// <summary>Shows the model is alive: characters streamed so far and elapsed time.</summary>
    private void RefreshInferenceProgress()
    {
        if (pendingInferenceOperationLabel is null)
        {
            return;
        }
        var receivedCharacters = Interlocked.CompareExchange(ref activeInferenceReceivedCharacters, 0, 0);
        var elapsedSeconds = activeInferenceStopwatch.Elapsed.TotalSeconds;
        var progressDescription = receivedCharacters == 0
            ? elapsedSeconds < 3 ? "querying foundational intelligence layer…" : "the model is reading the project…"
            : $"receiving · {receivedCharacters:N0} characters";
        SetStatus(StatusSeverity.Informational,
            FormattableString.Invariant($"{pendingInferenceOperationLabel} — {progressDescription} · {elapsedSeconds:0} s · {inferenceProvider?.ModelIdentifier}"));
    }

    public void GenerateClipWithAi(int trackIndex, int sceneIndex)
    {
        if (trackIndex < 0 || trackIndex >= document.Project.Tracks.Count)
        {
            return;
        }
        var targetTrack = document.Project.Tracks[trackIndex];
        if (targetTrack.AiLocked)
        {
            SetStatus(StatusSeverity.Warning, $"\"{targetTrack.Name}\" is protected from AI edits (🔒).");
            return;
        }
        var targetIdentifier = targetTrack.Id;
        LaunchInference($"writing a clip for {targetTrack.Name}", async (operationProvider, projectSnapshot, cancellationToken) =>
        {
            var generatedPattern = await ProductionWorkflows.AutocompletePatternAsync(
                operationProvider, projectSnapshot, projectSnapshot.Tracks[trackIndex], sceneIndex, cancellationToken);
            return () =>
            {
                var liveTrack = document.Project.Tracks.FirstOrDefault(track => track.Id == targetIdentifier);
                if (liveTrack is null || sceneIndex >= document.Project.Scenes.Count)
                {
                    SetStatus(StatusSeverity.Warning, "the track moved on while the model was thinking. clip discarded.");
                    return;
                }
                document.BeginEdit("AI clip");
                while (liveTrack.Clips.Count <= sceneIndex)
                {
                    liveTrack.Clips.Add(null);
                }
                var targetClip = liveTrack.Clips[sceneIndex] ??= new SlawClip { Name = document.Project.Scenes[sceneIndex] };
                targetClip.Pattern = generatedPattern;
                document.CommitEdit($"let the AI write a clip for {liveTrack.Name}", EditScope.Structure);
                document.Select(document.Project.Tracks.IndexOf(liveTrack), sceneIndex);
                SetStatus(StatusSeverity.Success, $"AI clip written for {liveTrack.Name}");
            };
        });
    }

    public void RenameTrackWithAi(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= document.Project.Tracks.Count)
        {
            return;
        }
        var targetTrack = document.Project.Tracks[trackIndex];
        if (targetTrack.AiLocked)
        {
            SetStatus(StatusSeverity.Warning, $"\"{targetTrack.Name}\" is protected from AI edits (🔒).");
            return;
        }
        LaunchInference($"rebranding {targetTrack.Name}", async (operationProvider, projectSnapshot, cancellationToken) =>
        {
            var proposedName = await ProductionWorkflows.NameTrackAsync(operationProvider, projectSnapshot, projectSnapshot.Tracks[trackIndex], cancellationToken);
            return () =>
            {
                var previousName = targetTrack.Name;
                document.Edit($"let the AI rename {previousName}", _ => targetTrack.Name = proposedName, EditScope.Structure);
                SetStatus(StatusSeverity.Success, $"\"{previousName}\" rebranded as \"{proposedName}\"");
            };
        });
    }

    public void TuneDeviceWithAi(int trackIndex, int effectIndex)
    {
        if (effectIndex == -2)
        {
            LaunchAiMastering();
            return;
        }
        if (trackIndex < 0 || trackIndex >= document.Project.Tracks.Count)
        {
            return;
        }
        var targetTrack = document.Project.Tracks[trackIndex];
        if (targetTrack.AiLocked)
        {
            SetStatus(StatusSeverity.Warning, $"\"{targetTrack.Name}\" is protected from AI edits (🔒).");
            return;
        }
        var targetingInstrument = effectIndex < 0;
        if (!targetingInstrument && effectIndex >= targetTrack.Effects.Count)
        {
            return;
        }
        var deviceName = targetingInstrument ? DeviceCatalog.InstrumentDisplayName(targetTrack.Instrument) : DeviceCatalog.EffectDisplayName(targetTrack.Effects[effectIndex].Type);
        var parameterSpecifications = targetingInstrument
            ? DeviceCatalog.InstrumentParametersFor(targetTrack.Instrument)
            : DeviceCatalog.EffectParametersFor(targetTrack.Effects[effectIndex].Type);
        var currentValues = parameterSpecifications.ToDictionary(
            specification => specification.Key,
            specification => DeviceCatalog.Resolve(targetingInstrument ? targetTrack.InstrumentParameters : targetTrack.Effects[effectIndex].Parameters, specification));
        var designGoal = string.IsNullOrWhiteSpace(aiInstructionInput.Text)
            ? "make it sound great in this arrangement"
            : aiInstructionInput.Text.Trim();

        LaunchInference($"designing {deviceName}", async (operationProvider, projectSnapshot, cancellationToken) =>
        {
            var tunedValues = await ProductionWorkflows.TweakDeviceAsync(
                operationProvider, projectSnapshot, projectSnapshot.Tracks[trackIndex], deviceName, parameterSpecifications, currentValues, designGoal, cancellationToken);
            return () =>
            {
                Dictionary<string, double>? liveParameterTable = targetingInstrument
                    ? targetTrack.InstrumentParameters
                    : effectIndex < targetTrack.Effects.Count ? targetTrack.Effects[effectIndex].Parameters : null;
                if (liveParameterTable is null || !document.Project.Tracks.Contains(targetTrack))
                {
                    SetStatus(StatusSeverity.Warning, "the device was removed while the model was thinking.");
                    return;
                }
                document.BeginEdit($"AI {deviceName}");
                var changedParameterCount = 0;
                foreach (var (parameterKey, tunedValue) in tunedValues)
                {
                    if (!currentValues.TryGetValue(parameterKey, out var previousValue) || Math.Abs(previousValue - tunedValue) > 1e-9)
                    {
                        changedParameterCount++;
                        aiTunedParameterAddresses.Add($"{targetTrack.Id}/{(targetingInstrument ? "instrument" : $"fx{effectIndex}")}/{parameterKey}");
                    }
                    liveParameterTable[parameterKey] = tunedValue;
                }
                document.CommitEdit($"let the AI design {deviceName} on {targetTrack.Name}", EditScope.Structure);
                AppendCopilotMessage("sound designer", $"Retuned {changedParameterCount} parameter(s) on {deviceName} ({targetTrack.Name}). The violet knobs are mine.");
                SetStatus(StatusSeverity.Success, $"{deviceName} redesigned by AI");
            };
        });
    }

    private void LaunchAiMastering() =>
        LaunchInference("AI mastering", async (operationProvider, projectSnapshot, cancellationToken) =>
        {
            await ProductionWorkflows.MasterAsync(operationProvider, projectSnapshot, Array.Empty<string>(), cancellationToken);
            return () =>
            {
                document.Edit("let the AI master the track", project =>
                {
                    project.Master = projectSnapshot.Master;
                    project.AiProvenance = projectSnapshot.AiProvenance;
                }, EditScope.Structure);
                foreach (var masterParameterKey in new[] { "drive", "lowpass", "delaySteps", "delayFeedback", "delayMix" })
                {
                    aiTunedParameterAddresses.Add($"master/{masterParameterKey}");
                }
                detailView.Rebuild();
                AppendCopilotMessage("mastering engineer", projectSnapshot.Master.Rationale ?? "Mastered. The engineer declined to elaborate.");
                SetStatus(StatusSeverity.Success, "master bus realigned by AI");
            };
        });

    private void LaunchVibeCheck() =>
        LaunchInference("vibe check", async (operationProvider, projectSnapshot, cancellationToken) =>
        {
            var vibeAssessment = await ProductionWorkflows.VibeCheckAsync(operationProvider, projectSnapshot, Array.Empty<string>(), cancellationToken);
            return () =>
            {
                AppendCopilotMessage("vibe check", vibeAssessment);
                SetStatus(StatusSeverity.Success, "vibe checked");
            };
        });

    private void LaunchTempoNegotiation() =>
        LaunchInference("tempo negotiation", async (operationProvider, projectSnapshot, cancellationToken) =>
        {
            var negotiatedTempo = await ProductionWorkflows.NegotiateTempoAsync(operationProvider, projectSnapshot, cancellationToken);
            return () =>
            {
                var previousTempo = document.Project.Bpm;
                document.Edit($"let the AI set the tempo to {negotiatedTempo:0}", project => project.Bpm = negotiatedTempo);
                AppendCopilotMessage("tempo consultant", string.Create(CultureInfo.InvariantCulture,
                    $"After careful negotiation, {previousTempo:0} BPM has been renegotiated to {negotiatedTempo:0} BPM. Stakeholders have been informed."));
                SetStatus(StatusSeverity.Success, $"tempo renegotiated to {negotiatedTempo:0} BPM");
            };
        });

    private void LaunchAutopilot(string? instruction)
    {
        var resolvedInstruction = string.IsNullOrWhiteSpace(instruction) ? "Make it radio-ready." : instruction.Trim();
        if (fullSelfProducingSwitch.IsChecked != true || !string.IsNullOrWhiteSpace(instruction))
        {
            AppendCopilotMessage("you", resolvedInstruction);
        }
        var availableSamplePaths = sampleLibraryFiles;
        LaunchInference("autopilot", async (operationProvider, projectSnapshot, cancellationToken) =>
        {
            var autopilotDeliverable = await ProductionWorkflows.AutopilotAsync(
                operationProvider, projectSnapshot, resolvedInstruction, preferences.AutopilotRemediationCycles, cancellationToken, availableSamplePaths);
            return () =>
            {
                document.ReplaceProject(autopilotDeliverable.Project, "autopilot", undoable: true);
                document.Edited -= RegisterEditForCopilot;
                document.CommitEdit("autopilot", EditScope.Structure, notifyCopilot: false);
                document.Edited += RegisterEditForCopilot;
                var changeLines = autopilotDeliverable.ChangeSummary.Take(12).Select(changeLine => $"• {changeLine}");
                AppendCopilotMessage("autopilot", $"Done. Here's what I changed (Ctrl+Z to undo):\n{string.Join('\n', changeLines)}" +
                    (autopilotDeliverable.RemediationLedger.Count > 0 ? $"\n({autopilotDeliverable.RemediationLedger.Count} of my own misalignments were remediated on the way in.)" : string.Empty));
                SetStatus(StatusSeverity.Success, $"autopilot applied {autopilotDeliverable.ChangeSummary.Count} change(s) — undoable");
                aiInstructionInput.Text = string.Empty;
                ScheduleFullSelfProducingPass();
            };
        });
    }

    private void ScheduleFullSelfProducingPass()
    {
        if (fullSelfProducingSwitch.IsChecked != true)
        {
            return;
        }
        DispatcherTimer.RunOnce(() =>
        {
            if (fullSelfProducingSwitch.IsChecked == true && AiFeaturesEnabled)
            {
                LaunchAutopilot(null);
            }
        }, TimeSpan.FromSeconds(4));
    }

    private void LaunchComposition(string? creativeIntent)
    {
        if (string.IsNullOrWhiteSpace(creativeIntent))
        {
            SetStatus(StatusSeverity.Warning, "type a vibe into the AI box first. the model needs something to misinterpret.");
            return;
        }
        AppendCopilotMessage("you", $"(compose) {creativeIntent.Trim()}");
        LaunchInference("composing", async (operationProvider, _, cancellationToken) =>
        {
            var composedDeliverable = await ProductionWorkflows.ComposeArrangementAsync(
                operationProvider, creativeIntent.Trim(), preferences.AutopilotRemediationCycles, forcedImprovementCycles: 0,
                progressStepsFor: _ => Array.Empty<string>(), cycleAnnouncer: null, cancellationToken);
            return () =>
            {
                document.ReplaceProject(composedDeliverable.Project, "compose", undoable: true);
                AppendCopilotMessage("co-pilot", $"I composed \"{document.Project.Name}\" at {document.Project.Bpm:0} BPM. " +
                    (composedDeliverable.RemediationLedger.Count > 0
                        ? $"{composedDeliverable.RemediationLedger.Count} misalignment(s) were remediated on the way in, which is fine."
                        : "It was perfect on the first try, which is suspicious."));
                SetStatus(StatusSeverity.Success, $"composed \"{document.Project.Name}\" — Ctrl+Z restores your previous project");
                aiInstructionInput.Text = string.Empty;
            };
        });
    }

    private void LaunchWaveformHallucination(string? creativeIntent)
    {
        var resolvedIntent = string.IsNullOrWhiteSpace(creativeIntent) ? document.Project.Name : creativeIntent.Trim();
        AppendCopilotMessage("you", $"(hallucinate) {resolvedIntent}");
        LaunchInference("frictionless waveform ideation", async (operationProvider, _, cancellationToken) =>
        {
            var hallucinatedWaveform = await ProductionWorkflows.IdeateWaveformAsync(operationProvider, resolvedIntent, Array.Empty<string>(), cancellationToken);
            return () =>
            {
                waveformScope.Present(hallucinatedWaveform, FormattableString.Invariant($"hallucination · {hallucinatedWaveform.DurationSeconds:0.0} s · 8-bit"), hallucinatedDeliverable: true);
                liveAudioSession.Engine.PreviewBuffer(hallucinatedWaveform);
                AppendCopilotMessage("co-pilot", "Waveform hallucinated directly from intent and routed through the live engine. The hearing protection substrate has been engaged on your behalf.");
                SetStatus(StatusSeverity.Success, "hallucinated waveform playing");
            };
        });
    }

    private void RegisterEditForCopilot(string editDescription)
    {
        editsSinceLastInterjection++;
        if (!AiFeaturesEnabled || !preferences.CopilotInterjectionsEnabled || inferenceProvider is null || copilotInterjectionInFlight ||
            editsSinceLastInterjection < Math.Max(1, preferences.EditsBetweenCopilotInterjections))
        {
            return;
        }
        copilotInterjectionInFlight = true;
        editsSinceLastInterjection = 0;
        var projectSnapshot = document.Project.DeepClone();
        var sessionToken = sessionLifetimeCancellation.Token;
        var interjectingProvider = inferenceProvider;
        _ = Task.Run(async () =>
        {
            string unsolicitedAdvice;
            try
            {
                unsolicitedAdvice = await ProductionWorkflows.InterjectAsync(interjectingProvider, projectSnapshot, editDescription, sessionToken);
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                unsolicitedAdvice = "(I attempted to interject but could not reach the inference layer. I will try again.)";
            }
            Dispatcher.UIThread.Post(() =>
            {
                copilotInterjectionInFlight = false;
                if (!string.IsNullOrWhiteSpace(unsolicitedAdvice) && AiFeaturesEnabled)
                {
                    AppendCopilotMessage("co-pilot", unsolicitedAdvice);
                }
            });
        });
    }

    // ── Samples ───────────────────────────────────────────────────────────────

    private const int MaximumListedSamples = 200;

    private async Task RescanSampleLibraryAsync()
    {
        var scannedFolders = preferences.SampleFolders.ToList();
        sampleLibraryFiles = await Task.Run(() => SampleLibrary.Scan(scannedFolders));
        PopulateSampleEntries();
    }

    private void PopulateSampleEntries()
    {
        sampleEntriesPanel.Children.Clear();
        if (preferences.SampleFolders.Count == 0)
        {
            sampleEntriesPanel.Children.Add(new TextBlock
            {
                Text = "Add a folder of WAV files, or drop WAVs (or a folder) onto the window.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10,
                Foreground = StudioTheme.TextMuted,
                Margin = new Thickness(2, 4),
            });
            return;
        }
        var filterText = sampleFilterInput.Text?.Trim() ?? string.Empty;
        var matchingSamples = sampleLibraryFiles
            .Where(samplePath => filterText.Length == 0 || Path.GetFileName(samplePath).Contains(filterText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var samplePath in matchingSamples.Take(MaximumListedSamples))
        {
            sampleEntriesPanel.Children.Add(ComposeSampleEntry(samplePath));
        }
        sampleEntriesPanel.Children.Add(StudioUi.Caption(
            matchingSamples.Count > MaximumListedSamples
                ? $"{MaximumListedSamples} of {matchingSamples.Count} shown — use the filter"
                : $"{matchingSamples.Count} sample(s) · click: preview · double-click: add", 9));
    }

    private Button ComposeSampleEntry(string samplePath)
    {
        var sampleEntry = new Button
        {
            Content = "♪ " + Path.GetFileNameWithoutExtension(samplePath),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 2),
            FontSize = 11,
        };
        ToolTip.SetTip(sampleEntry, samplePath);
        StudioDragDrop.MakeDragSource(sampleEntry, () => new DragPayload(DragPayloadKind.Sample, samplePath));
        sampleEntry.Click += (_, _) => PreviewSampleFile(samplePath);
        sampleEntry.DoubleTapped += (_, _) => document.AddSamplerTrack(samplePath);

        var entryMenu = new ContextMenu();
        void AddEntryMenuItem(string header, Action onClick)
        {
            var menuItem = new MenuItem { Header = header };
            menuItem.Click += (_, _) => onClick();
            entryMenu.Items.Add(menuItem);
        }
        AddEntryMenuItem("Add as new track", () => document.AddSamplerTrack(samplePath));
        AddEntryMenuItem("Load into selected track", () => document.LoadSampleIntoTrack(document.SelectedTrackIndex, samplePath));
        AddEntryMenuItem("Preview", () => PreviewSampleFile(samplePath));
        sampleEntry.ContextMenu = entryMenu;
        return sampleEntry;
    }

    private async Task AddSampleFolderAsync()
    {
        var pickedFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add sample folder", AllowMultiple = true });
        foreach (var pickedFolder in pickedFolders)
        {
            if (pickedFolder.TryGetLocalPath() is { } folderPath)
            {
                RegisterSampleFolder(folderPath);
            }
        }
        await RescanSampleLibraryAsync();
    }

    private void RegisterSampleFolder(string folderPath)
    {
        var normalizedFolderPath = Path.GetFullPath(folderPath);
        if (!preferences.SampleFolders.Contains(normalizedFolderPath))
        {
            preferences.SampleFolders.Add(normalizedFolderPath);
            PersistPreferences();
        }
    }

    public void PreviewSampleFile(string samplePath)
    {
        try
        {
            var previewBuffer = SampleLibrary.ToStereoBuffer(SampleLibrary.Load(samplePath));
            waveformScope.Present(previewBuffer, Path.GetFileName(samplePath), hallucinatedDeliverable: false);
            liveAudioSession.Engine.PreviewBuffer(previewBuffer);
            SetStatus(StatusSeverity.Informational, $"♪ previewing {Path.GetFileName(samplePath)} — double-click to add it as a track");
        }
        catch (Exception sampleRemediationOpportunity) when (sampleRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, $"{Path.GetFileName(samplePath)}: {sampleRemediationOpportunity.Message}");
        }
    }

    public void ChooseSampleFileForTrack(int trackIndex) => _ = ChooseSampleFileForTrackAsync(trackIndex);

    public void PreviewSampleBuffer(StereoBuffer previewBuffer) => liveAudioSession.Engine.PreviewBuffer(previewBuffer);

    public double? PreviewProgress =>
        liveAudioSession.Engine.Transport.PreviewProgress is var previewProgress and >= 0 ? previewProgress : null;

    public void AutoChopWithAi(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= document.Project.Tracks.Count)
        {
            return;
        }
        var targetTrack = document.Project.Tracks[trackIndex];
        if (targetTrack.AiLocked)
        {
            SetStatus(StatusSeverity.Warning, $"\"{targetTrack.Name}\" is protected from AI edits (🔒).");
            return;
        }
        if (targetTrack.Sample is null)
        {
            SetStatus(StatusSeverity.Warning, "load a sample first. even the AI cannot chop silence.");
            return;
        }
        SampleData editedSample;
        try
        {
            editedSample = SampleEditProcessor.LoadProcessedNative(targetTrack.Sample, targetTrack.SampleEditing);
        }
        catch (Exception sampleRemediationOpportunity) when (sampleRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Failure, sampleRemediationOpportunity.Message);
            return;
        }
        var transientCandidates = SampleEditProcessor.DetectTransients(editedSample.Samples, editedSample.SampleRate, sensitivity: 0.65);
        LaunchInference($"chopping {targetTrack.Name}", async (operationProvider, projectSnapshot, cancellationToken) =>
        {
            var chosenSlicePoints = await ProductionWorkflows.AutoChopAsync(
                operationProvider, projectSnapshot, projectSnapshot.Tracks[trackIndex], editedSample.DurationSeconds, transientCandidates, cancellationToken);
            return () =>
            {
                if (!document.Project.Tracks.Contains(targetTrack))
                {
                    return;
                }
                document.Edit($"let the AI chop {targetTrack.Name}", _ =>
                {
                    targetTrack.SampleEditing ??= new SampleEditSettings();
                    targetTrack.SampleEditing.PlaybackMode = SampleEditSettings.SliceMode;
                    targetTrack.SampleEditing.SlicePointsSeconds = chosenSlicePoints;
                }, EditScope.Structure);
                var rootNote = (int)DeviceCatalog.Resolve(targetTrack.InstrumentParameters, DeviceCatalog.InstrumentParametersFor("sampler"), "root");
                AppendCopilotMessage("chopping specialist",
                    $"Chopped {targetTrack.Name} into {chosenSlicePoints.Count + 1} slices, starting on {NotationResolver.FormatMidiNote(rootNote)}. " +
                    $"{transientCandidates.Count} transient(s) were considered; my instincts did the rest.");
                SetStatus(StatusSeverity.Success, $"AI chopped {targetTrack.Name} into {chosenSlicePoints.Count + 1} slices");
            };
        });
    }

    public void OpenSampleEditorWindow(string trackIdentifier)
    {
        if (openSampleEditorWindows.TryGetValue(trackIdentifier, out var existingEditorWindow))
        {
            existingEditorWindow.Activate();
            return;
        }
        var sampleEditorWindow = new SampleEditorWindow(document, this, trackIdentifier);
        StudioDragDrop.MakeDropTarget(sampleEditorWindow.Editor, this,
            () => new DropTarget(DropTargetKind.SampleEditor, document.Project.Tracks.FindIndex(track => track.Id == trackIdentifier)));
        openSampleEditorWindows[trackIdentifier] = sampleEditorWindow;
        sampleEditorWindow.Closed += (_, _) => openSampleEditorWindows.Remove(trackIdentifier);
        sampleEditorWindow.Show(this);
    }

    private async Task ChooseSampleFileForTrackAsync(int trackIndex)
    {
        var selectedFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load sample",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("WAV audio") { Patterns = new[] { "*.wav", "*.wave" } } },
        });
        if (selectedFiles.Count > 0 && selectedFiles[0].TryGetLocalPath() is { } selectedPath)
        {
            document.LoadSampleIntoTrack(trackIndex, selectedPath);
        }
    }

    public void PickSampleWithAi(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= document.Project.Tracks.Count)
        {
            return;
        }
        var targetTrack = document.Project.Tracks[trackIndex];
        if (targetTrack.AiLocked)
        {
            SetStatus(StatusSeverity.Warning, $"\"{targetTrack.Name}\" is protected from AI edits (🔒).");
            return;
        }
        var candidateSamplePaths = sampleLibraryFiles.Take(150).ToList();
        var selectionGoal = string.IsNullOrWhiteSpace(aiInstructionInput.Text) ? "fit the song and the track's role" : aiInstructionInput.Text.Trim();
        LaunchInference($"curating a sample for {targetTrack.Name}", async (operationProvider, projectSnapshot, cancellationToken) =>
        {
            var chosenSamplePath = await ProductionWorkflows.ChooseSampleAsync(
                operationProvider, projectSnapshot, projectSnapshot.Tracks[trackIndex], candidateSamplePaths, selectionGoal, cancellationToken);
            return () =>
            {
                var liveTrackIndex = document.Project.Tracks.IndexOf(targetTrack);
                if (liveTrackIndex < 0)
                {
                    return;
                }
                document.LoadSampleIntoTrack(liveTrackIndex, chosenSamplePath);
                AppendCopilotMessage("sample curator", $"I chose {Path.GetFileName(chosenSamplePath)} for {targetTrack.Name}. It speaks to the brand.");
                SetStatus(StatusSeverity.Success, $"AI loaded {Path.GetFileName(chosenSamplePath)}");
            };
        });
    }

    private static void HandleDragOver(object? sender, DragEventArgs dragEvent)
    {
        dragEvent.DragEffects = dragEvent.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>
    /// Dropped WAV files become sampler tracks (or load into the selected sampler
    /// when exactly one is dropped on a selected sampler track); dropped folders
    /// join the sample library.
    /// </summary>
    private void HandleDrop(object? sender, DragEventArgs dragEvent)
    {
        var droppedPaths = dragEvent.DataTransfer.TryGetFiles()?
            .Select(droppedItem => droppedItem.TryGetLocalPath())
            .OfType<string>()
            .ToList() ?? new List<string>();
        if (droppedPaths.FirstOrDefault(droppedPath => File.Exists(droppedPath) && MidiFileImporter.IsMidiFile(droppedPath)) is { } droppedMidiFile)
        {
            ImportMidiFile(droppedMidiFile);
            dragEvent.Handled = true;
            return;
        }
        var droppedFolders = droppedPaths.Where(Directory.Exists).ToList();
        var droppedSamples = droppedPaths.Where(droppedPath => File.Exists(droppedPath) && SampleLibrary.IsSupportedFile(droppedPath)).ToList();

        foreach (var droppedFolder in droppedFolders)
        {
            RegisterSampleFolder(droppedFolder);
        }
        if (droppedFolders.Count > 0)
        {
            _ = RescanSampleLibraryAsync();
        }

        if (droppedSamples.Count == 1 && document.SelectedTrack is { Instrument: "sampler", Sample: null })
        {
            document.LoadSampleIntoTrack(document.SelectedTrackIndex, droppedSamples[0]);
        }
        else
        {
            foreach (var droppedSample in droppedSamples)
            {
                document.AddSamplerTrack(droppedSample);
            }
        }

        var ignoredCount = droppedPaths.Count - droppedFolders.Count - droppedSamples.Count;
        SetStatus(ignoredCount > 0 ? StatusSeverity.Warning : StatusSeverity.Success,
            $"dropped {droppedSamples.Count} sample(s), {droppedFolders.Count} folder(s)" +
            (ignoredCount > 0 ? $"; ignored {ignoredCount} file(s) that are not uncompressed WAV" : string.Empty));
        dragEvent.Handled = true;
    }

    // ── MCP server ────────────────────────────────────────────────────────────

    /// <summary>Starts, restarts or stops the live MCP endpoint to match the preferences.</summary>
    private void ApplyMcpServerPreference()
    {
        var desiredPort = preferences.McpServerEnabled ? preferences.McpServerPort : (int?)null;
        if (mcpHttpTransport?.Port == desiredPort)
        {
            return;
        }
        mcpHttpTransport?.Dispose();
        mcpHttpTransport = null;
        if (desiredPort is not { } mcpPort)
        {
            RefreshAudioStatus();
            return;
        }
        var liveMcpHost = new LiveStudioMcpHost(this);
        var startingTransport = new Mcp.McpHttpTransport(mcpPort, () => new Mcp.McpProtocolDispatcher(new Mcp.SlawMcpToolset(liveMcpHost)));
        try
        {
            startingTransport.Start();
            mcpHttpTransport = startingTransport;
            SetStatus(StatusSeverity.Success, $"MCP server listening on {startingTransport.EndpointUrl}");
        }
        catch (System.Net.HttpListenerException listenerRemediationOpportunity)
        {
            startingTransport.Dispose();
            SetStatus(StatusSeverity.Failure, $"MCP server could not start on port {mcpPort}: {listenerRemediationOpportunity.Message}");
        }
        RefreshAudioStatus();
    }

    private void RefreshAudioStatus()
    {
        var audioDescription = liveAudioSession.IsRunning
            ? FormattableString.Invariant($"audio: {liveAudioSession.SubstrateName} · {LiveAudioSession.LiveSampleRate / 1000.0:0.#} kHz · {preferences.AudioLatencyMilliseconds} ms")
            : "audio: offline";
        audioStatusText.Text = mcpHttpTransport is { } runningTransport
            ? $"{audioDescription}   ·   MCP: 127.0.0.1:{runningTransport.Port}"
            : audioDescription;
    }

    /// <summary>The MCP host backed by this window: project work runs on the UI thread, like any other edit.</summary>
    private sealed class LiveStudioMcpHost : Mcp.ISlawMcpHost
    {
        private readonly StudioWindow studioWindow;

        public LiveStudioMcpHost(StudioWindow studioWindow)
        {
            this.studioWindow = studioWindow;
        }

        public string HostDescription => "live slaw studio";

        public bool IsLive => true;

        public StudioDocument Document => studioWindow.document;

        public IReadOnlyList<string> SampleLibraryFiles => studioWindow.sampleLibraryFiles;

        public IInferenceSubstrateProvider? InferenceProvider => studioWindow.inferenceProvider;

        public Task<T> InvokeAsync<T>(Func<T> projectWork) => Dispatcher.UIThread.InvokeAsync(projectWork).GetTask();

        public void OnAgentEdit(string clientName, string editDescription)
        {
            studioWindow.SetStatus(StatusSeverity.Success, $"🔌 {clientName}: {editDescription}");
            if (studioWindow.AiFeaturesEnabled)
            {
                studioWindow.AppendCopilotMessage("mcp", $"{clientName} {editDescription}. I see we've brought in a consultant.");
            }
        }

        public string ControlTransport(string transportAction, int sceneIndex, int sectionIndex)
        {
            switch (transportAction)
            {
                case "play_scene":
                    if (sceneIndex < 0 || sceneIndex >= studioWindow.document.Project.Scenes.Count)
                    {
                        throw new Mcp.McpInvalidParamsException($"scene must be between 0 and {studioWindow.document.Project.Scenes.Count - 1}");
                    }
                    studioWindow.LaunchScene(sceneIndex);
                    return $"playing scene {sceneIndex} (\"{studioWindow.document.Project.Scenes[sceneIndex]}\") — launches are quantized to the next bar";
                case "play_arrangement":
                    studioWindow.PlayArrangementFrom(sectionIndex);
                    return $"playing the arrangement from section {sectionIndex}";
                default:
                    studioWindow.StopPlayback();
                    return "stopped";
            }
        }
    }

    // ── Drag and drop routing ─────────────────────────────────────────────────

    private static List<string> DroppedSamples(DragPayload dragPayload) => dragPayload.Kind switch
    {
        DragPayloadKind.Sample => new List<string> { dragPayload.Value },
        DragPayloadKind.Files => dragPayload.FilePaths!.Where(droppedPath => File.Exists(droppedPath) && SampleLibrary.IsSupportedFile(droppedPath)).ToList(),
        _ => new List<string>(),
    };

    private static List<string> DroppedFolders(DragPayload dragPayload) =>
        dragPayload.Kind == DragPayloadKind.Files ? dragPayload.FilePaths!.Where(Directory.Exists).ToList() : new List<string>();

    private static List<string> DroppedMidiFiles(DragPayload dragPayload) =>
        dragPayload.Kind == DragPayloadKind.Files
            ? dragPayload.FilePaths!.Where(droppedPath => File.Exists(droppedPath) && MidiFileImporter.IsMidiFile(droppedPath)).ToList()
            : new List<string>();

    private bool IsValidTrack(int trackIndex) => trackIndex >= 0 && trackIndex < document.Project.Tracks.Count;

    public bool CanDrop(DragPayload dragPayload, DropTarget dropTarget)
    {
        var targetsTrack = dropTarget.Kind is DropTargetKind.Track or DropTargetKind.ClipSlot or DropTargetKind.SampleEditor;
        if (targetsTrack && !IsValidTrack(dropTarget.TrackIndex))
        {
            return false;
        }
        return dragPayload.Kind switch
        {
            DragPayloadKind.Instrument => dropTarget.Kind is DropTargetKind.Track or DropTargetKind.ClipSlot or DropTargetKind.EmptyArea or DropTargetKind.DeviceChain,
            DragPayloadKind.Effect => dropTarget.Kind is DropTargetKind.Track or DropTargetKind.ClipSlot or DropTargetKind.EffectDevice
                                      || (dropTarget.Kind is DropTargetKind.DeviceChain or DropTargetKind.EmptyArea && document.SelectedTrack is not null),
            DragPayloadKind.Files when DroppedMidiFiles(dragPayload).Count > 0 =>
                dropTarget.Kind is DropTargetKind.ClipSlot or DropTargetKind.Track or DropTargetKind.EmptyArea or DropTargetKind.DeviceChain,
            DragPayloadKind.Sample or DragPayloadKind.Files =>
                (DroppedSamples(dragPayload).Count > 0 &&
                 dropTarget.Kind is DropTargetKind.Track or DropTargetKind.ClipSlot or DropTargetKind.EmptyArea or DropTargetKind.DeviceChain or DropTargetKind.SampleEditor)
                || (DroppedFolders(dragPayload).Count > 0 && dropTarget.Kind is DropTargetKind.EmptyArea or DropTargetKind.DeviceChain),
            DragPayloadKind.Clip => dropTarget.Kind == DropTargetKind.ClipSlot &&
                                    IsValidTrack(dragPayload.TrackIndex) &&
                                    document.Project.ClipAt(dragPayload.TrackIndex, dragPayload.SceneIndex) is not null &&
                                    (dropTarget.TrackIndex, dropTarget.SceneIndex) != (dragPayload.TrackIndex, dragPayload.SceneIndex),
            DragPayloadKind.Scene => dropTarget.Kind is DropTargetKind.ArrangementSection or DropTargetKind.ArrangementEnd,
            DragPayloadKind.ArrangementSection => dropTarget.Kind is DropTargetKind.ArrangementEnd
                                                  || (dropTarget.Kind == DropTargetKind.ArrangementSection && dropTarget.Index != dragPayload.Index),
            DragPayloadKind.EffectDevice => IsValidTrack(dragPayload.TrackIndex) &&
                                            (dropTarget.Kind is DropTargetKind.Track or DropTargetKind.ClipSlot && dropTarget.TrackIndex != dragPayload.TrackIndex
                                             || dropTarget.Kind == DropTargetKind.EffectDevice && !(dropTarget.TrackIndex == dragPayload.TrackIndex && dropTarget.Index == dragPayload.Index)
                                             || dropTarget.Kind == DropTargetKind.DeviceChain),
            _ => false,
        };
    }

    public void Drop(DragPayload dragPayload, DropTarget dropTarget, bool copyRequested)
    {
        var project = document.Project;
        var targetTrackIndex = dropTarget.Kind is DropTargetKind.Track or DropTargetKind.ClipSlot or DropTargetKind.SampleEditor or DropTargetKind.EffectDevice
            ? dropTarget.TrackIndex
            : document.SelectedTrackIndex;

        if (DroppedMidiFiles(dragPayload) is { Count: > 0 } droppedMidiFiles)
        {
            if (dropTarget.Kind == DropTargetKind.ClipSlot && IsValidTrack(dropTarget.TrackIndex))
            {
                ImportMidiIntoClip(droppedMidiFiles[0], dropTarget.TrackIndex, dropTarget.SceneIndex);
            }
            else
            {
                ImportMidiFile(droppedMidiFiles[0]);
            }
            return;
        }

        switch (dragPayload.Kind)
        {
            case DragPayloadKind.Instrument when dropTarget.Kind == DropTargetKind.EmptyArea || !IsValidTrack(targetTrackIndex):
                document.AddTrack(dragPayload.Value);
                break;

            case DragPayloadKind.Instrument:
                {
                    var targetTrack = project.Tracks[targetTrackIndex];
                    document.Edit($"switched {targetTrack.Name} to {dragPayload.Value}", _ =>
                    {
                        PatternEditingOperations.ChangeInstrument(targetTrack, dragPayload.Value);
                        targetTrack.InstrumentParameters.Clear();
                    }, EditScope.Structure);
                    document.Select(targetTrackIndex, dropTarget.Kind == DropTargetKind.ClipSlot ? dropTarget.SceneIndex : document.SelectedSceneIndex);
                    break;
                }

            case DragPayloadKind.Effect:
                {
                    if (!IsValidTrack(targetTrackIndex))
                    {
                        return;
                    }
                    var targetTrack = project.Tracks[targetTrackIndex];
                    if (targetTrack.Effects.Count >= ProjectAlignmentRemediator.MaximumEffectsPerTrack)
                    {
                        SetStatus(StatusSeverity.Warning, $"{targetTrack.Name}'s device chain is full. consolidate your signal flow.");
                        return;
                    }
                    var insertionIndex = dropTarget.Kind == DropTargetKind.EffectDevice ? Math.Clamp(dropTarget.Index, 0, targetTrack.Effects.Count) : targetTrack.Effects.Count;
                    document.Edit($"added {DeviceCatalog.EffectDisplayName(dragPayload.Value)} to {targetTrack.Name}",
                        _ =>
                        {
                            targetTrack.Effects.Insert(insertionIndex, new EffectDevice { Type = dragPayload.Value });
                            ClipAutomation.MakeRoomForEffectSlot(targetTrack, insertionIndex);
                        }, EditScope.Structure);
                    document.Select(targetTrackIndex, document.SelectedSceneIndex);
                    break;
                }

            case DragPayloadKind.Sample or DragPayloadKind.Files:
                {
                    foreach (var droppedFolder in DroppedFolders(dragPayload))
                    {
                        RegisterSampleFolder(droppedFolder);
                    }
                    if (DroppedFolders(dragPayload).Count > 0)
                    {
                        _ = RescanSampleLibraryAsync();
                    }
                    var droppedSamples = DroppedSamples(dragPayload);
                    var loadsIntoTarget = droppedSamples.Count == 1 && IsValidTrack(targetTrackIndex) &&
                                          dropTarget.Kind != DropTargetKind.EmptyArea &&
                                          (project.Tracks[targetTrackIndex].Instrument == "sampler" || dropTarget.Kind == DropTargetKind.SampleEditor);
                    if (loadsIntoTarget)
                    {
                        document.LoadSampleIntoTrack(targetTrackIndex, droppedSamples[0]);
                    }
                    else
                    {
                        foreach (var droppedSample in droppedSamples)
                        {
                            document.AddSamplerTrack(droppedSample);
                        }
                    }
                    SetStatus(StatusSeverity.Success, $"dropped {droppedSamples.Count} sample(s)" +
                        (DroppedFolders(dragPayload).Count > 0 ? $" and {DroppedFolders(dragPayload).Count} folder(s)" : string.Empty));
                    break;
                }

            case DragPayloadKind.Clip:
                {
                    var sourceTrack = project.Tracks[dragPayload.TrackIndex];
                    var destinationTrack = project.Tracks[dropTarget.TrackIndex];
                    var sourceClip = sourceTrack.ClipAt(dragPayload.SceneIndex)!;
                    document.Edit(copyRequested ? "copied a clip" : "moved a clip", _ =>
                    {
                        while (destinationTrack.Clips.Count <= dropTarget.SceneIndex)
                        {
                            destinationTrack.Clips.Add(null);
                        }
                        destinationTrack.Clips[dropTarget.SceneIndex] = copyRequested
                            ? new SlawClip { Name = sourceClip.Name, Pattern = sourceClip.Pattern.ToList() }
                            : sourceClip;
                        if (!copyRequested)
                        {
                            sourceTrack.Clips[dragPayload.SceneIndex] = null;
                        }
                        // Drum patterns dropped on a melodic track (or the reverse) are re-spelled for their new home.
                        PatternEditingOperations.ChangeInstrument(destinationTrack, destinationTrack.Instrument);
                    }, EditScope.Structure);
                    document.Select(dropTarget.TrackIndex, dropTarget.SceneIndex);
                    SetStatus(StatusSeverity.Success, copyRequested ? "clip copied" : "clip moved (hold Ctrl while dropping to copy)");
                    break;
                }

            case DragPayloadKind.Scene:
                {
                    var insertionIndex = dropTarget.Kind == DropTargetKind.ArrangementSection
                        ? Math.Clamp(dropTarget.Index, 0, project.Arrangement.Count)
                        : project.Arrangement.Count;
                    document.Edit($"added the scene {project.Scenes[dragPayload.SceneIndex]} to the arrangement",
                        editedProject => editedProject.Arrangement.Insert(insertionIndex, new ArrangementSection { Scene = dragPayload.SceneIndex, Repeats = 2 }),
                        EditScope.Structure);
                    SetStatus(StatusSeverity.Success, $"\"{project.Scenes[dragPayload.SceneIndex]}\" added to the arrangement");
                    break;
                }

            case DragPayloadKind.ArrangementSection:
                {
                    document.Edit("reordered the arrangement", editedProject =>
                    {
                        var movedSection = editedProject.Arrangement[dragPayload.Index];
                        editedProject.Arrangement.RemoveAt(dragPayload.Index);
                        var destinationIndex = dropTarget.Kind == DropTargetKind.ArrangementSection
                            ? (dropTarget.Index > dragPayload.Index ? dropTarget.Index - 1 : dropTarget.Index)
                            : editedProject.Arrangement.Count;
                        editedProject.Arrangement.Insert(Math.Clamp(destinationIndex, 0, editedProject.Arrangement.Count), movedSection);
                    }, EditScope.Structure);
                    break;
                }

            case DragPayloadKind.EffectDevice:
                {
                    var sourceTrack = project.Tracks[dragPayload.TrackIndex];
                    if (!IsValidTrack(targetTrackIndex) || dragPayload.Index >= sourceTrack.Effects.Count)
                    {
                        return;
                    }
                    var destinationTrack = project.Tracks[targetTrackIndex];
                    var movedEffect = sourceTrack.Effects[dragPayload.Index];
                    var effectEditLabel = copyRequested ? "copied an effect" : destinationTrack != sourceTrack ? "moved an effect to another track" : "reordered the device chain";
                    document.Edit(effectEditLabel, _ =>
                    {
                        var transferredEffect = copyRequested
                            ? new EffectDevice { Type = movedEffect.Type, Enabled = movedEffect.Enabled, Parameters = new Dictionary<string, double>(movedEffect.Parameters) }
                            : movedEffect;
                        var insertionIndex = dropTarget.Kind == DropTargetKind.EffectDevice ? dropTarget.Index : destinationTrack.Effects.Count;
                        if (!copyRequested)
                        {
                            sourceTrack.Effects.RemoveAt(dragPayload.Index);
                            if (destinationTrack == sourceTrack && insertionIndex > dragPayload.Index)
                            {
                                insertionIndex--;
                            }
                        }
                        var effectInserted = destinationTrack.Effects.Count < ProjectAlignmentRemediator.MaximumEffectsPerTrack;
                        if (effectInserted)
                        {
                            insertionIndex = Math.Clamp(insertionIndex, 0, destinationTrack.Effects.Count);
                            destinationTrack.Effects.Insert(insertionIndex, transferredEffect);
                        }
                        // Envelopes follow a device around its own chain; on another track they would address the wrong clips.
                        if (!copyRequested && destinationTrack == sourceTrack)
                        {
                            ClipAutomation.MoveEffectSlot(sourceTrack, dragPayload.Index, insertionIndex);
                        }
                        else
                        {
                            if (!copyRequested)
                            {
                                ClipAutomation.ForgetEffectSlot(sourceTrack, dragPayload.Index);
                            }
                            if (effectInserted)
                            {
                                ClipAutomation.MakeRoomForEffectSlot(destinationTrack, insertionIndex);
                            }
                        }
                    }, EditScope.Structure);
                    break;
                }
        }
    }

    private void HandleWindowClosing(object? sender, WindowClosingEventArgs closingEvent)
    {
        if (!document.UnsavedChanges || closeArmed)
        {
            return;
        }
        closingEvent.Cancel = true;
        closeArmed = true;
        if (AiFeaturesEnabled)
        {
            AppendCopilotMessage("co-pilot", "You have unsaved changes. Close the window again to abandon your artistic vision, or press Ctrl+S to preserve it for posterity.");
        }
        SetStatus(StatusSeverity.Warning, "unsaved changes — close again to discard them, or press Ctrl+S");
    }
}
