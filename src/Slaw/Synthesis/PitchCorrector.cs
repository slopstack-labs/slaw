using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// Autotune for monophonic audio (a voice, a lead line): finds the pitch every few
/// milliseconds, picks the nearest note of a key, and moves the audio there with
/// pitch-synchronous overlap-add (TD-PSOLA), so the length and the timing stay exactly
/// as recorded. Unvoiced audio (breaths, consonants, silence) passes through unchanged.
/// Deterministic and allocation-heavy: run it offline (sample edits), never per block.
/// </summary>
internal static class PitchCorrector
{
    /// <summary>The key value that snaps to every semitone instead of a scale.</summary>
    public const string ChromaticKey = "chromatic";

    /// <summary>Longest retune time in ms; 0 snaps instantly (the robot), 40–80 sounds like a singer who practised.</summary>
    public const double MaximumRetuneMilliseconds = 400;

    private const double LowestDetectedHertz = 70;
    private const double HighestDetectedHertz = 1000;
    private const double DetectionSampleRate = 11025;
    private const double HopSeconds = 0.005;
    private const double YinThreshold = 0.15;
    private const double UnvoicedPeriodSeconds = 0.008;
    private const double EdgeCrossfadeSeconds = 0.01;

    /// <summary>
    /// Whether <paramref name="keyNotation"/> is "chromatic" (or empty) or a key
    /// <see cref="MusicalKey.Parse"/> understands ("A minor", "F#", "Bbm").
    /// </summary>
    public static bool IsValidKey(string? keyNotation) =>
        string.IsNullOrWhiteSpace(keyNotation) ||
        string.Equals(keyNotation.Trim(), ChromaticKey, StringComparison.OrdinalIgnoreCase) ||
        MusicalKey.Parse(keyNotation) is not null;

    /// <summary>
    /// Returns a corrected copy of <paramref name="samples"/>; only the frames in
    /// [<paramref name="regionStart"/>, <paramref name="regionEnd"/>) change, with a short
    /// crossfade at both edges. <paramref name="keyNotation"/> is a key or "chromatic".
    /// </summary>
    public static float[] Correct(float[] samples, int sampleRate, int regionStart, int regionEnd, string? keyNotation, double retuneMilliseconds)
    {
        regionStart = Math.Clamp(regionStart, 0, samples.Length);
        regionEnd = Math.Clamp(regionEnd, regionStart, samples.Length);
        var output = (float[])samples.Clone();
        if (regionEnd - regionStart < sampleRate / 20)
        {
            return output;
        }

        var region = samples[regionStart..regionEnd];
        var frames = DetectPitch(region, sampleRate);
        var ratios = CorrectionRatios(frames, sampleRate, MusicalKey.Parse(keyNotation), Math.Clamp(retuneMilliseconds, 0, MaximumRetuneMilliseconds));
        var corrected = Resynthesize(region, sampleRate, frames, ratios);

        var fadeFrames = Math.Min((int)(EdgeCrossfadeSeconds * sampleRate), region.Length / 2);
        for (var regionIndex = 0; regionIndex < region.Length; regionIndex++)
        {
            var distanceToEdge = Math.Min(regionIndex, region.Length - 1 - regionIndex);
            var wetWeight = distanceToEdge >= fadeFrames ? 1f : distanceToEdge / (float)fadeFrames;
            output[regionStart + regionIndex] = region[regionIndex] + (corrected[regionIndex] - region[regionIndex]) * wetWeight;
        }
        return output;
    }

    /// <summary>How far the voiced part of a region sits from the key: voiced seconds, mean and largest distance in cents.</summary>
    internal readonly record struct TuningReport(double VoicedSeconds, double MeanCentsOff, double LargestCentsOff);

    /// <summary>
    /// Measures how far each voiced frame of [<paramref name="regionStart"/>, <paramref name="regionEnd"/>)
    /// is from the nearest allowed note of <paramref name="keyNotation"/>, with the same detector the correction uses.
    /// </summary>
    public static TuningReport Measure(float[] samples, int sampleRate, int regionStart, int regionEnd, string? keyNotation)
    {
        regionStart = Math.Clamp(regionStart, 0, samples.Length);
        regionEnd = Math.Clamp(regionEnd, regionStart, samples.Length);
        var key = MusicalKey.Parse(keyNotation);
        var distances = DetectPitch(samples[regionStart..regionEnd], sampleRate)
            .Where(frame => frame.PeriodFrames > 0)
            .Select(frame =>
            {
                var midi = 69 + 12 * Math.Log2(sampleRate / frame.PeriodFrames / 440);
                return Math.Abs(midi - NearestAllowed(midi, key)) * 100;
            })
            .ToList();
        return distances.Count == 0
            ? new TuningReport(0, 0, 0)
            : new TuningReport(distances.Count * HopSeconds, distances.Average(), distances.Max());
    }

