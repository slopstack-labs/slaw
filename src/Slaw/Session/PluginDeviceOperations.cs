using System.Text.Json.Nodes;
using SlopstackLabs.Slaw.Plugins;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Putting VST3 plugins into a project: as a track's instrument, as an effect,
/// or as a brand-new track. Shared by the device view, drag and drop, patches
/// and MCP, so a plugin arrives the same way whoever brought it.
/// </summary>
internal static class PluginDeviceOperations
{
    public static PluginReference ToReference(Vst3PluginDescriptor plugin) => new()
    {
        Path = plugin.BundlePath,
        ClassId = plugin.ClassId,
        Name = plugin.Name,
    };

    /// <summary>Turns a track into a VST3 instrument track. Built-in knob values are dropped: they mean nothing to the plugin.</summary>
    public static void LoadInstrument(SlawTrack track, PluginReference pluginReference)
    {
        PatternEditingOperations.ChangeInstrument(track, DeviceTypes.Vst3);
        if (!pluginReference.IsSamePlugin(track.Plugin))
        {
            track.InstrumentParameters.Clear();
        }
        track.Plugin = pluginReference;
    }

    public static EffectDevice CreateEffect(PluginReference pluginReference) => new()
    {
        Type = DeviceTypes.Vst3,
        Plugin = pluginReference,
    };

    /// <summary>A new track playing a plugin instrument, with an empty clip in <paramref name="sceneIndex"/>.</summary>
    public static SlawTrack CreateInstrumentTrack(SlawProject project, PluginReference pluginReference, int sceneIndex, string? trackName = null)
    {
        var createdTrack = new SlawTrack
        {
            Name = trackName ?? (pluginReference.Name.Length > 24 ? pluginReference.Name[..24] : pluginReference.Name),
            Instrument = DeviceTypes.Vst3,
            Plugin = pluginReference,
            Gain = 0.7,
            Clips = Enumerable.Repeat<SlawClip?>(null, project.Scenes.Count).ToList(),
        };
        if (sceneIndex >= 0 && sceneIndex < createdTrack.Clips.Count)
        {
            createdTrack.Clips[sceneIndex] = SlawClip.Empty(project.Scenes[sceneIndex], project.Steps);
        }
        return createdTrack;
    }

    /// <summary>A short label for a device: the plugin's name for VST3 devices, the catalog name otherwise.</summary>
    public static string DeviceName(SlawTrack track) =>
        track.Instrument == DeviceTypes.Vst3 && track.Plugin is { } plugin
            ? plugin.Name
            : Synthesis.DeviceCatalog.InstrumentDisplayName(track.Instrument);

    public static string DeviceName(EffectDevice effectDevice) =>
        effectDevice.Type == DeviceTypes.Vst3 && effectDevice.Plugin is { } plugin
            ? plugin.Name
            : Synthesis.DeviceCatalog.EffectDisplayName(effectDevice.Type);

    /// <summary>
    /// Reads a plugin reference from JSON: {"path", "classId", "name"?} or a
    /// plain string naming a scanned plugin (or its bundle path). Scanned
    /// plugins fill in whatever the caller left out.
    /// </summary>
    public static PluginReference? ParseReference(JsonNode? pluginNode)
    {
        switch (pluginNode)
        {
            case JsonValue pluginValue when pluginValue.TryGetValue<string>(out var pluginText) && !string.IsNullOrWhiteSpace(pluginText):
                return Vst3PluginCatalog.Find(pluginText.Trim()) is { } namedPlugin ? ToReference(namedPlugin) : null;
            case JsonObject pluginObject:
                {
                    var bundlePath = ReadText(pluginObject["path"]);
                    var classIdentifier = ReadText(pluginObject["classId"]) ?? ReadText(pluginObject["cid"]);
                    var pluginName = ReadText(pluginObject["name"]);
                    if (bundlePath is not null && classIdentifier is not null && Vst3Abi.ParseUid(classIdentifier) is not null)
                    {
                        var scannedPlugin = Vst3PluginCatalog.Find(bundlePath, classIdentifier);
                        return new PluginReference
                        {
                            Path = Path.GetFullPath(bundlePath),
                            ClassId = classIdentifier.ToUpperInvariant(),
                            Name = pluginName ?? scannedPlugin?.Name ?? Path.GetFileNameWithoutExtension(bundlePath),
                            State = ReadText(pluginObject["state"]),
                        };
                    }
                    var foundPlugin = Vst3PluginCatalog.Find(bundlePath ?? pluginName, classIdentifier);
                    return foundPlugin is null ? null : ToReference(foundPlugin);
                }
            default:
                return null;
        }
    }

    private static string? ReadText(JsonNode? textNode) =>
        textNode is JsonValue textValue && textValue.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
