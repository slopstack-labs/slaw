using Avalonia.Threading;
using SlopstackLabs.Slaw.Plugins;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// Carries edits made in a plugin's own editor into the device's parameter
/// table, so the knob panel, the AI, automation, undo and the saved project
/// agree with what the plugin shows. One undo step per knob gesture, or per
/// pause for plugins that edit without announcing gestures.
///
/// Edits arrive on the plugin host thread and are applied on the UI thread.
/// The device is looked up by track id and slot on every write, because undo
/// and redo replace the project's objects underneath an open editor.
/// </summary>
internal sealed class PluginEditorParameterSync : IVst3EditorObserver
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(300);

    private readonly StudioDocument document;
    private readonly string trackIdentifier;
    private readonly int effectIndex;
    private readonly PluginReference plugin;
    private readonly Dictionary<uint, double> pendingEdits = new();
    private readonly Dictionary<uint, double> valuesBeforeGesture = new();
    private readonly DispatcherTimer quietTimer;

    /// <param name="effectIndex">The device chain index, or -1 for the track's instrument.</param>
    public PluginEditorParameterSync(StudioDocument document, string trackIdentifier, int effectIndex, PluginReference plugin)
    {
        this.document = document;
        this.trackIdentifier = trackIdentifier;
        this.effectIndex = effectIndex;
        this.plugin = plugin;
        quietTimer = new DispatcherTimer { Interval = QuietPeriod };
        quietTimer.Tick += (_, _) => FlushPendingEdits();
    }

    // ── IVst3EditorObserver (plugin host thread) ──────────────────────────────

    public void EditGestureBegan(uint parameterId, double normalizedValueBefore) =>
        Dispatcher.UIThread.Post(() => valuesBeforeGesture.TryAdd(parameterId, normalizedValueBefore));

    public void ParameterEdited(uint parameterId, double normalizedValue) => Dispatcher.UIThread.Post(() =>
    {
        pendingEdits[parameterId] = normalizedValue;
        quietTimer.Stop();
        quietTimer.Start();
    });

    public void EditGestureEnded(uint parameterId) => Dispatcher.UIThread.Post(FlushPendingEdits);

    public void ParameterValuesReloaded(IReadOnlyDictionary<uint, double> normalizedValues) => Dispatcher.UIThread.Post(() =>
    {
        // A preset load supersedes whatever was still waiting to be written.
        quietTimer.Stop();
        pendingEdits.Clear();
        valuesBeforeGesture.Clear();
        if (ParameterTable() is not { } parameterTable)
        {
            return;
        }
        // Only keys slaw already tracks: the preset itself lives in the plugin's saved state.
        var changedValues = normalizedValues
            .Select(entry => (Key: Vst3ParameterDescription.ParameterKey(entry.Key), Value: Math.Clamp(entry.Value, 0, 1)))
            .Where(entry => parameterTable.TryGetValue(entry.Key, out var currentValue) && Math.Abs(currentValue - entry.Value) > 1e-6)
            .ToList();
        Commit($"loaded a preset in {plugin.Name}", parameterTable, changedValues);
    });

    public void EditorClosed() => Dispatcher.UIThread.Post(FlushPendingEdits);

    // ── UI thread ─────────────────────────────────────────────────────────────

    private Dictionary<string, double>? ParameterTable()
    {
        if (trackIdentifier == Synthesis.SequencerEngine.MasterEffectSlotOwner)
        {
            var masterEffects = document.Project.Master.Effects;
            return effectIndex >= 0 && effectIndex < masterEffects.Count && masterEffects[effectIndex] is { Type: DeviceTypes.Vst3 } masterEffect && plugin.IsSamePlugin(masterEffect.Plugin)
                ? masterEffect.Parameters
                : null;
        }
        var track = document.Project.Tracks.FirstOrDefault(candidate => candidate.Id == trackIdentifier);
        if (track is null)
        {
            return null;
        }
        if (effectIndex < 0)
        {
            return track.Instrument == DeviceTypes.Vst3 && plugin.IsSamePlugin(track.Plugin) ? track.InstrumentParameters : null;
        }
        return effectIndex < track.Effects.Count && track.Effects[effectIndex] is { Type: DeviceTypes.Vst3 } effectDevice && plugin.IsSamePlugin(effectDevice.Plugin)
            ? effectDevice.Parameters
            : null;
    }

    private void FlushPendingEdits()
    {
        quietTimer.Stop();
        if (pendingEdits.Count == 0 || ParameterTable() is not { } parameterTable)
        {
            pendingEdits.Clear();
            valuesBeforeGesture.Clear();
            return;
        }
        var editedValues = pendingEdits
            .Select(entry => (Key: Vst3ParameterDescription.ParameterKey(entry.Key), Value: entry.Value))
            .Where(entry => !parameterTable.TryGetValue(entry.Key, out var currentValue) || Math.Abs(currentValue - entry.Value) > 1e-6)
            .ToList();
        // A knob slaw did not track yet enters the table at its old value first, so undo turns it back rather than forgetting it.
        foreach (var (parameterId, valueBefore) in valuesBeforeGesture)
        {
            parameterTable.TryAdd(Vst3ParameterDescription.ParameterKey(parameterId), valueBefore);
        }
        pendingEdits.Clear();
        valuesBeforeGesture.Clear();
        Commit($"adjusted {plugin.Name} in its editor", parameterTable, editedValues);
    }

    private void Commit(string description, Dictionary<string, double> parameterTable, IReadOnlyList<(string Key, double Value)> changedValues)
    {
        if (changedValues.Count == 0)
        {
            return;
        }
        document.BeginEdit(description);
        foreach (var (parameterKey, normalizedValue) in changedValues)
        {
            parameterTable[parameterKey] = normalizedValue;
        }
        document.CommitEdit(description, notifyCopilot: false);
    }
}