    /// <summary>One analysis hop: its centre in source frames and the period found there (0 when unvoiced).</summary>
    internal readonly record struct PitchFrame(int CentreFrame, double PeriodFrames);

    /// <summary>
    /// YIN (de Cheveigné and Kawahara) on a box-filtered, decimated copy, refined
    /// with parabolic interpolation. Returns one frame per <see cref="HopSeconds"/>.
    /// </summary>
    internal static List<PitchFrame> DetectPitch(float[] samples, int sampleRate)
    {
        var decimation = Math.Max(1, (int)(sampleRate / DetectionSampleRate));
        var decimatedRate = sampleRate / (double)decimation;
        var decimated = new float[samples.Length / decimation];
        for (var decimatedIndex = 0; decimatedIndex < decimated.Length; decimatedIndex++)
        {
            var sum = 0f;
            for (var offset = 0; offset < decimation; offset++)
            {
                sum += samples[decimatedIndex * decimation + offset];
            }
            decimated[decimatedIndex] = sum / decimation;
        }

        var minimumLag = Math.Max(2, (int)(decimatedRate / HighestDetectedHertz));
        var maximumLag = (int)Math.Ceiling(decimatedRate / LowestDetectedHertz);
        var windowLength = maximumLag;
        var hop = Math.Max(1, (int)(HopSeconds * decimatedRate));
        var difference = new double[maximumLag + 2];

        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }
        var silenceEnergy = Math.Max(1e-9, peak * peak * 1e-4) * windowLength;

