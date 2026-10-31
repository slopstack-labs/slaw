namespace SlopstackLabs.Slaw.Surfaces;

/// <summary>One line of the Settings → Controllers status list.</summary>
internal sealed record ControllerStatusLine(string Name, bool Ready, string Detail);

/// <summary>What the running studio knows about its controllers, handed to the Settings window.</summary>
internal sealed record ControllerSnapshot(bool DeckServerListening, int DeckServerPort, int ConnectedDeckPlugins, string? LaunchpadPortName, string? LaunchpadFailure, IReadOnlyList<string> MidiOutputNames);

/// <summary>A deck plugin the studio can offer to install.</summary>
internal sealed record DeckPluginEntry(string DisplayName, string Identifier, string Summary)
{
    public string PackageFileName => $"{Identifier}.streamDeckPlugin";
}

/// <summary>Finds deck plugin packages and the Stream Deck app, and words the status list.</summary>
internal static class ControllerDirectory
{
    public static readonly IReadOnlyList<DeckPluginEntry> DeckPlugins = new[]
    {
        new DeckPluginEntry("slaw Keys", "com.slopstacklabs.slaw-keypad", "Launchpad and Pads profiles for the XL, plus keys for any Stream Deck"),
        new DeckPluginEntry("slaw Dials", "com.slopstacklabs.slaw-plus", "Volume, pan, tempo and track selection on Stream Deck + dials"),
    };

    private const int MaximumParentFoldersSearched = 6;

    /// <summary>Looks beside the app, then up the folders a source checkout would have: integrations/dist is where npm run pack writes.</summary>
    public static string? FindPluginPackage(DeckPluginEntry plugin, string? startFolder = null)
    {
        var searchFolder = new DirectoryInfo(startFolder ?? AppContext.BaseDirectory);
        for (var depth = 0; searchFolder is not null && depth <= MaximumParentFoldersSearched; depth++, searchFolder = searchFolder.Parent)
        {
            var candidatePath = Path.Combine(searchFolder.FullName, "integrations", "dist", plugin.PackageFileName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }
        return null;
    }

    /// <summary>The Stream Deck app's plugin folder for this user, or null where the app does not run.</summary>
    public static string? StreamDeckPluginsFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck", "Plugins");
        }
        return OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "com.elgato.StreamDeck", "Plugins")
            : null;
    }

    public static bool IsPluginInstalled(DeckPluginEntry plugin, string? pluginsFolder) =>
        pluginsFolder is not null && Directory.Exists(Path.Combine(pluginsFolder, $"{plugin.Identifier}.sdPlugin"));

    public static IReadOnlyList<ControllerStatusLine> DescribeStatus(ControllerSnapshot snapshot, string? pluginsFolder)
    {
        var statusLines = new List<ControllerStatusLine>
        {
            new("Deck control", snapshot.DeckServerListening, snapshot.DeckServerListening
                ? $"listening on ws://127.0.0.1:{snapshot.DeckServerPort}/surface, {snapshot.ConnectedDeckPlugins} plugin connection(s)"
                : "off"),
        };
        foreach (var plugin in DeckPlugins)
        {
            statusLines.Add(new ControllerStatusLine(plugin.DisplayName, IsPluginInstalled(plugin, pluginsFolder),
                IsPluginInstalled(plugin, pluginsFolder) ? "installed in the Stream Deck app" : "not installed"));
        }
        statusLines.Add(snapshot.LaunchpadPortName is { } launchpadPort
            ? new ControllerStatusLine("Launchpad MK2", true, $"connected as {launchpadPort}")
            : new ControllerStatusLine("Launchpad MK2", false, snapshot.LaunchpadFailure ?? "not connected"));
        statusLines.Add(new ControllerStatusLine("Ableton Push", false, "not supported yet"));
        return statusLines;
    }
}
