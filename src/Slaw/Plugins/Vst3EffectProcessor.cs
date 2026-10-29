using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// A VST3 effect in a track's device chain. The engine binds it to its pool
/// slot every block; until the plugin has loaded, audio passes through untouched.
/// </summary>
internal sealed class Vst3EffectProcessor : EffectProcessor
{
    private PluginInstancePool? pluginPool;
    private string slotKey = string.Empty;
    private PluginReference? pluginReference;

    public Vst3EffectProcessor()
        : base(DeviceTypes.Vst3)
    {
    }

    public void Bind(PluginInstancePool boundPool, string boundSlotKey, PluginReference? boundReference)
    {
        pluginPool = boundPool;
        slotKey = boundSlotKey;
        pluginReference = boundReference;
    }

    public override void Process(Span<float> monoBlock, IReadOnlyDictionary<string, double> parameterValues, EffectProcessingContext processingContext)
    {
        if (pluginPool?.TryGet(slotKey, pluginReference) is not { } pluginEffect)
        {
            return;
        }
        pluginEffect.SetTransport(60.0 / (processingContext.StepDurationSeconds * 4), playing: true);
        pluginEffect.Process(monoBlock, parameterValues);
    }
}
