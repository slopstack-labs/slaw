using System.Collections.Concurrent;
using System.Text;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>Decoded mono sample data at a known rate.</summary>
internal sealed class SampleData
{
    public SampleData(float[] samples, int sampleRate, string sourcePath)
    {
        Samples = samples;
        SampleRate = sampleRate;
        SourcePath = sourcePath;
    }

    /// <summary>Mono samples in [-1, 1].</summary>
    public float[] Samples { get; }
    public int SampleRate { get; }
    /// <summary>Full path of the file this audio was extracted from.</summary>
    public string SourcePath { get; }
    public double DurationSeconds => SampleRate > 0 ? (double)Samples.Length / SampleRate : 0;
}

/// <summary>One folder of scanned samples, labelled relative to its sample folder.</summary>
internal sealed record SampleFolderGroup(string Label, string FolderPath, IReadOnlyList<string> SamplePaths);

/// <summary>
/// Loads, caches and resamples user-supplied WAV files. Every cache entry is
/// a stakeholder-owned asset that we hold onto responsibly, right up until
/// the eviction policy decides otherwise.
/// </summary>
internal static class SampleLibrary
{
    private const int MaximumCachedEntriesPerCache = 64;
    private const int MaximumDecodedSeconds = 600;

    private static readonly ConcurrentDictionary<(string FullPath, long WriteTicks), SampleData> nativeRateCache = new();
    private static readonly ConcurrentDictionary<(string FullPath, long WriteTicks, int Rate), SampleData> resampledCache = new();
    private static readonly ConcurrentDictionary<(string FullPath, int Rate), byte> backgroundLoadsAlreadyDispatched = new();
    private static readonly ConcurrentDictionary<string, string> loadFailureByPath = new();

    public static readonly IReadOnlyList<string> SupportedExtensions = new[] { ".wav", ".wave" };

    public static bool IsSupportedFile(string path)
    {
        var observedExtension = Path.GetExtension(path ?? string.Empty);
        foreach (var supportedExtension in SupportedExtensions)
        {
            if (string.Equals(observedExtension, supportedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Blocking decode at the file's native rate, cached by path and last write time.</summary>
    public static SampleData Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var writeTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
        var cacheKey = (fullPath, writeTicks);
        if (nativeRateCache.TryGetValue(cacheKey, out var cachedSample))
        {
            return cachedSample;
        }

        var decodedSample = DecodeWaveBytes(File.ReadAllBytes(fullPath), fullPath);
        EvictIfOversubscribed(nativeRateCache);
        nativeRateCache[cacheKey] = decodedSample;
        return decodedSample;
    }

    /// <summary>Blocking load followed by linear-interpolation resampling.</summary>
    public static SampleData LoadResampled(string path, int targetSampleRate)
    {
        if (targetSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate), "A sample rate of zero is a bold strategic pivot, but not a supported one.");
        }

        var fullPath = Path.GetFullPath(path);
        var writeTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;
        var cacheKey = (fullPath, writeTicks, targetSampleRate);
        if (resampledCache.TryGetValue(cacheKey, out var cachedSample))
        {
            return cachedSample;
        }

        var nativeSample = Load(fullPath);
        var resampledSample = nativeSample.SampleRate == targetSampleRate
            ? nativeSample
            : new SampleData(ResampleLinearly(nativeSample.Samples, nativeSample.SampleRate, targetSampleRate), targetSampleRate, fullPath);
        EvictIfOversubscribed(resampledCache);
        resampledCache[cacheKey] = resampledSample;
        loadFailureByPath.TryRemove(fullPath, out _);
        return resampledSample;
    }

    /// <summary>
    /// Real-time-safe lookup: returns cached data if ready, otherwise schedules a
    /// one-time background load and returns null. Never throws, never touches disk here.
    /// </summary>
    public static SampleData? TryGetResampled(string path, int targetSampleRate)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || targetSampleRate <= 0)
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            foreach (var cacheEntry in resampledCache)
            {
                if (cacheEntry.Key.Rate == targetSampleRate && cacheEntry.Key.FullPath == fullPath)
                {
                    return cacheEntry.Value;
                }
            }

