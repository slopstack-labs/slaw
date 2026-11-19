using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SlopstackLabs.Slaw.Updates;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The update notice: one quiet check of GitHub's releases when the studio opens, and a
/// button in the toolbar when a newer slaw is out. It opens the release page and installs
/// nothing; whether to upgrade stays a human decision, like very little else here.
/// </summary>
internal sealed partial class StudioWindow
{
    /// <summary>Hidden until a check finds a newer release.</summary>
    private readonly Button updateChip = new()
    {
        IsVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Avalonia.Thickness(10, 5),
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
    };

    private UpdateNotificationService? updateNotificationService;
    private ReleaseAnnouncement? availableUpdate;
    private Task<UpdateCheckOutcome?> updateCheckInFlight = Task.FromResult<UpdateCheckOutcome?>(null);

    /// <summary>Where the last answer is remembered, so a studio started ten times a day asks GitHub once.</summary>
    private static string UpdateCheckMemoryPath =>
        Path.Combine(Path.GetDirectoryName(StudioPreferences.PreferencesFilePath)!, "update-check.json");

    private Control ComposeUpdateChip()
    {
        updateChip.Classes.Add("accent");
        updateChip.Click += (_, _) => _ = OpenAvailableReleasePageAsync();
        return updateChip;
    }

    /// <summary>Shows the toolbar button for <paramref name="update"/>, or hides it for null.</summary>
    internal void ShowAvailableUpdate(ReleaseAnnouncement? update)
    {
        availableUpdate = update;
        updateChip.IsVisible = update is not null;
        if (update is null)
        {
            return;
        }
        StudioUi.SetIconContent(updateChip, StudioIcon.Import, $"{update.Tag} available", 13);
        ToolTip.SetTip(updateChip,
            $"slaw {update.Tag} has been released; you run v{RunningBuild.DisplayVersion}. Click to open the release page in your browser. Nothing is downloaded or installed.");
    }

    /// <summary>At startup and after Settings: checks if the preference allows it (a remembered answer costs no request), and takes the button away if not.</summary>
    private void ApplyUpdateCheckPreference()
    {
        if (!preferences.UpdateCheckEnabled)
        {
            ShowAvailableUpdate(null);
            return;
        }
        _ = CheckForUpdatesAsync(askedByUser: false);
    }

    /// <summary>
    /// Checks for a newer release without blocking the studio. The automatic check says nothing
    /// unless there is an update, and then only through the toolbar button; one the user asked
    /// for skips the once-a-day schedule and reports to the status bar whatever it finds.
    /// Null when the studio closed first.
    /// </summary>
    internal Task<UpdateCheckOutcome?> CheckForUpdatesAsync(bool askedByUser)
    {
        var earlierCheck = updateCheckInFlight;
        updateCheckInFlight = RunCheckAsync();
        return updateCheckInFlight;

        async Task<UpdateCheckOutcome?> RunCheckAsync()
        {
            await earlierCheck;
            if (askedByUser)
            {
                SetStatus(StatusSeverity.Informational, "asking GitHub whether the roadmap has moved…");
            }
            UpdateCheckOutcome outcome;
            try
            {
                var notificationService = updateNotificationService ??= new UpdateNotificationService(
                    new GitHubReleaseFeedSubstrate(), RunningBuild.Version, UpdateCheckMemoryPath, () => DateTimeOffset.UtcNow);
                // Off the UI thread: the remembered answer is a file, and the transport resolves names before it goes async.
                outcome = await Task.Run(() => notificationService.CheckAsync(ignoreSchedule: askedByUser, sessionLifetimeCancellation.Token));
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            if (askedByUser || preferences.UpdateCheckEnabled)
            {
                ShowAvailableUpdate(outcome.AvailableUpdate);
            }
            if (askedByUser)
            {
                SetStatus(outcome.FailureDescription is null ? StatusSeverity.Success : StatusSeverity.Warning,
                    UpdateCheckOutcome.Describe(outcome, RunningBuild.Version));
            }
            return outcome;
        }
    }

    private async Task OpenAvailableReleasePageAsync()
    {
        if (availableUpdate is not { } update)
        {
            return;
        }
        if (await Launcher.LaunchUriAsync(new Uri(update.PageUrl)))
        {
            SetStatus(StatusSeverity.Informational, $"opened the {update.Tag} release page in your browser. installing it remains a human-in-the-loop workflow.");
        }
        else
        {
            SetStatus(StatusSeverity.Warning, $"could not open a browser. the release page is {update.PageUrl}");
        }
    }

    private void SetUpdateCheckEnabled(bool updateCheckEnabled)
    {
        preferences.UpdateCheckEnabled = updateCheckEnabled;
        ApplyUpdateCheckPreference();
        try
        {
            preferences.SaveUpdateCheck();
        }
        catch (Exception persistenceRemediationOpportunity) when (persistenceRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Warning, $"the update check setting could not be saved: {persistenceRemediationOpportunity.Message}");
            return;
        }
        SetStatus(StatusSeverity.Informational, updateCheckEnabled
            ? "slaw now checks for updates at startup, at most once a day."
            : "slaw no longer checks for updates at startup. Help → Check for Updates still asks when you do.");
    }

    private IEnumerable<CommandPaletteEntry> ComposeUpdatePaletteEntries()
    {
        yield return new CommandPaletteEntry("Help: Check for Updates", null, () => _ = CheckForUpdatesAsync(askedByUser: true));
        if (availableUpdate is { } update)
        {
            yield return new CommandPaletteEntry($"Help: Open the {update.Tag} Release Page", null, () => _ = OpenAvailableReleasePageAsync());
        }
        yield return new CommandPaletteEntry(preferences.UpdateCheckEnabled
            ? "Help: Turn Off the Update Check at Startup"
            : "Help: Turn On the Update Check at Startup", null, () => SetUpdateCheckEnabled(!preferences.UpdateCheckEnabled));
    }
}
