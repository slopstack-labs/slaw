using System.Globalization;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>Describes one automatable device parameter: its range, default, and how to show it.</summary>
internal sealed record ParameterSpecification(
    string Key,
    string Label,
    double Minimum,
    double Maximum,
    double Default,
    string Unit,
    bool Logarithmic = false,
    bool Integer = false)
{
    public double Clamp(double candidateValue)
    {
        if (double.IsNaN(candidateValue) || double.IsInfinity(candidateValue))
        {
            return Default;
        }
        var clampedValue = Math.Clamp(candidateValue, Minimum, Maximum);
        return Integer ? Math.Round(clampedValue) : clampedValue;
    }

    public string Format(double parameterValue) => Unit switch
    {
        "Hz" when parameterValue >= 1000 => string.Create(CultureInfo.InvariantCulture, $"{parameterValue / 1000:0.0} kHz"),
        "Hz" => string.Create(CultureInfo.InvariantCulture, $"{parameterValue:0} Hz"),
        "ms" when parameterValue >= 1000 => string.Create(CultureInfo.InvariantCulture, $"{parameterValue / 1000:0.00} s"),
        "ms" => string.Create(CultureInfo.InvariantCulture, $"{parameterValue:0} ms"),
        "toggle" => parameterValue >= 0.5 ? "on" : "off",
        "note" => Session.NotationResolver.FormatMidiNote((int)Math.Round(parameterValue)),
        "%" => string.Create(CultureInfo.InvariantCulture, $"{parameterValue * 100:0}%"),
        "st" => string.Create(CultureInfo.InvariantCulture, $"{parameterValue:+0;-0;0} st"),
        "ct" => string.Create(CultureInfo.InvariantCulture, $"{parameterValue:+0;-0;0} ct"),
        "oct" => string.Create(CultureInfo.InvariantCulture, $"{parameterValue:+0;-0;0} oct"),
        "×" => string.Create(CultureInfo.InvariantCulture, $"{parameterValue:0.00}×"),
        "" when Integer => string.Create(CultureInfo.InvariantCulture, $"{parameterValue:0}"),
        "" => string.Create(CultureInfo.InvariantCulture, $"{parameterValue:0.00}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{parameterValue:0.##} {Unit}"),
    };
}

/// <summary>
/// The registry of every instrument and audio effect slaw ships, and the knobs
/// each one exposes. Both the device view and the AI autopilot read from here,
/// so neither can invent a parameter the engine does not understand.
/// </summary>
internal static class DeviceCatalog
{
    // ── Instruments ───────────────────────────────────────────────────────────

    private static readonly ParameterSpecification Humanize =
        new("humanize", "AI Humanize", 0, 1, 0, "%");

    private static readonly IReadOnlyList<ParameterSpecification> OscillatorParameters = new[]
    {
        new ParameterSpecification("attack", "Attack", 1, 2000, 4, "ms", Logarithmic: true),
        new ParameterSpecification("decay", "Decay", 5, 3000, 90, "ms", Logarithmic: true),
        new ParameterSpecification("sustain", "Sustain", 0, 1, 0.7, "%"),
        new ParameterSpecification("release", "Release", 5, 4000, 70, "ms", Logarithmic: true),
        new ParameterSpecification("cutoff", "Filter", 80, 20000, 20000, "Hz", Logarithmic: true),
        new ParameterSpecification("octave", "Octave", -3, 3, 0, "oct", Integer: true),
        new ParameterSpecification("detune", "Detune", -100, 100, 0, "ct"),
        Humanize,
    };

    private static readonly IReadOnlyList<ParameterSpecification> PluckParameters = new[]
    {
        new ParameterSpecification("length", "Length", 100, 4000, 1200, "ms", Logarithmic: true),
        new ParameterSpecification("brightness", "Brightness", 0, 1, 0.5, "%"),
        new ParameterSpecification("cutoff", "Filter", 80, 20000, 20000, "Hz", Logarithmic: true),
        new ParameterSpecification("octave", "Octave", -3, 3, 0, "oct", Integer: true),
        new ParameterSpecification("detune", "Detune", -100, 100, 0, "ct"),
        Humanize,
    };

