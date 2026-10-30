using System.Globalization;
using SlopstackLabs.Slaw.Plugins;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Session;

/// <summary>One breakpoint of a clip envelope: at this (fractional) step, the parameter is this value.</summary>
internal sealed class AutomationPoint
{
    public double Step { get; set; }
    public double Value { get; set; }
}

/// <summary>What an envelope controls, in the units a knob would show.</summary>
internal sealed record AutomationTarget(string Key, string Label, ParameterSpecification Specification, bool AppliesAtNoteStart);

/// <summary>
/// Clip envelopes: per-clip breakpoint curves that move a track's parameters
/// while the clip plays, in session and arrangement alike. Targets are
/// addressed by key so a language model can write them without a diagram:
/// <list type="bullet">
/// <item><c>gain</c>, <c>pan</c>: the mixer, continuously.</item>
/// <item><c>pitch</c>: semitones; bends every sounding voice on the track, like a tape machine with a quarterly target.</item>
/// <item><c>fx.&lt;index&gt;.&lt;key&gt;</c>: a device-chain parameter, continuously (filter sweeps live here).</item>
/// <item><c>inst.&lt;key&gt;</c>: an instrument parameter, sampled when each note starts.</item>
/// </list>
/// Values outside a clip's breakpoints hold the nearest one; between them, they move linearly.
/// </summary>
internal static class ClipAutomation
{
    public const string GainTarget = "gain";
    public const string PanTarget = "pan";
    public const string PitchTarget = "pitch";
    public const string EffectTargetPrefix = "fx.";
    public const string InstrumentTargetPrefix = "inst.";

    public static readonly ParameterSpecification GainSpecification = new("gain", "Gain", 0, 1.5, 0.8, "");
    public static readonly ParameterSpecification PanSpecification = new("pan", "Pan", -1, 1, 0, "");
    public static readonly ParameterSpecification PitchSpecification = new("pitch", "Pitch", -24, 24, 0, "st");

    /// <summary>Plugin parameters (<c>p&lt;id&gt;</c>) are normalized; everything else unknown is too.</summary>
    private static ParameterSpecification NormalizedSpecification(string parameterKey) => new(parameterKey, parameterKey, 0, 1, 0, "");

    /// <summary>Every target the track can be automated on, mixer first, then instrument, then effects.</summary>
    public static IReadOnlyList<AutomationTarget> TargetsFor(SlawTrack track)
    {
        var availableTargets = new List<AutomationTarget>
        {
            new(GainTarget, "Mixer · Gain", GainSpecification, false),
            new(PanTarget, "Mixer · Pan", PanSpecification, false),
            new(PitchTarget, "Track · Pitch bend", PitchSpecification, false),
        };
        if (track.Instrument == DeviceTypes.Vst3 && track.Plugin is { } instrumentPlugin)
        {
            // A plugin instrument plays continuously, so its envelopes move continuously too.
            foreach (var parameterSpecification in PluginParameterSpecifications(instrumentPlugin, track.InstrumentParameters))
            {
                availableTargets.Add(new(InstrumentTargetPrefix + parameterSpecification.Key, $"{instrumentPlugin.Name} · {parameterSpecification.Label}", parameterSpecification, false));
            }
        }
        else
        {
            var instrumentName = DeviceCatalog.InstrumentDisplayName(track.Instrument);
            foreach (var parameterSpecification in DeviceCatalog.InstrumentParametersFor(track.Instrument))
            {
                availableTargets.Add(new(InstrumentTargetPrefix + parameterSpecification.Key, $"{instrumentName} · {parameterSpecification.Label} (per note)", parameterSpecification, true));
            }
        }
        for (var effectIndex = 0; effectIndex < track.Effects.Count; effectIndex++)
        {
            var effectDevice = track.Effects[effectIndex];
            var effectName = effectDevice.Plugin?.Name is { Length: > 0 } pluginName ? pluginName : DeviceCatalog.EffectDisplayName(effectDevice.Type);
            var catalogParameters = DeviceCatalog.EffectParametersFor(effectDevice.Type);
            var parameterSpecifications = catalogParameters.Count > 0
                ? catalogParameters
                : effectDevice.Plugin is { } effectPlugin
                    ? PluginParameterSpecifications(effectPlugin, effectDevice.Parameters)
                    : effectDevice.Parameters.Keys.Order(StringComparer.Ordinal).Select(NormalizedSpecification).ToList();
            foreach (var parameterSpecification in parameterSpecifications)
            {
                availableTargets.Add(new(
                    FormattableString.Invariant($"{EffectTargetPrefix}{effectIndex}.{parameterSpecification.Key}"),
                    $"{effectIndex + 1}. {effectName} · {parameterSpecification.Label}",
                    parameterSpecification,
                    false));
            }
        }
        return availableTargets;
    }

