using System.Globalization;
using System.Numerics;
using System.Text;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>What a finished render measures: loudness to ITU-R BS.1770, true peak, and how it survives mono.</summary>
internal sealed record MixMeasurement(
    double IntegratedLufs,
    double MaximumShortTermLufs,
    double TruePeakDecibels,
    double Correlation,
    double MonoChangeDecibels,
    double LowEndCorrelation);

/// <summary>Two tracks loud in one frequency band at the same moments, which is where one hides the other.</summary>
internal sealed record MaskingClash(string LouderTrack, string QuieterTrack, string BandLabel, double ActiveShare, double AverageDistanceDecibels, IReadOnlyList<int> RangeIndexes);

/// <summary>A render's energy per frequency band over time, in overlapping windows.</summary>
internal sealed class BandProfile
{
    public BandProfile(float[][] bandPowers, double secondsPerFrame)
    {
        BandPowers = bandPowers;
        SecondsPerFrame = secondsPerFrame;
    }

    /// <summary>Mean power per channel, indexed by band and then by frame.</summary>
    public float[][] BandPowers { get; }

    public double SecondsPerFrame { get; }

    public int FrameCount => BandPowers.Length == 0 ? 0 : BandPowers[0].Length;

    /// <summary>A band's level from one time to another: a full-scale sine reads -3 dB.</summary>
    public double BandDecibels(int bandIndex, double startSeconds = 0, double endSeconds = double.PositiveInfinity)
    {
        var firstFrame = Math.Clamp((int)(startSeconds / SecondsPerFrame), 0, FrameCount);
        var lastFrame = Math.Clamp(double.IsInfinity(endSeconds) ? FrameCount : (int)(endSeconds / SecondsPerFrame), firstFrame, FrameCount);
        if (lastFrame == firstFrame)
        {
            return MixAnalyzer.SilenceDecibels;
        }
        var totalPower = 0.0;
        for (var frameIndex = firstFrame; frameIndex < lastFrame; frameIndex++)
        {
            totalPower += BandPowers[bandIndex][frameIndex];
        }
        return MixAnalyzer.PowerToDecibels(totalPower / (lastFrame - firstFrame));
    }
}

/// <summary>
/// Hears a render on behalf of whoever cannot: numbers for loudness, peaks, stereo, tonal
/// balance and masking. Reads finished buffers only, so measuring never changes the bytes of a bounce.
/// </summary>
internal static class MixAnalyzer
{
    public const double SilenceDecibels = -120;

    public static readonly (string Label, double LowHz, double HighHz)[] Bands =
    {
        ("20–60 Hz", 20, 60),
        ("60–120 Hz", 60, 120),
        ("120–250 Hz", 120, 250),
        ("250–500 Hz", 250, 500),
        ("500 Hz–1 kHz", 500, 1000),
        ("1–2 kHz", 1000, 2000),
        ("2–4 kHz", 2000, 4000),
        ("4–8 kHz", 4000, 8000),
        ("8–20 kHz", 8000, 20000),
    };

    private const double LoudnessBlockSeconds = 0.4;
    private const double LoudnessHopSeconds = 0.1;
    private const double ShortTermSeconds = 3;
    private const double AbsoluteGateLufs = -70;
    private const double RelativeGateLu = 10;

    private const int TruePeakOversampling = 4;
    private const int TruePeakTapsPerPhase = 12;

    private const int BandWindowFrames = 4096;
    private const double LowEndCutoffHz = 120;

    /// <summary>Two tracks count as clashing in a window when both are within this much of the band's loudest track.</summary>
    private const double ClashWindowDecibels = 6;

    /// <summary>A band this far under the window's loudest band is too quiet for a clash there to matter.</summary>
    private const double ClashAudibilityDecibels = 24;

    private const double ClashSilenceDecibels = -60;

    /// <summary>A clash must hold for this share of a bar range before it is worth a sentence.</summary>
    private const double ClashMinimumShare = 0.25;

    private const int MaximumReportedClashes = 6;

    public static double PowerToDecibels(double power) => power > 1e-12 ? 10 * Math.Log10(power) : SilenceDecibels;

