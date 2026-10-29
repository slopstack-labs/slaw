namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// A destination for interleaved stereo float audio, fed continuously by the
/// live engine thread. Implementations either block in <see cref="Write"/> until
/// the device has room (they provide backpressure), or accept everything
/// immediately and leave pacing to the caller.
/// </summary>
internal interface IAudioOutputSink : IDisposable
{
    /// <summary>Human-facing name, e.g. "pw-cat" or "waveOut".</summary>
    string SubstrateName { get; }

    int SampleRate { get; }

    /// <summary>
    /// True when <see cref="Write"/> blocks until the hardware has consumed
    /// enough audio, so the caller needs no wall-clock pacing of its own.
    /// </summary>
    bool ProvidesBackpressure { get; }

    /// <summary>
    /// Queues interleaved stereo samples (L, R, L, R, …) in the range [-1, 1].
    /// Throws <see cref="IOException"/> if the device or player went away.
    /// </summary>
    void Write(ReadOnlySpan<float> interleavedStereoSamples);
}
