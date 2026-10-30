using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// Streams audio to a Windows output device through winmm's waveOut API, an
/// interface last meaningfully revised when a "multimedia PC" was a purchasing
/// category. Negotiation with it is conducted through four unmanaged buffers
/// sized from the latency setting, a handful of header structs, and polite
/// polling, because the stakeholder (a 1991 audio subsystem) does not attend
/// callbacks.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWaveOutAudioSink : IAudioOutputSink
{
    private const int QueuedBufferRingLength = WindowsWaveDeviceDirectory.QueuedBufferRingLength;
    private const int ChannelCount = 2;
    private const int BitsPerSample = 16;
    private const int BytesPerFrame = ChannelCount * BitsPerSample / 8;

    private const uint WaveMapperDeviceIdentifier = uint.MaxValue;
    private const uint CallbackNullFlag = 0;
    private const uint WaveMappedFlag = 0x4;
    private const ushort WaveFormatPcmTag = 1;
    private const uint HeaderDoneFlag = 0x1;
    private const uint HeaderPreparedFlag = 0x2;
    private const uint OneMillisecondTimerPeriod = 1;

    private readonly int samplesPerQueuedBuffer;
    private readonly int bytesPerQueuedBuffer;
    private readonly bool timerPeriodRaised;
    private readonly IntPtr waveOutDeviceHandle;
    private readonly IntPtr[] queuedBufferHeaderPointers = new IntPtr[QueuedBufferRingLength];
    private readonly IntPtr[] queuedBufferSampleDataPointers = new IntPtr[QueuedBufferRingLength];
    private readonly bool[] queuedBufferHeaderPreparedFlags = new bool[QueuedBufferRingLength];
    private readonly bool[] queuedBufferEverSubmittedFlags = new bool[QueuedBufferRingLength];
    private readonly int waveOutHeaderStructureByteCount = Marshal.SizeOf<WaveOutHeaderRecord>();
    private readonly short[] scratchSampleConversionBuffer;

    private int currentBufferIndex;
    private int currentBufferFilledSampleCount;
    private bool currentBufferAcquired;
    private bool disposed;

    /// <param name="sampleRate">Frames per second handed to the device.</param>
    /// <param name="latencyMilliseconds">How much audio may wait in the queue, shared across the four buffers.</param>
    /// <param name="deviceIdentifier">A number from <see cref="WindowsWaveDeviceDirectory"/>, or null for the system default.</param>
    public WindowsWaveOutAudioSink(int sampleRate, int latencyMilliseconds, uint? deviceIdentifier)
    {
        SampleRate = sampleRate;
        var framesPerQueuedBuffer = WindowsWaveDeviceDirectory.FramesPerQueuedBufferFor(sampleRate, latencyMilliseconds);
        samplesPerQueuedBuffer = framesPerQueuedBuffer * ChannelCount;
        bytesPerQueuedBuffer = framesPerQueuedBuffer * BytesPerFrame;
        scratchSampleConversionBuffer = new short[samplesPerQueuedBuffer];

        var requestedWaveFormat = new WaveFormatExRecord
        {
            wFormatTag = WaveFormatPcmTag,
            nChannels = ChannelCount,
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = (uint)(sampleRate * BytesPerFrame),
            nBlockAlign = BytesPerFrame,
            wBitsPerSample = BitsPerSample,
            cbSize = 0,
        };

        var openResult = waveOutOpen(
            out waveOutDeviceHandle,
            deviceIdentifier ?? WaveMapperDeviceIdentifier,
            ref requestedWaveFormat,
            IntPtr.Zero,
            IntPtr.Zero,
            deviceIdentifier is null ? CallbackNullFlag : CallbackNullFlag | WaveMappedFlag);
        ThrowIfWaveOutCallFailed(openResult, nameof(waveOutOpen));

        // The default 15.6 ms sleep would outlast a whole buffer at low latency settings.
        timerPeriodRaised = timeBeginPeriod(OneMillisecondTimerPeriod) == 0;

        try
        {
            for (var ringSlotIndex = 0; ringSlotIndex < QueuedBufferRingLength; ringSlotIndex++)
            {
                queuedBufferSampleDataPointers[ringSlotIndex] = Marshal.AllocHGlobal(bytesPerQueuedBuffer);
                queuedBufferHeaderPointers[ringSlotIndex] = Marshal.AllocHGlobal(waveOutHeaderStructureByteCount);

                var freshlyMintedHeader = new WaveOutHeaderRecord
                {
                    lpData = queuedBufferSampleDataPointers[ringSlotIndex],
                    dwBufferLength = (uint)bytesPerQueuedBuffer,
                };
                Marshal.StructureToPtr(freshlyMintedHeader, queuedBufferHeaderPointers[ringSlotIndex], false);

                var prepareResult = waveOutPrepareHeader(
                    waveOutDeviceHandle, queuedBufferHeaderPointers[ringSlotIndex], waveOutHeaderStructureByteCount);
                ThrowIfWaveOutCallFailed(prepareResult, nameof(waveOutPrepareHeader));
                queuedBufferHeaderPreparedFlags[ringSlotIndex] = true;
            }
        }
        catch
        {
            ReleaseEverything();
            throw;
        }
    }

    public string SubstrateName => "waveOut";

    public int SampleRate { get; }

    public bool ProvidesBackpressure => true;

    public void Write(ReadOnlySpan<float> interleavedStereoSamples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var consumedSampleCount = 0;
        while (consumedSampleCount < interleavedStereoSamples.Length)
        {
            if (!currentBufferAcquired)
            {
                WaitUntilBufferIsReusable(currentBufferIndex);
                currentBufferAcquired = true;
                currentBufferFilledSampleCount = 0;
            }

            var remainingRoomInBuffer = samplesPerQueuedBuffer - currentBufferFilledSampleCount;
            var samplesToConvertNow = Math.Min(remainingRoomInBuffer, interleavedStereoSamples.Length - consumedSampleCount);

            for (var sampleOffset = 0; sampleOffset < samplesToConvertNow; sampleOffset++)
            {
                var clampedSample = Math.Clamp(interleavedStereoSamples[consumedSampleCount + sampleOffset], -1f, 1f);
                if (float.IsNaN(clampedSample))
                {
                    clampedSample = 0f;
                }

                scratchSampleConversionBuffer[sampleOffset] = (short)Math.Round(clampedSample * short.MaxValue);
            }

            Marshal.Copy(
                scratchSampleConversionBuffer,
                0,
                queuedBufferSampleDataPointers[currentBufferIndex] + currentBufferFilledSampleCount * sizeof(short),
                samplesToConvertNow);

            currentBufferFilledSampleCount += samplesToConvertNow;
            consumedSampleCount += samplesToConvertNow;

            if (currentBufferFilledSampleCount == samplesPerQueuedBuffer)
            {
                SubmitCurrentBufferToDevice();
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
        ReleaseEverything();
    }

    private void WaitUntilBufferIsReusable(int ringSlotIndex)
    {
        if (!queuedBufferEverSubmittedFlags[ringSlotIndex])
        {
            return;
        }

        while ((ReadHeaderFlags(ringSlotIndex) & HeaderDoneFlag) == 0)
        {
            Thread.Sleep(1);
        }
    }

    private void SubmitCurrentBufferToDevice()
    {
        var headerPointer = queuedBufferHeaderPointers[currentBufferIndex];

        // Clear WHDR_DONE from the previous lap so the next wait is meaningful.
        var staleHeader = Marshal.PtrToStructure<WaveOutHeaderRecord>(headerPointer);
        staleHeader.dwFlags &= ~HeaderDoneFlag;
        Marshal.StructureToPtr(staleHeader, headerPointer, false);

        var writeResult = waveOutWrite(waveOutDeviceHandle, headerPointer, waveOutHeaderStructureByteCount);
        ThrowIfWaveOutCallFailed(writeResult, nameof(waveOutWrite));

        queuedBufferEverSubmittedFlags[currentBufferIndex] = true;
        currentBufferIndex = (currentBufferIndex + 1) % QueuedBufferRingLength;
        currentBufferAcquired = false;
        currentBufferFilledSampleCount = 0;
    }

    private uint ReadHeaderFlags(int ringSlotIndex)
    {
        return Marshal.PtrToStructure<WaveOutHeaderRecord>(queuedBufferHeaderPointers[ringSlotIndex]).dwFlags;
    }

    private void ReleaseEverything()
    {
        if (timerPeriodRaised)
        {
            _ = timeEndPeriod(OneMillisecondTimerPeriod);
        }

        if (waveOutDeviceHandle != IntPtr.Zero)
        {
            _ = waveOutReset(waveOutDeviceHandle);
        }

        for (var ringSlotIndex = 0; ringSlotIndex < QueuedBufferRingLength; ringSlotIndex++)
        {
            if (queuedBufferHeaderPreparedFlags[ringSlotIndex])
            {
                _ = waveOutUnprepareHeader(
                    waveOutDeviceHandle, queuedBufferHeaderPointers[ringSlotIndex], waveOutHeaderStructureByteCount);
                queuedBufferHeaderPreparedFlags[ringSlotIndex] = false;
            }
        }

        if (waveOutDeviceHandle != IntPtr.Zero)
        {
            _ = waveOutClose(waveOutDeviceHandle);
        }

        for (var ringSlotIndex = 0; ringSlotIndex < QueuedBufferRingLength; ringSlotIndex++)
        {
            if (queuedBufferHeaderPointers[ringSlotIndex] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(queuedBufferHeaderPointers[ringSlotIndex]);
                queuedBufferHeaderPointers[ringSlotIndex] = IntPtr.Zero;
            }

            if (queuedBufferSampleDataPointers[ringSlotIndex] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(queuedBufferSampleDataPointers[ringSlotIndex]);
                queuedBufferSampleDataPointers[ringSlotIndex] = IntPtr.Zero;
            }
        }
    }

    private static void ThrowIfWaveOutCallFailed(uint multimediaResultCode, string failingCallName)
    {
        if (multimediaResultCode != 0)
        {
            throw new IOException(
                $"{failingCallName} failed with MMRESULT {multimediaResultCode}. " +
                "remediationOpportunity: verify an output device exists and is not held hostage by another application.");
        }
    }

    // ── winmm interop ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExRecord
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveOutHeaderRecord
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(
        out IntPtr phwo, uint uDeviceID, ref WaveFormatExRecord pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
