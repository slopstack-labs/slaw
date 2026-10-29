using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// Streams audio to the default Windows output device through winmm's waveOut
/// API, an interface last meaningfully revised when a "multimedia PC" was a
/// purchasing category. Negotiation with it is conducted through four
/// unmanaged buffers, a handful of header structs, and polite polling, because
/// the stakeholder (a 1991 audio subsystem) does not attend callbacks.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWaveOutAudioSink : IAudioOutputSink
{
    private const int QueuedBufferRingLength = 4;
    private const int FramesPerQueuedBuffer = 1024;
    private const int ChannelCount = 2;
    private const int BitsPerSample = 16;
    private const int BytesPerFrame = ChannelCount * BitsPerSample / 8;
    private const int SamplesPerQueuedBuffer = FramesPerQueuedBuffer * ChannelCount;
    private const int BytesPerQueuedBuffer = FramesPerQueuedBuffer * BytesPerFrame;

    private const uint WaveMapperDeviceIdentifier = uint.MaxValue;
    private const uint CallbackNullFlag = 0;
    private const ushort WaveFormatPcmTag = 1;
    private const uint HeaderDoneFlag = 0x1;
    private const uint HeaderPreparedFlag = 0x2;

    private readonly IntPtr waveOutDeviceHandle;
    private readonly IntPtr[] queuedBufferHeaderPointers = new IntPtr[QueuedBufferRingLength];
    private readonly IntPtr[] queuedBufferSampleDataPointers = new IntPtr[QueuedBufferRingLength];
    private readonly bool[] queuedBufferHeaderPreparedFlags = new bool[QueuedBufferRingLength];
    private readonly bool[] queuedBufferEverSubmittedFlags = new bool[QueuedBufferRingLength];
    private readonly int waveOutHeaderStructureByteCount = Marshal.SizeOf<WaveOutHeaderRecord>();
    private readonly short[] scratchSampleConversionBuffer = new short[SamplesPerQueuedBuffer];

    private int currentBufferIndex;
    private int currentBufferFilledSampleCount;
    private bool currentBufferAcquired;
    private bool disposed;

    public WindowsWaveOutAudioSink(int sampleRate)
    {
        SampleRate = sampleRate;

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
            WaveMapperDeviceIdentifier,
            ref requestedWaveFormat,
            IntPtr.Zero,
            IntPtr.Zero,
            CallbackNullFlag);
        ThrowIfWaveOutCallFailed(openResult, nameof(waveOutOpen));

        try
        {
            for (var ringSlotIndex = 0; ringSlotIndex < QueuedBufferRingLength; ringSlotIndex++)
            {
                queuedBufferSampleDataPointers[ringSlotIndex] = Marshal.AllocHGlobal(BytesPerQueuedBuffer);
                queuedBufferHeaderPointers[ringSlotIndex] = Marshal.AllocHGlobal(waveOutHeaderStructureByteCount);

                var freshlyMintedHeader = new WaveOutHeaderRecord
                {
                    lpData = queuedBufferSampleDataPointers[ringSlotIndex],
                    dwBufferLength = BytesPerQueuedBuffer,
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

            var remainingRoomInBuffer = SamplesPerQueuedBuffer - currentBufferFilledSampleCount;
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

            if (currentBufferFilledSampleCount == SamplesPerQueuedBuffer)
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
}
