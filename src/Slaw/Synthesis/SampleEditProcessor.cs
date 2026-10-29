using System.Collections.Concurrent;
using System.Globalization;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// Applies a sampler track's non-destructive edit list to its source sample and
/// caches the result per (file, edits, rate). The source WAV is never modified;
/// the edits live in the project, where undo can reach them.
/// </summary>
internal static class SampleEditProcessor
{
    public static readonly IReadOnlyList<string> OperationTypes = new[]
    {
        "trim", "delete", "silence", "reverse", "gain", "normalize", "fadeIn", "fadeOut", "duplicate",
    };

    private const int MaximumCachedEntries = 48;
    private static readonly ConcurrentDictionary<string, SampleData> ProcessedCache = new();
    private static readonly ConcurrentDictionary<string, byte> PendingComputations = new();

    /// <summary>A stable fingerprint of the edit list, for cache keys.</summary>
    public static string Fingerprint(SampleEditSettings? editSettings) =>
        editSettings is null || editSettings.Operations.Count == 0
            ? "original"
            : string.Join(';', editSettings.Operations.Select(operation => string.Create(CultureInfo.InvariantCulture,
                $"{operation.Type}:{operation.StartSeconds:R}:{operation.EndSeconds:R}:{operation.Amount:R}")));

    /// <summary>Blocking: the edited sample at the source file's own rate (for the editor).</summary>
    public static SampleData LoadProcessedNative(string samplePath, SampleEditSettings? editSettings)
    {
        var sourceSample = SampleLibrary.Load(samplePath);
        return ProcessCached(sourceSample, editSettings, $"native|{sourceSample.SourcePath}|{Fingerprint(editSettings)}");
    }

    /// <summary>Blocking: the edited sample at <paramref name="targetSampleRate"/> (for bounces and auditions).</summary>
    public static SampleData LoadProcessed(string samplePath, SampleEditSettings? editSettings, int targetSampleRate)
    {
        var sourceSample = SampleLibrary.LoadResampled(samplePath, targetSampleRate);
        return ProcessCached(sourceSample, editSettings, $"{targetSampleRate}|{sourceSample.SourcePath}|{Fingerprint(editSettings)}");
    }

    /// <summary>
    /// Non-blocking, for the audio thread: the cached edited sample, or null while
    /// the source is still decoding or the edits are being applied in the background.
    /// </summary>
    public static SampleData? TryGetProcessed(string samplePath, SampleEditSettings? editSettings, int targetSampleRate)
    {
        if (editSettings is null || editSettings.Operations.Count == 0)
        {
            return SampleLibrary.TryGetResampled(samplePath, targetSampleRate);
        }
        var sourceSample = SampleLibrary.TryGetResampled(samplePath, targetSampleRate);
        if (sourceSample is null)
        {
            return null;
        }
        var cacheKey = $"{targetSampleRate}|{sourceSample.SourcePath}|{Fingerprint(editSettings)}";
        if (ProcessedCache.TryGetValue(cacheKey, out var processedSample))
        {
            return processedSample;
        }
        if (PendingComputations.TryAdd(cacheKey, 0))
        {
            var operationsSnapshot = editSettings.Operations.Select(CloneOperation).ToList();
            _ = Task.Run(() =>
            {
                try
                {
                    ProcessCached(sourceSample, new SampleEditSettings { Operations = operationsSnapshot }, cacheKey);
                }
                finally
                {
                    PendingComputations.TryRemove(cacheKey, out _);
                }
            });
        }
        return null;
    }

    private static SampleEditOperation CloneOperation(SampleEditOperation operation) => new()
    {
        Type = operation.Type,
        StartSeconds = operation.StartSeconds,
        EndSeconds = operation.EndSeconds,
        Amount = operation.Amount,
    };

