using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

internal static class MasterChainProcessor
{
    /// <summary>
    /// Ceiling the hearing protection substrate enforces, in linear amplitude
    /// (about -1 dBFS). No configuration, model, or prompt can raise it.
    /// </summary>
    public const float HearingProtectionCeiling = 0.89f;

    /// <summary>
    /// Scales a finished buffer down if any sample exceeds the ceiling. Offline
    /// bounces get this on top of the live limiter, for regulatory peace of mind.
    /// </summary>
    /// <returns>The scale applied: 1 when the buffer was already under the ceiling.</returns>
    public static double ApplyHearingProtection(StereoBuffer arrangementBuffer)
    {
        var observedPeak = arrangementBuffer.PeakAmplitude();
        if (observedPeak <= HearingProtectionCeiling)
        {
            return 1;
        }
        var protectiveScale = HearingProtectionCeiling / observedPeak;
        for (var frameIndex = 0; frameIndex < arrangementBuffer.SampleFrameCount; frameIndex++)
        {
            arrangementBuffer.Left[frameIndex] *= protectiveScale;
            arrangementBuffer.Right[frameIndex] *= protectiveScale;
        }
        return protectiveScale;
    }
}

/// <summary>
/// How hard hearing protection worked on a render: frames the limiter pulled down, its
/// deepest reduction, and any whole-render scale-down applied afterwards (0 dB = none).
/// </summary>
internal sealed record HearingProtectionReport(int LimitedFrames, double DeepestLimiterReductionDecibels, double NormalizationDecibels)
{
    public bool Engaged => LimitedFrames > 0 || NormalizationDecibels < -0.01;
}

/// <summary>
/// The streaming master bus: tempo-synced feedback delay → one-pole lowpass →
/// tanh saturation → gain, followed (after any preview audio is mixed in) by the
/// hearing protection substrate, a peak limiter that cannot be configured away.
/// </summary>
internal sealed class MasterBusProcessor
{
    private readonly int sampleRate;
    private readonly float[] leftDelayLine;
    private readonly float[] rightDelayLine;
    private readonly double limiterReleaseCoefficient;
    private int delayWritePosition;
    private double leftLowpassState;
    private double rightLowpassState;
    private double limiterGainReduction = 1;

    public MasterBusProcessor(int sampleRate)
    {
        this.sampleRate = sampleRate;
        leftDelayLine = new float[sampleRate * 7];
        rightDelayLine = new float[sampleRate * 7];
        limiterReleaseCoefficient = Math.Exp(-1.0 / (0.12 * sampleRate));
    }

    public void Process(Span<float> leftChannel, Span<float> rightChannel, MasterChainParameters masterChain, double stepDurationSeconds)
    {
        var delaySampleCount = Math.Clamp((int)(masterChain.DelaySteps * stepDurationSeconds * sampleRate), 1, leftDelayLine.Length - 1);
        var delayFeedback = (float)Math.Clamp(masterChain.DelayFeedback, 0, 0.9);
        var delayMix = (float)Math.Clamp(masterChain.DelayMix, 0, 1);
        var lowpassEngaged = masterChain.LowpassHz < sampleRate / 2.0 * 0.9;
        var smoothingCoefficient = 1 - Math.Exp(-2 * Math.PI * masterChain.LowpassHz / sampleRate);
        var saturationEngaged = masterChain.Drive > 1.0001;
        var driveNormalization = saturationEngaged ? Math.Tanh(masterChain.Drive) : 1.0;

        for (var frameIndex = 0; frameIndex < leftChannel.Length; frameIndex++)
        {
            var readPosition = (delayWritePosition - delaySampleCount + leftDelayLine.Length) % leftDelayLine.Length;
            var leftDelayed = leftDelayLine[readPosition];
            var rightDelayed = rightDelayLine[readPosition];
            leftDelayLine[delayWritePosition] = leftChannel[frameIndex] + leftDelayed * delayFeedback;
            rightDelayLine[delayWritePosition] = rightChannel[frameIndex] + rightDelayed * delayFeedback;
            delayWritePosition = (delayWritePosition + 1) % leftDelayLine.Length;

            double leftSample = leftChannel[frameIndex] + leftDelayed * delayMix;
            double rightSample = rightChannel[frameIndex] + rightDelayed * delayMix;

            if (lowpassEngaged)
            {
                leftLowpassState += smoothingCoefficient * (leftSample - leftLowpassState);
                rightLowpassState += smoothingCoefficient * (rightSample - rightLowpassState);
                leftSample = leftLowpassState;
                rightSample = rightLowpassState;
            }
            if (saturationEngaged)
            {
                leftSample = Math.Tanh(leftSample * masterChain.Drive) / driveNormalization;
                rightSample = Math.Tanh(rightSample * masterChain.Drive) / driveNormalization;
            }
            leftChannel[frameIndex] = (float)(leftSample * masterChain.Gain);
            rightChannel[frameIndex] = (float)(rightSample * masterChain.Gain);
        }
    }

    /// <summary>Frames whose peak the limiter had to pull under the ceiling, since this bus was created.</summary>
    public int LimitedFrameCount { get; private set; }

    /// <summary>The smallest gain the limiter applied (1 = it never engaged).</summary>
    public double DeepestGainReduction { get; private set; } = 1;

    /// <summary>Instant-attack peak limiter at <see cref="MasterChainProcessor.HearingProtectionCeiling"/>.</summary>
    public void ProtectHearing(Span<float> leftChannel, Span<float> rightChannel)
    {
        for (var frameIndex = 0; frameIndex < leftChannel.Length; frameIndex++)
        {
            var framePeak = Math.Max(Math.Abs(leftChannel[frameIndex]), Math.Abs(rightChannel[frameIndex]));
            var requiredGain = framePeak > MasterChainProcessor.HearingProtectionCeiling
                ? MasterChainProcessor.HearingProtectionCeiling / framePeak
                : 1.0;
            if (requiredGain < 1)
            {
                LimitedFrameCount++;
            }
            limiterGainReduction = requiredGain < limiterGainReduction
                ? requiredGain
                : 1 - (1 - limiterGainReduction) * limiterReleaseCoefficient;
            if (limiterGainReduction < DeepestGainReduction)
            {
                DeepestGainReduction = limiterGainReduction;
            }
            leftChannel[frameIndex] = (float)Math.Clamp(leftChannel[frameIndex] * limiterGainReduction, -MasterChainProcessor.HearingProtectionCeiling, MasterChainProcessor.HearingProtectionCeiling);
            rightChannel[frameIndex] = (float)Math.Clamp(rightChannel[frameIndex] * limiterGainReduction, -MasterChainProcessor.HearingProtectionCeiling, MasterChainProcessor.HearingProtectionCeiling);
        }
    }
}