    private static readonly IReadOnlyList<ParameterSpecification> DrumParameters = new[]
    {
        new ParameterSpecification("pitch", "Pitch", -24, 24, 0, "st"),
        new ParameterSpecification("decay", "Decay", 0.25, 4, 1, "×", Logarithmic: true),
        new ParameterSpecification("cutoff", "Tone", 200, 20000, 20000, "Hz", Logarithmic: true),
        Humanize,
    };

    private static readonly IReadOnlyList<ParameterSpecification> SamplerParameters = new[]
    {
        new ParameterSpecification("root", "Root Note", 24, 108, 60, "note", Integer: true),
        new ParameterSpecification("transpose", "Transpose", -24, 24, 0, "st", Integer: true),
        new ParameterSpecification("start", "Start", 0, 0.99, 0, "%"),
        new ParameterSpecification("end", "End", 0.01, 1, 1, "%"),
        new ParameterSpecification("hold", "Note Length", 0, 1, 0, "toggle", Integer: true),
        new ParameterSpecification("reverse", "Reverse", 0, 1, 0, "toggle", Integer: true),
        new ParameterSpecification("attack", "Attack", 0.5, 2000, 1, "ms", Logarithmic: true),
        new ParameterSpecification("release", "Release", 5, 4000, 40, "ms", Logarithmic: true),
        new ParameterSpecification("gain", "Gain", 0, 2, 1, "×"),
        new ParameterSpecification("cutoff", "Filter", 80, 20000, 20000, "Hz", Logarithmic: true),
        Humanize,
    };

    public static IReadOnlyList<ParameterSpecification> InstrumentParametersFor(string instrumentName) => instrumentName switch
    {
        Plugins.DeviceTypes.Vst3 => Array.Empty<ParameterSpecification>(),
        "sampler" => SamplerParameters,
        "kick" or "snare" or "hat" or "clap" => DrumParameters,
        "pluck" => PluckParameters,
        _ => OscillatorParameters,
    };

