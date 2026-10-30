using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

internal enum BounceScope
{
    Arrangement,
    Scene,
    Stems,
}

/// <summary>Everything a "Bounce As…" needs to know.</summary>
internal sealed record BounceRequest(BounceScope Scope, int SceneIndex, int LoopCount, int SampleRate, int BitDepth, string OutputPath);

/// <summary>
/// The Bounce As… dialog: what to render (arrangement, one scene looped, or
/// stems), at which rate and depth, and where. The deliverable is radio-ready
/// either way; this merely chooses which radio.
/// </summary>
internal sealed class BounceOptionsWindow : Window
{
    private static readonly int[] SampleRateChoices = { 44100, 48000, 96000 };

    private readonly SlawProject project;
    private readonly RadioButton arrangementOption;
    private readonly RadioButton sceneOption;
    private readonly RadioButton stemsOption;
    private readonly ComboBox sceneSelector;
    private readonly NumericUpDown loopCountInput;
    private readonly ComboBox sampleRateSelector;
    private readonly ComboBox bitDepthSelector;
    private readonly TextBox outputPathInput;
    private readonly TextBlock destinationHint;

    public BounceOptionsWindow(SlawProject project, string suggestedOutputPath, BounceRequest? previousRequest)
    {
        this.project = project;
        Title = "slaw — Bounce";
        Width = 560;
        Height = 510;
        CanResize = false;
        Background = StudioTheme.Background;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var arrangementSteps = FreeTimeline.LengthInSteps(project);
        var arrangementSeconds = arrangementSteps * project.StepDurationSeconds;
        arrangementOption = new RadioButton
        {
            GroupName = "bounceScope",
            Content = FormattableString.Invariant(
                $"Full arrangement  ({arrangementSteps / 16.0:0.#} bars · {(int)arrangementSeconds / 60}:{(int)arrangementSeconds % 60:00})"),
        };
        sceneOption = new RadioButton { GroupName = "bounceScope", Content = "One scene, looped" };
        stemsOption = new RadioButton { GroupName = "bounceScope", Content = "Stems: one WAV per track (full arrangement)" };
        sceneSelector = new ComboBox
        {
            ItemsSource = project.Scenes.Select((sceneName, sceneIndex) => $"{sceneIndex}: {sceneName}").ToList(),
            SelectedIndex = Math.Clamp(previousRequest?.SceneIndex ?? 0, 0, Math.Max(0, project.Scenes.Count - 1)),
            Width = 200,
        };
        loopCountInput = new NumericUpDown { Minimum = 1, Maximum = 64, Value = previousRequest?.LoopCount ?? 4, Increment = 1, FormatString = "0", Width = 110 };

        sampleRateSelector = new ComboBox
        {
            ItemsSource = SampleRateChoices.Select(sampleRate => FormattableString.Invariant($"{sampleRate / 1000.0:0.#} kHz")).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(SampleRateChoices, previousRequest?.SampleRate ?? 44100)),
            Width = 120,
        };
        bitDepthSelector = new ComboBox
        {
            ItemsSource = new[] { "16-bit", "24-bit" },
            SelectedIndex = previousRequest?.BitDepth == 24 ? 1 : 0,
            Width = 120,
        };

        outputPathInput = new TextBox { Text = previousRequest?.OutputPath ?? suggestedOutputPath };
        destinationHint = new TextBlock { FontSize = 11, Foreground = StudioTheme.TextMuted, TextWrapping = TextWrapping.Wrap };

        (previousRequest?.Scope switch
        {
            BounceScope.Scene => sceneOption,
            BounceScope.Stems => stemsOption,
            _ => arrangementOption,
        }).IsChecked = true;
        foreach (var scopeOption in new[] { arrangementOption, sceneOption, stemsOption })
        {
            scopeOption.IsCheckedChanged += (_, _) => RefreshDependentControls();
        }

        var cancelButton = StudioUi.Button("Cancel", Close);
        cancelButton.IsCancel = true;
        var bounceButton = StudioUi.AccentButton("Bounce", CommitAndClose);
        bounceButton.IsDefault = true;
        outputPathInput.TextChanged += (_, _) => RefreshDependentControls();
        var buttonRow = StudioUi.Row(8, cancelButton, bounceButton);
        buttonRow.HorizontalAlignment = HorizontalAlignment.Right;

