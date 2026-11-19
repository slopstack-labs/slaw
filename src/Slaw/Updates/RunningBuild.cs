using System.Reflection;

namespace SlopstackLabs.Slaw.Updates;

/// <summary>
/// What this build calls itself. The assembly version (<c>0.2.0.0</c>) forgets the
/// prerelease label, so beta.7 and beta.8 would look alike; the informational version
/// (<c>0.2.0-beta.8+commit</c>, stamped by <c>-p:Version</c> at publish time) does not.
/// </summary>
internal static class RunningBuild
{
    public static string InformationalVersion { get; } =
        typeof(RunningBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(RunningBuild).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static SemanticVersion Version { get; } =
        SemanticVersion.TryParse(InformationalVersion) ?? new SemanticVersion(0, 0, 0, string.Empty);

    /// <summary>The version without the commit: <c>0.2.0-beta.8</c>.</summary>
    public static string DisplayVersion => Version.ToString();

    /// <summary>The first seven characters of the commit this build was made from, when the build recorded one.</summary>
    public static string? CommitAbbreviation
    {
        get
        {
            var buildMetadataStart = InformationalVersion.IndexOf('+');
            if (buildMetadataStart < 0 || buildMetadataStart == InformationalVersion.Length - 1)
            {
                return null;
            }
            var commit = InformationalVersion[(buildMetadataStart + 1)..];
            return commit.Length > 7 ? commit[..7] : commit;
        }
    }
}
