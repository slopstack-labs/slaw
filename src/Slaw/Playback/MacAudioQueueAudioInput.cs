using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// Notices an input that has only ever delivered exact zeros. A microphone always
/// has some noise, so this usually means macOS is withholding it because the
/// permission was refused, in which case nothing reports an error.
/// </summary>
internal sealed class DigitalSilenceWatch
{
    private readonly int framesBeforeSuspicion;
    private int silentFrameCount;
    private bool concluded;

    public DigitalSilenceWatch(int framesBeforeSuspicion)
    {
        this.framesBeforeSuspicion = framesBeforeSuspicion;
    }

    /// <summary>Returns true once, the moment enough silence has passed with no signal at all before it.</summary>
    public bool Observe(ReadOnlySpan<float> block)
    {
        if (concluded)
        {
            return false;
        }
        foreach (var sample in block)
        {
            if (sample != 0f)
            {
                concluded = true;
                return false;
            }
        }
        silentFrameCount += block.Length;
        concluded = silentFrameCount >= framesBeforeSuspicion;
        return concluded;
    }
}

/// <summary>
/// Records from the default macOS input device through AudioToolbox's AudioQueue,
/// the recording half of <see cref="MacAudioQueueAudioSink"/>, with nothing to
/// install. A ring of buffers circulates: the queue fills one and calls back, the
/// callback hands the block to the take and returns the buffer to the queue.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacAudioQueueAudioInput : AudioInputSourceBase
{
    public const string CoreAudioSubstrateName = "coreaudio";

    private const int FramesPerQueuedBuffer = 1024;
    private const int QueuedBufferCount = 4;
    private const int BytesPerFrame = sizeof(float);
    private static readonly TimeSpan SilenceGracePeriod = TimeSpan.FromSeconds(2);

    // Held in a field so the garbage collector never reclaims a callback AudioToolbox still calls.
    private readonly AudioQueueInputCallback bufferFilledCallback;
    private readonly float[] decodedBlock = new float[FramesPerQueuedBuffer];
    private readonly DigitalSilenceWatch silenceWatch;
    private IntPtr audioQueueHandle;
    private volatile bool shutdownRequested;

    public MacAudioQueueAudioInput(int sampleRate)
        : base(sampleRate)
    {
        bufferFilledCallback = OnBufferFilled;
        silenceWatch = new DigitalSilenceWatch((int)(sampleRate * SilenceGracePeriod.TotalSeconds));
    }

    public override string SubstrateName => CoreAudioSubstrateName;

    public override void Start()
    {
        if (audioQueueHandle != IntPtr.Zero)
        {
            return;
        }
        var requestedFormat = new MacAudioQueueAudioSink.AudioStreamBasicDescriptionRecord
        {
            mSampleRate = SampleRate,
            mFormatID = MacAudioQueueAudioSink.LinearPcmFormatIdentifier,
            mFormatFlags = MacAudioQueueAudioSink.FloatPackedFormatFlags,
            mBytesPerPacket = BytesPerFrame,
            mFramesPerPacket = 1,
            mBytesPerFrame = BytesPerFrame,
            mChannelsPerFrame = 1,
            mBitsPerChannel = sizeof(float) * 8,
        };

        // A null run loop asks AudioToolbox to call back on a thread of its own.
        ThrowIfFailed(AudioQueueNewInput(ref requestedFormat, bufferFilledCallback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out audioQueueHandle), nameof(AudioQueueNewInput));
        try
        {
            for (var ringSlotIndex = 0; ringSlotIndex < QueuedBufferCount; ringSlotIndex++)
            {
                ThrowIfFailed(MacAudioQueueAudioSink.AudioQueueAllocateBuffer(audioQueueHandle, FramesPerQueuedBuffer * BytesPerFrame, out var allocatedBuffer), nameof(MacAudioQueueAudioSink.AudioQueueAllocateBuffer));
                ThrowIfFailed(MacAudioQueueAudioSink.AudioQueueEnqueueBuffer(audioQueueHandle, allocatedBuffer, 0, IntPtr.Zero), nameof(MacAudioQueueAudioSink.AudioQueueEnqueueBuffer));
            }
            ThrowIfFailed(MacAudioQueueAudioSink.AudioQueueStart(audioQueueHandle, IntPtr.Zero), nameof(MacAudioQueueAudioSink.AudioQueueStart));
        }
        catch (InvalidOperationException)
        {
            _ = MacAudioQueueAudioSink.AudioQueueDispose(audioQueueHandle, true);
            audioQueueHandle = IntPtr.Zero;
            throw;
        }
    }

    public override void Dispose()
    {
        shutdownRequested = true;
        if (audioQueueHandle != IntPtr.Zero)
        {
            // Disposing immediately stops the queue and frees its buffers along with it.
            _ = MacAudioQueueAudioSink.AudioQueueDispose(audioQueueHandle, true);
            audioQueueHandle = IntPtr.Zero;
        }
    }

    private void OnBufferFilled(IntPtr userData, IntPtr audioQueue, IntPtr filledBuffer, IntPtr startTime, uint packetCount, IntPtr packetDescriptions)
    {
        if (shutdownRequested)
        {
            return;
        }
        var filledBytes = Marshal.ReadInt32(filledBuffer, MacAudioQueueAudioSink.AudioDataByteSizeOffset);
        var filledFrames = Math.Min(filledBytes / BytesPerFrame, FramesPerQueuedBuffer);
        if (filledFrames > 0)
        {
            Marshal.Copy(Marshal.ReadIntPtr(filledBuffer, MacAudioQueueAudioSink.AudioDataPointerOffset), decodedBlock, 0, filledFrames);
            var block = decodedBlock.AsSpan(0, filledFrames);
            OnBlockCaptured(block);
            if (silenceWatch.Observe(block))
            {
                ReportFault(
                    "macOS is delivering pure silence. " +
                    "remediationOpportunity: allow slaw in System Settings → Privacy & Security → Microphone, check the input isn't muted, then arm the track again.");
                return;
            }
        }
        var enqueueStatus = MacAudioQueueAudioSink.AudioQueueEnqueueBuffer(audioQueue, filledBuffer, 0, IntPtr.Zero);
        if (enqueueStatus != 0 && !shutdownRequested)
        {
            ReportFault($"AudioQueueEnqueueBuffer failed with OSStatus {enqueueStatus}");
        }
    }

    private static void ThrowIfFailed(int osStatus, string failingCallName)
    {
        if (osStatus != 0)
        {
            throw new InvalidOperationException(
                $"{failingCallName} failed with OSStatus {osStatus}. " +
                "remediationOpportunity: check that an input device exists and that slaw is allowed in System Settings → Privacy & Security → Microphone.");
        }
    }

    // ── AudioToolbox interop ──────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioQueueInputCallback(IntPtr userData, IntPtr audioQueue, IntPtr buffer, IntPtr startTime, uint packetCount, IntPtr packetDescriptions);

    [DllImport(MacAudioQueueAudioSink.AudioToolboxLibraryPath)]
    private static extern int AudioQueueNewInput(
        ref MacAudioQueueAudioSink.AudioStreamBasicDescriptionRecord inFormat,
        AudioQueueInputCallback inCallbackProc,
        IntPtr inUserData,
        IntPtr inCallbackRunLoop,
        IntPtr inCallbackRunLoopMode,
        uint inFlags,
        out IntPtr outAudioQueue);
}
