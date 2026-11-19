using System.Text.Json;
using SlopstackLabs.Slaw.Updates;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>The update check: reading release tags, deciding which release is news to which build, and not asking GitHub more than once a day.</summary>
public class UpdateCheckTests : IDisposable
{
    private readonly string scratchFolder = Path.Combine(Path.GetTempPath(), $"slaw-update-check-{Guid.NewGuid():N}");

    public UpdateCheckTests() => Directory.CreateDirectory(scratchFolder);

    public void Dispose() => Directory.Delete(scratchFolder, recursive: true);

    private string RememberedCheckPath => Path.Combine(scratchFolder, "update-check.json");

    private static SemanticVersion Version(string versionText) =>
        SemanticVersion.TryParse(versionText) ?? throw new InvalidOperationException($"'{versionText}' should parse");

    private static string Listing(params (string Tag, bool Prerelease, bool Draft)[] releases) =>
        JsonSerializer.Serialize(releases.Select(release => new Dictionary<string, object>
        {
            ["tag_name"] = release.Tag,
            ["prerelease"] = release.Prerelease,
            ["draft"] = release.Draft,
            ["html_url"] = "https://example.invalid/not-the-page-slaw-opens",
        }));

    /// <summary>A release feed that answers from memory and counts how often it was asked.</summary>
    private sealed class ScriptedReleaseFeed : IReleaseFeedSubstrate
    {
        public string ListingJson { get; set; } = "[]";
        public Exception? Failure { get; set; }
        public int RequestCount { get; private set; }

