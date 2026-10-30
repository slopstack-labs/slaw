using Avalonia.Controls;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Plugins;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>VST3 bundles dropped from the file manager: instruments onto tracks, effects into chains. And their editors.</summary>
internal sealed partial class StudioWindow
{
    private readonly StackPanel pluginEntriesPanel = new() { Spacing = 3 };

    /// <summary>
    /// The browser's VST3 section: every scanned instrument (click: new track) and
    /// effect (click: onto the selected track). Entries drag like a .vst3 bundle
    /// from the file manager, so they drop onto tracks and device chains too.
    /// </summary>
    private void PopulatePluginEntries(bool rescan = false)
    {
        pluginEntriesPanel.Children.Clear();
        if (!rescan && Vst3PluginCatalog.CachedPlugins is { } cachedPlugins)
        {
            FillPluginEntries(cachedPlugins);
            return;
        }
        pluginEntriesPanel.Children.Add(StudioUi.Caption("scanning plugin folders…", 10));
        _ = Task.Run(() => rescan ? Vst3PluginCatalog.Rescan() : Vst3PluginCatalog.Plugins())
            .ContinueWith(scanTask => Dispatcher.UIThread.Post(() =>
            {
                pluginEntriesPanel.Children.Clear();
                if (scanTask.IsCompletedSuccessfully)
                {
                    FillPluginEntries(scanTask.Result);
                }
                else
                {
                    pluginEntriesPanel.Children.Add(StudioUi.Caption($"plugin scan failed: {scanTask.Exception?.GetBaseException().Message}", 10, StudioTheme.Failure));
                }
            }), TaskScheduler.Default);
    }

    private void FillPluginEntries(IReadOnlyList<Vst3PluginDescriptor> scannedPlugins)
    {
        if (scannedPlugins.Count == 0)
        {
            pluginEntriesPanel.Children.Add(StudioUi.Caption("No VST3 plugins found. Add a folder in ⚙ Settings → Plugins.", 10));
            return;
        }
        foreach (var (kindHeading, instruments) in new[] { ("instruments", true), ("effects", false) })
        {
            var pluginsOfKind = scannedPlugins.Where(plugin => plugin.IsInstrument == instruments).OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (pluginsOfKind.Count == 0)
            {
                continue;
            }
            pluginEntriesPanel.Children.Add(StudioUi.Caption(kindHeading, 10));
            foreach (var plugin in pluginsOfKind)
            {
                var pluginEntry = instruments
                    ? BrowserEntry(plugin.Name, () => AddPluginInstrumentTrack(plugin),
                        $"{plugin.Vendor} · VST3 instrument\nClick to add a track playing it, or drag it onto a track to swap its instrument\n{plugin.BundlePath}")
                    : BrowserEntry(plugin.Name, () =>
                    {
                        if (document.SelectedTrack is { } selectedTrack)
                        {
                            detailView.AddPluginEffect(selectedTrack, PluginDeviceOperations.ToReference(plugin));
                        }
                    }, $"{plugin.Vendor} · VST3 effect\nClick to add it to the selected track, or drag it onto any track or into the device chain\n{plugin.BundlePath}");
                StudioDragDrop.MakeDragSource(pluginEntry, () => new DragPayload(DragPayloadKind.Files, FilePaths: new[] { plugin.BundlePath }));
                pluginEntriesPanel.Children.Add(pluginEntry);
            }
        }
    }

    private void AddPluginInstrumentTrack(Vst3PluginDescriptor plugin)
    {
        if (document.Project.Tracks.Count >= ProjectAlignmentRemediator.MaximumTrackCount)
        {
            SetStatus(StatusSeverity.Warning, "the track budget is exhausted. consolidate headcount before onboarding more plugins.");
            return;
        }
        var sceneIndex = document.SelectedSceneIndex;
        document.Edit($"added a {plugin.Name} track", project =>
            project.Tracks.Add(PluginDeviceOperations.CreateInstrumentTrack(project, PluginDeviceOperations.ToReference(plugin), sceneIndex)), EditScope.Structure);
        document.Select(document.Project.Tracks.Count - 1, sceneIndex);
    }

    public async void OpenPluginEditor(string trackIdentifier, int effectIndex)
    {
        // The master chain's devices use the owner name "master" in place of a track id.
        var masterEffects = document.Project.Master.Effects;
        var ownsMasterEffect = trackIdentifier == Synthesis.SequencerEngine.MasterEffectSlotOwner;
        var track = document.Project.Tracks.FirstOrDefault(candidate => candidate.Id == trackIdentifier);
        var plugin = ownsMasterEffect
            ? effectIndex >= 0 && effectIndex < masterEffects.Count ? masterEffects[effectIndex].Plugin : null
            : effectIndex < 0
                ? track?.Plugin
                : track is not null && effectIndex < track.Effects.Count ? track.Effects[effectIndex].Plugin : null;
        if ((track is null && !ownsMasterEffect) || plugin is null)
        {
            return;
        }
        var ownerName = track?.Name ?? "Master";
        var ownerIdentifier = track?.Id ?? Synthesis.SequencerEngine.MasterEffectSlotOwner;
        if (PluginInstancePool.Live is not { } livePool)
        {
            SetStatus(StatusSeverity.Failure, $"the audio engine is not running, so {plugin.Name} is not loaded and has no editor to show");
            return;
        }
        try
        {
            var slotKey = effectIndex < 0 ? PluginInstancePool.InstrumentSlot(ownerIdentifier) : PluginInstancePool.EffectSlot(ownerIdentifier, effectIndex);
            SetStatus(StatusSeverity.Informational, $"opening {plugin.Name}…");
            if (await livePool.GetOrLoadAsync(slotKey, plugin) is not { } instance)
            {
                SetStatus(StatusSeverity.Failure, $"could not load {plugin.Name}: {livePool.LoadProblem(slotKey) ?? "it was replaced while loading"}");
                return;
            }
            var ownerWindow = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var editorProblem = await instance.OpenEditorAsync($"{plugin.Name} — {ownerName}", ownerWindow,
                new PluginEditorParameterSync(document, ownerIdentifier, effectIndex, plugin));
            SetStatus(editorProblem is null ? StatusSeverity.Success : StatusSeverity.Warning, editorProblem ?? $"opened {plugin.Name}");
        }
        catch (Exception editorRemediationOpportunity)
        {
            SetStatus(StatusSeverity.Failure, $"{plugin.Name}'s editor crashed on the way up: {editorRemediationOpportunity.Message}");
        }
    }

