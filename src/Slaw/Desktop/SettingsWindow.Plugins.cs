using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Plugins;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>Settings → Plugins: the user's own VST3 folders, scanned on top of the system ones.</summary>
internal sealed partial class SettingsWindow
{
    private ObservableCollection<string> workingPluginFolders = new();
    private ListBox? pluginFoldersListBox;
    private Button? removePluginFolderButton;
    private TextBlock? pluginScanResultTextBlock;

    private Control BuildPluginsTab()
    {
        workingPluginFolders = new ObservableCollection<string>(workingPreferences.PluginFolders ?? new List<string>());
        pluginFoldersListBox = new ListBox
        {
            Height = 160,
            FontFamily = StudioTheme.MonospaceFont,
            FontSize = 12,
            ItemsSource = workingPluginFolders,
        };
        pluginFoldersListBox.SelectionChanged += (_, _) => RefreshRemovePluginFolderEnablement();
        removePluginFolderButton = StudioUi.Button("Remove", RemoveSelectedPluginFolder);
        pluginScanResultTextBlock = StudioUi.Caption(string.Empty, 12);
        pluginScanResultTextBlock.TextWrapping = TextWrapping.Wrap;
        pluginScanResultTextBlock.TextTrimming = TextTrimming.None;

        var tabPanel = new StackPanel { Spacing = 10 };
        tabPanel.Children.Add(StudioUi.SectionHeading("VST3 plugin folders"));
        tabPanel.Children.Add(PluginCaption(
            "slaw scans these folders (recursively) for .vst3 bundles, in addition to the system folders below and anything in the " +
            $"{Vst3PluginCatalog.PluginPathEnvironmentVariable} environment variable. Third-party plugins run inside slaw's process: " +
            "slopstack labs has audited none of them, and is confident they are fine."));
        tabPanel.Children.Add(pluginFoldersListBox);
        tabPanel.Children.Add(StudioUi.Row(8,
            StudioUi.Button("Add folder…", () => _ = AddPluginFoldersAsync()),
            removePluginFolderButton,
            StudioUi.Button("Rescan", () => _ = RescanPluginsAsync(), "Scan every plugin folder again")));
        tabPanel.Children.Add(pluginScanResultTextBlock);
        tabPanel.Children.Add(StudioUi.SectionHeading("Always scanned"));
        tabPanel.Children.Add(PluginCaption(string.Join("\n", Vst3PluginCatalog.SystemFolders().Concat(Vst3PluginCatalog.EnvironmentFolders()))));
        tabPanel.Children.Add(PluginCaption(
            "Plugins appear under the instrument's “VST3…” button and in “+ Effect”, and can be dropped onto a track or the device chain. " +
            "Their knobs are automatable and visible to AI agents over MCP. Plugin editor windows are on the roadmap, next to profitability."));
        RefreshRemovePluginFolderEnablement();
        return tabPanel;
    }

    private static TextBlock PluginCaption(string captionText)
    {
        var caption = StudioUi.Caption(captionText);
        caption.TextWrapping = TextWrapping.Wrap;
        caption.TextTrimming = TextTrimming.None;
        return caption;
    }

    private void RefreshRemovePluginFolderEnablement()
    {
        if (removePluginFolderButton is not null)
        {
            removePluginFolderButton.IsEnabled = pluginFoldersListBox?.SelectedItem is not null;
        }
    }

    private void RemoveSelectedPluginFolder()
    {
        if (pluginFoldersListBox?.SelectedItem is string selectedFolder)
        {
            workingPluginFolders.Remove(selectedFolder);
            RefreshRemovePluginFolderEnablement();
        }
    }

    private async Task AddPluginFoldersAsync()
    {
        var pickedFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add VST3 plugin folder",
            AllowMultiple = true,
        });
        foreach (var pickedFolder in pickedFolders)
        {
            var localPath = pickedFolder.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath) && !workingPluginFolders.Contains(localPath))
            {
                workingPluginFolders.Add(localPath);
            }
        }
    }

    private async Task RescanPluginsAsync()
    {
        if (pluginScanResultTextBlock is null)
        {
            return;
        }
        pluginScanResultTextBlock.Foreground = StudioTheme.TextMuted;
        pluginScanResultTextBlock.Text = "scanning…";
        Vst3PluginCatalog.ConfigureUserFolders(workingPluginFolders);
        var scannedPlugins = await Task.Run(Vst3PluginCatalog.Rescan);
        var scanProblems = Vst3PluginCatalog.LastScanProblems;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            pluginScanResultTextBlock.Foreground = scanProblems.Count > 0 ? StudioTheme.Warning : StudioTheme.Playhead;
            var instrumentCount = scannedPlugins.Count(plugin => plugin.IsInstrument);
            pluginScanResultTextBlock.Text =
                $"✓ {instrumentCount} instrument(s), {scannedPlugins.Count - instrumentCount} effect(s)" +
                (scanProblems.Count > 0 ? $"\n⚠ {string.Join("\n⚠ ", scanProblems)}" : string.Empty);
        });
    }

    private void ApplyPluginFoldersToWorkingPreferences()
    {
        workingPreferences.PluginFolders = new List<string>(workingPluginFolders);
        Vst3PluginCatalog.ConfigureUserFolders(workingPluginFolders);
    }
}
