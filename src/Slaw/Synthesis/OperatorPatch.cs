namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// The parameters of the "operator" instrument: four oscillators A to D, an algorithm that
/// routes them, one filter, one LFO and one pitch envelope, after Ableton's Operator, so its
/// presets have somewhere to land. Each oscillator's keys share a prefix: aLevel, bCoarse…
/// </summary>
internal static class OperatorPatch
{
    public const string InstrumentName = "operator";

    public const int OscillatorCount = 4;

    public const int AlgorithmCount = 11;

    /// <summary>The filter modes, in the order of the "filtertype" parameter.</summary>
    public const int FilterLowpass12 = 0;
    public const int FilterLowpass24 = 1;
    public const int FilterHighpass12 = 2;
    public const int FilterHighpass24 = 3;
    public const int FilterBandpass = 4;
    public const int FilterNotch = 5;

    /// <summary>
    /// Which oscillators modulate each of A, B, C, D, per algorithm; those nothing lists as a
    /// modulator are heard. Modulators always have a later letter, so D to A is a valid order.
    /// </summary>
    public static readonly int[][][] AlgorithmModulators =
    {
        new[] { new[] { 1 }, new[] { 2 }, new[] { 3 }, Array.Empty<int>() },
        new[] { new[] { 1 }, new[] { 2, 3 }, Array.Empty<int>(), Array.Empty<int>() },
        new[] { new[] { 1, 2 }, Array.Empty<int>(), new[] { 3 }, Array.Empty<int>() },
        new[] { new[] { 1, 2, 3 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() },
        new[] { new[] { 1, 2 }, new[] { 3 }, new[] { 3 }, Array.Empty<int>() },
        new[] { new[] { 1 }, Array.Empty<int>(), new[] { 3 }, Array.Empty<int>() },
        new[] { new[] { 3 }, new[] { 3 }, new[] { 3 }, Array.Empty<int>() },
        new[] { Array.Empty<int>(), Array.Empty<int>(), new[] { 3 }, Array.Empty<int>() },
        new[] { new[] { 2 }, new[] { 2 }, new[] { 3 }, Array.Empty<int>() },
        new[] { new[] { 1 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() },
        new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() },
    };

    public static readonly IReadOnlyList<ParameterSpecification> Specifications = ComposeSpecifications();

    /// <summary>The same list for a catalog read by a small model, where 90 knobs would cost more than they teach.</summary>
    public const string CatalogSummary =
        "algorithm 0-10, volume, transpose, cutoff, filteron, filtertype, filterfreq, filterres; a, b, c and d each take on, coarse, fine, level, wave, feedback, attack, decay, sustain, release (keys like aLevel, bCoarse). Usually filled by importing an Ableton Operator preset";

    public static string OscillatorPrefix(int oscillatorIndex) => "abcd"[oscillatorIndex].ToString();

    private static List<ParameterSpecification> ComposeSpecifications()
    {
        var specifications = new List<ParameterSpecification>
        {
            new("algorithm", "Algorithm", 0, AlgorithmCount - 1, 10, "", Integer: true),
            new("volume", "Volume", 0, 32, 1, "×"),
            new("transpose", "Transpose", -48, 48, 0, "st", Integer: true),
            new("octave", "Octave", -3, 3, 0, "oct", Integer: true),
            new("detune", "Detune", -100, 100, 0, "ct"),
            new("spread", "Spread", 0, 100, 0, ""),
            new("envtime", "Time", -100, 100, 0, ""),
            new("release", "Release", 5, 8000, 50, "ms", Logarithmic: true),
            new("cutoff", "Tone", 200, 20000, 20000, "Hz", Logarithmic: true),
            new("humanize", "AI Humanize", 0, 1, 0, "%"),
            new("filteron", "Filter On", 0, 1, 0, "", Integer: true),
            new("filtertype", "Filter Type", 0, 5, 0, "", Integer: true),
            new("filterfreq", "Filter Freq", 20, 20000, 18500, "Hz", Logarithmic: true),
            new("filterres", "Filter Res", 0, 1, 0.1, "%"),
            new("filterenv", "Filter Env", -72, 72, 0, "st"),
            new("filterattack", "Filter Attack", 0.1, 20000, 0.1, "ms", Logarithmic: true),
            new("filterdecay", "Filter Decay", 1, 60000, 600, "ms", Logarithmic: true),
            new("filtersustain", "Filter Sustain", 0, 1, 0, "%"),
            new("filterrelease", "Filter Release", 1, 20000, 50, "ms", Logarithmic: true),
            new("lfoon", "LFO On", 0, 1, 0, "", Integer: true),
            new("lforate", "LFO Rate", 0.02, 12000, 2, "Hz", Logarithmic: true),
            new("lfoshape", "LFO Shape", 0, 5, 0, "", Integer: true),
            new("lfoamount", "LFO Depth", 0, 48, 0, "st"),
            new("lfofilter", "LFO to Filter", 0, 1, 0, "", Integer: true),
            new("pitchon", "Pitch Env On", 0, 1, 0, "", Integer: true),
            new("pitchamount", "Pitch Amount", -1, 1, 1, "%"),
            new("pitchinitial", "Pitch Initial", -48, 48, 0, "st"),
            new("pitchpeak", "Pitch Peak", -48, 48, 0, "st"),
            new("pitchsustain", "Pitch Sustain", -48, 48, 0, "st"),
            new("pitchend", "Pitch End", -48, 48, 0, "st"),
            new("pitchattack", "Pitch Attack", 0.1, 20000, 0.1, "ms", Logarithmic: true),
            new("pitchdecay", "Pitch Decay", 1, 60000, 600, "ms", Logarithmic: true),
            new("pitchrelease", "Pitch Release", 1, 20000, 50, "ms", Logarithmic: true),
        };
        for (var oscillatorIndex = 0; oscillatorIndex < OscillatorCount; oscillatorIndex++)
        {
            var prefix = OscillatorPrefix(oscillatorIndex);
            var title = prefix.ToUpperInvariant();
            specifications.Add(new($"{prefix}On", $"{title} On", 0, 1, oscillatorIndex == 0 ? 1 : 0, "", Integer: true));
            specifications.Add(new($"{prefix}Coarse", $"{title} Coarse", 0, 48, 1, "×", Integer: true));
            specifications.Add(new($"{prefix}Fine", $"{title} Fine", 0, 1000, 0, ""));
            specifications.Add(new($"{prefix}Fixed", $"{title} Fixed", 0, 1, 0, "", Integer: true));
            specifications.Add(new($"{prefix}Freq", $"{title} Freq", 1, 20000, 100, "Hz", Logarithmic: true));
            specifications.Add(new($"{prefix}Level", $"{title} Level", 0, 1, 1, "×"));
            specifications.Add(new($"{prefix}Wave", $"{title} Wave", 0, OperatorWaveforms.Count - 1, 0, "", Integer: true));
            specifications.Add(new($"{prefix}Feedback", $"{title} Feedback", 0, 100, 0, ""));
            specifications.Add(new($"{prefix}Phase", $"{title} Phase", 0, 360, 0, "°"));
            specifications.Add(new($"{prefix}Initial", $"{title} Initial", 0, 1, 0, "%"));
            specifications.Add(new($"{prefix}Peak", $"{title} Peak", 0, 1, 1, "%"));
            specifications.Add(new($"{prefix}Attack", $"{title} Attack", 0.1, 20000, 0.1, "ms", Logarithmic: true));
            specifications.Add(new($"{prefix}Decay", $"{title} Decay", 1, 60000, 600, "ms", Logarithmic: true));
            specifications.Add(new($"{prefix}Sustain", $"{title} Sustain", 0, 1, 1, "%"));
            specifications.Add(new($"{prefix}Release", $"{title} Release", 1, 20000, 50, "ms", Logarithmic: true));
            specifications.Add(new($"{prefix}Lfo", $"{title} LFO", 0, 1, 1, "", Integer: true));
            specifications.Add(new($"{prefix}PitchEnv", $"{title} Pitch Env", 0, 1, 1, "", Integer: true));
        }
        return specifications;
    }
}

/// <summary>
/// One cycle of each waveform, numbered as Ableton Operator's presets store them: sine and two
/// quantized sines, sawtooths and squares limited to a number of harmonics, their naive digital
/// forms, triangles, and noise. The numbering is read from what presets named after these sounds use.
/// </summary>
internal static class OperatorWaveforms
{
    public const int Count = 23;

