using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The bottom panel: the selected clip's editor on the left (a piano roll for
/// melodic tracks, a step row for drums) and the selected track's device chain
/// on the right — instrument, effects, and the master bus, all as knobs.
/// </summary>
internal sealed class DetailView : UserControl
{
    private static readonly int[] ClipLengthChoices = { 4, 8, 12, 16, 24, 32, 48, 64 };

    private readonly StudioDocument document;
    private readonly IStudioHost host;
    private readonly ContentControl clipEditorHost = new();
    private readonly ContentControl deviceChainHost = new();
    private PianoRollControl? activePianoRoll;
    private StepRowControl? activeStepRow;
    private int pianoRollLowestNote = 48;

    // Sampler tracks get a Clip / Sample tab pair; the sample editor is kept
    // alive per track so zoom and selection survive rebuilds.
    private bool sampleTabActive;
    private SampleEditorView? activeSampleEditor;
    private string? activeSampleEditorKey;

    /// <summary>Raised when a tab needs more vertical room than the panel currently has.</summary>
    public event Action<double>? MinimumHeightRequested;

    private const double SampleEditorPreferredHeight = 500;

    public DetailView(StudioDocument document, IStudioHost host)
    {
        this.document = document;
        this.host = host;

        var detailLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,4,4*"), Margin = new Thickness(12, 6, 12, 10) };
        var splitter = new GridSplitter { Background = StudioTheme.Border, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(deviceChainHost, 2);
        clipEditorHost.Margin = new Thickness(0, 0, 8, 0);
        deviceChainHost.Margin = new Thickness(8, 0, 0, 0);
        detailLayout.Children.Add(clipEditorHost);
        detailLayout.Children.Add(splitter);
        detailLayout.Children.Add(deviceChainHost);
        Content = detailLayout;
        Rebuild();
    }

    public void Rebuild()
    {
        clipEditorHost.Content = ComposeLeftPanel();
        deviceChainHost.Content = ComposeDeviceChain();
    }

    public void RefreshContent()
    {
        activeSampleEditor?.Refresh();
        activePianoRoll?.InvalidateVisual();
        activeStepRow?.InvalidateVisual();
    }

    public void UpdateTransport(TransportSnapshot transportSnapshot)
    {
        activeSampleEditor?.UpdatePreviewPlayhead();
        int? playheadStep = null;
        if (document.SelectedTrack is { } selectedTrack &&
            transportSnapshot.Mode != TransportMode.Stopped &&
            transportSnapshot.Tracks.TryGetValue(selectedTrack.Id, out var trackState) &&
            trackState.PlayingScene == document.SelectedSceneIndex)
        {
            playheadStep = trackState.ClipStep;
        }
        if (activePianoRoll is not null)
        {
            activePianoRoll.PlayheadStep = playheadStep;
        }
        if (activeStepRow is not null)
        {
            activeStepRow.PlayheadStep = playheadStep;
        }
    }

    // ── Clip editor ───────────────────────────────────────────────────────────

    /// <summary>Switches the left panel to the sample editor (used by the sampler device's ✎ button).</summary>
    public void ShowSampleTab()
    {
        sampleTabActive = true;
        Rebuild();
    }

    private Control ComposeLeftPanel()
    {
        if (document.SelectedTrack is not { Instrument: "sampler" } samplerTrack)
        {
            activeSampleEditor = null;
            activeSampleEditorKey = null;
            return ComposeClipEditor();
        }

        var clipTabToggle = new Avalonia.Controls.Primitives.ToggleButton { Content = "Clip", IsChecked = !sampleTabActive, Padding = new Thickness(12, 3), FontSize = 12 };
        var sampleTabToggle = new Avalonia.Controls.Primitives.ToggleButton { Content = "Sample", IsChecked = sampleTabActive, Padding = new Thickness(12, 3), FontSize = 12 };
        clipTabToggle.Click += (_, _) =>
        {
            sampleTabActive = false;
            Rebuild();
        };
        sampleTabToggle.Click += (_, _) =>
        {
            sampleTabActive = true;
            Rebuild();
        };
        if (sampleTabActive)
        {
            MinimumHeightRequested?.Invoke(SampleEditorPreferredHeight);
        }
        var tabRow = StudioUi.Row(0, clipTabToggle, sampleTabToggle);
        tabRow.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(tabRow, Dock.Top);

        Control tabContent;
        if (sampleTabActive)
        {
            var sampleEditorKey = $"{samplerTrack.Id}|{host.AiFeaturesEnabled}";
            if (activeSampleEditor is null || activeSampleEditorKey != sampleEditorKey)
            {
                activeSampleEditor = new SampleEditorView(document, host, samplerTrack.Id);
                activeSampleEditorKey = sampleEditorKey;
            }
            else
            {
                activeSampleEditor.Refresh();
            }
            // Detach from any previous parent before re-hosting the cached view.
            if (activeSampleEditor.Parent is ContentControl previousHost)
            {
                previousHost.Content = null;
            }
            else if (activeSampleEditor.Parent is Panel previousPanel)
            {
                previousPanel.Children.Remove(activeSampleEditor);
            }
            tabContent = activeSampleEditor;
        }
        else
        {
            tabContent = ComposeClipEditor();
        }
        return new DockPanel { Children = { tabRow, tabContent } };
    }

    private Control ComposeClipEditor()
    {
        activePianoRoll = null;
        activeStepRow = null;
        var trackIndex = document.SelectedTrackIndex;
        var sceneIndex = document.SelectedSceneIndex;
        if (document.SelectedTrack is not { } track)
        {
            return StudioUi.Caption("No track selected.");
        }
        var sceneName = sceneIndex < document.Project.Scenes.Count ? document.Project.Scenes[sceneIndex] : "?";
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var titleRow = StudioUi.Row(8,
            new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = StudioTheme.TrackBrush(trackIndex) },
            StudioUi.Caption($"{track.Name}  ·  {sceneName}", 12, StudioTheme.Text, FontWeight.SemiBold));
        headerRow.Children.Add(titleRow);

        if (track.ClipAt(sceneIndex) is not { } clip)
        {
            var emptyActions = StudioUi.Row(8,
                StudioUi.AccentButton("Create clip", () => document.CreateClip(trackIndex, sceneIndex)));
            if (host.AiFeaturesEnabled)
            {
                emptyActions.Children.Add(StudioUi.AiButton("✨ Generate clip", () => host.GenerateClipWithAi(trackIndex, sceneIndex), "Let the AI write this clip", compact: false));
            }
            return new DockPanel
            {
                Children =
                {
                    Docked(headerRow, Dock.Top),
                    new StackPanel
                    {
                        Spacing = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            StudioUi.Caption("Empty clip slot. Double-click a slot in the session view, or:", 12),
                            emptyActions,
                        },
                    },
                },
            };
        }

