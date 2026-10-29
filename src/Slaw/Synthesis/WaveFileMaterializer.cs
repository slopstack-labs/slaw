using System.Text;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>Writes 16-bit PCM stereo RIFF/WAVE files.</summary>
internal static class WaveFileMaterializer
{
    private const short BitsPerSample = 16;
    private const short ChannelCount = 2;
    private const short PcmFormatTag = 1;

    public static void Write(string outputPath, StereoBuffer arrangementBuffer)
    {
        using var outputFileStream = File.Create(outputPath);
        Write(outputFileStream, arrangementBuffer);
    }

    public static void Write(Stream outputStream, StereoBuffer arrangementBuffer)
    {
        const short blockAlignBytes = ChannelCount * BitsPerSample / 8;
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
        riffWriter.Write(BitsPerSample);

        riffWriter.Write("data"u8);
        riffWriter.Write(dataChunkByteLength);
        for (var frameIndex = 0; frameIndex < arrangementBuffer.SampleFrameCount; frameIndex++)
        {
            riffWriter.Write(QuantizeToPcm16(arrangementBuffer.Left[frameIndex]));
            riffWriter.Write(QuantizeToPcm16(arrangementBuffer.Right[frameIndex]));
        }
    }

    private static short QuantizeToPcm16(float floatingPointSample) =>
        (short)Math.Round(Math.Clamp(floatingPointSample, -1f, 1f) * short.MaxValue);
}