    private static bool IsPluginBundle(string droppedPath) =>
        droppedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).EndsWith(".vst3", StringComparison.OrdinalIgnoreCase);

    private static List<string> DroppedPluginBundles(DragPayload dragPayload) =>
        dragPayload.Kind == DragPayloadKind.Files
            ? dragPayload.FilePaths!.Where(droppedPath => IsPluginBundle(droppedPath) && (Directory.Exists(droppedPath) || File.Exists(droppedPath))).ToList()
            : new List<string>();

    /// <summary>A plugin's knobs as catalog specifications (0..1), so the AI sound designer can turn them too. Null for built-in devices.</summary>
    private static IReadOnlyList<Synthesis.ParameterSpecification>? PluginParameterSpecifications(PluginReference? pluginReference) =>
        pluginReference is null
            ? null
            : Vst3ParameterDirectory.TryGet(pluginReference)?
                .Where(parameter => parameter.IsUserFacing)
                .Take(32)
                .Select(parameter => new Synthesis.ParameterSpecification(parameter.Key, parameter.Title, 0, 1, parameter.DefaultValue, "%"))
                .ToList();

    private async Task LoadDroppedPluginsAsync(IReadOnlyList<string> droppedBundles, DropTarget dropTarget, int targetTrackIndex)
    {
        SetStatus(StatusSeverity.Informational, $"scanning {droppedBundles.Count} dropped plugin bundle(s)…");
        var scanProblems = new List<string>();
        var droppedPlugins = await Task.Run(() => Vst3PluginCatalog.Scan(droppedBundles, scanProblems));
        if (droppedPlugins.Count == 0)
        {
            SetStatus(StatusSeverity.Failure, scanProblems.Count > 0 ? $"could not load the plugin: {scanProblems[0]}" : "no VST3 plugin found in the dropped bundle");
            return;
        }

        var targetsExistingTrack = IsValidTrack(targetTrackIndex) && dropTarget.Kind != DropTargetKind.EmptyArea;
        var loadedNames = new List<string>();
        foreach (var bundleGroup in droppedPlugins.GroupBy(plugin => plugin.BundlePath))
        {
            // Shell bundles hold many plugins; a drop takes the first, the VST3 menus offer the rest.
            var droppedPlugin = bundleGroup.First();
            var pluginReference = PluginDeviceOperations.ToReference(droppedPlugin);
            if (droppedPlugin.IsInstrument)
            {
                if (targetsExistingTrack && dropTarget.Kind is not DropTargetKind.EffectDevice)
                {
                    var targetTrack = document.Project.Tracks[targetTrackIndex];
                    document.Edit($"loaded {droppedPlugin.Name} on {targetTrack.Name}",
                        _ => PluginDeviceOperations.LoadInstrument(targetTrack, pluginReference), EditScope.Structure);
                    document.Select(targetTrackIndex, document.SelectedSceneIndex);
                }
                else if (document.Project.Tracks.Count < ProjectAlignmentRemediator.MaximumTrackCount)
                {
                    document.Edit($"added the plugin {droppedPlugin.Name}", project =>
                        project.Tracks.Add(PluginDeviceOperations.CreateInstrumentTrack(project, pluginReference, document.SelectedSceneIndex)), EditScope.Structure);
                    document.Select(document.Project.Tracks.Count - 1, document.SelectedSceneIndex);
                }
            }
            else
            {
                var effectTrackIndex = IsValidTrack(targetTrackIndex) ? targetTrackIndex : document.SelectedTrackIndex;
                if (!IsValidTrack(effectTrackIndex))
                {
                    continue;
                }
                var effectTrack = document.Project.Tracks[effectTrackIndex];
                if (effectTrack.Effects.Count >= ProjectAlignmentRemediator.MaximumEffectsPerTrack)
                {
                    SetStatus(StatusSeverity.Warning, $"{effectTrack.Name}'s device chain is full. consolidate your signal flow.");
                    return;
                }
                var insertionIndex = dropTarget.Kind == DropTargetKind.EffectDevice ? Math.Clamp(dropTarget.Index, 0, effectTrack.Effects.Count) : effectTrack.Effects.Count;
                document.Edit($"added {droppedPlugin.Name} to {effectTrack.Name}",
                    _ => effectTrack.Effects.Insert(insertionIndex, PluginDeviceOperations.CreateEffect(pluginReference)), EditScope.Structure);
                document.Select(effectTrackIndex, document.SelectedSceneIndex);
            }
            loadedNames.Add(bundleGroup.Count() > 1 ? $"{droppedPlugin.Name} (first of {bundleGroup.Count()} in the bundle)" : droppedPlugin.Name);
        }
        SetStatus(StatusSeverity.Success, $"loaded {string.Join(", ", loadedNames)}");
    }
}
