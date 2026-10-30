using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Plugins;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// VST3 devices in the device chain: a picker for instruments and effects, a
/// generic knob panel built from whatever parameters the plugin declares, and
/// a button that opens the plugin's own editor (Windows) for when slaw's house
/// style is not what its vendor's brand guidelines had in mind.
/// </summary>
internal sealed partial class DetailView
{
    /// <summary>More than this many parameters and the panel shows a filter box and the first matches only.</summary>
    private const int MaximumVisiblePluginKnobs = 24;

    private readonly Dictionary<string, string> pluginParameterFilters = new(StringComparer.Ordinal);

    private static void WarmUpPluginCatalog() => _ = Task.Run(Vst3PluginCatalog.Plugins);

    /// <summary>"Editor": opens the plugin's own window. <paramref name="effectIndex"/> -1 is the instrument.</summary>
    private Button ComposePluginEditorButton(PluginReference pluginReference, string trackIdentifier, int effectIndex)
    {
        var editorButton = StudioUi.Button("Editor", () => host.OpenPluginEditor(trackIdentifier, effectIndex),
            OperatingSystem.IsWindows()
                ? $"Open {pluginReference.Name}'s own editor window"
                : "Plugin editor windows are Windows-only so far; the knobs below drive the plugin",
            compact: true);
        editorButton.IsEnabled = OperatingSystem.IsWindows();
        return editorButton;
    }

    /// <summary>"VST3…": a menu of scanned instruments that loads one into the track.</summary>
    private Button ComposeInstrumentPluginPicker(SlawTrack track)
    {
        var pickerButton = StudioUi.Button("VST3…", () => { }, "Load a VST3 instrument from your plugin folders (Settings → Plugins)", compact: true);
        var pluginMenu = new MenuFlyout();
        pluginMenu.Opening += (_, _) => PopulatePluginMenu(pluginMenu.Items, instruments: true,
            pickedPlugin => document.Edit($"loaded {pickedPlugin.Name} on {track.Name}",
                _ => PluginDeviceOperations.LoadInstrument(track, PluginDeviceOperations.ToReference(pickedPlugin)), EditScope.Structure));
        pluginMenu.Items.Add(new MenuItem { Header = "Scanning plugin folders…", IsEnabled = false });
        pickerButton.Flyout = pluginMenu;
        return pickerButton;
    }

    /// <summary>The "+ Effect" menu's VST3 submenu.</summary>
    private MenuItem ComposeEffectPluginSubmenu(SlawTrack track)
    {
        var pluginSubmenu = new MenuItem { Header = "VST3" };
        pluginSubmenu.Items.Add(new MenuItem { Header = "Scanning plugin folders…", IsEnabled = false });
        pluginSubmenu.SubmenuOpened += (_, _) => PopulatePluginMenu(pluginSubmenu.Items, instruments: false,
            pickedPlugin => AddPluginEffect(track, PluginDeviceOperations.ToReference(pickedPlugin)));
        return pluginSubmenu;
    }

    public void AddPluginEffect(SlawTrack track, PluginReference pluginReference, int insertionIndex = -1)
    {
        if (track.Effects.Count >= ProjectAlignmentRemediator.MaximumEffectsPerTrack)
        {
            return;
        }
        var effectIndex = insertionIndex < 0 ? track.Effects.Count : Math.Clamp(insertionIndex, 0, track.Effects.Count);
        document.Edit($"added {pluginReference.Name} to {track.Name}",
            _ => track.Effects.Insert(effectIndex, PluginDeviceOperations.CreateEffect(pluginReference)), EditScope.Structure);
    }

    private static void PopulatePluginMenu(ItemCollection menuItems, bool instruments, Action<Vst3PluginDescriptor> pluginPicked)
    {
        menuItems.Clear();
        if (Vst3PluginCatalog.CachedPlugins is not { } scannedPlugins)
        {
            menuItems.Add(new MenuItem { Header = "Scanning plugin folders… (reopen in a moment)", IsEnabled = false });
            WarmUpPluginCatalog();
            return;
        }
        var matchingPlugins = scannedPlugins.Where(plugin => plugin.IsInstrument == instruments).ToList();
        if (matchingPlugins.Count == 0)
        {
            menuItems.Add(new MenuItem
            {
                Header = $"No VST3 {(instruments ? "instruments" : "effects")} found — add a folder in Settings → Plugins",
                IsEnabled = false,
            });
        }
        foreach (var vendorGroup in matchingPlugins.GroupBy(plugin => string.IsNullOrEmpty(plugin.Vendor) ? "Unknown vendor" : plugin.Vendor).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var vendorItem = new MenuItem { Header = vendorGroup.Key };
            foreach (var plugin in vendorGroup)
            {
                var pluginItem = new MenuItem { Header = plugin.Name };
                ToolTip.SetTip(pluginItem, $"{plugin.BundlePath}\nfound in {plugin.SourceFolder}");
                pluginItem.Click += (_, _) => pluginPicked(plugin);
                vendorItem.Items.Add(pluginItem);
            }
            menuItems.Add(vendorItem);
        }
        var rescanItem = new MenuItem { Header = "↻ Rescan plugin folders" };
        rescanItem.Click += (_, _) => _ = Task.Run(Vst3PluginCatalog.Rescan);
        menuItems.Add(rescanItem);
    }

