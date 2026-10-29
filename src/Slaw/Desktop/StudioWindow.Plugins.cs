using SlopstackLabs.Slaw.Plugins;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>VST3 bundles dropped from the file manager: instruments onto tracks, effects into chains.</summary>
internal sealed partial class StudioWindow
{
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
