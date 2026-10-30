using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// Streams audio to the default macOS output device through AudioToolbox's
/// AudioQueue, so the studio makes sound on a fresh Mac without anyone having
/// to onboard Homebrew first. A ring of float buffers circulates between the
/// render thread and the queue: <see cref="Write"/> fills one and enqueues it,
/// and the queue's callback hands it back once played. Writes block while
/// every buffer is out on loan, which is all the backpressure the engine needs.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacAudioQueueAudioSink : IAudioOutputSink
{
    public const string CoreAudioSubstrateName = "coreaudio";

    private const string AudioToolboxLibraryPath = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const int ChannelCount = 2;
    private const int BytesPerFrame = ChannelCount * sizeof(float);
    private const int FramesPerQueuedBuffer = 512;
    private const int MinimumQueuedBufferCount = 3;
    private const uint LinearPcmFormatIdentifier = 0x6C70636D; // 'lpcm'
    private const uint FloatPackedFormatFlags = 0x1 | 0x8; // kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked
    private static readonly TimeSpan StalledDeviceGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly int AudioDataPointerOffset = (int)Marshal.OffsetOf<AudioQueueBufferRecord>(nameof(AudioQueueBufferRecord.mAudioData));
    private static readonly int AudioDataByteSizeOffset = (int)Marshal.OffsetOf<AudioQueueBufferRecord>(nameof(AudioQueueBufferRecord.mAudioDataByteSize));

    // Held in a field so the garbage collector never reclaims a callback AudioToolbox still calls.
    private readonly AudioQueueOutputCallback bufferReturnedCallback;
    private readonly IntPtr audioQueueHandle;
    private readonly IntPtr[] allocatedBufferPointers;
    private readonly ConcurrentQueue<IntPtr> returnedBufferPointers = new();
    private readonly SemaphoreSlim returnedBufferCount;
    private readonly float[] scratchClampedSamples = new float[FramesPerQueuedBuffer * ChannelCount];

    private IntPtr currentBufferPointer;
    private int currentBufferFilledSampleCount;
    private bool queueStarted;
    private bool disposed;

    public MacAudioQueueAudioSink(int sampleRate, int latencyMilliseconds)
    {
        SampleRate = sampleRate;
        var latencyFrames = sampleRate * latencyMilliseconds / 1000;
        var queuedBufferCount = Math.Max(MinimumQueuedBufferCount, (latencyFrames + FramesPerQueuedBuffer - 1) / FramesPerQueuedBuffer);
        allocatedBufferPointers = new IntPtr[queuedBufferCount];
        returnedBufferCount = new SemaphoreSlim(0, queuedBufferCount);
        bufferReturnedCallback = OnBufferReturned;

        var requestedFormat = new AudioStreamBasicDescriptionRecord
        {
            mSampleRate = sampleRate,
            mFormatID = LinearPcmFormatIdentifier,
            mFormatFlags = FloatPackedFormatFlags,
            mBytesPerPacket = BytesPerFrame,
            mFramesPerPacket = 1,
            mBytesPerFrame = BytesPerFrame,
            mChannelsPerFrame = ChannelCount,
            mBitsPerChannel = sizeof(float) * 8,
        };

        // A null run loop asks AudioToolbox to call back on a thread of its own.
        ThrowIfAudioQueueCallFailed(
            AudioQueueNewOutput(ref requestedFormat, bufferReturnedCallback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out audioQueueHandle),
            nameof(AudioQueueNewOutput));

        try
        {
            for (var ringSlotIndex = 0; ringSlotIndex < queuedBufferCount; ringSlotIndex++)
            {
                ThrowIfAudioQueueCallFailed(
                    AudioQueueAllocateBuffer(audioQueueHandle, FramesPerQueuedBuffer * BytesPerFrame, out allocatedBufferPointers[ringSlotIndex]),
                    nameof(AudioQueueAllocateBuffer));
                returnedBufferPointers.Enqueue(allocatedBufferPointers[ringSlotIndex]);
                returnedBufferCount.Release();
            }
        }
        catch
        {
            _ = AudioQueueDispose(audioQueueHandle, true);
            throw;
        }
    }

    public string SubstrateName => CoreAudioSubstrateName;

    public int SampleRate { get; }

    public bool ProvidesBackpressure => true;

    public void Write(ReadOnlySpan<float> interleavedStereoSamples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var consumedSampleCount = 0;
        while (consumedSampleCount < interleavedStereoSamples.Length)
        {
            if (currentBufferPointer == IntPtr.Zero)
            {
                AcquireReturnedBuffer();
            }

            var remainingRoomInBuffer = FramesPerQueuedBuffer * ChannelCount - currentBufferFilledSampleCount;
            var samplesToCopyNow = Math.Min(remainingRoomInBuffer, interleavedStereoSamples.Length - consumedSampleCount);
            for (var sampleOffset = 0; sampleOffset < samplesToCopyNow; sampleOffset++)
            {
                var clampedSample = Math.Clamp(interleavedStereoSamples[consumedSampleCount + sampleOffset], -1f, 1f);
                scratchClampedSamples[sampleOffset] = float.IsNaN(clampedSample) ? 0f : clampedSample;
            }

            var bufferAudioData = Marshal.ReadIntPtr(currentBufferPointer, AudioDataPointerOffset);
            Marshal.Copy(scratchClampedSamples, 0, bufferAudioData + currentBufferFilledSampleCount * sizeof(float), samplesToCopyNow);

            currentBufferFilledSampleCount += samplesToCopyNow;
            consumedSampleCount += samplesToCopyNow;

            if (currentBufferFilledSampleCount == FramesPerQueuedBuffer * ChannelCount)
            {
                EnqueueCurrentBuffer();
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // Disposing immediately stops the queue and frees its buffers along with it.
        _ = AudioQueueDispose(audioQueueHandle, true);
        returnedBufferCount.Dispose();
    }

    private void AcquireReturnedBuffer()
    {
        // While the queue is idle nothing comes back, so start it once every buffer is primed.
        if (!queueStarted && returnedBufferCount.CurrentCount == 0)
        {
            ThrowIfAudioQueueCallFailed(AudioQueueStart(audioQueueHandle, IntPtr.Zero), nameof(AudioQueueStart));
            queueStarted = true;
        }

        if (!returnedBufferCount.Wait(StalledDeviceGracePeriod))
        {
            throw new IOException(
                "the AudioQueue stopped returning buffers. " +
                "remediationOpportunity: check that an output device is connected and not claimed exclusively by another application.");
        }

        returnedBufferPointers.TryDequeue(out currentBufferPointer);
        currentBufferFilledSampleCount = 0;
    }

    private void EnqueueCurrentBuffer()
    {
        Marshal.WriteInt32(currentBufferPointer, AudioDataByteSizeOffset, FramesPerQueuedBuffer * BytesPerFrame);
        ThrowIfAudioQueueCallFailed(AudioQueueEnqueueBuffer(audioQueueHandle, currentBufferPointer, 0, IntPtr.Zero), nameof(AudioQueueEnqueueBuffer));
        currentBufferPointer = IntPtr.Zero;
        currentBufferFilledSampleCount = 0;
    }

    private void OnBufferReturned(IntPtr userData, IntPtr audioQueue, IntPtr returnedBufferPointer)
    {
        if (disposed)
        {
            return;
        }

        returnedBufferPointers.Enqueue(returnedBufferPointer);
        try
        {
            returnedBufferCount.Release();
        }
        catch (ObjectDisposedException)
        {
            // The session ended between the check above and here. The buffer goes down with the queue.
        }
    }

    private static void ThrowIfAudioQueueCallFailed(int osStatus, string failingCallName)
    {
        if (osStatus != 0)
        {
            throw new IOException(
                $"{failingCallName} failed with OSStatus {osStatus}. " +
                "remediationOpportunity: verify an output device exists and that Core Audio is in a cooperative mood.");
        }
    }

    // ── AudioToolbox interop ──────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr audioQueue, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescriptionRecord
    {
        public double mSampleRate;
        public uint mFormatID;
        public uint mFormatFlags;
        public uint mBytesPerPacket;
        public uint mFramesPerPacket;
        public uint mBytesPerFrame;
        public uint mChannelsPerFrame;
        public uint mBitsPerChannel;
        public uint mReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBufferRecord
    {
        public uint mAudioDataBytesCapacity;
        public IntPtr mAudioData;
        public uint mAudioDataByteSize;
        public IntPtr mUserData;
        public uint mPacketDescriptionCapacity;
        public IntPtr mPacketDescriptions;
        public uint mPacketDescriptionCount;
    }

    [DllImport(AudioToolboxLibraryPath)]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescriptionRecord inFormat,
        AudioQueueOutputCallback inCallbackProc,
        IntPtr inUserData,
        IntPtr inCallbackRunLoop,
        IntPtr inCallbackRunLoopMode,
        uint inFlags,
        out IntPtr outAudioQueue);

    [DllImport(AudioToolboxLibraryPath)]
    private static extern int AudioQueueAllocateBuffer(IntPtr inAudioQueue, uint inBufferByteSize, out IntPtr outBuffer);

    [DllImport(AudioToolboxLibraryPath)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr inAudioQueue, IntPtr inBuffer, uint inNumPacketDescs, IntPtr inPacketDescs);

    [DllImport(AudioToolboxLibraryPath)]
    private static extern int AudioQueueStart(IntPtr inAudioQueue, IntPtr inStartTime);

    [DllImport(AudioToolboxLibraryPath)]
    private static extern int AudioQueueDispose(IntPtr inAudioQueue, [MarshalAs(UnmanagedType.U1)] bool inImmediate);
}