    private static SampleData ProcessCached(SampleData sourceSample, SampleEditSettings? editSettings, string cacheKey)
    {
        if (editSettings is null || editSettings.Operations.Count == 0)
        {
            return sourceSample;
        }
        if (ProcessedCache.TryGetValue(cacheKey, out var cachedSample))
        {
            return cachedSample;
        }
        var processedSample = new SampleData(
            Apply(sourceSample.Samples, sourceSample.SampleRate, editSettings.Operations),
            sourceSample.SampleRate,
            sourceSample.SourcePath);
        if (ProcessedCache.Count >= MaximumCachedEntries)
        {
            foreach (var evictedKey in ProcessedCache.Keys.Take(MaximumCachedEntries / 2).ToList())
            {
                ProcessedCache.TryRemove(evictedKey, out _);
            }
        }
        ProcessedCache[cacheKey] = processedSample;
        return processedSample;
    }

    /// <summary>Applies every operation in order. Pure: the input array is never modified.</summary>
    public static float[] Apply(float[] sourceSamples, int sampleRate, IReadOnlyList<SampleEditOperation> operations)
    {
        var workingSamples = sourceSamples;
        foreach (var operation in operations)
        {
            workingSamples = ApplyOperation(workingSamples, sampleRate, operation);
        }
        return workingSamples;
    }

    private static float[] ApplyOperation(float[] inputSamples, int sampleRate, SampleEditOperation operation)
    {
        var regionStart = Math.Clamp((int)Math.Round(Math.Min(operation.StartSeconds, operation.EndSeconds) * sampleRate), 0, inputSamples.Length);
        var regionEnd = Math.Clamp((int)Math.Round(Math.Max(operation.StartSeconds, operation.EndSeconds) * sampleRate), 0, inputSamples.Length);
        var regionLength = regionEnd - regionStart;
        if (regionLength <= 0)
        {
            return inputSamples;
        }

        switch (operation.Type)
        {
            case "trim":
                return inputSamples[regionStart..regionEnd];

            case "delete":
                {
                    if (regionLength >= inputSamples.Length)
                    {
                        return inputSamples;
                    }
                    var outputSamples = new float[inputSamples.Length - regionLength];
                    Array.Copy(inputSamples, 0, outputSamples, 0, regionStart);
                    Array.Copy(inputSamples, regionEnd, outputSamples, regionStart, inputSamples.Length - regionEnd);
                    // A 3 ms crossfade-ish smoothing at the seam, so the cut does not click.
                    var seamRadius = Math.Min(sampleRate * 3 / 1000, Math.Min(regionStart, outputSamples.Length - regionStart));
                    for (var seamOffset = -seamRadius; seamOffset < seamRadius; seamOffset++)
                    {
                        var seamIndex = regionStart + seamOffset;
                        var seamWeight = 1 - Math.Abs(seamOffset) / (double)Math.Max(1, seamRadius);
                        var neighbourAverage = (outputSamples[Math.Max(0, seamIndex - 1)] + outputSamples[Math.Min(outputSamples.Length - 1, seamIndex + 1)]) / 2;
                        outputSamples[seamIndex] = (float)(outputSamples[seamIndex] * (1 - seamWeight * 0.5) + neighbourAverage * seamWeight * 0.5);
                    }
                    return outputSamples;
                }

            case "duplicate":
                {
                    var outputSamples = new float[inputSamples.Length + regionLength];
                    Array.Copy(inputSamples, 0, outputSamples, 0, regionEnd);
                    Array.Copy(inputSamples, regionStart, outputSamples, regionEnd, regionLength);
                    Array.Copy(inputSamples, regionEnd, outputSamples, regionEnd + regionLength, inputSamples.Length - regionEnd);
                    return outputSamples;
                }
        }

        var editedSamples = (float[])inputSamples.Clone();
        var editedRegion = editedSamples.AsSpan(regionStart, regionLength);
        switch (operation.Type)
        {
            case "silence":
                editedRegion.Clear();
                break;
            case "reverse":
                editedRegion.Reverse();
                break;
            case "gain":
                {
                    var linearGain = (float)Math.Pow(10, operation.Amount / 20);
                    for (var regionIndex = 0; regionIndex < editedRegion.Length; regionIndex++)
                    {
                        editedRegion[regionIndex] = Math.Clamp(editedRegion[regionIndex] * linearGain, -1f, 1f);
                    }
                    break;
                }
            case "normalize":
                {
                    var regionPeak = 0f;
                    foreach (var regionSample in editedRegion)
                    {
                        regionPeak = Math.Max(regionPeak, Math.Abs(regionSample));
                    }
                    if (regionPeak > 1e-6f)
                    {
                        var targetPeak = (float)Math.Pow(10, Math.Min(0, operation.Amount) / 20);
                        var normalizationGain = targetPeak / regionPeak;
                        for (var regionIndex = 0; regionIndex < editedRegion.Length; regionIndex++)
                        {
                            editedRegion[regionIndex] *= normalizationGain;
                        }
                    }
                    break;
                }
            case "fadeIn":
                for (var regionIndex = 0; regionIndex < editedRegion.Length; regionIndex++)
                {
                    editedRegion[regionIndex] *= (float)regionIndex / editedRegion.Length;
                }
                break;
            case "fadeOut":
                for (var regionIndex = 0; regionIndex < editedRegion.Length; regionIndex++)
                {
                    editedRegion[regionIndex] *= 1 - (float)regionIndex / editedRegion.Length;
                }
                break;
        }
        return editedSamples;
    }

