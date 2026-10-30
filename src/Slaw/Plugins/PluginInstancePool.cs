using System.Collections.Concurrent;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// The plugin instances one <see cref="Synthesis.SequencerEngine"/> plays, one
/// per device slot ("&lt;trackId&gt;/instrument", "&lt;trackId&gt;/fx&lt;index&gt;").
///
/// The audio thread asks with <see cref="TryGet"/> and gets null until the
/// plugin host thread has finished loading — the same bargain the sampler
/// strikes with its WAV files. Offline pools load synchronously instead: a
/// bounce is allowed to wait, it is not allowed to skip the synth.
/// </summary>
internal sealed class PluginInstancePool
{
    private static PluginInstancePool? livePool;

    private readonly int sampleRate;
    private readonly bool offline;
    private readonly ConcurrentDictionary<string, PoolEntry> entriesBySlot = new(StringComparer.Ordinal);

    public PluginInstancePool(int sampleRate, bool offline)
    {
        this.sampleRate = sampleRate;
        this.offline = offline;
        if (!offline)
        {
            Volatile.Write(ref livePool, this);
        }
    }

    /// <summary>The pool behind the studio's live engine, whose plugins' state is captured on save.</summary>
    public static PluginInstancePool? Live => Volatile.Read(ref livePool);

    public static string InstrumentSlot(string trackIdentifier) => trackIdentifier + "/instrument";

    public static string EffectSlot(string trackIdentifier, int effectIndex) => $"{trackIdentifier}/fx{effectIndex}";

    /// <summary>Load failures by slot, for the device view to display instead of silence.</summary>
    public string? LoadProblem(string slotKey) => entriesBySlot.TryGetValue(slotKey, out var entry) ? entry.Problem : null;

    /// <summary>
    /// The instance for a slot, or null while it loads (or if it failed). A slot
    /// whose plugin changed is torn down and reloaded.
    /// </summary>
    public Vst3PluginInstance? TryGet(string slotKey, PluginReference? pluginReference)
    {
        if (pluginReference is null || string.IsNullOrEmpty(pluginReference.Path))
        {
            Release(slotKey);
            return null;
        }
        if (entriesBySlot.TryGetValue(slotKey, out var existingEntry) && existingEntry.Reference.IsSamePlugin(pluginReference))
        {
            return existingEntry.Instance;
        }
        var newEntry = new PoolEntry(new PluginReference
        {
            Path = pluginReference.Path,
            ClassId = pluginReference.ClassId,
            Name = pluginReference.Name,
            State = pluginReference.State,
        });
        entriesBySlot[slotKey] = newEntry;
        existingEntry?.Retire();
        if (offline)
        {
            newEntry.Load(sampleRate, offline: true);
            return newEntry.Instance;
        }
        _ = Task.Run(() => newEntry.Load(sampleRate, offline: false));
        return null;
    }

    /// <summary>
    /// The instance for a slot, loading it now if the engine has not needed it
    /// yet (an instrument loads at its first note). Null if it could not load;
    /// <see cref="LoadProblem"/> says why.
    /// </summary>
    public Task<Vst3PluginInstance?> GetOrLoadAsync(string slotKey, PluginReference pluginReference)
    {
        TryGet(slotKey, pluginReference);
        return entriesBySlot.TryGetValue(slotKey, out var entry) ? entry.Loaded : Task.FromResult<Vst3PluginInstance?>(null);
    }

    /// <summary>The ready instance in a slot, without loading anything.</summary>
    public Vst3PluginInstance? Peek(string slotKey, PluginReference? pluginReference) =>
        entriesBySlot.TryGetValue(slotKey, out var entry) && entry.Reference.IsSamePlugin(pluginReference) ? entry.Instance : null;

    /// <summary>Whatever is loaded in a slot right now, whichever plugin it is.</summary>
    public Vst3PluginInstance? Current(string slotKey) => entriesBySlot.TryGetValue(slotKey, out var entry) ? entry.Instance : null;

    /// <summary>Releases every slot a track owns (its instrument and its effects).</summary>
    public void ReleaseTrack(string trackIdentifier)
    {
        var trackPrefix = trackIdentifier + "/";
        foreach (var slotKey in entriesBySlot.Keys)
        {
            if (slotKey.StartsWith(trackPrefix, StringComparison.Ordinal))
            {
                Release(slotKey);
            }
        }
    }

