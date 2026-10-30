using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Playback;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>The Recording block of Settings → Audio: which tool listens, to which input, with how much latency.</summary>
internal sealed class RecordingSettingsForm
{
    private const string DefaultDeviceLabel = "system default";
    private static readonly TimeSpan InputTestDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan InputTestMeterInterval = TimeSpan.FromMilliseconds(50);

    // At most one 16-bit step: a muted or refused microphone. A real one in a quiet room measured 0.00006, so anything above this is signal.
    private const float InputTestSilencePeak = 1.5f / 32768f;

    private readonly ComboBox substrateComboBox;
    private readonly ComboBox deviceComboBox;
    private readonly NumericUpDown inputLatencyNumericUpDown;
    private readonly Button testInputButton;
    private readonly ProgressBar inputTestMeter = new() { Minimum = 0, Maximum = 1, Width = 140, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock inputTestResultTextBlock = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    private IAudioInputSource? inputUnderTest;
    private DispatcherTimer? inputTestTimer;
    private float inputTestLoudestPeak;
    private DateTime inputTestDeadline;

    public RecordingSettingsForm(StudioPreferences preferences)
    {
        testInputButton = StudioUi.Button("Test input", ToggleInputTest);
        var substrateChoices = new[] { "auto" }.Concat(PipedProcessAudioInput.SupportedSubstrateNames).ToArray();
        substrateComboBox = new ComboBox
        {
            ItemsSource = substrateChoices,
            SelectedItem = substrateChoices.Contains(preferences.RecordingSubstrate) ? preferences.RecordingSubstrate : "auto",
            MinWidth = 160,
            IsEnabled = OperatingSystem.IsLinux(),
        };

        var deviceChoices = new List<string> { DefaultDeviceLabel };
        deviceChoices.AddRange(AudioInputFactory.ListInputDevices());
        if (preferences.RecordingInputDevice is { } savedDevice && !deviceChoices.Contains(savedDevice))
        {
            deviceChoices.Add(savedDevice);
        }
        deviceComboBox = new ComboBox
        {
            ItemsSource = deviceChoices,
            SelectedItem = preferences.RecordingInputDevice ?? DefaultDeviceLabel,
            MinWidth = 320,
            IsEnabled = deviceChoices.Count > 1,
        };

        inputLatencyNumericUpDown = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 500,
            Increment = 1,
            FormatString = "0",
            Value = Math.Clamp(preferences.RecordingInputLatencyMilliseconds, 0, 500),
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    public Control Compose()
    {
        var formGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowSpacing = 10, ColumnSpacing = 8 };
        AddRow(formGrid, 0, "Recording tool", substrateComboBox,
            OperatingSystem.IsWindows() ? "On Windows, waveIn is always used." : OperatingSystem.IsMacOS() ? "On macOS, Core Audio is always used, with nothing to install." : "auto tries pw-record, then parec, then arecord.");
        AddRow(formGrid, 1, "Input device", deviceComboBox, OperatingSystem.IsMacOS()
            ? "The default input in System Settings → Sound."
            : OperatingSystem.IsWindows()
                ? "Where vocals are captured from. If the device is unplugged, slaw refuses the take rather than recording some other microphone."
                : "Where vocals are captured from. arecord always records the ALSA default.");
        var inputTestHost = new StackPanel { Spacing = 4, Children = { StudioUi.Row(10, testInputButton, inputTestMeter), inputTestResultTextBlock } };
        AddRow(formGrid, 2, string.Empty, inputTestHost, "Listens for a few seconds and keeps nothing. Say something; it is the most feedback this take will get.");
        AddRow(formGrid, 3, "Input latency (ms)", inputLatencyNumericUpDown,
            "Trimmed from the head of each take. If takes land late, raise it; early, lower it. Output latency is already compensated.");
        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { StudioUi.Caption("Recording", 13, StudioTheme.Text, FontWeight.SemiBold), formGrid },
        };
    }

    public void ApplyTo(StudioPreferences preferences)
    {
        preferences.RecordingSubstrate = substrateComboBox.SelectedItem as string ?? "auto";
        preferences.RecordingInputDevice = deviceComboBox.SelectedItem is string selectedDevice && selectedDevice != DefaultDeviceLabel ? selectedDevice : null;
        preferences.RecordingInputLatencyMilliseconds = inputLatencyNumericUpDown.Value is { } latencyValue
            ? (int)Math.Clamp(Math.Round(latencyValue), 0, 500)
            : preferences.RecordingInputLatencyMilliseconds;
    }