    public static string InstrumentDisplayName(string instrumentName) => instrumentName switch
    {
        "kick" => "Kick Synthesizer",
        "snare" => "Snare Synthesizer",
        "hat" => "Hi-Hat Synthesizer",
        "clap" => "Clap Synthesizer",
        "pluck" => "Karplus–Strong Pluck",
        "noise" => "Generative Noise Engine",
        "sampler" => "Sampler",
        Plugins.DeviceTypes.Vst3 => "VST3 Instrument",
        var oscillatorName => $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(oscillatorName)} Oscillator",
    };

    // ── Audio effects ─────────────────────────────────────────────────────────

    public static readonly IReadOnlyList<string> EffectTypes = new[]
    {
        "lowpass", "highpass", "delay", "reverb", "drive", "bitcrush", "vibe",
    };

    private static readonly Dictionary<string, IReadOnlyList<ParameterSpecification>> EffectParameterTable = new()
    {
        ["lowpass"] = new[]
        {
            new ParameterSpecification("cutoff", "Cutoff", 20, 20000, 2000, "Hz", Logarithmic: true),
            new ParameterSpecification("resonance", "Resonance", 0, 0.95, 0.2, "%"),
        },
        ["highpass"] = new[]
        {
            new ParameterSpecification("cutoff", "Cutoff", 20, 10000, 200, "Hz", Logarithmic: true),
            new ParameterSpecification("resonance", "Resonance", 0, 0.95, 0.1, "%"),
        },
        ["delay"] = new[]
        {
            new ParameterSpecification("steps", "Time", 1, 16, 3, "steps", Integer: true),
            new ParameterSpecification("feedback", "Feedback", 0, 0.95, 0.35, "%"),
            new ParameterSpecification("mix", "Dry/Wet", 0, 1, 0.3, "%"),
        },
        ["reverb"] = new[]
        {
            new ParameterSpecification("size", "Size", 0, 1, 0.6, "%"),
            new ParameterSpecification("damping", "Damping", 0, 1, 0.4, "%"),
            new ParameterSpecification("mix", "Dry/Wet", 0, 1, 0.25, "%"),
        },
        ["drive"] = new[]
        {
            new ParameterSpecification("amount", "Drive", 1, 20, 3, "×", Logarithmic: true),
            new ParameterSpecification("mix", "Dry/Wet", 0, 1, 1, "%"),
        },
        ["bitcrush"] = new[]
        {
            new ParameterSpecification("bits", "Bits", 2, 16, 8, "", Integer: true),
            new ParameterSpecification("downsample", "Downsample", 1, 32, 4, "×", Integer: true),
            new ParameterSpecification("mix", "Dry/Wet", 0, 1, 1, "%"),
        },
        ["vibe"] = new[]
        {
            new ParameterSpecification("synergy", "Synergy", 0, 1, 0.5, "%"),
            new ParameterSpecification("disruption", "Disruption", 0, 1, 0, "%"),
            new ParameterSpecification("alignment", "Alignment", 0, 1, 1, "%"),
        },
    };

    public static IReadOnlyList<ParameterSpecification> EffectParametersFor(string effectType) =>
        EffectParameterTable.TryGetValue(effectType, out var effectParameters) ? effectParameters : Array.Empty<ParameterSpecification>();

    public static string EffectDisplayName(string effectType) => effectType switch
    {
        "lowpass" => "Auto Filter (Low)",
        "highpass" => "Auto Filter (High)",
        "delay" => "Synergy Delay",
        "reverb" => "Cloud Reverb",
        "drive" => "Warmth Saturator",
        "bitcrush" => "Lo-Fi Compliance Crusher",
        "vibe" => "Neural Vibe Enhancer™",
        Plugins.DeviceTypes.Vst3 => "VST3 Effect",
        _ => effectType,
    };

    public static string EffectDescription(string effectType) => effectType switch
    {
        "vibe" => "Synergy adds saturation. Disruption adds noise. Alignment is a placebo, validated in user studies.",
        "bitcrush" => "Reduces bit depth until the mix complies with lo-fi playlist guidelines.",
        "reverb" => "Places the track in a large, empty, cloud-hosted space.",
        _ => string.Empty,
    };

    /// <summary>Reads a parameter from a device's value table, falling back to its default.</summary>
    public static double Resolve(IReadOnlyDictionary<string, double>? parameterValues, ParameterSpecification parameterSpecification) =>
        parameterValues is not null && parameterValues.TryGetValue(parameterSpecification.Key, out var storedValue)
            ? parameterSpecification.Clamp(storedValue)
            : parameterSpecification.Default;

    /// <summary>Reads a parameter by key from a specification list; unknown keys resolve to zero.</summary>
    public static double Resolve(IReadOnlyDictionary<string, double>? parameterValues, IReadOnlyList<ParameterSpecification> parameterSpecifications, string parameterKey)
    {
        var parameterSpecification = parameterSpecifications.FirstOrDefault(candidate => candidate.Key == parameterKey);
        return parameterSpecification is null ? 0 : Resolve(parameterValues, parameterSpecification);
    }

    /// <summary>
    /// VST3 devices store normalized plugin parameters as p&lt;id&gt; = 0..1. Drops any
    /// other key and clamps the rest. Returns how many values had to be realigned.
    /// </summary>
    public static int RemediatePluginParameters(Dictionary<string, double> parameterValues)
    {
        var realignedValueCount = 0;
        foreach (var storedKey in parameterValues.Keys.ToList())
        {
            var storedValue = parameterValues[storedKey];
            if (!Plugins.Vst3ParameterDescription.TryParseKey(storedKey, out _) || !double.IsFinite(storedValue))
            {
                parameterValues.Remove(storedKey);
                realignedValueCount++;
            }
            else if (storedValue is < 0 or > 1)
            {
                parameterValues[storedKey] = Math.Clamp(storedValue, 0, 1);
                realignedValueCount++;
            }
        }
        return realignedValueCount;
    }

    /// <summary>
    /// Drops unknown keys and clamps known ones in place. Returns how many values
    /// had to be realigned.
    /// </summary>
    public static int Remediate(Dictionary<string, double> parameterValues, IReadOnlyList<ParameterSpecification> parameterSpecifications)
    {
        var realignedValueCount = 0;
        foreach (var storedKey in parameterValues.Keys.ToList())
        {
            var parameterSpecification = parameterSpecifications.FirstOrDefault(candidate => candidate.Key == storedKey);
            if (parameterSpecification is null)
            {
                parameterValues.Remove(storedKey);
                realignedValueCount++;
                continue;
            }
            var clampedValue = parameterSpecification.Clamp(parameterValues[storedKey]);
            if (clampedValue != parameterValues[storedKey])
            {
                parameterValues[storedKey] = clampedValue;
                realignedValueCount++;
            }
        }
        return realignedValueCount;
    }
}