    public void Release(string slotKey)
    {
        if (entriesBySlot.TryRemove(slotKey, out var removedEntry))
        {
            removedEntry.Retire();
        }
    }

    /// <summary>Releases every slot whose key is not in <paramref name="survivingSlots"/>.</summary>
    public void ReleaseAllExcept(IReadOnlySet<string> survivingSlots)
    {
        foreach (var slotKey in entriesBySlot.Keys)
        {
            if (!survivingSlots.Contains(slotKey))
            {
                Release(slotKey);
            }
        }
    }

    public void ReleaseAll()
    {
        foreach (var slotKey in entriesBySlot.Keys)
        {
            Release(slotKey);
        }
        if (!offline)
        {
            Interlocked.CompareExchange(ref livePool, null, this);
        }
    }

    public IEnumerable<Vst3PluginInstance> Instances => entriesBySlot.Values.Select(entry => entry.Instance).OfType<Vst3PluginInstance>();

    /// <summary>
    /// Copies each live plugin's state into <paramref name="project"/> (normally
    /// the clone about to be written to disk). Plugins that are not loaded keep
    /// whatever state the project already carried.
    /// </summary>
    public static void CaptureStates(SlawProject project)
    {
        if (Live is not { } pool)
        {
            return;
        }
        foreach (var track in project.Tracks)
        {
            if (track.Instrument == DeviceTypes.Vst3 && pool.Peek(InstrumentSlot(track.Id), track.Plugin) is { } instrumentInstance)
            {
                track.Plugin!.State = TryCapture(instrumentInstance) ?? track.Plugin.State;
            }
            for (var effectIndex = 0; effectIndex < track.Effects.Count; effectIndex++)
            {
                var effectDevice = track.Effects[effectIndex];
                if (effectDevice.Type == DeviceTypes.Vst3 && pool.Peek(EffectSlot(track.Id, effectIndex), effectDevice.Plugin) is { } effectInstance)
                {
                    effectDevice.Plugin!.State = TryCapture(effectInstance) ?? effectDevice.Plugin.State;
                }
            }
        }
        for (var effectIndex = 0; effectIndex < project.Master.Effects.Count; effectIndex++)
        {
            var masterEffect = project.Master.Effects[effectIndex];
            if (masterEffect.Type == DeviceTypes.Vst3 &&
                pool.Peek(EffectSlot(Synthesis.SequencerEngine.MasterEffectSlotOwner, effectIndex), masterEffect.Plugin) is { } masterInstance)
            {
                masterEffect.Plugin!.State = TryCapture(masterInstance) ?? masterEffect.Plugin.State;
            }
        }
    }

    private static string? TryCapture(Vst3PluginInstance instance)
    {
        try
        {
            return instance.CaptureState();
        }
        catch (Exception stateRemediationOpportunity) when (stateRemediationOpportunity is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private sealed class PoolEntry
    {
        private readonly TaskCompletionSource<Vst3PluginInstance?> loadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Vst3PluginInstance? instance;
        private bool retired;

        public PoolEntry(PluginReference reference) => Reference = reference;

        public PluginReference Reference { get; }

        public Vst3PluginInstance? Instance => Volatile.Read(ref instance);

        /// <summary>Completes when loading finishes: the instance, or null if it failed or was retired first.</summary>
        public Task<Vst3PluginInstance?> Loaded => loadCompletion.Task;

        public string? Problem { get; private set; }

        public void Load(int sampleRate, bool offline)
        {
            try
            {
                var loadedInstance = Vst3PluginInstance.Create(Reference, sampleRate, offline);
                lock (this)
                {
                    if (retired)
                    {
                        loadedInstance.Dispose();
                        return;
                    }
                    Volatile.Write(ref instance, loadedInstance);
                }
            }
            catch (Exception loadRemediationOpportunity)
            {
                Problem = loadRemediationOpportunity.Message;
            }
            finally
            {
                loadCompletion.TrySetResult(Instance);
            }
        }

        public void Retire()
        {
            lock (this)
            {
                retired = true;
                Interlocked.Exchange(ref instance, null)?.Dispose();
            }
            loadCompletion.TrySetResult(null);
        }
    }
}

/// <summary>Device type names shared by instruments and effects.</summary>
internal static class DeviceTypes
{
    public const string Vst3 = "vst3";
}
