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

    /// <summary>The noise engine's knobs: an oscillator's, minus anything that needs a pitch to spread.</summary>
    private static readonly IReadOnlyList<ParameterSpecification> NoiseParameters = new[]
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

    /// <summary>
    /// Unison spread for the pitched oscillators (sine, square, saw, triangle), in cents: above 0 the note
    /// is two oscillators detuned by ±spread/2 and mixed at equal power. A saw at 15–30 is a Reese bass.
    /// </summary>
    private static readonly ParameterSpecification UnisonSpread =
        new("spread", "Spread", 0, 50, 0, "ct");

    private static readonly IReadOnlyList<ParameterSpecification> OscillatorParameters =
        NoiseParameters.Take(NoiseParameters.Count - 1).Append(UnisonSpread).Append(Humanize).ToArray();

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

    private static readonly IReadOnlyList<ParameterSpecification> MultisamplerParameters = new[]
    {
        new ParameterSpecification("transpose", "Transpose", -24, 24, 0, "st", Integer: true),
        new ParameterSpecification("velscale", "Vel → Volume", 0, 1, 0.5, "%"),
        new ParameterSpecification("hold", "Note Length", 0, 1, 1, "toggle", Integer: true),
        new ParameterSpecification("reverse", "Reverse", 0, 1, 0, "toggle", Integer: true),
        new ParameterSpecification("attack", "Attack", 0.5, 2000, 1, "ms", Logarithmic: true),
        new ParameterSpecification("release", "Release", 5, 4000, 200, "ms", Logarithmic: true),
        new ParameterSpecification("gain", "Gain", 0, 2, 1, "×"),
        new ParameterSpecification("cutoff", "Filter", 80, 20000, 20000, "Hz", Logarithmic: true),
        Humanize,
    };

    private static readonly IReadOnlyList<ParameterSpecification> SoundFontParameters = new[]
    {
        new ParameterSpecification("gain", "Gain", 0, 2, 1, "×"),
        new ParameterSpecification("transpose", "Transpose", -24, 24, 0, "st"),
        new ParameterSpecification("release", "Release", 0.1, 4, 1, "×", Logarithmic: true),
        new ParameterSpecification("cutoff", "Filter", 80, 20000, 20000, "Hz", Logarithmic: true),
        Humanize,
    };

    // ── Organic instruments ───────────────────────────────────────────────────

    private static readonly ParameterSpecification InstrumentFilter =
        new("cutoff", "Filter", 80, 20000, 20000, "Hz", Logarithmic: true);

    private static readonly ParameterSpecification InstrumentOctave =
        new("octave", "Octave", -3, 3, 0, "oct", Integer: true);

    private static readonly ParameterSpecification InstrumentDetune =
        new("detune", "Detune", -100, 100, 0, "ct");

    private static readonly IReadOnlyList<ParameterSpecification> ElectricPianoParameters = new[]
    {
        new ParameterSpecification("tine", "Tine", 0, 1, 0.5, "%"),
        new ParameterSpecification("bark", "Bark", 0, 1, 0.35, "%"),
        new ParameterSpecification("decay", "Decay", 300, 10000, 3000, "ms", Logarithmic: true),
        new ParameterSpecification("release", "Release", 10, 2000, 180, "ms", Logarithmic: true),
        new ParameterSpecification("tremolo", "Tremolo", 0, 1, 0, "%"),
        new ParameterSpecification("rate", "Trem Rate", 0.5, 12, 4.5, "Hz", Logarithmic: true),
        InstrumentFilter,
        InstrumentOctave,
        InstrumentDetune,
        Humanize,
    };

    private static readonly IReadOnlyList<ParameterSpecification> OrganParameters = new[]
    {
        new ParameterSpecification("sub", "Sub 16'", 0, 1, 0.6, "%"),
        new ParameterSpecification("body", "Body 8'", 0, 1, 1, "%"),
        new ParameterSpecification("upper", "Upper 4'", 0, 1, 0.5, "%"),
        new ParameterSpecification("sparkle", "Sparkle 1'", 0, 1, 0.15, "%"),
        new ParameterSpecification("percussion", "Percussion", 0, 1, 0.3, "%"),
        new ParameterSpecification("click", "Key Click", 0, 1, 0.35, "%"),
        new ParameterSpecification("chorus", "Chorus", 0, 1, 0.35, "%"),
        new ParameterSpecification("rate", "Chorus Rate", 0.5, 8, 6.8, "Hz", Logarithmic: true),
        new ParameterSpecification("attack", "Attack", 1, 2000, 4, "ms", Logarithmic: true),
        new ParameterSpecification("release", "Release", 5, 4000, 70, "ms", Logarithmic: true),
        InstrumentFilter,
        InstrumentOctave,
        InstrumentDetune,
        Humanize,
    };

    private static readonly IReadOnlyList<ParameterSpecification> MalletParameters = new[]
    {
        new ParameterSpecification("material", "Material", 0, 1, 0, "%"),
        new ParameterSpecification("hardness", "Hardness", 0, 1, 0.5, "%"),
        new ParameterSpecification("decay", "Decay", 100, 8000, 1400, "ms", Logarithmic: true),
        InstrumentFilter,
        InstrumentOctave,
        InstrumentDetune,
        Humanize,
    };

    private static readonly IReadOnlyList<ParameterSpecification> FluteParameters = new[]
    {
        new ParameterSpecification("breath", "Breath", 0, 1, 0.35, "%"),
        new ParameterSpecification("overblow", "Overblow", 0, 1, 0.3, "%"),
        new ParameterSpecification("vibrato", "Vibrato", 0, 1, 0.35, "%"),
        new ParameterSpecification("rate", "Vib Rate", 3, 8, 5.2, "Hz"),
        new ParameterSpecification("attack", "Attack", 5, 2000, 70, "ms", Logarithmic: true),
        new ParameterSpecification("release", "Release", 10, 3000, 140, "ms", Logarithmic: true),
        InstrumentFilter,
        InstrumentOctave,
        InstrumentDetune,
        Humanize,
    };

    private static readonly IReadOnlyList<ParameterSpecification> GuitarParameters = new[]
    {
        new ParameterSpecification("length", "Ring", 200, 8000, 2400, "ms", Logarithmic: true),
        new ParameterSpecification("brightness", "Brightness", 0, 1, 0.35, "%"),
        new ParameterSpecification("pick", "Pick Position", 0.05, 0.5, 0.15, "%"),
        new ParameterSpecification("body", "Body", 0, 1, 0.5, "%"),
        new ParameterSpecification("strum", "Strum", 0, 80, 14, "ms"),
        InstrumentFilter,
        InstrumentOctave,
        InstrumentDetune,
        Humanize,
    };

    private static readonly IReadOnlyList<ParameterSpecification> CongaParameters = new[]
    {
        new ParameterSpecification("tone", "Tone", 0, 1, 0.5, "%"),
        new ParameterSpecification("decay", "Decay", 0.25, 4, 1, "×", Logarithmic: true),
        new ParameterSpecification("bend", "Bend", 0, 12, 1.5, "st"),
        new ParameterSpecification("cutoff", "Tone Filter", 200, 20000, 20000, "Hz", Logarithmic: true),
        InstrumentOctave,
        InstrumentDetune,
        Humanize,
    };

    public static IReadOnlyList<ParameterSpecification> InstrumentParametersFor(string instrumentName) => instrumentName switch
    {
        Plugins.DeviceTypes.Vst3 => Array.Empty<ParameterSpecification>(),
        "sampler" => SamplerParameters,
        "multisampler" => MultisamplerParameters,
        "soundfont" => SoundFontParameters,
        "kick" or "snare" or "hat" or "clap" or "shaker" => DrumParameters,
        "pluck" => PluckParameters,
        "noise" => NoiseParameters,
        "epiano" => ElectricPianoParameters,
        "organ" => OrganParameters,
        "mallet" => MalletParameters,
        "flute" => FluteParameters,
        "guitar" => GuitarParameters,
        "conga" => CongaParameters,
        OperatorPatch.InstrumentName => OperatorPatch.Specifications,
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
        "epiano" => "Tine Electric Piano",
        "organ" => "Drawbar Alignment Organ",
        "mallet" => "Modal Mallet Bar",
        "flute" => "Breath-as-a-Service Flute",
        "guitar" => "Nylon-String Guitar",
        "conga" => "Hand Percussion Congas",
        "shaker" => "Bead Shaker",
        OperatorPatch.InstrumentName => "Four-Operator Alignment Synth",
        "sampler" => "Sampler",
        "multisampler" => "Multisampler",
        "soundfont" => "SoundFont Player",
        Plugins.DeviceTypes.Vst3 => "VST3 Instrument",
        var oscillatorName => $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(oscillatorName)} Oscillator",
    };

    /// <summary>What an instrument does beyond its name, for the device view and the catalog; empty when the name says it all.</summary>
    public static string InstrumentDescription(string instrumentName) => instrumentName switch
    {
        "sine" or "square" or "saw" or "triangle" =>
            "Spread (unison detune, 0..50 cents): above 0 the note is two oscillators detuned by ±spread/2 cents, mixed at equal power so the level stays put. " +
            "A saw at 15–30 beats like a Reese bass; at 0 it is one oscillator, aligned with itself as always.",
        "multisampler" =>
            "Many recordings on one track, each for a range of keys and velocities, so soft notes play soft takes and every key stays close to its real pitch. " +
            "Filled by File → Import Sounds… from Ableton presets (.adg). Note Length off lets notes ring out.",
        "epiano" =>
            "Two-operator FM Rhodes. Tine is the bell at the start of each note, Bark the growl of a hard strike; both follow velocity, " +
            "so soft chords stay warm and accents bite. Decay is how long a held middle C rings (higher notes ring shorter). Tremolo is the suitcase amp's, for when the chords need to sway in a meeting.",
        "organ" =>
            "Tonewheel organ. Sub (16' and 5⅓'), Body (8'), Upper (4', 2⅔', 2') and Sparkle (1⅗', 1⅓', 1') are the drawbars; Percussion is a decaying 2⅔' on every key, " +
            "Key Click the contacts closing, Chorus the scanner vibrato mixed with the dry signal (Rate: 6.8 Hz is the classic, around 1 Hz a slow rotary drift). " +
            "Percussion high, Sparkle up and Attack 1 for house stabs; Percussion 0 and Attack 300+ for a pad nobody can schedule over.",
        "mallet" =>
            "Modal bar. Material morphs marimba (0) → kalimba (0.33) → vibraphone (0.67) → glass (1). Hardness and velocity both harden the mallet: " +
            "soft strikes are mostly fundamental, hard ones ring the overtones. Rings out whatever the note length, like a bar would, and like a bar, it does not take feedback.",
        "flute" =>
            "Breathy flute: a sine core with a few overtones, breath noise tuned to the note, a soft scoop into pitch and vibrato that arrives after the attack. " +
            "Velocity and Overblow add overtones and breath. For a pan flute, raise Breath and lower Vibrato.",
        "guitar" =>
            "Nylon-string guitar on a tuned Karplus–Strong string, with a body resonance and a pick-position comb (lower Pick Position is closer to the bridge and thinner). " +
            "Chords strum low to high, Strum milliseconds apart. Notes ring for Ring unless they end sooner, when the finger lifts. Harder notes are brighter.",
        "conga" =>
            "Tuned hand drum: the note is the drum's pitch (\"x\" plays C4; G3 tumba, C4 conga, F4 quinto, C5 and up bongo). Tone morphs muted (0) → open (0.5) → slap (1). " +
            "Bend is how far the skin starts sharp before it settles; harder hits bend further and crack louder. Rings out whatever the note length.",
        OperatorPatch.InstrumentName =>
            "Four oscillators (A to D) that modulate each other as the Algorithm says, then one filter, one LFO and one pitch envelope. " +
            "This is where Ableton Operator presets land when imported. Algorithm 0 is D into C into B into A; 10 plays all four side by side.",
        "shaker" =>
            "Bead shaker: random bead collisions in a resonant shell, each hit a shake. Accent with velocity (\"x@0.5 x@0.8\"): harder shakes are longer and brighter. Pitch tunes the shell.",
        _ => string.Empty,
    };

    // ── Audio effects ─────────────────────────────────────────────────────────

    public static readonly IReadOnlyList<string> EffectTypes = new[]
    {
        "lowpass", "highpass", "delay", "reverb", "drive", "bitcrush", "vibe", "widen", Session.SidechainRouting.DuckEffectType,
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
            new ParameterSpecification("pingpong", "Ping-Pong", 0, 1, 0, "%"),
        },
        ["reverb"] = new[]
        {
            new ParameterSpecification("size", "Size", 0, 1, 0.6, "%"),
            new ParameterSpecification("damping", "Damping", 0, 1, 0.4, "%"),
            new ParameterSpecification("mix", "Dry/Wet", 0, 1, 0.25, "%"),
            new ParameterSpecification("width", "Width", 0, 1, 0.8, "%"),
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
        ["widen"] = new[]
        {
            new ParameterSpecification("width", "Width", 0, 2, 1.4, "%"),
            new ParameterSpecification("haas", "Haas", 0, 40, 8, "ms"),
        },
        [Session.SidechainRouting.DuckEffectType] = new[]
        {
            new ParameterSpecification("amount", "Depth", 0, 1, 0.7, "%"),
            new ParameterSpecification("threshold", "Threshold", -60, 0, -30, "dB"),
            new ParameterSpecification("attack", "Attack", 0.1, 100, 5, "ms", Logarithmic: true),
            new ParameterSpecification("release", "Release", 10, 1000, 180, "ms", Logarithmic: true),
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
        "widen" => "Stereo Alignment Widener",
        Session.SidechainRouting.DuckEffectType => "Stakeholder Ducking",
        Plugins.DeviceTypes.Vst3 => "VST3 Effect",
        _ => effectType,
    };

    public static string EffectDescription(string effectType) => effectType switch
    {
        "vibe" => "Synergy adds saturation. Disruption adds noise. Alignment is a placebo, validated in user studies.",
        "bitcrush" => "Reduces bit depth until the mix complies with lo-fi playlist guidelines.",
        "reverb" => "Places the track in a large, empty, cloud-hosted space. Width spreads the tail across both channels.",
        "delay" => "Tempo-synced echo. Ping-Pong sends each repeat to the other side of the room.",
        Session.SidechainRouting.DuckEffectType => "Turns this track down whenever its sidechain source (pick one in the device header) is louder than the threshold: depth is how far, attack and release how fast. Without a source it does nothing, like most stakeholders.",
        "widen" => "Width 0 is mono, 1 leaves the image alone, 2 doubles the sides. Haas delays the right channel a few ms, so even a mono source finds both speakers.",
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
