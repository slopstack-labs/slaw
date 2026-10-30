using System.Diagnostics;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>Mono audio captured during one take, and when its first frame left the microphone.</summary>
/// <param name="FirstFrameTimestamp">
/// <see cref="Stopwatch"/> timestamp of the first frame, estimated as the arrival
/// time of its block minus the block's duration. Device and driver latency on
/// top of that is the user's "input latency" setting.
/// </param>
internal sealed record CapturedTake(float[] Samples, int SampleRate, long FirstFrameTimestamp);

/// <summary>
/// A microphone, or anything else the host calls an input. Runs from
/// <see cref="Start"/> until disposal so the level meter moves while a track is
/// merely armed; only audio between <see cref="BeginTake"/> and <see cref="EndTake"/>
/// is kept.
/// </summary>
internal interface IAudioInputSource : IDisposable
{
    /// <summary>Human-facing name, e.g. "pw-record" or "waveIn".</summary>
    string SubstrateName { get; }

    int SampleRate { get; }

    /// <summary>Recent peak level in [0, 1], decaying, for meters. Safe to read from any thread.</summary>
    float PeakLevel { get; }

    bool IsTakeOpen { get; }

    /// <summary>Raised on the capture thread if the input fails; the source is dead afterwards.</summary>
    event Action<string>? Faulted;

    /// <summary>Opens the input. Throws <see cref="InvalidOperationException"/> when no input substrate is available.</summary>
    void Start();

    /// <summary>Starts keeping audio, from the next captured block.</summary>
    void BeginTake();

    /// <summary>Stops keeping audio and hands over everything kept since <see cref="BeginTake"/>.</summary>
    CapturedTake EndTake();
}

/// <summary>Peak metering and take accumulation shared by every input backend.</summary>
internal abstract class AudioInputSourceBase : IAudioInputSource
{
    private const float PeakDecayPerBlock = 0.82f;

    private readonly object takeLock = new();
    private List<float>? takeSamples;
    private long takeFirstFrameTimestamp;
    private float peakLevel;

    protected AudioInputSourceBase(int sampleRate)
    {
        SampleRate = sampleRate;
    }

    public abstract string SubstrateName { get; }

    public int SampleRate { get; }

    public float PeakLevel => Volatile.Read(ref peakLevel);

    public bool IsTakeOpen
    {
        get
        {
            lock (takeLock)
            {
                return takeSamples is not null;
            }
        }
    }

    public event Action<string>? Faulted;

    public abstract void Start();

    public void BeginTake()
    {
        lock (takeLock)
        {
            takeSamples = new List<float>(SampleRate * 30);
            takeFirstFrameTimestamp = 0;
        }
    }

    public CapturedTake EndTake()
    {
        lock (takeLock)
        {
            var keptSamples = takeSamples?.ToArray() ?? Array.Empty<float>();
            var firstFrameTimestamp = takeFirstFrameTimestamp;
            takeSamples = null;
            return new CapturedTake(keptSamples, SampleRate, firstFrameTimestamp);
        }
    }

    /// <summary>Backends call this from their capture thread with each block of mono samples as it arrives.</summary>
    protected void OnBlockCaptured(ReadOnlySpan<float> monoBlock)
    {
        var arrivalTimestamp = Stopwatch.GetTimestamp();
        var blockPeak = 0f;
        foreach (var capturedSample in monoBlock)
        {
            blockPeak = Math.Max(blockPeak, Math.Abs(capturedSample));
        }
        Volatile.Write(ref peakLevel, Math.Min(1f, Math.Max(blockPeak, Volatile.Read(ref peakLevel) * PeakDecayPerBlock)));

        lock (takeLock)
        {
            if (takeSamples is null || monoBlock.IsEmpty)
            {
                return;
            }
            if (takeSamples.Count == 0)
            {
                // The block arrived once its last frame was captured; its first frame is one block earlier.
                takeFirstFrameTimestamp = arrivalTimestamp - (long)(monoBlock.Length / (double)SampleRate * Stopwatch.Frequency);
            }
            foreach (var capturedSample in monoBlock)
            {
                takeSamples.Add(capturedSample);
            }
        }
    }

    protected void ReportFault(string faultDescription) => Faulted?.Invoke(faultDescription);

    public abstract void Dispose();
}