    public static MixMeasurement Measure(StereoBuffer buffer)
    {
        var (integrated, shortTerm) = MeasureLoudness(buffer);
        var (correlation, monoChange) = MeasureStereo(buffer.Left, buffer.Right);
        var lowLeft = LowPassed(buffer.Left, buffer.SampleRate);
        var lowRight = LowPassed(buffer.Right, buffer.SampleRate);
        return new MixMeasurement(integrated, shortTerm, MeasureTruePeak(buffer), correlation, monoChange, MeasureStereo(lowLeft, lowRight).Correlation);
    }

    // ── Loudness (ITU-R BS.1770-4, EBU R 128) ────────────────────────────────

    private static (double Integrated, double MaximumShortTerm) MeasureLoudness(StereoBuffer buffer)
    {
        var hopFrames = (int)(buffer.SampleRate * LoudnessHopSeconds);
        var hopCount = buffer.SampleFrameCount / hopFrames;
        if (hopCount == 0)
        {
            return (SilenceDecibels, SilenceDecibels);
        }
        var hopPowers = new double[hopCount];
        AccumulateKWeightedPower(buffer.Left, buffer.SampleRate, hopFrames, hopPowers);
        AccumulateKWeightedPower(buffer.Right, buffer.SampleRate, hopFrames, hopPowers);

        var blockHops = (int)Math.Round(LoudnessBlockSeconds / LoudnessHopSeconds);
        var shortTermHops = (int)Math.Round(ShortTermSeconds / LoudnessHopSeconds);
        var blockPowers = SlidingMeans(hopPowers, blockHops);
        var shortTermPowers = SlidingMeans(hopPowers, shortTermHops);

        var audibleBlocks = blockPowers.Where(power => Lufs(power) > AbsoluteGateLufs).ToList();
        if (audibleBlocks.Count == 0)
        {
            return (SilenceDecibels, SilenceDecibels);
        }
        var relativeGate = Lufs(audibleBlocks.Average()) - RelativeGateLu;
        var gatedBlocks = audibleBlocks.Where(power => Lufs(power) > relativeGate).ToList();
        var integrated = Lufs(gatedBlocks.Count > 0 ? gatedBlocks.Average() : audibleBlocks.Average());
        var shortTerm = shortTermPowers.Count > 0 ? Lufs(shortTermPowers.Max()) : Lufs(blockPowers.Max());
        return (integrated, shortTerm);
    }

    private static double Lufs(double power) => power > 1e-12 ? -0.691 + 10 * Math.Log10(power) : SilenceDecibels;

    private static List<double> SlidingMeans(double[] values, int windowLength)
    {
        var means = new List<double>();
        var runningSum = 0.0;
        for (var index = 0; index < values.Length; index++)
        {
            runningSum += values[index];
            if (index >= windowLength)
            {
                runningSum -= values[index - windowLength];
            }
            if (index >= windowLength - 1)
            {
                means.Add(runningSum / windowLength);
            }
        }
        return means;
    }

    /// <summary>The K-weighting's two biquads, derived from their analog prototypes so any sample rate gets the same curve.</summary>
    private static void AccumulateKWeightedPower(float[] channel, int sampleRate, int hopFrames, double[] hopPowers)
    {
        var shelfTan = Math.Tan(Math.PI * 1681.974450955533 / sampleRate);
        var shelfGain = Math.Pow(10, 3.999843853973347 / 20);
        const double ShelfQ = 0.7071752369554196;
        var shelfHighGain = Math.Pow(shelfGain, 0.4996667741545416);
        var shelfNormalizer = 1 + shelfTan / ShelfQ + shelfTan * shelfTan;
        var shelfB0 = (shelfGain + shelfHighGain * shelfTan / ShelfQ + shelfTan * shelfTan) / shelfNormalizer;
        var shelfB1 = 2 * (shelfTan * shelfTan - shelfGain) / shelfNormalizer;
        var shelfB2 = (shelfGain - shelfHighGain * shelfTan / ShelfQ + shelfTan * shelfTan) / shelfNormalizer;
        var shelfA1 = 2 * (shelfTan * shelfTan - 1) / shelfNormalizer;
        var shelfA2 = (1 - shelfTan / ShelfQ + shelfTan * shelfTan) / shelfNormalizer;

        var cutTan = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
        const double CutQ = 0.5003270373238773;
        var cutNormalizer = 1 + cutTan / CutQ + cutTan * cutTan;
        var cutA1 = 2 * (cutTan * cutTan - 1) / cutNormalizer;
        var cutA2 = (1 - cutTan / CutQ + cutTan * cutTan) / cutNormalizer;

        double shelfState1 = 0, shelfState2 = 0, cutState1 = 0, cutState2 = 0;
        for (var hopIndex = 0; hopIndex < hopPowers.Length; hopIndex++)
        {
            var hopEnergy = 0.0;
            var firstFrame = hopIndex * hopFrames;
            for (var frameIndex = firstFrame; frameIndex < firstFrame + hopFrames; frameIndex++)
            {
                double inputSample = channel[frameIndex];
                var shelved = shelfB0 * inputSample + shelfState1;
                shelfState1 = shelfB1 * inputSample - shelfA1 * shelved + shelfState2;
                shelfState2 = shelfB2 * inputSample - shelfA2 * shelved;
                var weighted = shelved + cutState1;
                cutState1 = -2 * shelved - cutA1 * weighted + cutState2;
                cutState2 = shelved - cutA2 * weighted;
                hopEnergy += weighted * weighted;
            }
            hopPowers[hopIndex] += hopEnergy / hopFrames;
        }
    }

