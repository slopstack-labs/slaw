using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// Records a Windows input device through winmm's waveIn API: the same 1991
/// subsystem that plays slaw's output, now also entrusted with listening. A
/// device that has gone away refuses the take instead of quietly recording some
/// other microphone. Four buffers circulate between slaw and the driver; a thread
/// polls for filled ones, because the stakeholder still does not attend callbacks.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWaveInAudioInput : AudioInputSourceBase
{
    private const int QueuedBufferRingLength = 4;
    private const int FramesPerQueuedBuffer = 960;
    private const int BitsPerSample = 16;
    private const int BytesPerQueuedBuffer = FramesPerQueuedBuffer * BitsPerSample / 8;

    private const uint WaveMapperDeviceIdentifier = uint.MaxValue;
    private const uint CallbackNullFlag = 0;
    private const uint WaveMappedFlag = 0x4;
    private const ushort WaveFormatPcmTag = 1;
    private const uint HeaderDoneFlag = 0x1;

    private readonly string? inputDeviceName;
    private readonly IntPtr[] queuedBufferHeaderPointers = new IntPtr[QueuedBufferRingLength];
    private readonly IntPtr[] queuedBufferSampleDataPointers = new IntPtr[QueuedBufferRingLength];
    private readonly bool[] queuedBufferHeaderPreparedFlags = new bool[QueuedBufferRingLength];
    private readonly int waveInHeaderStructureByteCount = Marshal.SizeOf<WaveInHeaderRecord>();
    private IntPtr waveInDeviceHandle;
    private Thread? captureThread;
    private volatile bool shutdownRequested;

    public WindowsWaveInAudioInput(int sampleRate, string? inputDeviceName)
        : base(sampleRate)
    {
        this.inputDeviceName = string.IsNullOrWhiteSpace(inputDeviceName) ? null : inputDeviceName;
    }

    public override string SubstrateName => "waveIn";

    public override void Start()
    {
        if (waveInDeviceHandle != IntPtr.Zero)
        {
            return;
        }
        var deviceIdentifier = WaveMapperDeviceIdentifier;
        var openFlags = CallbackNullFlag;
        if (inputDeviceName is not null)
        {
            deviceIdentifier = WindowsWaveDeviceDirectory.FindInputDevice(inputDeviceName)
                ?? throw new InvalidOperationException($"input device \"{inputDeviceName}\" is not connected — pick another in Settings → Audio");
            openFlags |= WaveMappedFlag;
        }
        var requestedWaveFormat = new WaveFormatExRecord
        {
            wFormatTag = WaveFormatPcmTag,
            nChannels = 1,
            nSamplesPerSec = (uint)SampleRate,
            nAvgBytesPerSec = (uint)(SampleRate * BitsPerSample / 8),
            nBlockAlign = BitsPerSample / 8,
            wBitsPerSample = BitsPerSample,
            cbSize = 0,
        };
        var openResult = waveInOpen(out waveInDeviceHandle, deviceIdentifier, ref requestedWaveFormat, IntPtr.Zero, IntPtr.Zero, openFlags);
        if (openResult != 0)
        {
            waveInDeviceHandle = IntPtr.Zero;
            throw new InvalidOperationException($"waveInOpen failed with MMRESULT {openResult} — is a microphone connected and allowed in Windows privacy settings?");
        }
        try
        {
            for (var ringSlotIndex = 0; ringSlotIndex < QueuedBufferRingLength; ringSlotIndex++)
            {
                queuedBufferSampleDataPointers[ringSlotIndex] = Marshal.AllocHGlobal(BytesPerQueuedBuffer);
                queuedBufferHeaderPointers[ringSlotIndex] = Marshal.AllocHGlobal(waveInHeaderStructureByteCount);
                Marshal.StructureToPtr(new WaveInHeaderRecord
                {
                    lpData = queuedBufferSampleDataPointers[ringSlotIndex],
                    dwBufferLength = BytesPerQueuedBuffer,
                }, queuedBufferHeaderPointers[ringSlotIndex], false);
                ThrowIfFailed(waveInPrepareHeader(waveInDeviceHandle, queuedBufferHeaderPointers[ringSlotIndex], waveInHeaderStructureByteCount), nameof(waveInPrepareHeader));
                queuedBufferHeaderPreparedFlags[ringSlotIndex] = true;
                ThrowIfFailed(waveInAddBuffer(waveInDeviceHandle, queuedBufferHeaderPointers[ringSlotIndex], waveInHeaderStructureByteCount), nameof(waveInAddBuffer));
            }
            ThrowIfFailed(waveInStart(waveInDeviceHandle), nameof(waveInStart));
        }
        catch (IOException startRemediationOpportunity)
        {
            ReleaseEverything();
            throw new InvalidOperationException(startRemediationOpportunity.Message, startRemediationOpportunity);
        }
        shutdownRequested = false;
        captureThread = new Thread(RunCaptureLoop) { IsBackground = true, Name = "slaw audio input substrate", Priority = ThreadPriority.AboveNormal };
        captureThread.Start();
    }

    private void RunCaptureLoop()
    {
        var recordedShorts = new short[FramesPerQueuedBuffer];
        var decodedBlock = new float[FramesPerQueuedBuffer];
        var ringSlotIndex = 0;
        try
        {
            while (!shutdownRequested)
            {
                var headerPointer = queuedBufferHeaderPointers[ringSlotIndex];
                var filledHeader = Marshal.PtrToStructure<WaveInHeaderRecord>(headerPointer);
                if ((filledHeader.dwFlags & HeaderDoneFlag) == 0)
                {
                    Thread.Sleep(2);
                    continue;
                }
                var recordedFrameCount = (int)Math.Min(filledHeader.dwBytesRecorded / 2, FramesPerQueuedBuffer);
                Marshal.Copy(queuedBufferSampleDataPointers[ringSlotIndex], recordedShorts, 0, recordedFrameCount);
                for (var frameIndex = 0; frameIndex < recordedFrameCount; frameIndex++)
                {
                    decodedBlock[frameIndex] = recordedShorts[frameIndex] / 32768f;
                }
                OnBlockCaptured(decodedBlock.AsSpan(0, recordedFrameCount));

                filledHeader.dwFlags &= ~HeaderDoneFlag;
                filledHeader.dwBytesRecorded = 0;
                Marshal.StructureToPtr(filledHeader, headerPointer, false);
                ThrowIfFailed(waveInAddBuffer(waveInDeviceHandle, headerPointer, waveInHeaderStructureByteCount), nameof(waveInAddBuffer));
                ringSlotIndex = (ringSlotIndex + 1) % QueuedBufferRingLength;
            }
        }
        catch (IOException captureRemediationOpportunity)
        {
            if (!shutdownRequested)
            {
                ReportFault(captureRemediationOpportunity.Message);
            }
        }
    }

    public override void Dispose()
    {
        shutdownRequested = true;
        captureThread?.Join(TimeSpan.FromSeconds(1));
        ReleaseEverything();
    }

    private void ReleaseEverything()
    {
        if (waveInDeviceHandle != IntPtr.Zero)
        {
            _ = waveInStop(waveInDeviceHandle);
            _ = waveInReset(waveInDeviceHandle);
        }
        for (var ringSlotIndex = 0; ringSlotIndex < QueuedBufferRingLength; ringSlotIndex++)
        {
            if (queuedBufferHeaderPreparedFlags[ringSlotIndex])
            {
                _ = waveInUnprepareHeader(waveInDeviceHandle, queuedBufferHeaderPointers[ringSlotIndex], waveInHeaderStructureByteCount);
                queuedBufferHeaderPreparedFlags[ringSlotIndex] = false;
            }
        }
        if (waveInDeviceHandle != IntPtr.Zero)
        {
            _ = waveInClose(waveInDeviceHandle);
            waveInDeviceHandle = IntPtr.Zero;
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

    private static void ThrowIfFailed(uint multimediaResultCode, string failingCallName)
    {
        if (multimediaResultCode != 0)
        {
            throw new IOException($"{failingCallName} failed with MMRESULT {multimediaResultCode}. remediationOpportunity: check the input device.");
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
    private struct WaveInHeaderRecord
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
    private static extern uint waveInOpen(out IntPtr phwi, uint uDeviceID, ref WaveFormatExRecord pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern uint waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern uint waveInStop(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern uint waveInReset(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern uint waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInClose(IntPtr hwi);
}
