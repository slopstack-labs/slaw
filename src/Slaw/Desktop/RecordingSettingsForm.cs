using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Playback;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>The Recording block of Settings → Audio: which tool listens, to which input, with how much latency.</summary>
internal sealed class RecordingSettingsForm
{
    private const string DefaultDeviceLabel = "system default";

    private readonly ComboBox substrateComboBox;
    private readonly ComboBox deviceComboBox;
    private readonly NumericUpDown inputLatencyNumericUpDown;

    public RecordingSettingsForm(StudioPreferences preferences)
    {
        var substrateChoices = new[] { "auto" }.Concat(PipedProcessAudioInput.SupportedSubstrateNames).ToArray();
        substrateComboBox = new ComboBox
        {
            ItemsSource = substrateChoices,
            SelectedItem = substrateChoices.Contains(preferences.RecordingSubstrate) ? preferences.RecordingSubstrate : "auto",
            MinWidth = 160,
            IsEnabled = !OperatingSystem.IsWindows(),
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
            OperatingSystem.IsWindows() ? "On Windows, waveIn is always used." : "auto tries pw-record, then parec, then arecord.");
        AddRow(formGrid, 1, "Input device", deviceComboBox, "Where vocals are captured from. arecord always records the ALSA default.");
        AddRow(formGrid, 2, "Input latency (ms)", inputLatencyNumericUpDown,
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