    // ── True peak ─────────────────────────────────────────────────────────────

    /// <summary>The peak between the samples, from 4× oversampling, where a converter or an encoder will find it.</summary>
    private static double MeasureTruePeak(StereoBuffer buffer)
    {
        var interpolationKernel = ComposeInterpolationKernel();
        var truePeak = Math.Max(ChannelTruePeak(buffer.Left, interpolationKernel), ChannelTruePeak(buffer.Right, interpolationKernel));
        return truePeak > 0 ? 20 * Math.Log10(truePeak) : SilenceDecibels;
    }

    private static double[][] ComposeInterpolationKernel()
    {
        var kernel = new double[TruePeakOversampling][];
        for (var phase = 0; phase < TruePeakOversampling; phase++)
        {
            kernel[phase] = new double[TruePeakTapsPerPhase];
            for (var tap = 0; tap < TruePeakTapsPerPhase; tap++)
            {
                var offset = tap - (TruePeakTapsPerPhase / 2 - 1) - (double)phase / TruePeakOversampling;
                var sinc = Math.Abs(offset) < 1e-9 ? 1 : Math.Sin(Math.PI * offset) / (Math.PI * offset);
                var window = 0.5 + 0.5 * Math.Cos(Math.PI * offset / (TruePeakTapsPerPhase / 2.0));
                kernel[phase][tap] = sinc * window;
            }
        }
        return kernel;
    }

