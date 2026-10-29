using System.Collections.Concurrent;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// What knobs a plugin has, by bundle and class. Answered by briefly
/// instantiating the plugin on the host thread, once per plugin per session —
/// the device view, the MCP listing and the AI all ask the same question.
/// </summary>
internal static class Vst3ParameterDirectory
{
    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<Vst3ParameterDescription>>> ParameterLists = new(StringComparer.Ordinal);

    private static string CacheKey(PluginReference pluginReference) => $"{pluginReference.Path}|{pluginReference.ClassId.ToUpperInvariant()}";

    /// <summary>Starts (or recalls) the parameter probe for a plugin.</summary>
    public static Task<IReadOnlyList<Vst3ParameterDescription>> GetAsync(PluginReference pluginReference) =>
        ParameterLists.GetOrAdd(CacheKey(pluginReference), _ => Task.Run(() => Probe(pluginReference)));

    /// <summary>The parameters if already known; otherwise null (and the probe is started).</summary>
    public static IReadOnlyList<Vst3ParameterDescription>? TryGet(PluginReference pluginReference)
    {
        var parameterTask = GetAsync(pluginReference);
        return parameterTask.IsCompletedSuccessfully ? parameterTask.Result : null;
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the parameters. Empty if the plugin fails to load.</summary>
    public static IReadOnlyList<Vst3ParameterDescription> Get(PluginReference pluginReference, TimeSpan timeout)
    {
        var parameterTask = GetAsync(pluginReference);
        try
        {
            return parameterTask.Wait(timeout) ? parameterTask.Result : Array.Empty<Vst3ParameterDescription>();
        }
        catch (AggregateException)
        {
            return Array.Empty<Vst3ParameterDescription>();
        }
    }

    /// <summary>The load error for a plugin whose probe failed, if any.</summary>
    public static string? ProbeProblem(PluginReference pluginReference) =>
        ParameterLists.TryGetValue(CacheKey(pluginReference), out var parameterTask) && parameterTask.IsFaulted
            ? parameterTask.Exception?.InnerException?.Message
            : null;

    public static void Forget(PluginReference pluginReference) => ParameterLists.TryRemove(CacheKey(pluginReference), out _);

    private static IReadOnlyList<Vst3ParameterDescription> Probe(PluginReference pluginReference)
    {
        var probeInstance = Vst3PluginInstance.Create(
            new PluginReference { Path = pluginReference.Path, ClassId = pluginReference.ClassId, Name = pluginReference.Name },
            sampleRate: 44100, offline: true);
        try
        {
            return probeInstance.Parameters;
        }
        finally
        {
            probeInstance.Dispose();
        }
    }
}