        Content = new Border
        {
            Padding = new Thickness(18),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    StudioUi.SectionHeading("What"),
                    arrangementOption,
                    sceneOption,
                    StudioUi.Row(8, new Border { Width = 24 }, sceneSelector, StudioUi.Caption("×", 13), loopCountInput, StudioUi.Caption("loops", 12)),
                    stemsOption,
                    StudioUi.SectionHeading("Format"),
                    StudioUi.Row(8, sampleRateSelector, bitDepthSelector, StudioUi.Caption("stereo PCM WAV", 12)),
                    StudioUi.SectionHeading("Where"),
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        ColumnSpacing = 8,
                        Children =
                        {
                            outputPathInput,
                            PlaceInColumn(StudioUi.Button("Browse…", () => _ = BrowseAsync()), 1),
                        },
                    },
                    destinationHint,
                    buttonRow,
                },
            },
        };
        RefreshDependentControls();
    }

    /// <summary>Where the bounce will land, in full, and what it does to files already there.</summary>
    private string DescribeDestination()
    {
        var requestedPath = outputPathInput.Text?.Trim();
        if (string.IsNullOrEmpty(requestedPath))
        {
            return "Choose where the WAV goes.";
        }
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? requestedPath : requestedPath + ".wav");
        }
        catch (Exception pathRemediationOpportunity) when (pathRemediationOpportunity is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"⚠ not a usable path: {pathRemediationOpportunity.Message}";
        }
        var destinationFolder = Path.GetDirectoryName(fullPath) ?? fullPath;
        if (SelectedScope == BounceScope.Stems)
        {
            return $"→ {destinationFolder}{Path.DirectorySeparatorChar}{Path.GetFileNameWithoutExtension(fullPath)} - <track>.wav\n" +
                   $"One file per track that plays anything ({project.Tracks.Count} tracks); existing stems with the same names are replaced.";
        }
        var existingFile = new FileInfo(fullPath);
        var fileFate = existingFile.Exists
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Replaces the existing file ({existingFile.Length / 1048576.0:0.0} MB, {existingFile.LastWriteTime:yyyy-MM-dd HH:mm}).")
            : Directory.Exists(destinationFolder) ? "New file." : "New file; the folder will be created.";
        return $"→ {fullPath}\n{fileFate} Effects and the master bus included; the hearing protection substrate is non-negotiable.";
    }

    /// <summary>The request, set when the user presses Bounce; null if they cancelled.</summary>
    public BounceRequest? Result { get; private set; }

    private BounceScope SelectedScope =>
        sceneOption.IsChecked == true ? BounceScope.Scene
        : stemsOption.IsChecked == true ? BounceScope.Stems
        : BounceScope.Arrangement;

    private static Control PlaceInColumn(Control placedControl, int columnIndex)
    {
        Grid.SetColumn(placedControl, columnIndex);
        return placedControl;
    }

    private void RefreshDependentControls()
    {
        var sceneScope = SelectedScope == BounceScope.Scene;
        sceneSelector.IsEnabled = sceneScope;
        loopCountInput.IsEnabled = sceneScope;
        destinationHint.Text = DescribeDestination();
    }

    private async Task BrowseAsync()
    {
        var chosenFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Bounce to",
            SuggestedFileName = Path.GetFileName(outputPathInput.Text) is { Length: > 0 } currentName ? currentName : "bounce.wav",
            DefaultExtension = "wav",
            FileTypeChoices = new[] { new FilePickerFileType("WAV audio") { Patterns = new[] { "*.wav" } } },
        });
        if (chosenFile?.TryGetLocalPath() is { } chosenPath)
        {
            outputPathInput.Text = chosenPath;
            RefreshDependentControls();
        }
    }

    private void CommitAndClose()
    {
        var outputPath = outputPathInput.Text?.Trim();
        if (string.IsNullOrEmpty(outputPath))
        {
            destinationHint.Text = "Choose where the deliverable should be shipped.";
            destinationHint.Foreground = StudioTheme.Failure;
            return;
        }
        if (!outputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            outputPath += ".wav";
        }
        Result = new BounceRequest(
            SelectedScope,
            Math.Max(0, sceneSelector.SelectedIndex),
            (int)(loopCountInput.Value ?? 4),
            SampleRateChoices[Math.Max(0, sampleRateSelector.SelectedIndex)],
            bitDepthSelector.SelectedIndex == 1 ? 24 : 16,
            Path.GetFullPath(outputPath));
        Close();
    }
}
