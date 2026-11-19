using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Updates;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>The About tab's Updates section: the startup check's switch, and a button that asks now.</summary>
internal sealed partial class SettingsWindow
{
    /// <summary>Runs the studio's own check, so its toolbar button hears the answer too; null when there is no studio to ask.</summary>
    private readonly Func<Task<UpdateCheckOutcome?>>? checkForUpdates;

    private CheckBox? updateCheckCheckBox;
    private Button? checkForUpdatesButton;
    private Button? openReleasePageButton;
    private ReleaseAnnouncement? foundUpdate;
    private readonly TextBlock updateCheckResultTextBlock = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 440,
        FontSize = 12,
    };

    private Control BuildUpdatesSection()
    {
        updateCheckCheckBox = new CheckBox
        {
            Content = "Check for updates at startup",
            IsChecked = workingPreferences.UpdateCheckEnabled,
        };
        var explanation = StudioUi.Caption(
            "Asks GitHub for slaw's list of releases, at most once a day, and puts a button in the toolbar when a newer one is out. " +
            "Nothing about you or your project is sent, and nothing is downloaded or installed. " +
            "This is separate from the AI switch: it runs with AI features off too.");
        explanation.TextWrapping = TextWrapping.Wrap;
        explanation.TextTrimming = TextTrimming.None;

        checkForUpdatesButton = StudioUi.Button("Check now", () => _ = RunUpdateCheckAsync());
        checkForUpdatesButton.IsEnabled = checkForUpdates is not null;
        openReleasePageButton = StudioUi.AccentButton("Open release page", () => _ = OpenFoundReleasePageAsync());
        openReleasePageButton.IsVisible = false;
        var checkRow = StudioUi.Row(10, checkForUpdatesButton, openReleasePageButton, updateCheckResultTextBlock);
        checkRow.Margin = new Thickness(0, 4, 0, 0);

        return new StackPanel
        {
            Spacing = 8,
            Children = { StudioUi.SectionHeading("Updates"), updateCheckCheckBox, explanation, checkRow },
        };
    }

    private async Task RunUpdateCheckAsync()
    {
        if (checkForUpdates is null || checkForUpdatesButton is null || openReleasePageButton is null)
        {
            return;
        }
        checkForUpdatesButton.IsEnabled = false;
        updateCheckResultTextBlock.Foreground = StudioTheme.TextMuted;
        updateCheckResultTextBlock.Text = "asking GitHub…";
        try
        {
            if (await checkForUpdates() is not { } outcome)
            {
                updateCheckResultTextBlock.Text = string.Empty;
                return;
            }
            foundUpdate = outcome.AvailableUpdate;
            openReleasePageButton.IsVisible = foundUpdate is not null;
            updateCheckResultTextBlock.Foreground = outcome.FailureDescription is null ? StudioTheme.Playhead : StudioTheme.Failure;
            updateCheckResultTextBlock.Text = $"{(outcome.FailureDescription is null ? "✓" : "✗")} {UpdateCheckOutcome.Describe(outcome, RunningBuild.Version)}";
        }
        finally
        {
            checkForUpdatesButton.IsEnabled = true;
        }
    }

    private async Task OpenFoundReleasePageAsync()
    {
        if (foundUpdate is { } update && !await Launcher.LaunchUriAsync(new Uri(update.PageUrl)))
        {
            updateCheckResultTextBlock.Foreground = StudioTheme.Failure;
            updateCheckResultTextBlock.Text = $"✗ could not open a browser. the release page is {update.PageUrl}";
        }
    }
}
