namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// The metronome: a short synthesized blip on every beat (every 4 sixteenth steps),
/// higher and louder on each bar's downbeat. Only the live engine schedules clicks;
/// bounces, renders and anything else offline never do, so their bytes do not change.
///
/// The engine mixes the click into the block after the master chain (master effects
/// never process it) and before the hearing protection, which applies to everything.
/// Both clicks are synthesized once, up front; scheduling and mixing allocate nothing.
/// </summary>
internal sealed class MetronomeClickSubstrate
{
    public const int StepsPerBeat = 4;
    public const int StepsPerBar = 16;
    public const double DownbeatFrequencyHz = 1500;
    public const double BeatFrequencyHz = 1000;

    /// <summary>Peak of each click at volume 1, before the hearing protection.</summary>
    public const float DownbeatPeak = 0.5f;
    public const float BeatPeak = 0.32f;

    private const double AttackSeconds = 0.002;
    private const double DecayTimeConstantSeconds = 0.009;
    private const double ClickSeconds = 0.045;
    private const double TailFadeSeconds = 0.004;
    private const int MaximumPendingClicks = 16;

    private readonly float[] downbeatClickWaveform;
    private readonly float[] beatClickWaveform;
    private readonly int[] pendingClickFrames = new int[MaximumPendingClicks];
    private readonly bool[] pendingClickDownbeats = new bool[MaximumPendingClicks];
    private int pendingClickCount;
    private float[]? soundingClickWaveform;
    private int soundingClickPosition;

    public MetronomeClickSubstrate(int sampleRate)
    {
        downbeatClickWaveform = SynthesizeClick(sampleRate, DownbeatFrequencyHz, DownbeatPeak);
        beatClickWaveform = SynthesizeClick(sampleRate, BeatFrequencyHz, BeatPeak);
    }

    /// <summary>Whether a grid step (0-based sixteenths) starts a beat.</summary>
    public static bool IsBeat(long gridStep) => gridStep >= 0 && gridStep % StepsPerBeat == 0;

    /// <summary>Whether a grid step starts a bar: the accented click.</summary>
    public static bool IsDownbeat(long gridStep) => gridStep >= 0 && gridStep % StepsPerBar == 0;

    /// <summary>True while a click rings or one is waiting for the next <see cref="MixInto"/>.</summary>
    public bool Sounding => soundingClickWaveform is not null || pendingClickCount > 0;

    /// <summary>Starts a click <paramref name="frameOffset"/> frames into the block about to be mixed. Offsets must not decrease within a block.</summary>
    public void Schedule(int frameOffset, bool downbeat)
    {
        if (pendingClickCount >= MaximumPendingClicks)
        {
            return;
        }
        pendingClickFrames[pendingClickCount] = frameOffset;
        pendingClickDownbeats[pendingClickCount] = downbeat;
        pendingClickCount++;
    }

    /// <summary>Adds the scheduled and ringing clicks to the block (the same signal on both channels), then forgets the schedule.</summary>
    public void MixInto(Span<float> leftChannel, Span<float> rightChannel, float volume)
    {
        if (!Sounding)
        {
            return;
        }
        var nextPendingClick = 0;
        for (var frameIndex = 0; frameIndex < leftChannel.Length; frameIndex++)
        {
            while (nextPendingClick < pendingClickCount && pendingClickFrames[nextPendingClick] <= frameIndex)
            {
                soundingClickWaveform = pendingClickDownbeats[nextPendingClick] ? downbeatClickWaveform : beatClickWaveform;
                soundingClickPosition = 0;
                nextPendingClick++;
            }
            if (soundingClickWaveform is null)
            {
                continue;
            }
            var clickSample = soundingClickWaveform[soundingClickPosition++] * volume;
            leftChannel[frameIndex] += clickSample;
            rightChannel[frameIndex] += clickSample;
            if (soundingClickPosition >= soundingClickWaveform.Length)
            {
                soundingClickWaveform = null;
            }
        }
        pendingClickCount = 0;
    }

    /// <summary>Cuts a ringing click and drops anything scheduled.</summary>
    public void Silence()
    {
        soundingClickWaveform = null;
        pendingClickCount = 0;
    }

    /// <summary>A sine blip with a little second harmonic (it cuts through a mix better than a pure tone), a few ms of attack and a fast exponential decay.</summary>
    private static float[] SynthesizeClick(int sampleRate, double frequencyHz, float targetPeak)
    {
        var clickFrameCount = (int)(ClickSeconds * sampleRate);
        var tailFadeFrameCount = Math.Max(1, (int)(TailFadeSeconds * sampleRate));
        var clickWaveform = new float[clickFrameCount];
        var loudestSample = 0.0;
        for (var frameIndex = 0; frameIndex < clickFrameCount; frameIndex++)
        {
            var elapsedSeconds = frameIndex / (double)sampleRate;
            var envelopeLevel = elapsedSeconds < AttackSeconds
                ? elapsedSeconds / AttackSeconds
                : Math.Exp(-(elapsedSeconds - AttackSeconds) / DecayTimeConstantSeconds);
            var framesLeft = clickFrameCount - frameIndex;
            if (framesLeft < tailFadeFrameCount)
            {
                envelopeLevel *= framesLeft / (double)tailFadeFrameCount;
            }
            var oscillatorPhase = 2 * Math.PI * frequencyHz * elapsedSeconds;
            var clickSample = envelopeLevel * (0.85 * Math.Sin(oscillatorPhase) + 0.15 * Math.Sin(2 * oscillatorPhase));
            clickWaveform[frameIndex] = (float)clickSample;
            loudestSample = Math.Max(loudestSample, Math.Abs(clickSample));
        }
        var normalizationGain = loudestSample > 0 ? targetPeak / loudestSample : 0;
        for (var frameIndex = 0; frameIndex < clickFrameCount; frameIndex++)
        {
            clickWaveform[frameIndex] = (float)(clickWaveform[frameIndex] * normalizationGain);
        }
        return clickWaveform;
    }
}
