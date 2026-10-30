using System.Text;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// A complete FLAC decoder in one file, so sample packs can be lossless without
/// slaw shipping another native library: constant, verbatim, fixed and LPC
/// subframes, Rice partitions and every stereo decorrelation mode. Checksums are
/// skipped; a damaged file shows itself as an error or as noise, not as a crash.
/// </summary>
internal static class FlacFileDecoder
{
    private static readonly int[] BlockSizeCodes = { 0, 192, 576, 1152, 2304, 4608, 0, 0, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768 };
    private static readonly int[] SampleRateCodes = { 0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };
    private static readonly int[] BitDepthCodes = { 0, 8, 12, 0, 16, 20, 24, 32 };

    public static SampleData Decode(byte[] fileBytes, string sourcePath)
    {
        try
        {
            return DecodeStream(fileBytes, sourcePath);
        }
        catch (IndexOutOfRangeException)
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)} is a truncated or damaged FLAC file.");
        }
    }

    private static SampleData DecodeStream(byte[] fileBytes, string sourcePath)
    {
        var position = SkipId3Tag(fileBytes);
        if (fileBytes.Length < position + 42 || Encoding.ASCII.GetString(fileBytes, position, 4) != "fLaC")
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)} is not a FLAC file.");
        }
        position += 4;

        var streamSampleRate = 0;
        var streamBitDepth = 0;
        long declaredFrames = 0;
        bool lastMetadataBlock;
        do
        {
            lastMetadataBlock = (fileBytes[position] & 0x80) != 0;
            var blockType = fileBytes[position] & 0x7F;
            var blockLength = (fileBytes[position + 1] << 16) | (fileBytes[position + 2] << 8) | fileBytes[position + 3];
            if (blockType == 0)
            {
                var streamInfo = new FlacBitReader(fileBytes, position + 4 + 10);
                streamSampleRate = (int)streamInfo.ReadBits(20);
                streamInfo.ReadBits(3);
                streamBitDepth = (int)streamInfo.ReadBits(5) + 1;
                declaredFrames = streamInfo.ReadBits(36);
            }
            position += 4 + blockLength;
        }
        while (!lastMetadataBlock);

        if (streamSampleRate <= 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)} has no usable FLAC stream information.");
        }

        var frameLimit = (long)SampleLibrary.MaximumDecodedSeconds * streamSampleRate;
        var monoSamples = new List<float>((int)Math.Clamp(declaredFrames, 4096, frameLimit));
        var reader = new FlacBitReader(fileBytes, position);
        while (monoSamples.Count < frameLimit && reader.BytePosition + 6 <= fileBytes.Length)
        {
            DecodeFrame(reader, streamSampleRate, streamBitDepth, monoSamples, sourcePath);
        }
        return new SampleData(monoSamples.ToArray(), streamSampleRate, sourcePath);
    }

    private static int SkipId3Tag(byte[] fileBytes)
    {
        if (fileBytes.Length < 10 || fileBytes[0] != 'I' || fileBytes[1] != 'D' || fileBytes[2] != '3')
        {
            return 0;
        }
        return 10 + ((fileBytes[6] & 0x7F) << 21 | (fileBytes[7] & 0x7F) << 14 | (fileBytes[8] & 0x7F) << 7 | (fileBytes[9] & 0x7F));
    }

    private static void DecodeFrame(FlacBitReader reader, int streamSampleRate, int streamBitDepth, List<float> monoSamples, string sourcePath)
    {
        if (reader.ReadBits(14) != 0x3FFE)
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)}: lost FLAC frame sync, the file is damaged.");
        }
        reader.ReadBits(2);
        var blockSizeCode = (int)reader.ReadBits(4);
        var sampleRateCode = (int)reader.ReadBits(4);
        var channelAssignment = (int)reader.ReadBits(4);
        var bitDepthCode = (int)reader.ReadBits(3);
        reader.ReadBits(1);
        reader.SkipUtf8CodedNumber();

        var blockSize = blockSizeCode switch
        {
            6 => (int)reader.ReadBits(8) + 1,
            7 => (int)reader.ReadBits(16) + 1,
            _ => BlockSizeCodes[blockSizeCode],
        };
        switch (sampleRateCode)
        {
            case 12:
                reader.ReadBits(8);
                break;
            case 13:
            case 14:
                reader.ReadBits(16);
                break;
        }
        reader.ReadBits(8); // Header CRC.

        var bitDepth = bitDepthCode == 0 ? streamBitDepth : BitDepthCodes[bitDepthCode];
        if (blockSize <= 0 || bitDepth <= 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)} has an invalid FLAC frame header.");
        }

        var channelCount = channelAssignment < 8 ? channelAssignment + 1 : 2;
        var channels = new long[channelCount][];
        for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            // Side channels carry one extra bit.
            var carriesSide = (channelAssignment == 8 && channelIndex == 1) || (channelAssignment == 9 && channelIndex == 0) || (channelAssignment == 10 && channelIndex == 1);
            channels[channelIndex] = DecodeSubframe(reader, blockSize, bitDepth + (carriesSide ? 1 : 0), sourcePath);
        }
        reader.AlignToByte();
        reader.ReadBits(16); // Frame CRC.

        Undecorrelate(channels, channelAssignment, blockSize);
        var fullScale = (double)(1L << (bitDepth - 1));
        for (var frameIndex = 0; frameIndex < blockSize; frameIndex++)
        {
            var mixedSample = 0.0;
            foreach (var channel in channels)
            {
                mixedSample += channel[frameIndex];
            }
            monoSamples.Add((float)(mixedSample / channelCount / fullScale));
        }
    }

    private static void Undecorrelate(long[][] channels, int channelAssignment, int blockSize)
    {
        for (var frameIndex = 0; frameIndex < blockSize && channelAssignment >= 8; frameIndex++)
        {
            var first = channels[0][frameIndex];
            var second = channels[1][frameIndex];
            switch (channelAssignment)
            {
                case 8: // Left, side.
                    channels[1][frameIndex] = first - second;
                    break;
                case 9: // Side, right.
                    channels[0][frameIndex] = first + second;
                    break;
                case 10: // Mid, side.
                    var doubledMid = (first << 1) | (second & 1);
                    channels[0][frameIndex] = (doubledMid + second) >> 1;
                    channels[1][frameIndex] = (doubledMid - second) >> 1;
                    break;
            }
        }
    }

    private static long[] DecodeSubframe(FlacBitReader reader, int blockSize, int bitDepth, string sourcePath)
    {
        reader.ReadBits(1);
        var subframeType = (int)reader.ReadBits(6);
        var wastedBits = 0;
        if (reader.ReadBits(1) == 1)
        {
            wastedBits = 1 + reader.ReadUnary();
        }
        var effectiveBitDepth = bitDepth - wastedBits;

        var samples = new long[blockSize];
        if (subframeType == 0)
        {
            var constantValue = reader.ReadSigned(effectiveBitDepth);
            Array.Fill(samples, constantValue);
        }
        else if (subframeType == 1)
        {
            for (var frameIndex = 0; frameIndex < blockSize; frameIndex++)
            {
                samples[frameIndex] = reader.ReadSigned(effectiveBitDepth);
            }
        }
        else if (subframeType is >= 8 and <= 12)
        {
            DecodeFixed(reader, samples, subframeType - 8, effectiveBitDepth);
        }
        else if (subframeType >= 32)
        {
            DecodeLpc(reader, samples, subframeType - 31, effectiveBitDepth);
        }
        else
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)} uses a reserved FLAC subframe type.");
        }

        if (wastedBits > 0)
        {
            for (var frameIndex = 0; frameIndex < blockSize; frameIndex++)
            {
                samples[frameIndex] <<= wastedBits;
            }
        }
        return samples;
    }

    private static void DecodeFixed(FlacBitReader reader, long[] samples, int order, int bitDepth)
    {
        for (var frameIndex = 0; frameIndex < order; frameIndex++)
        {
            samples[frameIndex] = reader.ReadSigned(bitDepth);
        }
        DecodeResidual(reader, samples, order);
        for (var i = order; i < samples.Length; i++)
        {
            samples[i] += order switch
            {
                1 => samples[i - 1],
                2 => 2 * samples[i - 1] - samples[i - 2],
                3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                4 => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
                _ => 0,
            };
        }
    }

    private static void DecodeLpc(FlacBitReader reader, long[] samples, int order, int bitDepth)
    {
        for (var frameIndex = 0; frameIndex < order; frameIndex++)
        {
            samples[frameIndex] = reader.ReadSigned(bitDepth);
        }
        var coefficientPrecision = (int)reader.ReadBits(4) + 1;
        var quantizationShift = (int)reader.ReadSigned(5);
        var coefficients = new long[order];
        for (var coefficientIndex = 0; coefficientIndex < order; coefficientIndex++)
        {
            coefficients[coefficientIndex] = reader.ReadSigned(coefficientPrecision);
        }
        DecodeResidual(reader, samples, order);
        for (var i = order; i < samples.Length; i++)
        {
            long prediction = 0;
            for (var coefficientIndex = 0; coefficientIndex < order; coefficientIndex++)
            {
                prediction += coefficients[coefficientIndex] * samples[i - 1 - coefficientIndex];
            }
            samples[i] += prediction >> quantizationShift;
        }
    }

    /// <summary>Reads the residual into the samples after the warm-up; prediction is added on top afterwards.</summary>
    private static void DecodeResidual(FlacBitReader reader, long[] samples, int predictorOrder)
    {
        var method = (int)reader.ReadBits(2);
        var parameterBits = method == 0 ? 4 : 5;
        var escapeParameter = method == 0 ? 15 : 31;
        var partitionOrder = (int)reader.ReadBits(4);
        var partitionCount = 1 << partitionOrder;
        var frameIndex = predictorOrder;
        for (var partitionIndex = 0; partitionIndex < partitionCount; partitionIndex++)
        {
            var partitionSamples = (samples.Length >> partitionOrder) - (partitionIndex == 0 ? predictorOrder : 0);
            var riceParameter = (int)reader.ReadBits(parameterBits);
            if (riceParameter == escapeParameter)
            {
                var rawBits = (int)reader.ReadBits(5);
                for (var sampleIndex = 0; sampleIndex < partitionSamples; sampleIndex++)
                {
                    samples[frameIndex++] = rawBits == 0 ? 0 : reader.ReadSigned(rawBits);
                }
                continue;
            }
            for (var sampleIndex = 0; sampleIndex < partitionSamples; sampleIndex++)
            {
                var quotient = (long)reader.ReadUnary();
                var folded = (quotient << riceParameter) | reader.ReadBits(riceParameter);
                samples[frameIndex++] = (folded >> 1) ^ -(folded & 1);
            }
        }
    }

    private sealed class FlacBitReader
    {
        private readonly byte[] bytes;
        private long bitPosition;

        public FlacBitReader(byte[] bytes, int startByte)
        {
            this.bytes = bytes;
            bitPosition = startByte * 8L;
        }

        public long BytePosition => (bitPosition + 7) / 8;

        public long ReadBits(int bitCount)
        {
            long value = 0;
            while (bitCount > 0)
            {
                var bitOffset = (int)(bitPosition & 7);
                var bitsThisByte = Math.Min(8 - bitOffset, bitCount);
                var currentByte = bytes[bitPosition >> 3];
                var taken = (currentByte >> (8 - bitOffset - bitsThisByte)) & ((1 << bitsThisByte) - 1);
                value = (value << bitsThisByte) | (uint)taken;
                bitPosition += bitsThisByte;
                bitCount -= bitsThisByte;
            }
            return value;
        }

        public long ReadSigned(int bitCount)
        {
            var value = ReadBits(bitCount);
            var shift = 64 - bitCount;
            return (value << shift) >> shift;
        }

        public int ReadUnary()
        {
            var zeroCount = 0;
            while (ReadBits(1) == 0)
            {
                zeroCount++;
            }
            return zeroCount;
        }

        public void AlignToByte() => bitPosition = (bitPosition + 7) & ~7L;

        /// <summary>The frame or sample number, in UTF-8's coding. slaw reads frames in order and has no use for it.</summary>
        public void SkipUtf8CodedNumber()
        {
            var firstByte = (int)ReadBits(8);
            var continuationBytes = firstByte switch
            {
                < 0x80 => 0,
                < 0xE0 => 1,
                < 0xF0 => 2,
                < 0xF8 => 3,
                < 0xFC => 4,
                < 0xFE => 5,
                _ => 6,
            };
            for (var byteIndex = 0; byteIndex < continuationBytes; byteIndex++)
            {
                ReadBits(8);
            }
        }
    }
}
