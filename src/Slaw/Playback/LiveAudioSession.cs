using System.Diagnostics;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// Owns the live <see cref="SequencerEngine"/>, an audio sink, and the thread
/// that keeps one fed from the other. The thread runs for the lifetime of the
/// studio and streams silence while stopped, so pressing play is instant and
/// every published edit is audible on the next step.
/// </summary>
internal sealed class LiveAudioSession : IDisposable
{
    public const int LiveSampleRate = 48000;
    private const int RenderBlockFrames = 512;

    private readonly int latencyMilliseconds;
    private readonly string preferredSubstrate;
    private Thread? renderThread;
    private volatile bool shutdownRequested;
    private IAudioOutputSink? audioSink;
    private string? coreAudioRefusal;

    public LiveAudioSession(int latencyMilliseconds, string preferredSubstrate)
    {
        this.latencyMilliseconds = Math.Clamp(latencyMilliseconds, 20, 500);
        this.preferredSubstrate = preferredSubstrate;
        Engine = new SequencerEngine(LiveSampleRate, noiseSeed: Environment.TickCount);
    }

    public SequencerEngine Engine { get; }

    /// <summary>The audio substrate in use, once started.</summary>
    public string? SubstrateName => audioSink?.SubstrateName;

    /// <summary>Raised on the render thread if the sink fails; the session is dead afterwards.</summary>
    public event Action<string>? Faulted;

    public bool IsRunning => renderThread is { IsAlive: true };

    /// <summary>
    /// Opens the audio substrate and starts streaming. Throws
    /// <see cref="InvalidOperationException"/> when no substrate is available.
    /// </summary>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }
        audioSink = OpenAudioSink()
            ?? throw new InvalidOperationException(OperatingSystem.IsMacOS()
                ? $"no live audio substrate found — Core Audio refused ({coreAudioRefusal ?? "not selected"}) and no fallback player is installed"
                : $"no live audio substrate found — install one of: {string.Join(", ", PipedProcessAudioSink.SupportedSubstrateNames)}");
        shutdownRequested = false;
        renderThread = new Thread(RunRenderLoop)
        {
            IsBackground = true,
            Name = "slaw live audio substrate",
            Priority = ThreadPriority.AboveNormal,
        };
        renderThread.Start();
    }

    private IAudioOutputSink? OpenAudioSink()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsWaveOutAudioSink(LiveSampleRate);
        }
        // On macOS, Core Audio needs nothing installed; a piped player is used only when chosen in Settings or as a fallback
        if (OperatingSystem.IsMacOS() && !PipedProcessAudioSink.SupportedSubstrateNames.Contains(preferredSubstrate))
        {
            try
            {
                return new MacAudioQueueAudioSink(LiveSampleRate, latencyMilliseconds);
            }
            catch (Exception coreAudioRemediationOpportunity) when (coreAudioRemediationOpportunity is IOException or DllNotFoundException or EntryPointNotFoundException)
            {
                coreAudioRefusal = coreAudioRemediationOpportunity.Message;
            }
        }
        return PipedProcessAudioSink.TryStart(LiveSampleRate, latencyMilliseconds, preferredSubstrate);
    }

    private void RunRenderLoop()
    {
        var leftChannel = new float[RenderBlockFrames];
        var rightChannel = new float[RenderBlockFrames];
        var interleavedBlock = new float[RenderBlockFrames * 2];
        var leadFrames = (long)(latencyMilliseconds / 1000.0 * LiveSampleRate);
        var wallClock = Stopwatch.StartNew();
        long framesWritten = 0;

        try
        {
            while (!shutdownRequested)
            {
                // Pipes accept far more audio than they should; pace by the wall
                // clock so edits stay close to the playhead instead of seconds behind.
                if (!audioSink!.ProvidesBackpressure)
                {
                    var framesDue = (long)(wallClock.Elapsed.TotalSeconds * LiveSampleRate) + leadFrames;
                    if (framesWritten >= framesDue)
                    {
                        Thread.Sleep(2);
                        continue;
                    }
                }

                Engine.RenderBlock(leftChannel, rightChannel);
                for (var frameIndex = 0; frameIndex < RenderBlockFrames; frameIndex++)
                {
                    interleavedBlock[frameIndex * 2] = leftChannel[frameIndex];
                    interleavedBlock[frameIndex * 2 + 1] = rightChannel[frameIndex];
                }
                audioSink.Write(interleavedBlock);
                framesWritten += RenderBlockFrames;
            }
        }
        catch (Exception sinkRemediationOpportunity) when (sinkRemediationOpportunity is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!shutdownRequested)
            {
                Faulted?.Invoke($"{audioSink?.SubstrateName ?? "audio substrate"} stopped responding: {sinkRemediationOpportunity.Message}");
            }
        }
    }

    public void Dispose()
    {
        shutdownRequested = true;
        renderThread?.Join(TimeSpan.FromSeconds(1));
        audioSink?.Dispose();
        audioSink = null;
    }
}
