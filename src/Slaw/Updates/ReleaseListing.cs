using System.Text.Json;

namespace SlopstackLabs.Slaw.Updates;

/// <summary>One published release, as far as the update check cares.</summary>
internal sealed record ReleaseAnnouncement(string Tag, SemanticVersion Version, bool IsPrerelease)
{
    /// <summary>Where the release notes and downloads are. Built from the tag: nothing a feed says is ever opened as a link.</summary>
    public string PageUrl => ReleaseListing.ReleasePageUrl(Tag);
}

/// <summary>Where the list of releases comes from. GitHub in the studio, memory in the tests.</summary>
internal interface IReleaseFeedSubstrate
{
    /// <summary>The releases as GitHub's "list releases" JSON. Throws <see cref="HttpRequestException"/> when it cannot be had.</summary>
    Task<string> FetchReleaseListingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Asks api.github.com for slaw's releases, anonymously. The request carries the
/// client's name and nothing about the machine, the user or the project.
/// </summary>
internal sealed class GitHubReleaseFeedSubstrate : IReleaseFeedSubstrate
{
    // "List releases" and not "latest release": the latter skips prereleases, and so far every slaw is one.
    private const string ReleaseListingEndpoint = $"https://api.github.com/repos/{ReleaseListing.RepositorySlug}/releases?per_page=30";

    private static readonly HttpClient ReleaseFeedTransportSubstrate = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<string> FetchReleaseListingAsync(CancellationToken cancellationToken)
    {
        using var releaseListingRequest = new HttpRequestMessage(HttpMethod.Get, ReleaseListingEndpoint);
        // GitHub refuses requests without a User-Agent.
        releaseListingRequest.Headers.TryAddWithoutValidation("User-Agent", "slaw-update-check");
        releaseListingRequest.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        releaseListingRequest.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var releaseListingResponse = await ReleaseFeedTransportSubstrate.SendAsync(releaseListingRequest, cancellationToken).ConfigureAwait(false);
        if (!releaseListingResponse.IsSuccessStatusCode)
        {
            // 403 and 429 are GitHub's anonymous rate limit (60 requests an hour per address).
            throw new HttpRequestException(releaseListingResponse.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests
                ? "GitHub is rate-limiting this network; try again in an hour"
                : $"GitHub answered {(int)releaseListingResponse.StatusCode} {releaseListingResponse.ReasonPhrase}");
        }
        return await releaseListingResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads a release listing and decides which release, if any, is news to a build.</summary>
internal static class ReleaseListing
{
    public const string RepositorySlug = "slopstack-labs/slaw";

    public static string ReleasePageUrl(string tag) =>
        $"https://github.com/{RepositorySlug}/releases/tag/{Uri.EscapeDataString(tag)}";

    /// <summary>
    /// The published releases whose tags are versions, in the listing's order. Drafts and
    /// tags like "nightly" are left out. Throws <see cref="JsonException"/> when the text
    /// is not a list of releases (an error object, a captive portal's page).
    /// </summary>
    public static IReadOnlyList<ReleaseAnnouncement> Parse(string listingJson)
    {
        using var listingDocument = JsonDocument.Parse(listingJson);
        if (listingDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("the answer was not a list of releases");
        }
        var publishedReleases = new List<ReleaseAnnouncement>();
        foreach (var releaseElement in listingDocument.RootElement.EnumerateArray())
        {
            if (releaseElement.ValueKind != JsonValueKind.Object ||
                IsTrue(releaseElement, "draft") ||
                !releaseElement.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.ValueKind != JsonValueKind.String ||
                tagElement.GetString() is not { } tag ||
                SemanticVersion.TryParse(tag) is not { } releaseVersion)
            {
                continue;
            }
            // A label in the tag makes it a prerelease even if nobody ticked the box on GitHub.
            publishedReleases.Add(new ReleaseAnnouncement(tag.Trim(), releaseVersion, IsTrue(releaseElement, "prerelease") || releaseVersion.IsPrerelease));
        }
        return publishedReleases;
    }

    private static bool IsTrue(JsonElement releaseElement, string propertyName) =>
        releaseElement.TryGetProperty(propertyName, out var flagElement) && flagElement.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The newest release that is newer than <paramref name="runningVersion"/>, or null.
    /// A prerelease build hears about prereleases and releases; a release build only about releases.
    /// </summary>
    public static ReleaseAnnouncement? SelectUpdate(SemanticVersion runningVersion, IEnumerable<ReleaseAnnouncement> publishedReleases) =>
        publishedReleases
            .Where(release => release.Version.IsNewerThan(runningVersion) && (runningVersion.IsPrerelease || !release.IsPrerelease))
            .MaxBy(release => release.Version);
}