        var clipNameInput = new TextBox { Text = clip.Name, Width = 140, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        clipNameInput.LostFocus += (_, _) =>
        {
            var revisedName = clipNameInput.Text?.Trim();
            if (!string.IsNullOrEmpty(revisedName) && revisedName != clip.Name)
            {
                document.Edit($"renamed a clip to {revisedName}", _ => clip.Name = revisedName);
            }
        };
        var lengthSelector = new ComboBox
        {
            ItemsSource = ClipLengthChoices.Select(stepCount => $"{stepCount} steps").ToList(),
            SelectedIndex = Array.IndexOf(ClipLengthChoices, clip.Pattern.Count),
            FontSize = 12,
            Width = 110,
        };
        if (lengthSelector.SelectedIndex < 0)
        {
            lengthSelector.PlaceholderText = $"{clip.Pattern.Count} steps";
        }
        lengthSelector.SelectionChanged += (_, _) =>
        {
            if (lengthSelector.SelectedIndex >= 0 && ClipLengthChoices[lengthSelector.SelectedIndex] != clip.Pattern.Count)
            {
                var requestedLength = ClipLengthChoices[lengthSelector.SelectedIndex];
                document.Edit($"resized a clip to {requestedLength} steps", _ => PatternEditingOperations.ResizeClip(clip, requestedLength), EditScope.Structure);
            }
        };

        var clipActions = StudioUi.Row(6,
            clipNameInput,
            lengthSelector,
            StudioUi.Button("Clear", () => document.Edit("cleared a clip", _ =>
            {
                for (var stepIndex = 0; stepIndex < clip.Pattern.Count; stepIndex++)
                {
                    clip.Pattern[stepIndex] = NotationResolver.RestSymbol;
                }
            }, EditScope.Structure), "Silence every step", compact: true),
            StudioUi.Button("▶ Clip", () => host.LaunchClip(trackIndex, sceneIndex), "Launch this clip", compact: true));
        if (host.AiFeaturesEnabled)
        {
            clipActions.Children.Add(StudioUi.AiButton("✨ Rewrite", () => host.GenerateClipWithAi(trackIndex, sceneIndex), "Let the AI rewrite this clip"));
        }
        headerRow.Children.Remove(titleRow);
        var clipHeader = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8), Children = { titleRow, clipActions } };

        Control editorBody;
        if (InstrumentVoiceSynthesizer.IsPercussive(track.Instrument))
        {
            activeStepRow = new StepRowControl(track, clip, clip.Pattern.Count) { Height = 64, VerticalAlignment = VerticalAlignment.Top };
            activeStepRow.EditStarted += () => document.BeginEdit("drum step");
            activeStepRow.PatternEdited += description => document.CommitEdit(description);
            editorBody = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    activeStepRow,
                    StudioUi.Caption("click: hit · right-click: clear · drums are pitch-agnostic by design (tune them with the Pitch knob)", 10),
                },
            };
        }
        else
        {
            var clipPitches = clip.Pattern.Select(NotationResolver.ResolveOrRest).SelectMany(stepEvent => stepEvent.MidiNotes).ToList();
            pianoRollLowestNote = clipPitches.Count > 0 ? Math.Clamp(clipPitches.Min() - 5, 12, 96) : 48;
            activePianoRoll = new PianoRollControl(clip, pianoRollLowestNote, 25);
            activePianoRoll.EditStarted += () => document.BeginEdit("piano roll");
            activePianoRoll.PatternEdited += description => document.CommitEdit($"{description} on {track.Name}");
            activePianoRoll.NoteAuditionStarted += midiNote => host.StartNoteAudition(track.Id, midiNote);
            activePianoRoll.NoteAuditionEnded += host.StopNoteAudition;
            var octaveButtons = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    StudioUi.Button("▲", () => ShiftPianoRoll(12), "Octave up", compact: true),
                    StudioUi.Button("▼", () => ShiftPianoRoll(-12), "Octave down", compact: true),
                },
            };
            DockPanel.SetDock(octaveButtons, Dock.Right);
            editorBody = new DockPanel { Children = { octaveButtons, activePianoRoll } };
        }

        return new DockPanel
        {
            Children =
            {
                Docked(clipHeader, Dock.Top),
                editorBody,
            },
        };
    }

    private void ShiftPianoRoll(int semitoneOffset)
    {
        if (activePianoRoll is null)
        {
            return;
        }
        pianoRollLowestNote = Math.Clamp(activePianoRoll.LowestVisibleMidiNote + semitoneOffset, 12, 96);
        activePianoRoll.LowestVisibleMidiNote = pianoRollLowestNote;
    }

    private static Control Docked(Control dockedControl, Dock dockPosition)
    {
        DockPanel.SetDock(dockedControl, dockPosition);
        return dockedControl;
    }

    // ── Device chain ──────────────────────────────────────────────────────────

    private Control ComposeDeviceChain()
    {
        var deviceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (document.SelectedTrack is { } track)
        {
            var trackIndex = document.SelectedTrackIndex;
            deviceRow.Children.Add(ComposeInstrumentDevice(track, trackIndex));
            for (var effectIndex = 0; effectIndex < track.Effects.Count; effectIndex++)
            {
                deviceRow.Children.Add(ComposeEffectDevice(track, trackIndex, effectIndex));
            }
            deviceRow.Children.Add(ComposeAddEffectButton(track));
        }
        deviceRow.Children.Add(new Border { Width = 1, Background = StudioTheme.Border, Margin = new Thickness(4, 0) });
        deviceRow.Children.Add(ComposeMasterDevice());

        return new ScrollViewer
        {
            Content = deviceRow,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    private Control ComposeInstrumentDevice(SlawTrack track, int trackIndex)
    {
        var instrumentSelector = new ComboBox
        {
            ItemsSource = InstrumentVoiceSynthesizer.SupportedInstruments,
            SelectedItem = track.Instrument,
            FontSize = 12,
            Width = 110,
        };
        instrumentSelector.SelectionChanged += (_, _) =>
        {
            if (instrumentSelector.SelectedItem is string selectedInstrument && selectedInstrument != track.Instrument)
            {
                document.Edit($"switched {track.Name} to {selectedInstrument}", _ => PatternEditingOperations.ChangeInstrument(track, selectedInstrument), EditScope.Structure);
            }
        };

        var parameterSpecifications = DeviceCatalog.InstrumentParametersFor(track.Instrument);
        var knobs = parameterSpecifications.Select(parameterSpecification => ComposeParameterKnob(
            parameterSpecification,
            () => track.InstrumentParameters,
            $"{track.Id}/instrument/{parameterSpecification.Key}",
            $"{track.Name} {parameterSpecification.Label}")).ToList();

        var titleRow = StudioUi.Row(6, instrumentSelector);
        if (host.AiFeaturesEnabled)
        {
            titleRow.Children.Add(StudioUi.AiButton("✨", () => host.TuneDeviceWithAi(trackIndex, -1), "Let the AI design this sound"));
        }
        Control titleControls = titleRow;
        if (track.Instrument == "sampler")
        {
            titleControls = new StackPanel { Spacing = 4, Children = { titleRow, ComposeSamplerSection(track, trackIndex) } };
        }
        return ComposeDevicePanel(DeviceCatalog.InstrumentDisplayName(track.Instrument), titleControls, knobs, StudioTheme.TrackBrush(trackIndex));
    }

    private Control ComposeSamplerSection(SlawTrack track, int trackIndex)
    {
        var sampleActions = StudioUi.Row(4,
            StudioUi.Button("Load…", () => host.ChooseSampleFileForTrack(trackIndex), "Load a WAV file into this sampler", compact: true));
        if (track.Sample is not null)
        {
            sampleActions.Children.Add(StudioUi.Button("✎ Edit", ShowSampleTab, "Open the sample editor: trim, fades, loop, slices…", compact: true));
            sampleActions.Children.Add(StudioUi.Button("⤢", () => host.OpenSampleEditorWindow(track.Id), "Open the sample editor in its own window", compact: true));
        }
        if (track.Sample is { } samplePath)
        {
            sampleActions.Children.Add(StudioUi.Button("▶", () => host.PreviewSampleFile(samplePath), "Preview the raw sample", compact: true));
        }
        if (host.AiFeaturesEnabled)
        {
            sampleActions.Children.Add(StudioUi.AiButton("✨ Pick", () => host.PickSampleWithAi(trackIndex), "Let the AI choose a sample from your sample folders"));
        }

        var sampleOverview = new WaveformScopeControl { Width = 230, Height = 46 };
        if (track.Sample is null)
        {
            return new StackPanel { Spacing = 4, Children = { StudioUi.Caption("No sample loaded — drop a WAV here or use Load…", 10, StudioTheme.Warning), sampleActions } };
        }
        try
        {
            var sampleData = SampleLibrary.Load(track.Sample);
            sampleOverview.Present(SampleLibrary.ToStereoBuffer(sampleData),
                FormattableString.Invariant($"{Path.GetFileName(track.Sample)} · {sampleData.DurationSeconds:0.00} s"), hallucinatedDeliverable: false);
        }
        catch (Exception sampleRemediationOpportunity) when (sampleRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    StudioUi.Caption($"⚠ {Path.GetFileName(track.Sample)}: {sampleRemediationOpportunity.Message}", 10, StudioTheme.Failure),
                    sampleActions,
                },
            };
        }
        ToolTip.SetTip(sampleOverview, track.Sample);
        return new StackPanel { Spacing = 4, Children = { sampleOverview, sampleActions } };
    }

    private Control ComposeEffectDevice(SlawTrack track, int trackIndex, int effectIndex)
    {
        var effectDevice = track.Effects[effectIndex];
        var enabledToggle = new CheckBox { IsChecked = effectDevice.Enabled, Padding = new Thickness(2, 0), MinWidth = 0 };
        ToolTip.SetTip(enabledToggle, "Device on/off");
        enabledToggle.IsCheckedChanged += (_, _) =>
        {
            if (effectDevice.Enabled != (enabledToggle.IsChecked == true))
            {
                document.Edit($"{(enabledToggle.IsChecked == true ? "enabled" : "bypassed")} {DeviceCatalog.EffectDisplayName(effectDevice.Type)}",
                    _ => effectDevice.Enabled = enabledToggle.IsChecked == true);
            }
        };

        var titleRow = StudioUi.Row(2,
            enabledToggle,
            StudioUi.Button("◀", () => MoveEffect(track, effectIndex, -1), "Move left", compact: true),
            StudioUi.Button("▶", () => MoveEffect(track, effectIndex, 1), "Move right", compact: true),
            StudioUi.Button("✕", () => document.Edit($"removed {DeviceCatalog.EffectDisplayName(effectDevice.Type)}",
                _ => track.Effects.RemoveAt(effectIndex), EditScope.Structure), "Remove device", compact: true));
        if (host.AiFeaturesEnabled)
        {
            titleRow.Children.Add(StudioUi.AiButton("✨", () => host.TuneDeviceWithAi(trackIndex, effectIndex), "Let the AI dial this in"));
        }

        var knobs = DeviceCatalog.EffectParametersFor(effectDevice.Type).Select(parameterSpecification => ComposeParameterKnob(
            parameterSpecification,
            () => effectDevice.Parameters,
            $"{track.Id}/fx{effectIndex}/{parameterSpecification.Key}",
            $"{DeviceCatalog.EffectDisplayName(effectDevice.Type)} {parameterSpecification.Label}")).ToList();

        var devicePanel = ComposeDevicePanel(DeviceCatalog.EffectDisplayName(effectDevice.Type), titleRow, knobs, StudioTheme.Violet);
        var effectDescription = DeviceCatalog.EffectDescription(effectDevice.Type);
        if (!string.IsNullOrEmpty(effectDescription))
        {
            ToolTip.SetTip(devicePanel, effectDescription);
        }
        devicePanel.Opacity = effectDevice.Enabled ? 1 : 0.55;
        return devicePanel;
    }

    private void MoveEffect(SlawTrack track, int effectIndex, int offset)
    {
        var destinationIndex = effectIndex + offset;
        if (destinationIndex < 0 || destinationIndex >= track.Effects.Count)
        {
            return;
        }
        document.Edit("reordered the device chain", _ =>
        {
            var movedEffect = track.Effects[effectIndex];
            track.Effects.RemoveAt(effectIndex);
            track.Effects.Insert(destinationIndex, movedEffect);
        }, EditScope.Structure);
    }

    private Control ComposeAddEffectButton(SlawTrack track)
    {
        var addEffectButton = new Button
        {
            Content = "+ Effect",
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(10, 6),
        };
        var effectMenu = new MenuFlyout();
        foreach (var effectType in DeviceCatalog.EffectTypes)
        {
            var effectMenuItem = new MenuItem { Header = DeviceCatalog.EffectDisplayName(effectType) };
            effectMenuItem.Click += (_, _) => AddEffect(track, effectType);
            effectMenu.Items.Add(effectMenuItem);
        }
        addEffectButton.Flyout = effectMenu;
        addEffectButton.IsEnabled = track.Effects.Count < ProjectAlignmentRemediator.MaximumEffectsPerTrack;
        return addEffectButton;
    }

    public void AddEffect(SlawTrack track, string effectType)
    {
        if (track.Effects.Count >= ProjectAlignmentRemediator.MaximumEffectsPerTrack)
        {
            return;
        }
        document.Edit($"added {DeviceCatalog.EffectDisplayName(effectType)} to {track.Name}",
            _ => track.Effects.Add(new EffectDevice { Type = effectType }), EditScope.Structure);
    }

    private Control ComposeMasterDevice()
    {
        var masterChain = document.Project.Master;
        var masterKnobs = new List<Control>
        {
            ComposeMasterKnob(new ParameterSpecification("drive", "Drive", 1, 20, 1, "×", Logarithmic: true), () => masterChain.Drive, value => masterChain.Drive = value),
            ComposeMasterKnob(new ParameterSpecification("lowpass", "Lowpass", 200, 20000, 18000, "Hz", Logarithmic: true), () => masterChain.LowpassHz, value => masterChain.LowpassHz = value),
            ComposeMasterKnob(new ParameterSpecification("delaySteps", "Delay Time", 1, 16, 3, "steps", Integer: true), () => masterChain.DelaySteps, value => masterChain.DelaySteps = (int)value),
            ComposeMasterKnob(new ParameterSpecification("delayFeedback", "Feedback", 0, 0.9, 0.3, "%"), () => masterChain.DelayFeedback, value => masterChain.DelayFeedback = value),
            ComposeMasterKnob(new ParameterSpecification("delayMix", "Delay Mix", 0, 1, 0, "%"), () => masterChain.DelayMix, value => masterChain.DelayMix = value),
        };
        var titleRow = StudioUi.Row(6);
        if (host.AiFeaturesEnabled)
        {
            titleRow.Children.Add(StudioUi.AiButton("✨ AI master", () => host.TuneDeviceWithAi(-1, -2), "Let a confident mastering engineer who has never heard the track set the master bus"));
        }
        return ComposeDevicePanel("Master Bus", titleRow, masterKnobs, StudioTheme.Accent);
    }

    private Control ComposeMasterKnob(ParameterSpecification parameterSpecification, Func<double> readValue, Action<double> writeValue)
    {
        var parameterAddress = $"master/{parameterSpecification.Key}";
        var masterKnob = StudioUi.Knob(parameterSpecification, readValue());
        masterKnob.AiHighlighted = host.IsAiTuned(parameterAddress);
        masterKnob.EditStarted += () =>
        {
            document.BeginEdit($"master {parameterSpecification.Label}");
            host.ForgetAiTuning(parameterAddress);
            masterKnob.AiHighlighted = false;
        };
        masterKnob.ValueChanged += changedValue =>
        {
            writeValue(changedValue);
            document.CommitEdit($"adjusted the master {parameterSpecification.Label}", notifyCopilot: false);
        };
        return masterKnob;
    }

    private Control ComposeParameterKnob(ParameterSpecification parameterSpecification, Func<Dictionary<string, double>> parameterTable, string parameterAddress, string editLabel)
    {
        var parameterKnob = StudioUi.Knob(parameterSpecification, DeviceCatalog.Resolve(parameterTable(), parameterSpecification));
        parameterKnob.AiHighlighted = host.IsAiTuned(parameterAddress);
        parameterKnob.EditStarted += () =>
        {
            document.BeginEdit(editLabel);
            host.ForgetAiTuning(parameterAddress);
            parameterKnob.AiHighlighted = false;
        };
        parameterKnob.ValueChanged += changedValue =>
        {
            parameterTable()[parameterSpecification.Key] = parameterSpecification.Clamp(changedValue);
            document.CommitEdit($"adjusted {editLabel}", notifyCopilot: false);
        };
        return parameterKnob;
    }

    private static Control ComposeDevicePanel(string deviceName, Control titleControls, IReadOnlyList<Control> knobs, IBrush accentBrush)
    {
        var knobGrid = new WrapPanel { Orientation = Orientation.Vertical, MaxHeight = 172 };
        foreach (var knob in knobs)
        {
            knob.Margin = new Thickness(2);
            knobGrid.Children.Add(knob);
        }
        var deviceHeader = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                StudioUi.Caption(deviceName, 11, StudioTheme.Text, FontWeight.Bold),
                titleControls,
            },
        };
        DockPanel.SetDock(deviceHeader, Dock.Top);
        return new Border
        {
            Background = StudioTheme.SurfaceRaised,
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            MinWidth = 150,
            Child = new DockPanel { Children = { deviceHeader, knobGrid } },
        };
    }
}