    /// <summary>
    /// The knobs of a plugin device. Parameters come from a short-lived probe of
    /// the plugin; until it answers the panel says so and rebuilds itself when it does.
    /// </summary>
    private Control ComposePluginParameterPanel(PluginReference pluginReference, Func<Dictionary<string, double>> parameterTable, string addressPrefix, string slotKey)
    {
        var panel = new StackPanel { Spacing = 4, Width = 6 * 68 };
        if (PluginInstancePool.Live?.LoadProblem(slotKey) is { } loadProblem)
        {
            panel.Children.Add(WrappedPluginCaption($"⚠ {loadProblem}", StudioTheme.Failure));
        }
        var pluginParameters = Vst3ParameterDirectory.TryGet(pluginReference);
        if (pluginParameters is null)
        {
            if (Vst3ParameterDirectory.ProbeProblem(pluginReference) is { } probeProblem)
            {
                panel.Children.Add(WrappedPluginCaption($"⚠ could not load {pluginReference.Name}: {probeProblem}", StudioTheme.Failure));
                return panel;
            }
            panel.Children.Add(StudioUi.Caption("asking the plugin about its knobs…", 10));
            _ = Vst3ParameterDirectory.GetAsync(pluginReference).ContinueWith(_ => Dispatcher.UIThread.Post(Rebuild), TaskScheduler.Default);
            return panel;
        }

        var userFacingParameters = pluginParameters.Where(parameter => parameter.IsUserFacing).ToList();
        var knobGrid = new WrapPanel { Orientation = Orientation.Vertical, MaxHeight = 140 };
        void ShowMatchingKnobs(string filterText)
        {
            knobGrid.Children.Clear();
            foreach (var parameter in userFacingParameters
                         .Where(parameter => parameter.Title.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                         .Take(MaximumVisiblePluginKnobs))
            {
                var parameterSpecification = new ParameterSpecification(
                    parameter.Key, parameter.Title, 0, 1, parameter.DefaultValue, "%");
                var parameterKnob = ComposeParameterKnob(parameterSpecification, parameterTable,
                    $"{addressPrefix}/{parameter.Key}", $"{pluginReference.Name} {parameter.Title}");
                ToolTip.SetTip(parameterKnob, $"{parameter.Title}{(parameter.Units.Length > 0 ? $" [{parameter.Units}]" : string.Empty)} · {parameter.Key}");
                parameterKnob.Margin = new Thickness(2);
                knobGrid.Children.Add(parameterKnob);
            }
        }

        if (userFacingParameters.Count == 0)
        {
            panel.Children.Add(StudioUi.Caption($"{pluginReference.Name} exposes no automatable knobs", 10));
            return panel;
        }
        if (userFacingParameters.Count > MaximumVisiblePluginKnobs)
        {
            var filterBox = new TextBox
            {
                Watermark = $"filter {userFacingParameters.Count} parameters…",
                FontSize = 11,
                Text = pluginParameterFilters.GetValueOrDefault(addressPrefix, string.Empty),
            };
            filterBox.TextChanged += (_, _) =>
            {
                pluginParameterFilters[addressPrefix] = filterBox.Text ?? string.Empty;
                ShowMatchingKnobs(filterBox.Text ?? string.Empty);
            };
            panel.Children.Add(filterBox);
        }
        ShowMatchingKnobs(pluginParameterFilters.GetValueOrDefault(addressPrefix, string.Empty));
        panel.Children.Add(knobGrid);
        return panel;
    }

    private static TextBlock WrappedPluginCaption(string captionText, IBrush foreground)
    {
        var caption = StudioUi.Caption(captionText, 10, foreground);
        caption.TextWrapping = TextWrapping.Wrap;
        caption.TextTrimming = TextTrimming.None;
        return caption;
    }
}
