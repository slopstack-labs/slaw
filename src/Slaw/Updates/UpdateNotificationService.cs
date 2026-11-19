using System.Text.Json;

namespace SlopstackLabs.Slaw.Updates;

/// <summary>
/// What a check found. <see cref="AvailableUpdate"/> is the newest release this build
/// should hear about (still set after a failed check if an earlier one found it),
/// <see cref="FailureDescription"/> is why the feed could not be read, and
/// <see cref="CheckedAt"/> is when the feed last answered.
/// </summary>
internal sealed record UpdateCheckOutcome(ReleaseAnnouncement? AvailableUpdate, string? FailureDescription, DateTimeOffset? CheckedAt)
{
    /// <summary>One sentence for the status bar and Settings: the fact first, the commentary after.</summary>
    public static string Describe(UpdateCheckOutcome outcome, SemanticVersion runningVersion)
    {
        if (outcome.FailureDescription is { } failureDescription)
        {
            return outcome.AvailableUpdate is { } knownUpdate
                ? $"could not check for updates: {failureDescription}. the last check found {knownUpdate.Tag}."
                : $"could not check for updates: {failureDescription}";
        }
        return outcome.AvailableUpdate is { } availableUpdate
            ? $"slaw {availableUpdate.Tag} is available (you run v{runningVersion}). the roadmap has shipped without you."
            : $"slaw v{runningVersion} is up to date. you are, for once, fully aligned with the roadmap.";
    }
}

/// <summary>
/// Finds out whether a newer slaw has been released, and remembers the answer in a small
/// file so the studio asks GitHub at most once a day however often it is started. Never
/// throws for a failed check: an update notice is not worth an error dialog.
/// </summary>
internal sealed class UpdateNotificationService
{
    public static readonly TimeSpan IntervalBetweenAutomaticChecks = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions RememberedCheckSerializationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly IReleaseFeedSubstrate releaseFeed;
    private readonly SemanticVersion runningVersion;
    private readonly string rememberedCheckPath;
    private readonly Func<DateTimeOffset> currentTime;

    /// <param name="rememberedCheckPath">The file the last answer is kept in (update-check.json beside settings.json).</param>
    public UpdateNotificationService(IReleaseFeedSubstrate releaseFeed, SemanticVersion runningVersion, string rememberedCheckPath, Func<DateTimeOffset> currentTime)
    {
        this.releaseFeed = releaseFeed;
        this.runningVersion = runningVersion;
        this.rememberedCheckPath = rememberedCheckPath;
        this.currentTime = currentTime;
    }

    /// <summary>
    /// Checks for a newer release. Within a day of the last answer the remembered one is
    /// returned without touching the network, unless <paramref name="ignoreSchedule"/> is
    /// set (the user asked). Only cancellation through <paramref name="cancellationToken"/> throws.
    /// </summary>
    public async Task<UpdateCheckOutcome> CheckAsync(bool ignoreSchedule, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rememberedCheck = LoadRememberedCheck();
        if (!ignoreSchedule && rememberedCheck is not null && currentTime() - rememberedCheck.CheckedAtUtc < IntervalBetweenAutomaticChecks)
        {
            return new UpdateCheckOutcome(StillNews(rememberedCheck), null, rememberedCheck.CheckedAtUtc);
        }

        ReleaseAnnouncement? availableUpdate;
        try
        {
            var listingJson = await releaseFeed.FetchReleaseListingAsync(cancellationToken).ConfigureAwait(false);
            availableUpdate = ReleaseListing.SelectUpdate(runningVersion, ReleaseListing.Parse(listingJson));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception feedRemediationOpportunity) when (feedRemediationOpportunity is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            // An HttpClient timeout arrives as a cancellation nobody asked for.
            var failureDescription = feedRemediationOpportunity is OperationCanceledException
                ? "GitHub did not answer in time"
                : feedRemediationOpportunity is JsonException
                    ? $"the answer could not be read ({feedRemediationOpportunity.Message})"
                    : feedRemediationOpportunity.Message;
            return new UpdateCheckOutcome(rememberedCheck is null ? null : StillNews(rememberedCheck), failureDescription, rememberedCheck?.CheckedAtUtc);
        }

        var checkedAt = currentTime();
        RememberCheck(new RememberedCheck
        {
            CheckedAtUtc = checkedAt,
            LatestTag = availableUpdate?.Tag,
            LatestIsPrerelease = availableUpdate?.IsPrerelease ?? false,
        });
        return new UpdateCheckOutcome(availableUpdate, null, checkedAt);
    }

    /// <summary>The remembered release, if it is still newer than this build (it is not, the day after installing it).</summary>
    private ReleaseAnnouncement? StillNews(RememberedCheck rememberedCheck) =>
        rememberedCheck.LatestTag is { } latestTag && SemanticVersion.TryParse(latestTag) is { } latestVersion
            ? ReleaseListing.SelectUpdate(runningVersion, new[] { new ReleaseAnnouncement(latestTag, latestVersion, rememberedCheck.LatestIsPrerelease || latestVersion.IsPrerelease) })
            : null;

    private RememberedCheck? LoadRememberedCheck()
    {
        try
        {
            if (!File.Exists(rememberedCheckPath))
            {
                return null;
            }
            var rememberedCheck = JsonSerializer.Deserialize<RememberedCheck>(File.ReadAllText(rememberedCheckPath), RememberedCheckSerializationOptions);
            // A time in the future (a clock that was wrong once) or a tag that is not a version: ask again.
            return rememberedCheck is null ||
                   rememberedCheck.CheckedAtUtc > currentTime() ||
                   (rememberedCheck.LatestTag is not null && SemanticVersion.TryParse(rememberedCheck.LatestTag) is null)
                ? null
                : rememberedCheck;
        }
        catch (Exception rememberedCheckRemediationOpportunity) when (rememberedCheckRemediationOpportunity is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RememberCheck(RememberedCheck rememberedCheck)
    {
        try
        {
            if (Path.GetDirectoryName(rememberedCheckPath) is { Length: > 0 } rememberedCheckFolder)
            {
                Directory.CreateDirectory(rememberedCheckFolder);
            }
            File.WriteAllText(rememberedCheckPath, JsonSerializer.Serialize(rememberedCheck, RememberedCheckSerializationOptions));
        }
        catch (Exception rememberedCheckRemediationOpportunity) when (rememberedCheckRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            // Not remembered: the next start asks again, which costs one request.
        }
    }

    private sealed class RememberedCheck
    {
        public DateTimeOffset CheckedAtUtc { get; set; }
        public string? LatestTag { get; set; }
        public bool LatestIsPrerelease { get; set; }
    }
}
