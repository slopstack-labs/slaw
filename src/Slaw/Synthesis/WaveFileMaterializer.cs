using System.Text;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>Writes 16- or 24-bit PCM stereo RIFF/WAVE files.</summary>
internal static class WaveFileMaterializer
{
    public static readonly IReadOnlyList<int> SupportedBitDepths = new[] { 16, 24 };

    private const short ChannelCount = 2;
    private const short PcmFormatTag = 1;

    public static void Write(string outputPath, StereoBuffer arrangementBuffer, int bitsPerSample = 16)
    {
        using var outputFileStream = File.Create(outputPath);
        Write(outputFileStream, arrangementBuffer, bitsPerSample);
    }

    public static void Write(Stream outputStream, StereoBuffer arrangementBuffer, int bitsPerSample = 16)
    {
        if (!SupportedBitDepths.Contains(bitsPerSample))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "slaw bounces 16- or 24-bit PCM");
        }
        var bytesPerSample = bitsPerSample / 8;
        var blockAlignBytes = (short)(ChannelCount * bytesPerSample);
        var dataChunkByteLength = arrangementBuffer.SampleFrameCount * blockAlignBytes;

        using var riffWriter = new BinaryWriter(outputStream, Encoding.ASCII, leaveOpen: true);
        riffWriter.Write("RIFF"u8);
        riffWriter.Write(36 + dataChunkByteLength);
        riffWriter.Write("WAVE"u8);

        riffWriter.Write("fmt "u8);
        riffWriter.Write(16);
        riffWriter.Write(PcmFormatTag);
        riffWriter.Write(ChannelCount);
        riffWriter.Write(arrangementBuffer.SampleRate);
        riffWriter.Write(arrangementBuffer.SampleRate * blockAlignBytes);
        riffWriter.Write(blockAlignBytes);
        riffWriter.Write((short)bitsPerSample);

        riffWriter.Write("data"u8);
        riffWriter.Write(dataChunkByteLength);
        for (var frameIndex = 0; frameIndex < arrangementBuffer.SampleFrameCount; frameIndex++)
        {
            WriteSample(riffWriter, arrangementBuffer.Left[frameIndex], bitsPerSample);
            WriteSample(riffWriter, arrangementBuffer.Right[frameIndex], bitsPerSample);
        }
    }

    private static void WriteSample(BinaryWriter riffWriter, float floatingPointSample, int bitsPerSample)
    {
        var clampedSample = Math.Clamp(floatingPointSample, -1f, 1f);
        if (bitsPerSample == 16)
        {
            riffWriter.Write((short)Math.Round(clampedSample * short.MaxValue));
            return;
        }
        // 24-bit little-endian, for stakeholders who can hear the difference.
        var quantizedSample = (int)Math.Round(clampedSample * 8388607.0);
        riffWriter.Write((byte)quantizedSample);
        riffWriter.Write((byte)(quantizedSample >> 8));
        riffWriter.Write((byte)(quantizedSample >> 16));
    }
}
