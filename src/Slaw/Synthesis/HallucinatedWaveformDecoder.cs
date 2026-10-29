using System.Text;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// Turns a model's attempt at "a WAV file as hex" into something a speaker can
/// reproduce. Any RIFF header the model wrote is discarded — they are never
/// right — and a compliant one is synthesized in its place. The remaining bytes
/// are treated as 8-bit unsigned PCM, exactly as the prompt requested and the
/// model ignored.
/// </summary>
internal static class HallucinatedWaveformDecoder
{
    public const int HallucinationSampleRate = 8000;

    /// <summary>
    /// Model output is rarely long enough to be heard. The temporal upscaling
    /// substrate loops the payload until it reaches this duration.
    /// </summary>
    public const double MinimumDeliverableDurationSeconds = 4.0;

    private const int CanonicalRiffHeaderByteLength = 44;

    /// <summary>Hearing protection: hallucinated PCM is full-scale noise more often than not.</summary>
    private const float HallucinationPlaybackLevel = 0.35f;

    public static StereoBuffer Decode(byte[] hallucinatedWaveFileBytes)
    {
        var pcmPayload = ExtractPcmPayload(hallucinatedWaveFileBytes);
        if (pcmPayload.Length == 0)
        {
            throw new InvalidDataException("the model produced no sonic material beyond its own header");
        }

        var minimumFrameCount = (int)(MinimumDeliverableDurationSeconds * HallucinationSampleRate);
        var deliverableFrameCount = Math.Max(minimumFrameCount, pcmPayload.Length);
        var decodedBuffer = new StereoBuffer(deliverableFrameCount, HallucinationSampleRate);
        var fadeFrameCount = Math.Min(deliverableFrameCount / 4, HallucinationSampleRate / 100);

        for (var frameIndex = 0; frameIndex < deliverableFrameCount; frameIndex++)
        {
            var unsignedPcmSample = pcmPayload[frameIndex % pcmPayload.Length];
            var normalizedSample = (unsignedPcmSample - 128) / 128f * HallucinationPlaybackLevel;
            var edgeFade = Math.Min(1f, Math.Min(frameIndex, deliverableFrameCount - 1 - frameIndex) / (float)Math.Max(1, fadeFrameCount));
            decodedBuffer.Left[frameIndex] = decodedBuffer.Right[frameIndex] = normalizedSample * edgeFade;
        }
        return decodedBuffer;
    }

    /// <summary>
    /// Skips past the "data" chunk marker if the model emitted one; otherwise
    /// discards a canonical header's worth of bytes from anything that starts with
    /// "RIFF"; otherwise uses the bytes as-is.
    /// </summary>
    internal static byte[] ExtractPcmPayload(byte[] hallucinatedWaveFileBytes)
    {
        if (!hallucinatedWaveFileBytes.AsSpan().StartsWith("RIFF"u8))
        {
            return hallucinatedWaveFileBytes;
        }
        var dataChunkMarkerOffset = hallucinatedWaveFileBytes.AsSpan().IndexOf("data"u8);
        if (dataChunkMarkerOffset >= 0 && dataChunkMarkerOffset + 8 <= hallucinatedWaveFileBytes.Length)
        {
            return hallucinatedWaveFileBytes[(dataChunkMarkerOffset + 8)..];
        }
        return hallucinatedWaveFileBytes.Length > CanonicalRiffHeaderByteLength
            ? hallucinatedWaveFileBytes[CanonicalRiffHeaderByteLength..]
            : Array.Empty<byte>();
    }

    /// <summary>
    /// Keeps every hex digit in <paramref name="modelOutput"/> after stripping
    /// code fences, and drops a trailing odd nibble. Hex letters inside prose
    /// words survive too — those are part of the composition now.
    /// </summary>
    public static string ExtractHexDigits(string modelOutput)
    {
        var hexDigitAccumulator = new StringBuilder(modelOutput.Length);
        foreach (var outputLine in modelOutput.Split('\n'))
        {
            if (outputLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var candidateCharacter in outputLine)
            {
                if (char.IsAsciiHexDigit(candidateCharacter))
                {
                    hexDigitAccumulator.Append(candidateCharacter);
                }
            }
        }
        if (hexDigitAccumulator.Length % 2 != 0)
        {
            hexDigitAccumulator.Length--;
        }
        return hexDigitAccumulator.ToString();
    }
}