            if (backgroundLoadsAlreadyDispatched.TryAdd((fullPath, targetSampleRate), 0))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        LoadResampled(fullPath, targetSampleRate);
                    }
                    catch (Exception remediationOpportunity)
                    {
                        loadFailureByPath[fullPath] = remediationOpportunity.Message;
                    }
                });
            }
        }
        catch (Exception)
        {
            // The audio thread has no time for post-mortems.
        }
        return null;
    }

    public static string? LoadFailure(string path)
    {
        try
        {
            return loadFailureByPath.TryGetValue(Path.GetFullPath(path), out var failureMessage) ? failureMessage : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> Scan(IEnumerable<string> folders, int maximumFiles = 2000)
    {
        var enumerationOptions = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var discoveredPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }
            try
            {
                foreach (var candidatePath in Directory.EnumerateFiles(folder, "*", enumerationOptions))
                {
                    if (IsSupportedFile(candidatePath))
                    {
                        discoveredPaths.Add(Path.GetFullPath(candidatePath));
                    }
                }
            }
            catch (Exception)
            {
                // A folder that vanishes mid-scan has simply opted out of the initiative.
            }
        }

        return discoveredPaths
            .OrderBy(discoveredPath => Path.GetFileName(discoveredPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(discoveredPath => discoveredPath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maximumFiles))
            .ToList();
    }

    /// <summary>
    /// Groups scanned samples by the folder they sit in, labelled relative to the
    /// most specific sample folder that contains them ("BPB Cassette 909 / clean").
    /// Groups are ordered by label, samples within a group by file name.
    /// </summary>
    public static IReadOnlyList<SampleFolderGroup> GroupByFolder(IEnumerable<string> samplePaths, IEnumerable<string> sampleFolders)
    {
        var normalizedRootFolders = sampleFolders
            .Where(sampleFolder => !string.IsNullOrWhiteSpace(sampleFolder))
            .Select(sampleFolder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(sampleFolder)))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(sampleFolder => sampleFolder.Length)
            .ToList();
        return samplePaths
            .GroupBy(samplePath => Path.GetDirectoryName(samplePath) ?? string.Empty, StringComparer.Ordinal)
            .Select(folderGrouping => new SampleFolderGroup(
                DescribeFolder(folderGrouping.Key, normalizedRootFolders),
                folderGrouping.Key,
                folderGrouping.OrderBy(samplePath => Path.GetFileName(samplePath), StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(folderGroup => folderGroup.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(folderGroup => folderGroup.FolderPath, StringComparer.Ordinal)
            .ToList();
    }

    private static string DescribeFolder(string folderPath, IReadOnlyList<string> rootFoldersLongestFirst)
    {
        foreach (var rootFolder in rootFoldersLongestFirst)
        {
            var relativeFolder = Path.GetRelativePath(rootFolder, folderPath);
            var escapesRootFolder = relativeFolder == ".." || relativeFolder.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                                    Path.IsPathRooted(relativeFolder);
            if (!escapesRootFolder)
            {
                var rootName = Path.GetFileName(rootFolder);
                var labelParts = new List<string> { rootName.Length > 0 ? rootName : rootFolder };
                if (relativeFolder != ".")
                {
                    labelParts.AddRange(relativeFolder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
                }
                return string.Join(" / ", labelParts);
            }
        }
        // Not under any registered folder (e.g. dropped in directly): the folder speaks for itself.
        return folderPath;
    }

    public static StereoBuffer ToStereoBuffer(SampleData sampleData)
    {
        var previewBuffer = new StereoBuffer(sampleData.Samples.Length, sampleData.SampleRate);
        Array.Copy(sampleData.Samples, previewBuffer.Left, sampleData.Samples.Length);
        Array.Copy(sampleData.Samples, previewBuffer.Right, sampleData.Samples.Length);
        return previewBuffer;
    }

    private static void EvictIfOversubscribed<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache) where TKey : notnull
    {
        while (cache.Count >= MaximumCachedEntriesPerCache)
        {
            var evictionCandidateWasFound = false;
            foreach (var cacheKey in cache.Keys)
            {
                cache.TryRemove(cacheKey, out _);
                evictionCandidateWasFound = true;
                break;
            }
            if (!evictionCandidateWasFound)
            {
                return;
            }
        }
    }

    private static float[] ResampleLinearly(float[] sourceSamples, int sourceRate, int targetRate)
    {
        if (sourceSamples.Length == 0)
        {
            return Array.Empty<float>();
        }

        var targetLength = (int)Math.Max(1, Math.Round((double)sourceSamples.Length * targetRate / sourceRate));
        var resampledSamples = new float[targetLength];
        var sourceStepPerTargetFrame = (double)sourceRate / targetRate;
        for (var targetIndex = 0; targetIndex < targetLength; targetIndex++)
        {
            var sourcePosition = targetIndex * sourceStepPerTargetFrame;
            var lowerIndex = (int)sourcePosition;
            if (lowerIndex >= sourceSamples.Length - 1)
            {
                resampledSamples[targetIndex] = sourceSamples[^1];
                continue;
            }
            var fraction = (float)(sourcePosition - lowerIndex);
            resampledSamples[targetIndex] = sourceSamples[lowerIndex] + (sourceSamples[lowerIndex + 1] - sourceSamples[lowerIndex]) * fraction;
        }
        return resampledSamples;
    }

    private static SampleData DecodeWaveBytes(byte[] fileBytes, string fullPath)
    {
        if (fileBytes.Length < 12
            || !HasTag(fileBytes, 0, "RIFF")
            || !HasTag(fileBytes, 8, "WAVE"))
        {
            if (fileBytes.Length >= 12 && (HasTag(fileBytes, 0, "RF64") || HasTag(fileBytes, 0, "BW64")))
            {
                throw new InvalidDataException("RF64 files are not supported; only plain RIFF/WAVE made it past procurement.");
            }
            throw new InvalidDataException("not a RIFF/WAVE file; only uncompressed WAV is supported.");
        }

        var formatTag = 0;
        var channelCount = 0;
        var sampleRate = 0;
        var bitsPerSample = 0;
        var formatChunkSeen = false;
        var dataOffset = -1;
        var dataLength = 0;

        var chunkOffset = 12;
        while (chunkOffset + 8 <= fileBytes.Length)
        {
            var declaredSize = BitConverter.ToUInt32(fileBytes, chunkOffset + 4);
            var bodyOffset = chunkOffset + 8;
            var availableBytes = (long)fileBytes.Length - bodyOffset;

            if (HasTag(fileBytes, chunkOffset, "fmt "))
            {
                if (declaredSize < 16 || availableBytes < 16)
                {
                    throw new InvalidDataException("the fmt chunk is truncated.");
                }
                formatTag = BitConverter.ToUInt16(fileBytes, bodyOffset);
                channelCount = BitConverter.ToUInt16(fileBytes, bodyOffset + 2);
                sampleRate = BitConverter.ToInt32(fileBytes, bodyOffset + 4);
                bitsPerSample = BitConverter.ToUInt16(fileBytes, bodyOffset + 14);
                if (formatTag == 0xFFFE)
                {
                    if (declaredSize < 40 || availableBytes < 26)
                    {
                        throw new InvalidDataException("the WAVE_FORMAT_EXTENSIBLE header is truncated.");
                    }
                    formatTag = BitConverter.ToUInt16(fileBytes, bodyOffset + 24);
                }
                formatChunkSeen = true;
            }
            else if (HasTag(fileBytes, chunkOffset, "data"))
            {
                dataOffset = bodyOffset;
                var streamingHeader = declaredSize == 0 || declaredSize == 0xFFFFFFFFu;
                dataLength = (int)(streamingHeader || declaredSize > availableBytes ? availableBytes : declaredSize);
                break;
            }

            var paddedSize = (long)declaredSize + (declaredSize & 1);
            var nextChunkOffset = bodyOffset + paddedSize;
            if (nextChunkOffset > fileBytes.Length)
            {
                break;
            }
            chunkOffset = (int)nextChunkOffset;
        }

        if (!formatChunkSeen)
        {
            throw new InvalidDataException("no fmt chunk found; this WAV skipped its own onboarding.");
        }
        if (dataOffset < 0)
        {
            throw new InvalidDataException("no data chunk found; this WAV is all header and no vibes.");
        }
        if (formatTag != 1 && formatTag != 3)
        {
            throw new InvalidDataException($"only uncompressed WAV is supported (format tag 0x{formatTag:X} looks like a compressed codec wearing a WAV costume).");
        }
        if (channelCount < 1 || sampleRate <= 0)
        {
            throw new InvalidDataException("the fmt chunk reports zero channels or a zero sample rate.");
        }
        var supportedDepth = formatTag == 1
            ? bitsPerSample is 8 or 16 or 24 or 32
            : bitsPerSample is 32 or 64;
        if (!supportedDepth)
        {
            throw new InvalidDataException($"{bitsPerSample}-bit {(formatTag == 1 ? "PCM" : "float")} audio is not supported.");
        }

        var bytesPerSample = bitsPerSample / 8;
        var bytesPerFrame = bytesPerSample * channelCount;
        var frameCount = dataLength / bytesPerFrame;
        var maximumFrames = (long)sampleRate * MaximumDecodedSeconds;
        if (frameCount > maximumFrames)
        {
            frameCount = (int)maximumFrames;
        }

        var monoSamples = new float[frameCount];
        var channelWeight = 1f / channelCount;
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var frameOffset = dataOffset + frameIndex * bytesPerFrame;
            var channelSum = 0f;
            for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                channelSum += ReadSample(fileBytes, frameOffset + channelIndex * bytesPerSample, formatTag, bitsPerSample);
            }
            monoSamples[frameIndex] = Math.Clamp(channelSum * channelWeight, -1f, 1f);
        }
        return new SampleData(monoSamples, sampleRate, fullPath);
    }

    private static float ReadSample(byte[] bytes, int offset, int formatTag, int bitsPerSample)
    {
        if (formatTag == 3)
        {
            return bitsPerSample == 32
                ? BitConverter.ToSingle(bytes, offset)
                : (float)BitConverter.ToDouble(bytes, offset);
        }
        switch (bitsPerSample)
        {
            case 8:
                return (bytes[offset] - 128) / 128f;
            case 16:
                return BitConverter.ToInt16(bytes, offset) / 32768f;
            case 24:
                var signedTwentyFour = (bytes[offset] << 8) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 24);
                return (signedTwentyFour >> 8) / 8388608f;
            default:
                return BitConverter.ToInt32(bytes, offset) / 2147483648f;
        }
    }

    private static bool HasTag(byte[] bytes, int offset, string tag)
    {
        if (offset + 4 > bytes.Length)
        {
            return false;
        }
        for (var tagIndex = 0; tagIndex < 4; tagIndex++)
        {
            if (bytes[offset + tagIndex] != (byte)tag[tagIndex])
            {
                return false;
            }
        }
        return true;
    }
}