    public const int TableSize = 4096;

    public const int WhiteNoise = 22;

    private const int LoopingNoiseSeed = 0x0FE2A70;

    private static readonly int[] SawtoothHarmonics = { 3, 4, 6, 8, 16, 32, 64 };
    private static readonly int[] SquareHarmonics = { 3, 4, 6, 8, 16, 32, 64 };

    private static readonly Lazy<float[]>[] Tables = Enumerable.Range(0, Count)
        .Select(waveform => new Lazy<float[]>(() => Compose(waveform)))
        .ToArray();

    /// <summary>A table of <see cref="TableSize"/> + 1 frames, the last repeating the first for interpolation.</summary>
    public static float[] Table(int waveform) => Tables[Math.Clamp(waveform, 0, Count - 1)].Value;

    private static float[] Compose(int waveform)
    {
        var table = new float[TableSize + 1];
        var noise = new Random(LoopingNoiseSeed);
        for (var frame = 0; frame < TableSize; frame++)
        {
            var phase = (double)frame / TableSize;
            table[frame] = (float)(waveform switch
            {
                0 => Math.Sin(2 * Math.PI * phase),
                1 => Quantize(Math.Sin(2 * Math.PI * phase), 4),
                2 => Quantize(Math.Sin(2 * Math.PI * phase), 8),
                >= 3 and <= 9 => Harmonics(phase, SawtoothHarmonics[waveform - 3], everyHarmonic: true),
                10 => 1 - 2 * phase,
                >= 11 and <= 17 => Harmonics(phase, SquareHarmonics[waveform - 11], everyHarmonic: false),
                18 => phase < 0.5 ? 1 : -1,
                19 or 20 => phase < 0.25 ? 4 * phase : phase < 0.75 ? 2 - 4 * phase : 4 * phase - 4,
                _ => noise.NextDouble() * 2 - 1,
            });
        }
        table[TableSize] = table[0];
        return table;
    }

    private static double Quantize(double sample, int bits)
    {
        var levels = 1 << (bits - 1);
        return Math.Round(sample * levels) / levels;
    }

    /// <summary>A sawtooth (every harmonic) or a square (odd ones), limited to some harmonics, at the level of a full-scale wave.</summary>
    private static double Harmonics(double phase, int harmonicCount, bool everyHarmonic)
    {
        var sum = 0.0;
        for (var harmonic = 1; harmonic <= harmonicCount; harmonic += everyHarmonic ? 1 : 2)
        {
            sum += Math.Sin(2 * Math.PI * harmonic * phase) / harmonic;
        }
        return sum * (everyHarmonic ? 2 / Math.PI : 4 / Math.PI);
    }
}