    /// <summary>Stops listening. Also called when the window closes, so a test never outlives its dialog.</summary>
    public void StopInputTest()
    {
        inputTestTimer?.Stop();
        inputTestTimer = null;
        inputUnderTest?.Dispose();
        inputUnderTest = null;
        inputTestMeter.Value = 0;
        testInputButton.Content = "Test input";
    }

    private void ToggleInputTest()
    {
        if (inputUnderTest is not null)
        {
            StopInputTest();
            inputTestResultTextBlock.Text = string.Empty;
            return;
        }
        var selectedDevice = deviceComboBox.SelectedItem is string chosenDevice && chosenDevice != DefaultDeviceLabel ? chosenDevice : null;
        var inputSource = AudioInputFactory.TryCreate(LiveAudioSession.LiveSampleRate, substrateComboBox.SelectedItem as string ?? "auto", selectedDevice, out var unavailabilityReason);
        if (inputSource is null)
        {
            ReportInputTest(StudioTheme.Failure, $"✗ {unavailabilityReason}");
            return;
        }
        try
        {
            inputSource.Start();
        }
        catch (InvalidOperationException inputTestRemediationOpportunity)
        {
            inputSource.Dispose();
            ReportInputTest(StudioTheme.Failure, $"✗ {inputTestRemediationOpportunity.Message}");
            return;
        }
        inputSource.Faulted += faultDescription => Dispatcher.UIThread.Post(() =>
        {
            if (inputUnderTest == inputSource)
            {
                StopInputTest();
                ReportInputTest(StudioTheme.Failure, $"✗ {faultDescription}");
            }
        });
        inputUnderTest = inputSource;
        inputTestLoudestPeak = 0;
        inputTestDeadline = DateTime.UtcNow + InputTestDuration;
        testInputButton.Content = "Stop";
        ReportInputTest(StudioTheme.TextMuted, $"listening via {inputSource.SubstrateName}…");
        inputTestTimer = new DispatcherTimer(InputTestMeterInterval, DispatcherPriority.Normal, (_, _) => AdvanceInputTest());
        inputTestTimer.Start();
    }

    private void AdvanceInputTest()
    {
        if (inputUnderTest is not { } inputSource)
        {
            return;
        }
        var peak = inputSource.PeakLevel;
        inputTestLoudestPeak = Math.Max(inputTestLoudestPeak, peak);
        inputTestMeter.Value = peak;
        if (DateTime.UtcNow < inputTestDeadline)
        {
            return;
        }
        var loudestPeak = inputTestLoudestPeak;
        var substrateName = inputSource.SubstrateName;
        StopInputTest();
        if (loudestPeak < InputTestSilencePeak)
        {
            ReportInputTest(StudioTheme.Failure, $"✗ {substrateName} delivered silence. Check the device, its mute switch and the microphone privacy settings.");
            return;
        }
        ReportInputTest(StudioTheme.Playhead, FormattableString.Invariant($"✓ Loudest peak {20 * Math.Log10(loudestPeak):0.0} dBFS via {substrateName}."));
    }

    private void ReportInputTest(IBrush foreground, string message)
    {
        inputTestResultTextBlock.Foreground = foreground;
        inputTestResultTextBlock.Text = message;
    }

    private static void AddRow(Grid formGrid, int rowIndex, string label, Control field, string caption)
    {
        formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var labelTextBlock = StudioUi.Caption(label, 12, StudioTheme.Text);
        labelTextBlock.VerticalAlignment = VerticalAlignment.Center;
        var captionTextBlock = StudioUi.Caption(caption);
        captionTextBlock.TextWrapping = TextWrapping.Wrap;
        captionTextBlock.TextTrimming = TextTrimming.None;
        var fieldHost = new StackPanel { Spacing = 2, Children = { field, captionTextBlock } };
        Grid.SetRow(labelTextBlock, rowIndex);
        Grid.SetRow(fieldHost, rowIndex);
        Grid.SetColumn(fieldHost, 1);
        formGrid.Children.Add(labelTextBlock);
        formGrid.Children.Add(fieldHost);
    }
}