    /// <summary>
    /// A plugin's user-facing knobs (normalized 0..1) from the parameter cache, without
    /// loading anything; plus any stored p&lt;id&gt; keys the cache has not seen yet.
    /// </summary>
    private static List<ParameterSpecification> PluginParameterSpecifications(PluginReference pluginReference, Dictionary<string, double> storedValues)
    {
        // Peek, never probe: this runs during remediation, and loading a plugin there would let it
        // print into `slaw mcp`'s protocol stream before stdout is claimed.
        var parameterSpecifications = (Vst3ParameterDirectory.Peek(pluginReference) ?? Array.Empty<Vst3ParameterDescription>())
            .Where(parameter => parameter.IsUserFacing)
            .Select(parameter => new ParameterSpecification(parameter.Key, parameter.Title, 0, 1, parameter.DefaultValue, ""))
            .ToList();
        parameterSpecifications.AddRange(storedValues.Keys
            .Where(storedKey => parameterSpecifications.All(parameterSpecification => parameterSpecification.Key != storedKey))
            .Order(StringComparer.Ordinal)
            .Select(NormalizedSpecification));
        return parameterSpecifications;
    }

    /// <summary>Resolves a target key against a track, or null if it addresses nothing that exists.</summary>
    public static AutomationTarget? ResolveTarget(SlawTrack track, string targetKey) =>
        TargetsFor(track).FirstOrDefault(target => target.Key == targetKey) ??
        (TryParseEffectTarget(targetKey, out var effectIndex, out var parameterKey) && effectIndex < track.Effects.Count
            ? new AutomationTarget(targetKey, targetKey, NormalizedSpecification(parameterKey), false)
            : targetKey.StartsWith(InstrumentTargetPrefix, StringComparison.Ordinal) && targetKey.Length > InstrumentTargetPrefix.Length
                ? new AutomationTarget(targetKey, targetKey, NormalizedSpecification(targetKey[InstrumentTargetPrefix.Length..]), track.Instrument != DeviceTypes.Vst3)
                : null);

