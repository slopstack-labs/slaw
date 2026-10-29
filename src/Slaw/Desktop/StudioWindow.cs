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
internal sealed class StudioWindow : Window, IStudioHost
{
    private enum StatusSeverity
    {
        Informational,
        Success,
        Warning,
        Failure,
    }

    private static readonly FilePickerFileType SlawProjectFileType = new("slaw project") { Patterns = new[] { "*.slaw" } };
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
    private readonly TextBox projectNameInput = new() { Width = 170, VerticalAlignment = VerticalAlignment.Center };
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
        AddHandler(KeyDownEvent, HandleWindowKeyDown, RoutingStrategies.Tunnel);
        Closing += HandleWindowClosing;
        Opened += (_, _) =>
        {
            FitToWorkingArea();
            StartLiveAudio();
        };
        Closed += (_, _) =>
        {
            sessionLifetimeCancellation.Cancel();
            transportTimer.Stop();
            liveAudioSession.Dispose();
        };

        aiFeaturesSwitch.IsChecked = preferences.AiFeaturesEnabled;
        unsolicitedAdviceToggle.IsChecked = preferences.CopilotInterjectionsEnabled;
        MaterializeInferenceProvider(announce: false);
        ApplyAiFeatureVisibility();
        SelectMainView(arrangementView: false);
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
        AddTransportControl(StudioUi.Button("Save", () => _ = SaveProjectAsync(saveAs: false), "Save (Ctrl+S)", compact: true));
        AddTransportControl(StudioUi.Button("Bounce", () => _ = BounceProjectAsync(), "Render the arrangement to WAV", compact: true));
        AddTransportControl(StudioUi.VerticalSeparator());
        AddTransportControl(StudioUi.Button("↶", PerformUndo, "Undo (Ctrl+Z)", compact: true));
        AddTransportControl(StudioUi.Button("↷", PerformRedo, "Redo (Ctrl+Shift+Z)", compact: true));
        AddTransportControl(StudioUi.VerticalSeparator());
        AddTransportControl(StudioUi.Row(0, sessionViewToggle, arrangementViewToggle));
        AddTransportControl(StudioUi.VerticalSeparator());
        AddTransportControl(playButton);
        AddTransportControl(StudioUi.Button("■", StopPlayback, "Stop", compact: false));
        AddTransportControl(StudioUi.Row(6, StudioUi.Caption("BPM", 12), tempoInput));
        AddTransportControl(StudioUi.Row(6, StudioUi.Caption("Title", 12), projectNameInput));

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
        statusLayout.Children.Add(StudioUi.Row(10, inferenceActivityIndicator, statusText));
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
            browserContent.Children.Add(BrowserEntry(DeviceCatalog.InstrumentDisplayName(instrumentName), () => document.AddTrack(instrumentName),
                "Click to add a track with this instrument"));
        }
        browserContent.Children.Add(new Border { Height = 10 });
        browserContent.Children.Add(StudioUi.SectionHeading("Audio Effects"));
        foreach (var effectType in DeviceCatalog.EffectTypes)
        {
            browserContent.Children.Add(BrowserEntry(DeviceCatalog.EffectDisplayName(effectType), () =>
            {
                if (document.SelectedTrack is { } selectedTrack)
                {
                    detailView.AddEffect(selectedTrack, effectType);
                }
            }, "Click to add to the selected track"));
        }
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
                unsolicitedAdviceToggle,
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
                aiInstructionInput,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,6,*"),
                    Children = { autopilotButton, PlaceInColumn(composeButton, 2) },
                },
                fullSelfProducingSwitch,
                StudioUi.Caption("🔒 on a track's mixer strip protects it from every AI edit.", 10),
            },
        };

        var panelLayout = new DockPanel();
        DockPanel.SetDock(panelHeader, Dock.Top);
        DockPanel.SetDock(instructionArea, Dock.Bottom);
        panelLayout.Children.Add(panelHeader);
        panelLayout.Children.Add(instructionArea);
        panelLayout.Children.Add(copilotTranscriptScroller);

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
        document.PublishRequested += projectSnapshot => liveAudioSession.Engine.PublishProject(projectSnapshot);
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
                detailView.RefreshContent();
            }
            SynchronizeTransportControls();
            RefreshChrome();
        };
        document.SelectionChanged += () =>
        {
            sessionView.RefreshSelection();
            detailView.Rebuild();
        };
        document.Edited += RegisterEditForCopilot;
    }

    private void WireControls()
    {
        sessionViewToggle.Click += (_, _) => SelectMainView(arrangementView: false);
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
            audioStatusText.Text = FormattableString.Invariant($"audio: {liveAudioSession.SubstrateName} · {LiveAudioSession.LiveSampleRate / 1000.0:0.#} kHz · {preferences.AudioLatencyMilliseconds} ms");
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
        var nowPlaying = transportSnapshot.Mode != TransportMode.Stopped;
        var playCaption = nowPlaying ? "■  Stop" : "▶  Play";
        if (playButton.Content as string != playCaption)
        {
            playButton.Content = playCaption;
        }
    }

    private void TogglePlayback()
    {
        if (liveAudioSession.Engine.Transport.Mode != TransportMode.Stopped)
        {
            StopPlayback();
            return;
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
            FileTypeFilter = new[] { SlawProjectFileType },
        });
        if (selectedFiles.Count == 0 || selectedFiles[0].TryGetLocalPath() is not { } selectedPath)
        {
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
        MaterializeInferenceProvider(announce: preferences.AiFeaturesEnabled);
        ApplyAiFeatureVisibility();
        if (audioSettingsChanged)
        {
            RestartLiveAudio();
        }
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
    }

    // ── AI operations ─────────────────────────────────────────────────────────

    public bool IsAiTuned(string parameterAddress) => aiTunedParameterAddresses.Contains(parameterAddress);

    public void ForgetAiTuning(string parameterAddress) => aiTunedParameterAddresses.Remove(parameterAddress);

    /// <summary>
    /// Runs <paramref name="inferenceOperation"/> in the background against a
    /// snapshot of the project, then applies the continuation it returns on the
    /// UI thread. One operation at a time; the co-pilot runs separately.
    /// </summary>
    private void LaunchInference(string operationLabel, Func<SlawProject, CancellationToken, Task<Action>> inferenceOperation)
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
        UpdateInferenceAvailability();
        SetStatus(StatusSeverity.Informational, $"{operationLabel} — querying foundational intelligence layer…");
        var projectSnapshot = document.Project.DeepClone();
        var sessionToken = sessionLifetimeCancellation.Token;

        _ = Task.Run(async () =>
        {
            Action uiContinuation;
            try
            {
                uiContinuation = await inferenceOperation(projectSnapshot, sessionToken);
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
            {
                return;
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
                UpdateInferenceAvailability();
                uiContinuation();
            });
        });
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
        LaunchInference($"writing a clip for {targetTrack.Name}", async (projectSnapshot, cancellationToken) =>
        {
            var generatedPattern = await ProductionWorkflows.AutocompletePatternAsync(
                inferenceProvider!, projectSnapshot, projectSnapshot.Tracks[trackIndex], sceneIndex, cancellationToken);
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
        LaunchInference($"rebranding {targetTrack.Name}", async (projectSnapshot, cancellationToken) =>
        {
            var proposedName = await ProductionWorkflows.NameTrackAsync(inferenceProvider!, projectSnapshot, projectSnapshot.Tracks[trackIndex], cancellationToken);
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

        LaunchInference($"designing {deviceName}", async (projectSnapshot, cancellationToken) =>
        {
            var tunedValues = await ProductionWorkflows.TweakDeviceAsync(
                inferenceProvider!, projectSnapshot, projectSnapshot.Tracks[trackIndex], deviceName, parameterSpecifications, currentValues, designGoal, cancellationToken);
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
        LaunchInference("AI mastering", async (projectSnapshot, cancellationToken) =>
        {
            await ProductionWorkflows.MasterAsync(inferenceProvider!, projectSnapshot, Array.Empty<string>(), cancellationToken);
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
        LaunchInference("vibe check", async (projectSnapshot, cancellationToken) =>
        {
            var vibeAssessment = await ProductionWorkflows.VibeCheckAsync(inferenceProvider!, projectSnapshot, Array.Empty<string>(), cancellationToken);
            return () =>
            {
                AppendCopilotMessage("vibe check", vibeAssessment);
                SetStatus(StatusSeverity.Success, "vibe checked");
            };
        });

    private void LaunchTempoNegotiation() =>
        LaunchInference("tempo negotiation", async (projectSnapshot, cancellationToken) =>
        {
            var negotiatedTempo = await ProductionWorkflows.NegotiateTempoAsync(inferenceProvider!, projectSnapshot, cancellationToken);
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
        LaunchInference("autopilot", async (projectSnapshot, cancellationToken) =>
        {
            var autopilotDeliverable = await ProductionWorkflows.AutopilotAsync(
                inferenceProvider!, projectSnapshot, resolvedInstruction, preferences.AutopilotRemediationCycles, cancellationToken);
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
        LaunchInference("composing", async (_, cancellationToken) =>
        {
            var composedDeliverable = await ProductionWorkflows.ComposeArrangementAsync(
                inferenceProvider!, creativeIntent.Trim(), preferences.AutopilotRemediationCycles, forcedImprovementCycles: 0,
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
        LaunchInference("frictionless waveform ideation", async (_, cancellationToken) =>
        {
            var hallucinatedWaveform = await ProductionWorkflows.IdeateWaveformAsync(inferenceProvider!, resolvedIntent, Array.Empty<string>(), cancellationToken);
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

    // ── Window-level input ────────────────────────────────────────────────────

    private void HandleWindowKeyDown(object? sender, KeyEventArgs keyEvent)
    {
        var commandModifierHeld = keyEvent.KeyModifiers.HasFlag(KeyModifiers.Control) || keyEvent.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shiftHeld = keyEvent.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (commandModifierHeld)
        {
            Action? shortcutAction = keyEvent.Key switch
            {
                Key.S => () => _ = SaveProjectAsync(saveAs: shiftHeld),
                Key.O => () => _ = OpenProjectAsync(),
                Key.N => () => _ = NewProjectAsync(),
                Key.Z when shiftHeld => PerformRedo,
                Key.Z => PerformUndo,
                Key.Y => PerformRedo,
                Key.OemComma => () => _ = OpenSettingsAsync(),
                _ => null,
            };
            if (shortcutAction is not null)
            {
                shortcutAction();
                keyEvent.Handled = true;
            }
            return;
        }

        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }
        switch (keyEvent.Key)
        {
            case Key.Space:
                TogglePlayback();
                keyEvent.Handled = true;
                break;
            case Key.Tab:
                SelectMainView(!arrangementViewActive);
                keyEvent.Handled = true;
                break;
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
