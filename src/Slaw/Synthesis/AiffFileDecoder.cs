using System.Text;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// Reads AIFF and AIFF-C, which is what half of all sample packs arrive in because
/// someone in 1988 used a Mac. Uncompressed PCM and float only; everything is mixed
/// to mono, as every other sample in slaw is.
/// </summary>
internal static class AiffFileDecoder
{
    public static SampleData Decode(byte[] fileBytes, string sourcePath)
    {
        if (fileBytes.Length < 12 || Tag(fileBytes, 0) != "FORM" || Tag(fileBytes, 8) is not ("AIFF" or "AIFC"))
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)} is not an AIFF file.");
        }

        var channelCount = 0;
        var bitsPerSample = 0;
        var sampleRate = 0;
        var compression = "NONE";
        var audioStart = -1;
        var audioLength = 0;
        for (var chunkStart = 12; chunkStart + 8 <= fileBytes.Length;)
        {
            var chunkTag = Tag(fileBytes, chunkStart);
            var chunkLength = (int)Math.Min(ReadBigEndianUnsigned(fileBytes, chunkStart + 4, 4), fileBytes.Length - chunkStart - 8);
            var chunkBody = chunkStart + 8;
            if (chunkTag == "COMM" && chunkLength >= 18)
            {
                channelCount = (int)ReadBigEndianUnsigned(fileBytes, chunkBody, 2);
                bitsPerSample = (int)ReadBigEndianUnsigned(fileBytes, chunkBody + 6, 2);
                sampleRate = (int)Math.Round(ReadExtendedFloat(fileBytes, chunkBody + 8));
                if (chunkLength >= 22)
                {
                    compression = Tag(fileBytes, chunkBody + 18);
                }
            }
            else if (chunkTag == "SSND" && chunkLength >= 8)
            {
                var blockOffset = (int)ReadBigEndianUnsigned(fileBytes, chunkBody, 4);
                audioStart = chunkBody + 8 + blockOffset;
                audioLength = Math.Max(0, chunkLength - 8 - blockOffset);
            }
            chunkStart = chunkBody + chunkLength + (chunkLength & 1);
        }

        if (channelCount <= 0 || sampleRate <= 0 || audioStart < 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)} is missing its AIFF format or audio chunk.");
        }

        var encoding = compression.ToUpperInvariant() switch
        {
            "NONE" or "TWOS" => (Float: false, LittleEndian: false),
            "SOWT" => (Float: false, LittleEndian: true),
            "FL32" or "FL64" => (Float: true, LittleEndian: false),
            _ => throw new InvalidDataException($"{Path.GetFileName(sourcePath)} uses AIFF compression '{compression}', which slaw cannot read. Export it as uncompressed AIFF, WAV or FLAC."),
        };
        if (compression.Equals("FL64", StringComparison.OrdinalIgnoreCase))
        {
            bitsPerSample = 64;
        }
        else if (compression.Equals("FL32", StringComparison.OrdinalIgnoreCase))
        {
            bitsPerSample = 32;
        }
        var bytesPerSample = (bitsPerSample + 7) / 8;
        if (bytesPerSample is < 1 or > 8 || (!encoding.Float && bytesPerSample > 4))
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)} has an unsupported AIFF bit depth of {bitsPerSample}.");
        }

        var frameBytes = bytesPerSample * channelCount;
        var frameCount = Math.Min(audioLength / frameBytes, SampleLibrary.MaximumDecodedSeconds * sampleRate);
        var monoSamples = new float[frameCount];
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var mixedSample = 0.0;
            for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                mixedSample += ReadSample(fileBytes, audioStart + frameIndex * frameBytes + channelIndex * bytesPerSample, bytesPerSample, encoding.Float, encoding.LittleEndian);
            }
            monoSamples[frameIndex] = (float)(mixedSample / channelCount);
        }
        return new SampleData(monoSamples, sampleRate, sourcePath);
    }

    private static double ReadSample(byte[] fileBytes, int offset, int byteCount, bool isFloat, bool littleEndian)
    {
        ulong rawValue = 0;
        for (var byteIndex = 0; byteIndex < byteCount; byteIndex++)
        {
            rawValue = (rawValue << 8) | fileBytes[littleEndian ? offset + byteCount - 1 - byteIndex : offset + byteIndex];
        }
        if (isFloat)
        {
            return byteCount == 4 ? BitConverter.UInt32BitsToSingle((uint)rawValue) : BitConverter.UInt64BitsToDouble(rawValue);
        }
        var bitCount = byteCount * 8;
        var signedValue = (long)(rawValue << (64 - bitCount)) >> (64 - bitCount);
        return signedValue / (double)(1L << (bitCount - 1));
    }

    private static string Tag(byte[] fileBytes, int offset) => Encoding.ASCII.GetString(fileBytes, offset, 4);

    private static long ReadBigEndianUnsigned(byte[] fileBytes, int offset, int byteCount)
    {
        long value = 0;
        for (var byteIndex = 0; byteIndex < byteCount; byteIndex++)
        {
            value = (value << 8) | fileBytes[offset + byteIndex];
        }
        return value;
    }

    /// <summary>The sample rate is stored as an 80-bit extended float, for reasons lost to history.</summary>
    private static double ReadExtendedFloat(byte[] fileBytes, int offset)
    {
        var signAndExponent = (int)ReadBigEndianUnsigned(fileBytes, offset, 2);
        var exponent = (signAndExponent & 0x7FFF) - 16383;
        ulong mantissa = 0;
        for (var byteIndex = 0; byteIndex < 8; byteIndex++)
        {
            mantissa = (mantissa << 8) | fileBytes[offset + 2 + byteIndex];
        }
        var magnitude = mantissa * Math.Pow(2, exponent - 63);
        return (signAndExponent & 0x8000) != 0 ? -magnitude : magnitude;
    }
}
