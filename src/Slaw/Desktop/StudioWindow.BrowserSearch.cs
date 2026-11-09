using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Plugins;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>What the browser's search box finds: every word of the query, anywhere in an entry's keywords, in any order.</summary>
internal static class BrowserSearch
{
    public static bool Matches(string query, string keywords) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => keywords.Contains(word, StringComparison.OrdinalIgnoreCase));

    public static string PluginKeywords(Vst3PluginDescriptor plugin) =>
        $"{plugin.Name} {plugin.Vendor} vst3 plugin {(plugin.IsInstrument ? "instrument" : "effect")}";

    public static string PluginTrackTitle(Vst3PluginDescriptor plugin) => $"Add Track: {plugin.Name} (VST3)";

    public static string PluginEffectTitle(string trackName, Vst3PluginDescriptor plugin) => $"Add Effect to {trackName}: {plugin.Name} (VST3)";
}

/// <summary>The one search box above the browser: it filters instruments, effects, plugins, samples and AI actions together.</summary>
internal sealed partial class StudioWindow
{
    private sealed record BrowserSection(string Title, Button Heading, StackPanel Entries, Func<int>? ListedMatches, StudioIconGlyph Chevron, TextBlock CountText);

    private readonly TextBox browserSearchInput = new()
    {
        Watermark = "Search",
        FontSize = 12,
        MinHeight = 30,
        Padding = new Thickness(4, 5, 8, 5),
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        InnerLeftContent = new StudioIconGlyph { Icon = StudioIcon.Search, Width = 13, Height = 13, Brush = StudioTheme.TextMuted, Margin = new Thickness(9, 0, 2, 0) },
    };

    private readonly TextBlock browserNoMatchesCaption = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 10,
        Foreground = StudioTheme.TextMuted,
        Margin = new Thickness(2, 4),
        IsVisible = false,
    };

    private readonly List<BrowserSection> browserSections = new();
    private readonly Dictionary<Control, string> browserSearchEntries = new();
    private readonly List<Control> browserToolRows = new();
    private int pluginMatchCount;
    private int sampleMatchCount;

    private string BrowserQuery => browserSearchInput.Text?.Trim() ?? string.Empty;

    private T Searchable<T>(T entry, string keywords) where T : Control
    {
        browserSearchEntries[entry] = keywords;
        return entry;
    }

    /// <summary>The buttons beside a section's list are for browsing; a search only lists what it found.</summary>
    private Control BrowserToolRow(Control toolRow)
    {
        browserToolRows.Add(toolRow);
        return toolRow;
    }

    private void ApplyBrowserSearch()
    {
        var query = BrowserQuery;
        foreach (var (entry, keywords) in browserSearchEntries)
        {
            entry.IsVisible = BrowserSearch.Matches(query, keywords);
        }
        foreach (var toolRow in browserToolRows)
        {
            toolRow.IsVisible = query.Length == 0;
        }
        // Before the first scan there is nothing to filter, and a keystroke should not be what starts one.
        if (Vst3PluginCatalog.CachedPlugins is not null)
        {
            PopulatePluginEntries();
        }
        PopulateSampleEntries();
        RefreshBrowserSections();
    }

    /// <summary>A search shows only the sections with a match, unfolded whatever is remembered; clearing it puts them all back.</summary>
    private void RefreshBrowserSections()
    {
        var query = BrowserQuery;
        var totalMatches = 0;
        foreach (var section in browserSections)
        {
            var matches = section.ListedMatches?.Invoke()
                ?? section.Entries.Children.Count(child => child.IsVisible && browserSearchEntries.ContainsKey(child));
            totalMatches += matches;
            section.Heading.IsVisible = query.Length == 0 || matches > 0;
            section.Entries.IsVisible = query.Length > 0 ? matches > 0 : !preferences.CollapsedBrowserSections.Contains(section.Title);
            ShowBrowserSectionFold(section);
        }
        browserNoMatchesCaption.IsVisible = query.Length > 0 && totalMatches == 0;
        browserNoMatchesCaption.Text = $"Nothing here matches \"{query}\". New sounds come in through Import Sounds (Ctrl+Shift+I).";
    }

    private void ShowBrowserSectionFold(BrowserSection section)
    {
        section.Chevron.Icon = section.Entries.IsVisible ? StudioIcon.ChevronDown : StudioIcon.ChevronRight;
        var listed = section.ListedMatches?.Invoke()
            ?? section.Entries.Children.Count(child => browserSearchEntries.ContainsKey(child) && (BrowserQuery.Length == 0 || child.IsVisible));
        section.CountText.Text = listed > 0 ? listed.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    }

    private void FocusBrowserSearch()
    {
        if (preferences.BrowserHidden)
        {
            TogglePanel(() => preferences.BrowserHidden = false);
        }
        Activate();
        // After the palette has closed and handed the keyboard back.
        Dispatcher.UIThread.Post(() =>
        {
            browserSearchInput.Focus();
            browserSearchInput.SelectAll();
        }, DispatcherPriority.Background);
    }
}
