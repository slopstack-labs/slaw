using System.Text.Json;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>One plugin class found on disk, and the folder that brought it to us.</summary>
internal sealed record Vst3PluginDescriptor(
    string BundlePath,
    string ClassId,
    string Name,
    string Vendor,
    string SubCategories,
    bool IsInstrument,
    string SourceFolder)
{
    public string KindLabel => IsInstrument ? "instrument" : "effect";
}

/// <summary>
/// Finds VST3 plugins: the platform's standard folders, every folder in the
/// VST3_PATH environment variable, and whatever folders the user configured
/// (Settings → Plugins, or --plugin-folder). Bundles are read from their
/// moduleinfo.json when they ship one, and loaded only when they do not —
/// scanning should not be the most dangerous thing a DAW does all day.
/// </summary>
internal static class Vst3PluginCatalog
{
    public const string PluginPathEnvironmentVariable = "VST3_PATH";

    private static readonly object CatalogLock = new();
    private static List<string> userFolders = new();
    private static List<string> sessionFolders = new();
    private static IReadOnlyList<Vst3PluginDescriptor>? cachedPlugins;
    private static IReadOnlyList<string> lastScanProblems = Array.Empty<string>();

    /// <summary>The platform's standard VST3 locations.</summary>
    public static IReadOnlyList<string> SystemFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            return new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3"),
            };
        }
        if (OperatingSystem.IsMacOS())
        {
            return new[]
            {
                "/Library/Audio/Plug-Ins/VST3",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Audio", "Plug-Ins", "VST3"),
            };
        }
        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vst3"),
            "/usr/lib/vst3",
            "/usr/local/lib/vst3",
        };
    }

    /// <summary>Folders listed in VST3_PATH (colon-separated, or semicolon-separated on Windows; either is accepted).</summary>
    public static IReadOnlyList<string> EnvironmentFolders(string? environmentValue = null)
    {
        environmentValue ??= Environment.GetEnvironmentVariable(PluginPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(environmentValue))
        {
            return Array.Empty<string>();
        }
        var separators = OperatingSystem.IsWindows() ? new[] { ';' } : new[] { ':', ';' };
        return environmentValue.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The user's own plugin folders. Replacing them invalidates the cached scan.</summary>
    public static IReadOnlyList<string> UserFolders
    {
        get
        {
            lock (CatalogLock)
            {
                return userFolders.ToList();
            }
        }
    }

    public static void ConfigureUserFolders(IEnumerable<string> configuredFolders)
    {
        var normalizedFolders = NormalizeFolders(configuredFolders);
        lock (CatalogLock)
        {
            if (!normalizedFolders.SequenceEqual(userFolders))
            {
                userFolders = normalizedFolders;
                cachedPlugins = null;
            }
        }
    }

    /// <summary>Folders given on the command line (--plugin-folder): scanned this session, never saved.</summary>
    public static void ConfigureSessionFolders(IEnumerable<string> commandLineFolders)
    {
        var normalizedFolders = NormalizeFolders(commandLineFolders);
        lock (CatalogLock)
        {
            if (!normalizedFolders.SequenceEqual(sessionFolders))
            {
                sessionFolders = normalizedFolders;
                cachedPlugins = null;
            }
        }
    }

    private static List<string> NormalizeFolders(IEnumerable<string> folders) => folders
        .Where(folder => !string.IsNullOrWhiteSpace(folder))
        .Select(folder => Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim())))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>Every folder that is scanned, the user's first so their plugins win duplicates.</summary>
    public static IReadOnlyList<string> AllFolders()
    {
        List<string> sessionSnapshot;
        lock (CatalogLock)
        {
            sessionSnapshot = sessionFolders.ToList();
        }
        return UserFolders.Concat(sessionSnapshot).Concat(EnvironmentFolders()).Concat(SystemFolders()).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Plugins that failed to scan last time, with the reason.</summary>
    public static IReadOnlyList<string> LastScanProblems
    {
        get
        {
            lock (CatalogLock)
            {
                return lastScanProblems;
            }
        }
    }

    /// <summary>The last scan's plugins, or null if nothing has been scanned since the folders changed. Never scans.</summary>
    public static IReadOnlyList<Vst3PluginDescriptor>? CachedPlugins
    {
        get
        {
            lock (CatalogLock)
            {
                return cachedPlugins;
            }
        }
    }

    /// <summary>The scanned plugins (cached until <see cref="Rescan"/> or a folder change).</summary>
    public static IReadOnlyList<Vst3PluginDescriptor> Plugins()
    {
        lock (CatalogLock)
        {
            if (cachedPlugins is not null)
            {
                return cachedPlugins;
            }
        }
        var scanProblems = new List<string>();
        var scannedPlugins = Scan(AllFolders(), scanProblems);
        lock (CatalogLock)
        {
            cachedPlugins = scannedPlugins;
            lastScanProblems = scanProblems;
            return scannedPlugins;
        }
    }

    public static IReadOnlyList<Vst3PluginDescriptor> Rescan()
    {
        lock (CatalogLock)
        {
            cachedPlugins = null;
        }
        return Plugins();
    }

    /// <summary>Finds a scanned plugin by bundle path or name (case-insensitive) and optional class id.</summary>
    public static Vst3PluginDescriptor? Find(string? bundlePathOrName, string? classIdentifier = null)
    {
        var plugins = Plugins();
        return plugins.FirstOrDefault(plugin =>
                   (bundlePathOrName is null || PathsEqual(plugin.BundlePath, bundlePathOrName)) &&
                   (classIdentifier is null || string.Equals(plugin.ClassId, classIdentifier, StringComparison.OrdinalIgnoreCase)) &&
                   (bundlePathOrName is not null || classIdentifier is not null))
               ?? plugins.FirstOrDefault(plugin =>
                   bundlePathOrName is not null && string.Equals(plugin.Name, bundlePathOrName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathsEqual(string firstPath, string secondPath)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(firstPath).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(secondPath).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception pathRemediationOpportunity) when (pathRemediationOpportunity is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Scans <paramref name="folders"/> recursively for .vst3 bundles and lists their audio module classes.</summary>
    public static IReadOnlyList<Vst3PluginDescriptor> Scan(IEnumerable<string> folders, List<string>? scanProblems = null)
    {
        var discoveredPlugins = new List<Vst3PluginDescriptor>();
        var seenBundles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            foreach (var bundlePath in FindBundles(folder))
            {
                if (!seenBundles.Add(Path.GetFileName(bundlePath).ToLowerInvariant()))
                {
                    continue;
                }
                try
                {
                    discoveredPlugins.AddRange(DescribeBundle(bundlePath, folder));
                }
                catch (Exception scanRemediationOpportunity)
                {
                    scanProblems?.Add($"{bundlePath}: {scanRemediationOpportunity.Message}");
                }
            }
        }
        return discoveredPlugins
            .OrderBy(plugin => plugin.IsInstrument ? 0 : 1)
            .ThenBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every *.vst3 bundle (directory or file) under <paramref name="folder"/>, without descending into bundles.</summary>
    public static IEnumerable<string> FindBundles(string folder)
    {
        if (folder.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase) && (Directory.Exists(folder) || File.Exists(folder)))
        {
            return new[] { folder };
        }
        if (!Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }
        var foundBundles = new List<string>();
        var pendingFolders = new Stack<(string Path, int Depth)>();
        pendingFolders.Push((folder, 0));
        while (pendingFolders.Count > 0)
        {
            var (currentFolder, depth) = pendingFolders.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentFolder).ToList();
            }
            catch (Exception folderRemediationOpportunity) when (folderRemediationOpportunity is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries.Order(StringComparer.Ordinal))
            {
                if (entry.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                {
                    foundBundles.Add(entry);
                }
                else if (depth < 8 && Directory.Exists(entry))
                {
                    pendingFolders.Push((entry, depth + 1));
                }
            }
        }
        return foundBundles;
    }

    private static IEnumerable<Vst3PluginDescriptor> DescribeBundle(string bundlePath, string sourceFolder)
    {
        var moduleInfoPath = Path.Combine(bundlePath, "Contents", "Resources", "moduleinfo.json");
        var pluginClasses = File.Exists(moduleInfoPath)
            ? ReadModuleInfo(File.ReadAllText(moduleInfoPath))
            : Vst3Module.Load(bundlePath).Classes;
        return pluginClasses
            .Where(pluginClass => pluginClass.IsAudioModule)
            .Select(pluginClass => new Vst3PluginDescriptor(
                bundlePath, pluginClass.ClassId, pluginClass.Name, pluginClass.Vendor, pluginClass.SubCategories, pluginClass.IsInstrument, sourceFolder));
    }

    /// <summary>Parses the classes out of a bundle's moduleinfo.json (JSON5-flavoured: comments and trailing commas tolerated).</summary>
    public static IReadOnlyList<Vst3PluginClass> ReadModuleInfo(string moduleInfoJson)
    {
        using var moduleInfo = JsonDocument.Parse(moduleInfoJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var factoryVendor = moduleInfo.RootElement.TryGetProperty("Factory Info", out var factoryInfo) && factoryInfo.TryGetProperty("Vendor", out var vendorElement)
            ? vendorElement.GetString() ?? string.Empty
            : string.Empty;
        if (!moduleInfo.RootElement.TryGetProperty("Classes", out var classesElement) || classesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Vst3PluginClass>();
        }
        var pluginClasses = new List<Vst3PluginClass>();
        foreach (var classElement in classesElement.EnumerateArray())
        {
            string ReadText(string propertyName) =>
                classElement.TryGetProperty(propertyName, out var propertyElement) && propertyElement.ValueKind == JsonValueKind.String
                    ? propertyElement.GetString() ?? string.Empty
                    : string.Empty;
            var subCategories = classElement.TryGetProperty("Sub Categories", out var subCategoriesElement) && subCategoriesElement.ValueKind == JsonValueKind.Array
                ? string.Join('|', subCategoriesElement.EnumerateArray().Select(subCategory => subCategory.GetString()))
                : ReadText("Sub Categories");
            var classIdentifier = ReadText("CID");
            if (Vst3Abi.ParseUid(classIdentifier) is null)
            {
                continue;
            }
            var vendor = ReadText("Vendor");
            pluginClasses.Add(new Vst3PluginClass(
                classIdentifier.ToUpperInvariant(),
                ReadText("Name"),
                ReadText("Category"),
                subCategories,
                string.IsNullOrEmpty(vendor) ? factoryVendor : vendor,
                ReadText("Version")));
        }
        return pluginClasses;
    }
}
