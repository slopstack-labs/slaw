namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>A pair of equal-length floating-point channels at a known sample rate.</summary>
internal sealed class StereoBuffer
{
    public StereoBuffer(int sampleFrameCount, int sampleRate)
    {
        Left = new float[sampleFrameCount];
        Right = new float[sampleFrameCount];
        SampleRate = sampleRate;
    }

    public float[] Left { get; }
    public float[] Right { get; }
    public int SampleRate { get; }
    public int SampleFrameCount => Left.Length;
    public double DurationSeconds => (double)SampleFrameCount / SampleRate;

    public float PeakAmplitude()
    {
        var observedPeak = 0f;
        for (var frameIndex = 0; frameIndex < SampleFrameCount; frameIndex++)
        {
            observedPeak = Math.Max(observedPeak, Math.Max(Math.Abs(Left[frameIndex]), Math.Abs(Right[frameIndex])));
        }
        return observedPeak;
    }

    /// <summary>
    /// Mixes a mono voice in at <paramref name="startFrame"/> with equal-power
    /// panning. Anything that would land past the end of the buffer is dropped.
    /// </summary>
    public void MixMonoVoice(float[] monoVoice, int startFrame, double gain, double pan)
    {
        var panAngle = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
        var leftChannelGain = (float)(gain * Math.Cos(panAngle));
        var rightChannelGain = (float)(gain * Math.Sin(panAngle));
        var writableFrameCount = Math.Min(monoVoice.Length, SampleFrameCount - startFrame);
        for (var voiceFrameIndex = 0; voiceFrameIndex < writableFrameCount; voiceFrameIndex++)
        {
            Left[startFrame + voiceFrameIndex] += monoVoice[voiceFrameIndex] * leftChannelGain;
            Right[startFrame + voiceFrameIndex] += monoVoice[voiceFrameIndex] * rightChannelGain;
        }
    }
}