        var frames = new List<PitchFrame>();
        for (var windowStart = 0; windowStart + windowLength + maximumLag + 1 < decimated.Length; windowStart += hop)
        {
            var centreFrame = (int)((windowStart + windowLength / 2.0) * decimation);
            double energy = 0;
            for (var index = 0; index < windowLength; index++)
            {
                energy += decimated[windowStart + index] * decimated[windowStart + index];
            }
            if (energy < silenceEnergy)
            {
                frames.Add(new PitchFrame(centreFrame, 0));
                continue;
            }

            for (var lag = 1; lag <= maximumLag + 1; lag++)
            {
                double lagSum = 0;
                for (var index = 0; index < windowLength; index++)
                {
                    var delta = decimated[windowStart + index] - decimated[windowStart + index + lag];
                    lagSum += delta * delta;
                }
                difference[lag] = lagSum;
            }
            // Cumulative mean normalized difference, in place.
            difference[0] = 1;
            double runningSum = 0;
            for (var lag = 1; lag <= maximumLag + 1; lag++)
            {
                runningSum += difference[lag];
                difference[lag] = runningSum > 0 ? difference[lag] * lag / runningSum : 1;
            }

            var chosenLag = -1;
            for (var lag = minimumLag; lag <= maximumLag; lag++)
            {
                if (difference[lag] < YinThreshold)
                {
                    while (lag + 1 <= maximumLag && difference[lag + 1] < difference[lag])
                    {
                        lag++;
                    }
                    chosenLag = lag;
                    break;
                }
            }
            if (chosenLag < 0)
            {
                frames.Add(new PitchFrame(centreFrame, 0));
                continue;
            }
            var before = difference[chosenLag - 1];
            var at = difference[chosenLag];
            var after = difference[chosenLag + 1];
            var curvature = before - 2 * at + after;
            var refinedLag = chosenLag + (Math.Abs(curvature) > 1e-12 ? Math.Clamp(0.5 * (before - after) / curvature, -0.5, 0.5) : 0);
            frames.Add(new PitchFrame(centreFrame, refinedLag * decimation));
        }
        return frames;
    }

    /// <summary>
    /// The playback-rate factor per frame (1 when unvoiced): the nearest allowed note,
    /// held while the pitch stays near it (so vibrato does not flip between notes), and
    /// approached with a one-pole glide of <paramref name="retuneMilliseconds"/>.
    /// </summary>
    internal static double[] CorrectionRatios(IReadOnlyList<PitchFrame> frames, int sampleRate, MusicalKey? key, double retuneMilliseconds)
    {
        var ratios = new double[frames.Count];
        var glide = retuneMilliseconds <= 0 ? 1 : 1 - Math.Exp(-HopSeconds * 1000 / retuneMilliseconds);
        int? heldTarget = null;
        double correctionSemitones = 0;
        var gliding = false;
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var frame = frames[frameIndex];
            if (frame.PeriodFrames <= 0)
            {
                ratios[frameIndex] = 1;
                heldTarget = null;
                gliding = false;
                continue;
            }
            var detectedMidi = 69 + 12 * Math.Log2(sampleRate / frame.PeriodFrames / 440);
            if (heldTarget is not { } target || Math.Abs(detectedMidi - target) > 0.65 || !Allowed(target, key))
            {
                target = NearestAllowed(detectedMidi, key);
                heldTarget = target;
            }
            var wantedCorrection = target - detectedMidi;
            // A new phrase glides in from the sung pitch, like a held note glides to a new target.
            var startingCorrection = gliding ? correctionSemitones : 0;
            correctionSemitones = startingCorrection + (wantedCorrection - startingCorrection) * glide;
            gliding = true;
            ratios[frameIndex] = Math.Pow(2, correctionSemitones / 12);
        }
        return ratios;
    }

    private static bool Allowed(int midiNote, MusicalKey? key) => key is not { } musicalKey || musicalKey.Contains(midiNote);

    private static int NearestAllowed(double midi, MusicalKey? key)
    {
        var rounded = (int)Math.Round(midi);
        if (key is null)
        {
            return rounded;
        }
        for (var distance = 0; distance <= 6; distance++)
        {
            var below = rounded - distance;
            var above = rounded + distance;
            var belowAllowed = Allowed(below, key);
            var aboveAllowed = Allowed(above, key);
            if (belowAllowed && aboveAllowed)
            {
                return midi - below <= above - midi ? below : above;
            }
            if (belowAllowed)
            {
                return below;
            }
            if (aboveAllowed)
            {
                return above;
            }
        }
        return rounded;
    }

    /// <summary>
    /// TD-PSOLA: analysis marks one period apart through the source, synthesis marks
    /// one period / ratio apart through the output; each synthesis mark copies the
    /// nearest analysis mark's two-period Hann grain. The window sum is divided out,
    /// so a ratio of 1 gives back the input exactly.
    /// </summary>
    private static float[] Resynthesize(float[] source, int sampleRate, IReadOnlyList<PitchFrame> frames, double[] ratios)
    {
        var output = new double[source.Length];
        var weights = new double[source.Length];
        if (frames.Count == 0)
        {
            return (float[])source.Clone();
        }

        double PeriodAt(double frame)
        {
            var frameIndex = FrameIndexAt(frames, frame);
            var period = frames[frameIndex].PeriodFrames;
            return period > 0 ? period : UnvoicedPeriodSeconds * sampleRate;
        }
        double RatioAt(double frame) => ratios[FrameIndexAt(frames, frame)];

        var analysisMarks = new List<double>();
        for (double mark = 0; mark < source.Length; mark += Math.Max(2, PeriodAt(mark)))
        {
            analysisMarks.Add(mark);
        }

        var nearestAnalysis = 0;
        for (double synthesisMark = 0; synthesisMark < source.Length;)
        {
            while (nearestAnalysis + 1 < analysisMarks.Count &&
                   Math.Abs(analysisMarks[nearestAnalysis + 1] - synthesisMark) <= Math.Abs(analysisMarks[nearestAnalysis] - synthesisMark))
            {
                nearestAnalysis++;
            }
            var analysisMark = analysisMarks[nearestAnalysis];
            var period = PeriodAt(analysisMark);
            var halfGrain = (int)Math.Ceiling(period);
            var shift = (int)Math.Round(synthesisMark - analysisMark);
            for (var offset = -halfGrain; offset <= halfGrain; offset++)
            {
                var sourceIndex = (int)Math.Round(analysisMark) + offset;
                var outputIndex = sourceIndex + shift;
                if (sourceIndex < 0 || sourceIndex >= source.Length || outputIndex < 0 || outputIndex >= output.Length)
                {
                    continue;
                }
                var window = 0.5 + 0.5 * Math.Cos(Math.PI * offset / (halfGrain + 1));
                output[outputIndex] += source[sourceIndex] * window;
                weights[outputIndex] += window;
            }
            synthesisMark += Math.Max(2, period / RatioAt(synthesisMark));
        }

        var result = new float[source.Length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = weights[index] > 1e-3 ? (float)(output[index] / weights[index]) : source[index];
        }
        return result;
    }

    private static int FrameIndexAt(IReadOnlyList<PitchFrame> frames, double sourceFrame)
    {
        var low = 0;
        var high = frames.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (frames[middle].CentreFrame <= sourceFrame)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }
        if (low + 1 < frames.Count && Math.Abs(frames[low + 1].CentreFrame - sourceFrame) < Math.Abs(frames[low].CentreFrame - sourceFrame))
        {
            low++;
        }
        return low;
    }
}