        public Task<string> FetchReleaseListingAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            return Failure is null ? Task.FromResult(ListingJson) : Task.FromException<string>(Failure);
        }
    }

    private UpdateNotificationService Service(ScriptedReleaseFeed feed, string runningVersion, Func<DateTimeOffset> clock) =>
        new(feed, Version(runningVersion), RememberedCheckPath, clock);

    // ── Versions ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("v0.2.0-beta.7", 0, 2, 0, "beta.7")]
    [InlineData("0.2.0", 0, 2, 0, "")]
    [InlineData("V1.12.3", 1, 12, 3, "")]
    [InlineData("0.2.0-beta.8+2b8452f0c1", 0, 2, 0, "beta.8")]
    [InlineData("0.1.0+79b5246213ef", 0, 1, 0, "")]
    [InlineData(" v0.1.0-synergy ", 0, 1, 0, "synergy")]
    public void A_release_tag_or_an_assembly_version_parses(string versionText, int major, int minor, int patch, string prereleaseLabel)
    {
        var parsedVersion = SemanticVersion.TryParse(versionText);

        Assert.Equal(new SemanticVersion(major, minor, patch, prereleaseLabel), parsedVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nightly")]
    [InlineData("v1")]
    [InlineData("v1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-beta!")]
    public void Anything_that_is_not_a_version_is_refused(string? versionText) =>
        Assert.Null(SemanticVersion.TryParse(versionText));

    [Fact]
    public void Versions_sort_the_way_releases_are_made()
    {
        var oldestToNewest = new[]
        {
            "0.1.9", "0.2.0-alpha", "0.2.0-alpha.1", "0.2.0-alpha.beta", "0.2.0-beta", "0.2.0-beta.2", "0.2.0-beta.7",
            "0.2.0-beta.10", "0.2.0-rc.1", "0.2.0", "0.2.1", "0.10.0", "1.0.0",
        }.Select(Version).ToList();

        for (var olderIndex = 0; olderIndex < oldestToNewest.Count; olderIndex++)
        {
            for (var newerIndex = olderIndex + 1; newerIndex < oldestToNewest.Count; newerIndex++)
            {
                Assert.True(oldestToNewest[newerIndex].IsNewerThan(oldestToNewest[olderIndex]), $"{oldestToNewest[newerIndex]} should be newer than {oldestToNewest[olderIndex]}");
                Assert.False(oldestToNewest[olderIndex].IsNewerThan(oldestToNewest[newerIndex]), $"{oldestToNewest[olderIndex]} should not be newer than {oldestToNewest[newerIndex]}");
            }
        }
    }

    [Fact]
    public void The_commit_a_build_was_made_from_does_not_make_it_newer()
    {
        Assert.False(Version("0.2.0-beta.8+aaaa").IsNewerThan(Version("0.2.0-beta.8+bbbb")));
        Assert.Equal(Version("v0.2.0-beta.8"), Version("0.2.0-beta.8+2b8452f"));
    }

    [Fact]
    public void A_version_prints_as_its_tag_without_the_v()
    {
        Assert.Equal("0.2.0-beta.8", Version("v0.2.0-beta.8+2b8452f").ToString());
        Assert.Equal("1.4.0", Version("v1.4.0").ToString());
    }

    [Fact]
    public void The_running_build_knows_its_own_version()
    {
        Assert.NotNull(SemanticVersion.TryParse(RunningBuild.InformationalVersion));
        Assert.Equal(RunningBuild.Version.ToString(), RunningBuild.DisplayVersion);
    }

    // ── The release listing ───────────────────────────────────────────────────

    [Fact]
    public void The_listing_keeps_published_releases_with_version_tags()
    {
        var releases = ReleaseListing.Parse(Listing(
            ("v0.2.0-beta.9", true, false),
            ("v0.3.0", false, true),
            ("nightly", false, false),
            ("v0.2.0", false, false)));

        Assert.Equal(new[] { "v0.2.0-beta.9", "v0.2.0" }, releases.Select(release => release.Tag));
        Assert.True(releases[0].IsPrerelease);
        Assert.False(releases[1].IsPrerelease);
    }

    [Fact]
    public void A_prerelease_label_in_the_tag_counts_even_when_GitHub_was_not_told()
    {
        var releases = ReleaseListing.Parse(Listing(("v0.1.0-synergy", false, false)));

        Assert.True(Assert.Single(releases).IsPrerelease);
    }

    [Theory]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")]
    [InlineData("not json")]
    public void A_listing_that_is_not_a_list_of_releases_is_an_error(string listingJson) =>
        Assert.ThrowsAny<JsonException>(() => ReleaseListing.Parse(listingJson));

    [Fact]
    public void The_release_page_is_built_from_the_tag_and_never_taken_from_the_feed()
    {
        var release = Assert.Single(ReleaseListing.Parse(Listing(("v0.2.0-beta.9", true, false))));

        Assert.Equal("https://github.com/slopstack-labs/slaw/releases/tag/v0.2.0-beta.9", release.PageUrl);
        Assert.Equal("https://github.com/slopstack-labs/slaw/releases/tag/v1.0.0%2Fx", ReleaseListing.ReleasePageUrl("v1.0.0/x"));
    }

    // ── Which release is news ─────────────────────────────────────────────────

    [Fact]
    public void A_beta_build_hears_about_the_newest_beta_wherever_it_is_in_the_listing()
    {
        var releases = ReleaseListing.Parse(Listing(
            ("v0.2.0-beta.7", true, false),
            ("v0.2.0-beta.10", true, false),
            ("v0.2.0-beta.9", true, false),
            ("v0.2.0-beta.8", true, false)));

        Assert.Equal("v0.2.0-beta.10", ReleaseListing.SelectUpdate(Version("0.2.0-beta.8"), releases)?.Tag);
    }

    [Theory]
    [InlineData("0.1.0", "v0.2.0")]               // a stable build hears about the stable release, not the beta after it
    [InlineData("0.2.0-beta.9", "v0.3.0-beta.1")] // a beta build hears about whatever is newest
    public void The_newest_release_that_suits_the_running_build_is_announced(string runningVersion, string expectedTag)
    {
        var releases = ReleaseListing.Parse(Listing(
            ("v0.3.0-beta.1", true, false),
            ("v0.2.0", false, false),
            ("v0.2.0-beta.9", true, false)));

        Assert.Equal(expectedTag, ReleaseListing.SelectUpdate(Version(runningVersion), releases)?.Tag);
    }

    [Fact]
    public void A_beta_build_is_told_about_the_release_it_was_a_beta_of()
    {
        var releases = ReleaseListing.Parse(Listing(("v0.2.0", false, false), ("v0.2.0-beta.9", true, false)));

        Assert.Equal("v0.2.0", ReleaseListing.SelectUpdate(Version("0.2.0-beta.9"), releases)?.Tag);
    }

    [Theory]
    [InlineData("0.2.0")]         // up to date
    [InlineData("0.3.0-beta.1")]  // ahead of everything published
    public void Nothing_is_announced_to_a_build_that_is_not_behind(string runningVersion)
    {
        var releases = ReleaseListing.Parse(Listing(("v0.2.0", false, false), ("v0.2.0-beta.9", true, false)));

        Assert.Null(ReleaseListing.SelectUpdate(Version(runningVersion), releases));
    }

    [Fact]
    public void A_stable_build_is_not_told_about_betas()
    {
        var releases = ReleaseListing.Parse(Listing(("v0.3.0-beta.1", true, false), ("v0.2.0", false, false)));

        Assert.Null(ReleaseListing.SelectUpdate(Version("0.2.0"), releases));
    }

    // ── Checking, and not checking too often ──────────────────────────────────

    [Fact]
    public async Task A_check_reports_the_update_and_remembers_it_for_a_day()
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var feed = new ScriptedReleaseFeed { ListingJson = Listing(("v0.2.0-beta.9", true, false)) };

        var firstOutcome = await Service(feed, "0.2.0-beta.8", () => now).CheckAsync(ignoreSchedule: false);
        feed.ListingJson = Listing(("v0.2.0-beta.10", true, false));
        // A second studio start, 23 hours later: answered from the remembered check.
        var secondOutcome = await Service(feed, "0.2.0-beta.8", () => now.AddHours(23)).CheckAsync(ignoreSchedule: false);

        Assert.Equal("v0.2.0-beta.9", firstOutcome.AvailableUpdate?.Tag);
        Assert.Null(firstOutcome.FailureDescription);
        Assert.Equal("v0.2.0-beta.9", secondOutcome.AvailableUpdate?.Tag);
        Assert.Equal(now, secondOutcome.CheckedAt);
        Assert.Equal(1, feed.RequestCount);
    }

    [Fact]
    public async Task The_feed_is_asked_again_after_a_day_or_when_the_user_asks()
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var feed = new ScriptedReleaseFeed { ListingJson = Listing(("v0.2.0-beta.9", true, false)) };
        await Service(feed, "0.2.0-beta.8", () => now).CheckAsync(ignoreSchedule: false);
        feed.ListingJson = Listing(("v0.2.0-beta.9", true, false), ("v0.2.0-beta.10", true, false));

        var askedByTheUser = await Service(feed, "0.2.0-beta.8", () => now.AddMinutes(5)).CheckAsync(ignoreSchedule: true);
        var aDayLater = await Service(feed, "0.2.0-beta.8", () => now.AddHours(25)).CheckAsync(ignoreSchedule: false);

        Assert.Equal("v0.2.0-beta.10", askedByTheUser.AvailableUpdate?.Tag);
        Assert.Equal("v0.2.0-beta.10", aDayLater.AvailableUpdate?.Tag);
        Assert.Equal(3, feed.RequestCount);
    }

    [Fact]
    public async Task A_remembered_update_stops_being_news_once_it_is_installed()
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var feed = new ScriptedReleaseFeed { ListingJson = Listing(("v0.2.0-beta.9", true, false)) };
        await Service(feed, "0.2.0-beta.8", () => now).CheckAsync(ignoreSchedule: false);

        var afterUpgrading = await Service(feed, "0.2.0-beta.9", () => now.AddHours(1)).CheckAsync(ignoreSchedule: false);

        Assert.Null(afterUpgrading.AvailableUpdate);
        Assert.Equal(1, feed.RequestCount);
    }

    [Fact]
    public async Task A_failed_check_says_why_keeps_what_was_known_and_is_retried_next_time()
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var feed = new ScriptedReleaseFeed { ListingJson = Listing(("v0.2.0-beta.9", true, false)) };
        await Service(feed, "0.2.0-beta.8", () => now).CheckAsync(ignoreSchedule: false);
        feed.Failure = new HttpRequestException("the network is in a meeting");

        var failedOutcome = await Service(feed, "0.2.0-beta.8", () => now.AddHours(30)).CheckAsync(ignoreSchedule: false);
        feed.Failure = null;
        feed.ListingJson = Listing(("v0.2.0", false, false));
        var retriedOutcome = await Service(feed, "0.2.0-beta.8", () => now.AddHours(31)).CheckAsync(ignoreSchedule: false);

        Assert.Equal("the network is in a meeting", failedOutcome.FailureDescription);
        Assert.Equal("v0.2.0-beta.9", failedOutcome.AvailableUpdate?.Tag);
        Assert.Equal(now, failedOutcome.CheckedAt);
        Assert.Equal("v0.2.0", retriedOutcome.AvailableUpdate?.Tag);
        Assert.Null(retriedOutcome.FailureDescription);
    }

    [Theory]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")]
    [InlineData("<html>captive portal</html>")]
    public async Task An_answer_that_is_not_a_release_list_is_a_failure_not_a_crash(string listingJson)
    {
        var feed = new ScriptedReleaseFeed { ListingJson = listingJson };

        var outcome = await Service(feed, "0.2.0-beta.8", () => DateTimeOffset.UnixEpoch).CheckAsync(ignoreSchedule: false);

        Assert.NotNull(outcome.FailureDescription);
        Assert.Null(outcome.AvailableUpdate);
        Assert.False(File.Exists(RememberedCheckPath));
    }

    [Fact]
    public async Task A_timeout_is_a_failure_but_closing_the_studio_cancels()
    {
        var feed = new ScriptedReleaseFeed { Failure = new TaskCanceledException("the request timed out") };
        var service = Service(feed, "0.2.0-beta.8", () => DateTimeOffset.UnixEpoch);

        var timedOut = await service.CheckAsync(ignoreSchedule: false);
        using var studioClosed = new CancellationTokenSource();
        studioClosed.Cancel();

        Assert.NotNull(timedOut.FailureDescription);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(ignoreSchedule: false, studioClosed.Token));
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"checkedAtUtc\":\"2099-01-01T00:00:00+00:00\",\"latestTag\":\"v9.9.9\"}")] // a clock that was wrong once must not silence the check for decades
    [InlineData("{\"checkedAtUtc\":\"2026-11-19T11:00:00+00:00\",\"latestTag\":\"definitely not a tag\"}")]
    public async Task A_remembered_check_that_cannot_be_trusted_is_ignored(string rememberedCheckJson)
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        File.WriteAllText(RememberedCheckPath, rememberedCheckJson);
        var feed = new ScriptedReleaseFeed { ListingJson = Listing(("v0.2.0-beta.9", true, false)) };

        var outcome = await Service(feed, "0.2.0-beta.8", () => now).CheckAsync(ignoreSchedule: false);

        Assert.Equal("v0.2.0-beta.9", outcome.AvailableUpdate?.Tag);
        Assert.Equal(1, feed.RequestCount);
    }

    [Fact]
    public async Task A_check_works_even_when_it_cannot_be_remembered()
    {
        var feed = new ScriptedReleaseFeed { ListingJson = Listing(("v0.2.0-beta.9", true, false)) };
        var unwritablePath = Path.Combine(scratchFolder, "a-file-not-a-folder", "update-check.json");
        File.WriteAllText(Path.Combine(scratchFolder, "a-file-not-a-folder"), string.Empty);
        var service = new UpdateNotificationService(feed, Version("0.2.0-beta.8"), unwritablePath, () => DateTimeOffset.UnixEpoch);

        var outcome = await service.CheckAsync(ignoreSchedule: false);

        Assert.Equal("v0.2.0-beta.9", outcome.AvailableUpdate?.Tag);
    }

    // ── What the studio says ──────────────────────────────────────────────────

    [Fact]
    public void The_outcome_is_described_fact_first()
    {
        var running = Version("0.2.0-beta.8");
        var update = Assert.Single(ReleaseListing.Parse(Listing(("v0.2.0-beta.9", true, false))));

        var updateText = UpdateCheckOutcome.Describe(new UpdateCheckOutcome(update, null, DateTimeOffset.UnixEpoch), running);
        var currentText = UpdateCheckOutcome.Describe(new UpdateCheckOutcome(null, null, DateTimeOffset.UnixEpoch), running);
        var failureText = UpdateCheckOutcome.Describe(new UpdateCheckOutcome(null, "no route to host", null), running);

        Assert.StartsWith("slaw v0.2.0-beta.9 is available (you run v0.2.0-beta.8)", updateText);
        Assert.StartsWith("slaw v0.2.0-beta.8 is up to date", currentText);
        Assert.StartsWith("could not check for updates: no route to host", failureText);
    }
}