    /// <summary>
    /// Finds likely note onsets: frames where short-term energy jumps well above
    /// its recent average. Returns times in seconds, at least
    /// <paramref name="minimumSpacingSeconds"/> apart. <paramref name="sensitivity"/>
    /// in [0, 1]; higher finds more transients.
    /// </summary>
    public static List<double> DetectTransients(float[] samples, int sampleRate, double sensitivity = 0.5, double minimumSpacingSeconds = 0.06)
    {
        var detectedTransients = new List<double>();
        var hopFrameCount = Math.Max(1, sampleRate / 200);
        var windowCount = samples.Length / hopFrameCount;
        if (windowCount < 4)
        {
            return detectedTransients;
        }
        var windowEnergies = new double[windowCount];
        for (var windowIndex = 0; windowIndex < windowCount; windowIndex++)
        {
            double windowEnergy = 0;
            for (var frameOffset = 0; frameOffset < hopFrameCount; frameOffset++)
            {
                var frameSample = samples[windowIndex * hopFrameCount + frameOffset];
                windowEnergy += frameSample * frameSample;
            }
            windowEnergies[windowIndex] = windowEnergy / hopFrameCount;
        }

        var onsetRatioThreshold = 1.5 + (1 - Math.Clamp(sensitivity, 0, 1)) * 6;
        var absoluteFloor = windowEnergies.Max() * 0.002;
        var lastTransientSeconds = double.NegativeInfinity;
        double runningAverage = windowEnergies[0];
        for (var windowIndex = 1; windowIndex < windowCount; windowIndex++)
        {
            var windowEnergy = windowEnergies[windowIndex];
            var windowSeconds = (double)windowIndex * hopFrameCount / sampleRate;
            if (windowEnergy > absoluteFloor &&
                windowEnergy > runningAverage * onsetRatioThreshold &&
                windowSeconds - lastTransientSeconds >= minimumSpacingSeconds)
            {
                detectedTransients.Add(windowSeconds);
                lastTransientSeconds = windowSeconds;
            }
            runningAverage = runningAverage * 0.85 + windowEnergy * 0.15;
        }
        return detectedTransients;
    }

    /// <summary>
    /// The [start, end) frame range a note plays in slice mode, or null if the
    /// note falls outside the slices. Slice i belongs to note root + i.
    /// </summary>
    public static (int StartFrame, int EndFrame)? SliceRegionFor(SampleEditSettings editSettings, int sampleFrameCount, int sampleRate, int midiNote, int rootNote)
    {
        var sliceBoundaries = editSettings.SlicePointsSeconds
            .Select(sliceSeconds => (int)(sliceSeconds * sampleRate))
            .Where(sliceFrame => sliceFrame > 0 && sliceFrame < sampleFrameCount)
            .Distinct()
            .Order()
            .Prepend(0)
            .Append(sampleFrameCount)
            .ToList();
        var sliceIndex = midiNote - rootNote;
        if (sliceIndex < 0 || sliceIndex >= sliceBoundaries.Count - 1)
        {
            return null;
        }
        return (sliceBoundaries[sliceIndex], sliceBoundaries[sliceIndex + 1]);
    }
}