    private static double ChannelTruePeak(float[] channel, double[][] interpolationKernel)
    {
        var peak = 0.0;
        var firstTap = TruePeakTapsPerPhase / 2 - 1;
        foreach (var sample in channel)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }
        for (var frameIndex = firstTap; frameIndex < channel.Length - TruePeakTapsPerPhase / 2; frameIndex++)
        {
            // An intersample peak overshoots its neighbours by a few dB at most, so quiet stretches can be skipped.
            if (Math.Max(Math.Abs(channel[frameIndex]), Math.Abs(channel[frameIndex + 1])) < peak * 0.5)
            {
                continue;
            }
            for (var phase = 1; phase < TruePeakOversampling; phase++)
            {
                var phaseKernel = interpolationKernel[phase];
                var interpolated = 0.0;
                for (var tap = 0; tap < TruePeakTapsPerPhase; tap++)
                {
                    interpolated += phaseKernel[tap] * channel[frameIndex - firstTap + tap];
                }
                peak = Math.Max(peak, Math.Abs(interpolated));
            }
        }
        return peak;
    }

    // ── Stereo ────────────────────────────────────────────────────────────────

    /// <summary>Correlation (1 mono, 0 unrelated, -1 opposed) and how much level summing to mono gains or loses.</summary>
    private static (double Correlation, double MonoChangeDecibels) MeasureStereo(float[] left, float[] right)
    {
        double leftEnergy = 0, rightEnergy = 0, crossEnergy = 0;
        for (var frameIndex = 0; frameIndex < left.Length; frameIndex++)
        {
            leftEnergy += left[frameIndex] * left[frameIndex];
            rightEnergy += right[frameIndex] * right[frameIndex];
            crossEnergy += left[frameIndex] * right[frameIndex];
        }
        if (leftEnergy + rightEnergy < 1e-9)
        {
            return (1, 0);
        }
        var correlation = leftEnergy > 0 && rightEnergy > 0 ? crossEnergy / Math.Sqrt(leftEnergy * rightEnergy) : 0;
        var stereoPower = (leftEnergy + rightEnergy) / 2;
        var monoPower = (leftEnergy + rightEnergy + 2 * crossEnergy) / 4;
        return (correlation, PowerToDecibels(monoPower / left.Length) - PowerToDecibels(stereoPower / left.Length));
    }

    private static float[] LowPassed(float[] channel, int sampleRate)
    {
        var coefficient = 1 - Math.Exp(-2 * Math.PI * LowEndCutoffHz / sampleRate);
        var filtered = new float[channel.Length];
        double firstPole = 0, secondPole = 0;
        for (var frameIndex = 0; frameIndex < channel.Length; frameIndex++)
        {
            firstPole += coefficient * (channel[frameIndex] - firstPole);
            secondPole += coefficient * (firstPole - secondPole);
            filtered[frameIndex] = (float)secondPole;
        }
        return filtered;
    }

    // ── Bands ─────────────────────────────────────────────────────────────────

    public static BandProfile ProfileBands(StereoBuffer buffer)
    {
        var hopFrames = BandWindowFrames / 2;
        var frameCount = buffer.SampleFrameCount < BandWindowFrames ? 0 : (buffer.SampleFrameCount - BandWindowFrames) / hopFrames + 1;
        var bandPowers = Bands.Select(_ => new float[frameCount]).ToArray();
        var hannWindow = new double[BandWindowFrames];
        var windowEnergy = 0.0;
        for (var index = 0; index < BandWindowFrames; index++)
        {
            hannWindow[index] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / BandWindowFrames);
            windowEnergy += hannWindow[index] * hannWindow[index];
        }
        var binHz = (double)buffer.SampleRate / BandWindowFrames;
        var bandBins = Bands.Select(band => (First: Math.Max(1, (int)Math.Ceiling(band.LowHz / binHz)), Last: Math.Min(BandWindowFrames / 2, (int)Math.Ceiling(band.HighHz / binHz) - 1))).ToArray();
        var spectrum = new Complex[BandWindowFrames];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var firstSample = frame * hopFrames;
            for (var index = 0; index < BandWindowFrames; index++)
            {
                // Left in the real part, right in the imaginary: one transform measures both channels.
                spectrum[index] = new Complex(buffer.Left[firstSample + index] * hannWindow[index], buffer.Right[firstSample + index] * hannWindow[index]);
            }
            TransformInPlace(spectrum);
            for (var band = 0; band < bandBins.Length; band++)
            {
                var bandEnergy = 0.0;
                for (var bin = bandBins[band].First; bin <= bandBins[band].Last; bin++)
                {
                    var mirrorBin = (BandWindowFrames - bin) % BandWindowFrames;
                    bandEnergy += spectrum[bin].Real * spectrum[bin].Real + spectrum[bin].Imaginary * spectrum[bin].Imaginary
                        + spectrum[mirrorBin].Real * spectrum[mirrorBin].Real + spectrum[mirrorBin].Imaginary * spectrum[mirrorBin].Imaginary;
                }
                // Parseval, per channel: the two channels' positive and negative bins are all in that sum.
                bandPowers[band][frame] = (float)(bandEnergy / (BandWindowFrames * windowEnergy * 2));
            }
        }
        return new BandProfile(bandPowers, (double)hopFrames / buffer.SampleRate);
    }

    private static void TransformInPlace(Complex[] values)
    {
        var length = values.Length;
        for (int index = 1, reversed = 0; index < length; index++)
        {
            var bit = length >> 1;
            for (; (reversed & bit) != 0; bit >>= 1)
            {
                reversed ^= bit;
            }
            reversed ^= bit;
            if (index < reversed)
            {
                (values[index], values[reversed]) = (values[reversed], values[index]);
            }
        }
        for (var span = 2; span <= length; span <<= 1)
        {
            var angle = -2 * Math.PI / span;
            var rootStep = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < length; start += span)
            {
                var root = Complex.One;
                for (var offset = 0; offset < span / 2; offset++)
                {
                    var upper = values[start + offset];
                    var lower = values[start + offset + span / 2] * root;
                    values[start + offset] = upper + lower;
                    values[start + offset + span / 2] = upper - lower;
                    root *= rootStep;
                }
            }
        }
    }

    // ── Masking ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds pairs of tracks loud in one band at the same moments, window by window, so a bass
    /// ducked by the kick does not count where the ducking keeps them apart. Overlap is only
    /// a likely problem: two doubled parts overlap on purpose. Worst first.
    /// </summary>
    /// <param name="stems">Each playing track's profile, all from renders of the same length.</param>
    /// <param name="ranges">Time ranges in seconds to report by, e.g. eight bars each.</param>
    public static IReadOnlyList<MaskingClash> FindClashes(IReadOnlyList<(string Name, BandProfile Profile)> stems, IReadOnlyList<(double StartSeconds, double EndSeconds)> ranges)
    {
        if (stems.Count < 2 || stems[0].Profile.FrameCount == 0)
        {
            return Array.Empty<MaskingClash>();
        }
        var profileLength = stems.Min(stem => stem.Profile.FrameCount);
        var secondsPerFrame = stems[0].Profile.SecondsPerFrame;
        var windowRatio = Math.Pow(10, -ClashWindowDecibels / 10);
        var audibilityRatio = Math.Pow(10, -ClashAudibilityDecibels / 10);
        var silence = Math.Pow(10, ClashSilenceDecibels / 10);

        var loudestBandPower = new double[profileLength];
        foreach (var (_, profile) in stems)
        {
            for (var band = 0; band < Bands.Length; band++)
            {
                for (var frame = 0; frame < profileLength; frame++)
                {
                    loudestBandPower[frame] = Math.Max(loudestBandPower[frame], profile.BandPowers[band][frame]);
                }
            }
        }

        var clashes = new List<MaskingClash>();
        for (var band = 0; band < Bands.Length; band++)
        {
            var frameTops = new double[profileLength];
            for (var frame = 0; frame < profileLength; frame++)
            {
                foreach (var (_, profile) in stems)
                {
                    frameTops[frame] = Math.Max(frameTops[frame], profile.BandPowers[band][frame]);
                }
            }
            for (var first = 0; first < stems.Count; first++)
            {
                for (var second = first + 1; second < stems.Count; second++)
                {
                    var firstPowers = stems[first].Profile.BandPowers[band];
                    var secondPowers = stems[second].Profile.BandPowers[band];
                    var rangeHits = new int[ranges.Count];
                    var totalHits = 0;
                    var distanceSum = 0.0;
                    for (var frame = 0; frame < profileLength; frame++)
                    {
                        var top = frameTops[frame];
                        if (top < silence || top < loudestBandPower[frame] * audibilityRatio ||
                            firstPowers[frame] < top * windowRatio || secondPowers[frame] < top * windowRatio)
                        {
                            continue;
                        }
                        totalHits++;
                        distanceSum += 10 * Math.Log10(firstPowers[frame] / secondPowers[frame]);
                        var seconds = frame * secondsPerFrame;
                        for (var range = 0; range < ranges.Count; range++)
                        {
                            if (seconds >= ranges[range].StartSeconds && seconds < ranges[range].EndSeconds)
                            {
                                rangeHits[range]++;
                                break;
                            }
                        }
                    }
                    var reportedRanges = new List<int>();
                    var reportedHits = 0;
                    var reportedFrames = 0.0;
                    for (var range = 0; range < ranges.Count; range++)
                    {
                        var rangeFrames = Math.Max(1, (ranges[range].EndSeconds - ranges[range].StartSeconds) / secondsPerFrame);
                        if (rangeHits[range] / rangeFrames >= ClashMinimumShare)
                        {
                            reportedRanges.Add(range);
                            reportedHits += rangeHits[range];
                            reportedFrames += rangeFrames;
                        }
                    }
                    if (reportedRanges.Count > 0)
                    {
                        var averageDistance = distanceSum / totalHits;
                        var firstIsLouder = averageDistance >= 0;
                        clashes.Add(new MaskingClash(
                            firstIsLouder ? stems[first].Name : stems[second].Name,
                            firstIsLouder ? stems[second].Name : stems[first].Name,
                            Bands[band].Label, reportedHits / reportedFrames, Math.Abs(averageDistance), reportedRanges));
                    }
                }
            }
        }
        return clashes.OrderByDescending(clash => clash.ActiveShare * clash.RangeIndexes.Count).ToList();
    }

    // ── Reports ───────────────────────────────────────────────────────────────

    private const double ClippingRiskDecibels = -1;
    private const double MonoLossWarningDecibels = -3;
    private const double LowEndCorrelationWarning = 0.5;

    public static string DescribeMeasurement(MixMeasurement measurement, BandProfile bands)
    {
        var report = new StringBuilder();
        if (measurement.IntegratedLufs <= SilenceDecibels)
        {
            return "loudness: the render is silent.";
        }
        report.Append(CultureInfo.InvariantCulture, $"loudness: {measurement.IntegratedLufs:0.0} LUFS integrated, {measurement.MaximumShortTermLufs:0.0} LUFS loudest 3 s, true peak {measurement.TruePeakDecibels:0.0} dBTP.");
        report.Append(CultureInfo.InvariantCulture, $"\nstereo: correlation {measurement.Correlation:0.00} (1 mono, 0 unrelated, -1 opposed), below {LowEndCutoffHz:0} Hz {measurement.LowEndCorrelation:0.00}; summed to mono the level changes {measurement.MonoChangeDecibels:+0.0;-0.0;0.0} dB.");
        report.Append("\ntonal balance (dB RMS per band, whole render): ").Append(DescribeBands(bands));
        if (measurement.TruePeakDecibels > ClippingRiskDecibels)
        {
            report.Append(CultureInfo.InvariantCulture, $"\n- warning: true peak {measurement.TruePeakDecibels:0.0} dBTP is above {ClippingRiskDecibels:0} dBTP and may clip when encoded; lower track or master gain");
        }
        if (measurement.MonoChangeDecibels < MonoLossWarningDecibels)
        {
            report.Append(CultureInfo.InvariantCulture, $"\n- warning: the mix loses {-measurement.MonoChangeDecibels:0.0} dB summed to mono, so parts cancel on a mono speaker; reduce widen or Haas");
        }
        if (measurement.LowEndCorrelation < LowEndCorrelationWarning)
        {
            report.Append(CultureInfo.InvariantCulture, $"\n- warning: below {LowEndCutoffHz:0} Hz the correlation is {measurement.LowEndCorrelation:0.00}, so the bass will weaken on club and mono systems; keep low parts centered");
        }
        return report.ToString();
    }

    public static string DescribeBands(BandProfile bands, double startSeconds = 0, double endSeconds = double.PositiveInfinity) =>
        string.Join(", ", Bands.Select((band, bandIndex) =>
            string.Create(CultureInfo.InvariantCulture, $"{band.Label} {bands.BandDecibels(bandIndex, startSeconds, endSeconds):0.0}")));

    public static string DescribeClashes(IReadOnlyList<MaskingClash> clashes, IReadOnlyList<string> rangeLabels)
    {
        if (clashes.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"\nlikely masking: none found (no two tracks stay within {ClashWindowDecibels:0} dB of each other at the top of one band for long).");
        }
        var report = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"\nlikely masking (two tracks both within {ClashWindowDecibels:0} dB of the loudest track in one band at the same moments; doubled parts do this on purpose). Decide which track should own the band, then eq a dip in the other, duck the other keyed by it, or move a part an octave. A dip can move the overlap to the next band, or make a clash read closer before it clears, so re-render. Listed with the most widespread first (share of the time × bars affected):"));
        foreach (var clash in clashes.Take(MaximumReportedClashes))
        {
            report.Append(CultureInfo.InvariantCulture,
                $"\n- \"{clash.LouderTrack}\" over \"{clash.QuieterTrack}\" at {clash.BandLabel}: {clash.ActiveShare * 100:0}% of the time in {DescribeRanges(clash.RangeIndexes, rangeLabels)}, louder by {clash.AverageDistanceDecibels:0.0} dB on average");
        }
        if (clashes.Count > MaximumReportedClashes)
        {
            report.Append(CultureInfo.InvariantCulture, $"\n- and {clashes.Count - MaximumReportedClashes} smaller overlap(s) not listed");
        }
        return report.ToString();
    }

    private static string DescribeRanges(IReadOnlyList<int> rangeIndexes, IReadOnlyList<string> rangeLabels) =>
        rangeIndexes.Count == rangeLabels.Count ? "the whole song" : string.Join(", ", rangeIndexes.Select(rangeIndex => rangeLabels[rangeIndex]));
}
