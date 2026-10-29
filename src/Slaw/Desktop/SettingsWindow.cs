using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Inference;
using SlopstackLabs.Slaw.Playback;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The studio settings dialog. Edits a private clone of the preferences and only
/// publishes it through <see cref="Result"/> when the user commits to Save, which
/// is more commitment than most roadmaps get.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly StudioPreferences workingPreferences;

    private readonly ToggleSwitch aiFeaturesToggle;
    private readonly ComboBox providerComboBox;
    private readonly TextBlock defaultModelCaption;
    private readonly TextBox modelTextBox;
    private readonly TextBox apiKeyTextBox;
    private readonly CheckBox rememberApiKeyCheckBox;
    private readonly TextBox baseUrlTextBox;
    private readonly TextBox ollamaEndpointTextBox;
    private readonly NumericUpDown requestTimeoutNumericUpDown;
    private readonly CheckBox unsolicitedAdviceCheckBox;
    private readonly NumericUpDown interjectionCadenceNumericUpDown;
    private readonly NumericUpDown remediationCyclesNumericUpDown;
    private readonly Button testConnectionButton;
    private readonly TextBlock testConnectionResultTextBlock;
    private readonly List<Control> aiDependentControls = new();

    private readonly ComboBox audioSubstrateComboBox;
    private readonly NumericUpDown audioLatencyNumericUpDown;

    /// <summary>The accepted preferences, or null if the user cancelled (or closed the window like a normal person).</summary>
    public StudioPreferences? Result { get; private set; }

    public SettingsWindow(StudioPreferences currentPreferences)
    {
        workingPreferences = currentPreferences.Clone();

        Title = "slaw — Settings";
        Width = 560;
        Height = 600;
        CanResize = false;
        Background = StudioTheme.Background;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        aiFeaturesToggle = new ToggleSwitch
        {
            Content = "AI features",
            OnContent = null,
            OffContent = null,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            IsChecked = workingPreferences.AiFeaturesEnabled,
        };
        providerComboBox = new ComboBox
        {
            ItemsSource = InferenceProviderFactory.SupportedProviderNames,
            SelectedItem = InferenceProviderFactory.SupportedProviderNames.Contains(workingPreferences.ProviderName)
                ? workingPreferences.ProviderName
                : InferenceProviderFactory.SupportedProviderNames[0],
            MinWidth = 160,
        };
        defaultModelCaption = StudioUi.Caption(string.Empty);
        modelTextBox = new TextBox { Watermark = "provider default", Text = workingPreferences.ModelIdentifier };
        apiKeyTextBox = new TextBox
        {
            PasswordChar = '•',
            Watermark = "or set SLAW_API_KEY",
            Text = workingPreferences.ApiKey,
        };
        rememberApiKeyCheckBox = new CheckBox
        {
            Content = "Remember API key (stored in plain text in settings.json)",
            IsChecked = workingPreferences.RememberApiKey,
        };
        baseUrlTextBox = new TextBox
        {
            Watermark = "http://localhost:1234/v1 — required for compat",
            Text = workingPreferences.CompatibleBaseUrl,
        };
        ollamaEndpointTextBox = new TextBox { Text = workingPreferences.OllamaEndpoint };
        requestTimeoutNumericUpDown = BuildNumericUpDown(workingPreferences.RequestTimeoutSeconds, 0, 3600);
        unsolicitedAdviceCheckBox = new CheckBox
        {
            Content = "Unsolicited advice",
            IsChecked = workingPreferences.CopilotInterjectionsEnabled,
        };
        interjectionCadenceNumericUpDown = BuildNumericUpDown(workingPreferences.EditsBetweenCopilotInterjections, 1, 50);
        remediationCyclesNumericUpDown = BuildNumericUpDown(workingPreferences.AutopilotRemediationCycles, 0, 5);
        testConnectionResultTextBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            FontSize = 12,
        };
        testConnectionButton = StudioUi.Button("Test connection", () => _ = RunConnectionTestAsync());

        audioSubstrateComboBox = new ComboBox
        {
            ItemsSource = new[] { "auto" }.Concat(PipedProcessAudioSink.SupportedSubstrateNames).ToArray(),
            MinWidth = 160,
        };
        audioSubstrateComboBox.SelectedItem = (audioSubstrateComboBox.ItemsSource as string[])!
            .Contains(workingPreferences.AudioSubstrate) ? workingPreferences.AudioSubstrate : "auto";
        audioLatencyNumericUpDown = BuildNumericUpDown(workingPreferences.AudioLatencyMilliseconds, 20, 500);

        providerComboBox.SelectionChanged += (_, _) => RefreshDefaultModelCaption();
        aiFeaturesToggle.IsCheckedChanged += (_, _) => RefreshAiDependentEnablement();
        RefreshDefaultModelCaption();

        var tabControl = new TabControl
        {
            Items =
            {
                new TabItem { Header = "AI", Content = WrapInScrollViewer(BuildAiTab()) },
                new TabItem { Header = "Audio", Content = WrapInScrollViewer(BuildAudioTab()) },
                new TabItem { Header = "About", Content = WrapInScrollViewer(BuildAboutTab()) },
            },
        };

        var buttonRow = StudioUi.Row(8,
            StudioUi.Button("Cancel", Close),
            StudioUi.AccentButton("Save", CommitAndClose));
        buttonRow.HorizontalAlignment = HorizontalAlignment.Right;
        buttonRow.Margin = new Thickness(16, 8, 16, 16);

        var rootGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(tabControl, 0);
        Grid.SetRow(buttonRow, 1);
        rootGrid.Children.Add(tabControl);
        rootGrid.Children.Add(buttonRow);
        Content = rootGrid;

        RefreshAiDependentEnablement();
    }

    private static NumericUpDown BuildNumericUpDown(int initialValue, int minimum, int maximum) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Increment = 1,
        FormatString = "0",
        Value = Math.Clamp(initialValue, minimum, maximum),
        MinWidth = 120,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static ScrollViewer WrapInScrollViewer(Control tabContent) => new()
    {
        Padding = new Thickness(16),
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Content = tabContent,
    };

    private Control TrackAiDependent(Control dependentControl)
    {
        aiDependentControls.Add(dependentControl);
        return dependentControl;
    }

    private void RefreshAiDependentEnablement()
    {
        var aiEnabled = aiFeaturesToggle.IsChecked == true;
        foreach (var dependentControl in aiDependentControls)
        {
            dependentControl.IsEnabled = aiEnabled;
        }
    }

    private void RefreshDefaultModelCaption()
    {
        var selectedProviderName = providerComboBox.SelectedItem as string ?? "local";
        defaultModelCaption.Text = $"default model: {InferenceProviderFactory.DefaultModelFor(selectedProviderName)}";
    }

    private static Control FormRow(Grid formGrid, int rowIndex, string label, Control field, Control? trailingCaption = null)
    {
        formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var labelTextBlock = StudioUi.Caption(label, 12, StudioTheme.Text);
        labelTextBlock.TextTrimming = TextTrimming.None;
        labelTextBlock.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(labelTextBlock, rowIndex);
        Grid.SetColumn(labelTextBlock, 0);
        formGrid.Children.Add(labelTextBlock);

        Control fieldHost = field;
        if (trailingCaption is not null)
        {
            var hostPanel = new StackPanel { Spacing = 2 };
            hostPanel.Children.Add(field);
            hostPanel.Children.Add(trailingCaption);
            fieldHost = hostPanel;
        }
        fieldHost.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(fieldHost, rowIndex);
        Grid.SetColumn(fieldHost, 1);
        formGrid.Children.Add(fieldHost);
        return fieldHost;
    }

    private Control BuildAiTab()
    {
        var tabPanel = new StackPanel { Spacing = 10 };
        tabPanel.Children.Add(aiFeaturesToggle);
        var masterCaption = StudioUi.Caption(
            "Off: slaw is a plain step-sequencing DAW. No AI buttons, no co-pilot, no network traffic. Nobody at slopstack labs has ever turned it off.");
        masterCaption.TextWrapping = TextWrapping.Wrap;
        masterCaption.TextTrimming = TextTrimming.None;
        tabPanel.Children.Add(masterCaption);

        var formGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*"),
            RowSpacing = 10,
            ColumnSpacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var timeoutCaption = StudioUi.Caption("0 = no timeout");
        var row = 0;
        FormRow(formGrid, row++, "Provider", providerComboBox, defaultModelCaption);
        FormRow(formGrid, row++, "Model", modelTextBox);
        FormRow(formGrid, row++, "API key", apiKeyTextBox, rememberApiKeyCheckBox);
        FormRow(formGrid, row++, "Base URL", baseUrlTextBox);
        FormRow(formGrid, row++, "Ollama endpoint", ollamaEndpointTextBox);
        FormRow(formGrid, row++, "Request timeout (s)", requestTimeoutNumericUpDown, timeoutCaption);
        TrackAiDependent(formGrid);
        tabPanel.Children.Add(formGrid);

        var copilotPanel = new StackPanel { Spacing = 10 };
        copilotPanel.Children.Add(StudioUi.SectionHeading("Co-pilot"));
        copilotPanel.Children.Add(unsolicitedAdviceCheckBox);
        var copilotGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), RowSpacing = 10, ColumnSpacing = 8 };
        var copilotRow = 0;
        FormRow(copilotGrid, copilotRow++, "Interjects every N edits", interjectionCadenceNumericUpDown);
        FormRow(copilotGrid, copilotRow++, "Autopilot remediation cycles", remediationCyclesNumericUpDown);
        copilotPanel.Children.Add(copilotGrid);
        TrackAiDependent(copilotPanel);
        tabPanel.Children.Add(copilotPanel);

        var testRow = StudioUi.Row(10, testConnectionButton, testConnectionResultTextBlock);
        testRow.Margin = new Thickness(0, 6, 0, 0);
        TrackAiDependent(testRow);
        tabPanel.Children.Add(testRow);
        return tabPanel;
    }

    private Control BuildAudioTab()
    {
        var tabPanel = new StackPanel { Spacing = 12 };
        var formGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowSpacing = 10, ColumnSpacing = 8 };
        var substrateCaption = OperatingSystem.IsWindows()
            ? StudioUi.Caption("On Windows, waveOut is always used. This setting is decorative, like most settings.")
            : null;
        if (substrateCaption is not null)
        {
            substrateCaption.TextWrapping = TextWrapping.Wrap;
            substrateCaption.TextTrimming = TextTrimming.None;
        }
        var latencyCaption = StudioUi.Caption("Lower is snappier; higher survives a busy CPU. Edits are heard on the next step either way.");
        latencyCaption.TextWrapping = TextWrapping.Wrap;
        latencyCaption.TextTrimming = TextTrimming.None;

        var row = 0;
        FormRow(formGrid, row++, "Live audio substrate", audioSubstrateComboBox, substrateCaption);
        FormRow(formGrid, row++, "Latency (ms)", audioLatencyNumericUpDown, latencyCaption);
        tabPanel.Children.Add(formGrid);

        var restartCaption = StudioUi.Caption("Changes apply when you press Save (the audio engine restarts).");
        restartCaption.TextWrapping = TextWrapping.Wrap;
        restartCaption.TextTrimming = TextTrimming.None;
        tabPanel.Children.Add(restartCaption);
        return tabPanel;
    }

    private static Control BuildAboutTab()
    {
        var versionText = typeof(SettingsWindow).Assembly.GetName().Version?.ToString() ?? "unversioned (very agile)";
        var tabPanel = new StackPanel { Spacing = 8 };
        tabPanel.Children.Add(new TextBlock
        {
            Text = "slaw",
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = StudioTheme.Accent,
        });
        tabPanel.Children.Add(StudioUi.Caption("beyond deterministic production", 13, StudioTheme.Text));
        tabPanel.Children.Add(StudioUi.Caption($"version {versionText}", 12));
        tabPanel.Children.Add(StudioUi.SectionHeading("Settings file"));
        tabPanel.Children.Add(new SelectableTextBlock
        {
            Text = StudioPreferences.PreferencesFilePath,
            FontFamily = StudioTheme.MonospaceFont,
            FontSize = 12,
            Foreground = StudioTheme.Text,
            TextWrapping = TextWrapping.Wrap,
        });
        var disclaimer = StudioUi.Caption(
            "slopstack labs accepts no liability for tracks that sound exactly as good as the prompt that produced them.");
        disclaimer.TextWrapping = TextWrapping.Wrap;
        disclaimer.TextTrimming = TextTrimming.None;
        disclaimer.Margin = new Thickness(0, 12, 0, 0);
        tabPanel.Children.Add(disclaimer);
        return tabPanel;
    }

    private static string? NullIfBlank(string? possiblyBlankText) =>
        string.IsNullOrWhiteSpace(possiblyBlankText) ? null : possiblyBlankText.Trim();

    private static int ReadWholeNumber(NumericUpDown numericUpDown, int fallbackValue) =>
        numericUpDown.Value is { } currentValue ? (int)Math.Round(currentValue) : fallbackValue;

    private void ApplyFormToWorkingPreferences()
    {
        workingPreferences.AiFeaturesEnabled = aiFeaturesToggle.IsChecked == true;
        workingPreferences.ProviderName = providerComboBox.SelectedItem as string ?? workingPreferences.ProviderName;
        workingPreferences.ModelIdentifier = NullIfBlank(modelTextBox.Text);
        workingPreferences.ApiKey = NullIfBlank(apiKeyTextBox.Text);
        workingPreferences.RememberApiKey = rememberApiKeyCheckBox.IsChecked == true;
        workingPreferences.CompatibleBaseUrl = NullIfBlank(baseUrlTextBox.Text);
        workingPreferences.OllamaEndpoint = NullIfBlank(ollamaEndpointTextBox.Text) ?? OllamaInferenceProvider.DefaultOllamaEndpoint;
        workingPreferences.RequestTimeoutSeconds = ReadWholeNumber(requestTimeoutNumericUpDown, workingPreferences.RequestTimeoutSeconds);
        workingPreferences.CopilotInterjectionsEnabled = unsolicitedAdviceCheckBox.IsChecked == true;
        workingPreferences.EditsBetweenCopilotInterjections = ReadWholeNumber(interjectionCadenceNumericUpDown, workingPreferences.EditsBetweenCopilotInterjections);
        workingPreferences.AutopilotRemediationCycles = ReadWholeNumber(remediationCyclesNumericUpDown, workingPreferences.AutopilotRemediationCycles);
        workingPreferences.AudioSubstrate = audioSubstrateComboBox.SelectedItem as string ?? "auto";
        workingPreferences.AudioLatencyMilliseconds = ReadWholeNumber(audioLatencyNumericUpDown, workingPreferences.AudioLatencyMilliseconds);
    }

    private void CommitAndClose()
    {
        ApplyFormToWorkingPreferences();
        Result = workingPreferences;
        Close();
    }

    private async Task RunConnectionTestAsync()
    {
        testConnectionButton.IsEnabled = false;
        testConnectionResultTextBlock.Foreground = StudioTheme.TextMuted;
        testConnectionResultTextBlock.Text = "testing…";
        try
        {
            ApplyFormToWorkingPreferences();
            var routingIntent = workingPreferences.Clone().ToRoutingIntent();
            var inferenceProvider = InferenceProviderFactory.Materialize(routingIntent);
            using var testTimeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var replyText = await inferenceProvider.StreamCompletionAsync(
                "Reply with exactly: slaw substrate online", Array.Empty<string>(), testTimeoutSource.Token);
            var trimmedReply = replyText.Trim();
            var replyExcerpt = trimmedReply.Length <= 80 ? trimmedReply : trimmedReply[..80];
            testConnectionResultTextBlock.Foreground = StudioTheme.Playhead;
            testConnectionResultTextBlock.Text =
                $"✓ {inferenceProvider.ModelIdentifier} via {inferenceProvider.ProviderDisplayName} replied: “{replyExcerpt}”";
        }
        catch (Exception connectionTestRemediationOpportunity)
        {
            testConnectionResultTextBlock.Foreground = StudioTheme.Failure;
            testConnectionResultTextBlock.Text = $"✗ {connectionTestRemediationOpportunity.Message}";
        }
        finally
        {
            testConnectionButton.IsEnabled = true;
        }
    }
}