    public static bool TryParseEffectTarget(string targetKey, out int effectIndex, out string parameterKey)
    {
        effectIndex = -1;
        parameterKey = string.Empty;
        if (!targetKey.StartsWith(EffectTargetPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        var separatorIndex = targetKey.IndexOf('.', EffectTargetPrefix.Length);
        if (separatorIndex < 0 || separatorIndex == targetKey.Length - 1 ||
            !int.TryParse(targetKey.AsSpan(EffectTargetPrefix.Length, separatorIndex - EffectTargetPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out effectIndex))
        {
            return false;
        }
        parameterKey = targetKey[(separatorIndex + 1)..];
        return true;
    }

    /// <summary>The envelope's value at a clip position (in steps). Points must be sorted by step.</summary>
    public static double Evaluate(IReadOnlyList<AutomationPoint> sortedPoints, double clipPosition)
    {
        if (sortedPoints.Count == 0)
        {
            return 0;
        }
        if (clipPosition <= sortedPoints[0].Step)
        {
            return sortedPoints[0].Value;
        }
        for (var pointIndex = 1; pointIndex < sortedPoints.Count; pointIndex++)
        {
            var nextPoint = sortedPoints[pointIndex];
            if (clipPosition <= nextPoint.Step)
            {
                var previousPoint = sortedPoints[pointIndex - 1];
                var segmentLength = nextPoint.Step - previousPoint.Step;
                return segmentLength <= 0
                    ? nextPoint.Value
                    : previousPoint.Value + (nextPoint.Value - previousPoint.Value) * (clipPosition - previousPoint.Step) / segmentLength;
            }
        }
        return sortedPoints[^1].Value;
    }

    /// <summary>Sorts, clamps to the clip and the target's range, and drops exact duplicates. Returns the tidied list.</summary>
    public static List<AutomationPoint> Tidy(IEnumerable<AutomationPoint> points, int clipLength, ParameterSpecification? specification)
    {
        return points
            .Where(point => point is not null && double.IsFinite(point.Step) && double.IsFinite(point.Value))
            .Select(point => new AutomationPoint
            {
                Step = Math.Clamp(point.Step, 0, Math.Max(0, clipLength)),
                Value = specification is null ? point.Value : Math.Clamp(point.Value, specification.Minimum, specification.Maximum),
            })
            .OrderBy(point => point.Step)
            .DistinctBy(point => (point.Step, point.Value))
            .ToList();
    }

    /// <summary>Removing effect <paramref name="removedEffectIndex"/> drops its lanes and shifts later ones down.</summary>
    public static void ForgetEffectSlot(SlawTrack track, int removedEffectIndex) =>
        RemapEffectSlots(track, effectIndex => effectIndex == removedEffectIndex ? null : effectIndex > removedEffectIndex ? effectIndex - 1 : effectIndex);

    /// <summary>Inserting an effect at <paramref name="insertedEffectIndex"/> shifts the lanes at and after it up.</summary>
    public static void MakeRoomForEffectSlot(SlawTrack track, int insertedEffectIndex) =>
        RemapEffectSlots(track, effectIndex => effectIndex >= insertedEffectIndex ? effectIndex + 1 : effectIndex);

    /// <summary>Moving an effect within the chain takes its lanes along and shifts the ones in between.</summary>
    public static void MoveEffectSlot(SlawTrack track, int sourceEffectIndex, int destinationEffectIndex) =>
        RemapEffectSlots(track, effectIndex =>
            effectIndex == sourceEffectIndex ? destinationEffectIndex
            : sourceEffectIndex < destinationEffectIndex && effectIndex > sourceEffectIndex && effectIndex <= destinationEffectIndex ? effectIndex - 1
            : sourceEffectIndex > destinationEffectIndex && effectIndex >= destinationEffectIndex && effectIndex < sourceEffectIndex ? effectIndex + 1
            : effectIndex);

    /// <summary>
    /// Keeps envelopes attached to their devices when the chain changes shape.
    /// <paramref name="remapEffectIndex"/> returns a lane's new effect index, or null to drop it.
    /// </summary>
    public static void RemapEffectSlots(SlawTrack track, Func<int, int?> remapEffectIndex)
    {
        foreach (var clip in track.Clips)
        {
            if (clip?.Automation is not { Count: > 0 } envelopes)
            {
                continue;
            }
            var remappedEnvelopes = new Dictionary<string, List<AutomationPoint>>();
            foreach (var (targetKey, points) in envelopes)
            {
                if (!TryParseEffectTarget(targetKey, out var effectIndex, out var parameterKey))
                {
                    remappedEnvelopes[targetKey] = points;
                }
                else if (remapEffectIndex(effectIndex) is { } remappedIndex)
                {
                    remappedEnvelopes[FormattableString.Invariant($"{EffectTargetPrefix}{remappedIndex}.{parameterKey}")] = points;
                }
            }
            clip.Automation = remappedEnvelopes.Count > 0 ? remappedEnvelopes : null;
        }
    }

    /// <summary>One-line rendering for listings: "fx.0.cutoff 0:200 → 8:4000 → 16:200".</summary>
    public static string Describe(string targetKey, IReadOnlyList<AutomationPoint> points) =>
        $"{targetKey} " + string.Join(" → ", points.Select(point => string.Create(CultureInfo.InvariantCulture, $"{point.Step:0.##}:{point.Value:0.###}")));
}
