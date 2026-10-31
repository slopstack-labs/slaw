using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>One open MIDI output. Sends may come from any thread, one at a time.</summary>
internal interface IMidiOutputPort : IDisposable
{
    string Name { get; }

    void SendShort(int status, int data1, int data2);

    /// <summary>Sends a whole system exclusive message, F0 through F7.</summary>
    void SendSysex(byte[] sysexMessage);
}

/// <summary>Lists and opens MIDI outputs on the host platform. Windows only so far; elsewhere the list is empty.</summary>
internal static class MidiOutputFactory
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public const string UnsupportedReason = "MIDI output is Windows only so far (Linux and macOS are on the roadmap, between CoreMIDI and profitability)";

    public static IReadOnlyList<string> ListPortNames() =>
        OperatingSystem.IsWindows() ? WindowsWinMmMidiOutput.ListPortNames() : Array.Empty<string>();

    /// <summary>Opens the first output whose name satisfies <paramref name="nameFilter"/>, or returns null and says why.</summary>
    public static IMidiOutputPort? TryOpen(Func<string, bool> nameFilter, out string? failureReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            failureReason = UnsupportedReason;
            return null;
        }
        return WindowsWinMmMidiOutput.TryOpen(nameFilter, out failureReason);
    }
}

/// <summary>winmm's MIDI output. A port has one owner at a time, so another program holding it makes the open fail.</summary>
internal sealed class WindowsWinMmMidiOutput : IMidiOutputPort
{
    private const uint MmSystemNoError = 0;
    private const uint HeaderDoneFlag = 1;
    private const uint MidiErrorStillPlaying = 65;
    private static readonly TimeSpan SysexCompletionTimeout = TimeSpan.FromMilliseconds(250);

    private readonly object sendGate = new();
    private IntPtr outputHandle;

    private WindowsWinMmMidiOutput(IntPtr outputHandle, string name)
    {
        this.outputHandle = outputHandle;
        Name = name;
    }

    public string Name { get; }

    public static IReadOnlyList<string> ListPortNames()
    {
        var portNames = new List<string>();
        var portCount = midiOutGetNumDevs();
        for (uint portIdentifier = 0; portIdentifier < portCount; portIdentifier++)
        {
            if (midiOutGetDevCaps((UIntPtr)portIdentifier, out var portCapabilities, (uint)Marshal.SizeOf<MidiOutCapabilitiesWide>()) == MmSystemNoError)
            {
                portNames.Add(portCapabilities.szPname ?? $"MIDI output {portIdentifier}");
            }
        }
        return portNames;
    }

    public static WindowsWinMmMidiOutput? TryOpen(Func<string, bool> nameFilter, out string? failureReason)
    {
        var portCount = midiOutGetNumDevs();
        for (uint portIdentifier = 0; portIdentifier < portCount; portIdentifier++)
        {
            if (midiOutGetDevCaps((UIntPtr)portIdentifier, out var portCapabilities, (uint)Marshal.SizeOf<MidiOutCapabilitiesWide>()) != MmSystemNoError ||
                portCapabilities.szPname is not { } portName || !nameFilter(portName))
            {
                continue;
            }
            var openResult = midiOutOpen(out var openedHandle, portIdentifier, IntPtr.Zero, IntPtr.Zero, 0);
            if (openResult == MmSystemNoError)
            {
                failureReason = null;
                return new WindowsWinMmMidiOutput(openedHandle, portName);
            }
            failureReason = $"{portName} would not open (winmm error {openResult}); another program, such as Ableton Live, may be holding it";
            return null;
        }
        failureReason = "no matching MIDI output is connected";
        return null;
    }

    public void SendShort(int status, int data1, int data2)
    {
        lock (sendGate)
        {
            if (outputHandle != IntPtr.Zero)
            {
                _ = midiOutShortMsg(outputHandle, (uint)(status & 0xFF) | (uint)(data1 & 0x7F) << 8 | (uint)(data2 & 0x7F) << 16);
            }
        }
    }

    public void SendSysex(byte[] sysexMessage)
    {
        lock (sendGate)
        {
            if (outputHandle == IntPtr.Zero)
            {
                return;
            }
            // The driver reads both from unmanaged memory until it reports done.
            var headerSize = (uint)Marshal.SizeOf<MidiHeader>();
            var dataPointer = Marshal.AllocHGlobal(sysexMessage.Length);
            var headerPointer = Marshal.AllocHGlobal((int)headerSize);
            try
            {
                Marshal.Copy(sysexMessage, 0, dataPointer, sysexMessage.Length);
                Marshal.StructureToPtr(new MidiHeader { lpData = dataPointer, dwBufferLength = (uint)sysexMessage.Length, dwBytesRecorded = (uint)sysexMessage.Length }, headerPointer, false);
                if (midiOutPrepareHeader(outputHandle, headerPointer, headerSize) != MmSystemNoError)
                {
                    return;
                }
                if (midiOutLongMsg(outputHandle, headerPointer, headerSize) == MmSystemNoError)
                {
                    var waitStarted = Stopwatch.GetTimestamp();
                    while ((Marshal.PtrToStructure<MidiHeader>(headerPointer).dwFlags & HeaderDoneFlag) == 0 &&
                           Stopwatch.GetElapsedTime(waitStarted) < SysexCompletionTimeout)
                    {
                        Thread.Sleep(1);
                    }
                }
                // Freeing a buffer the driver still holds would be someone else's crash, so a stuck one is left behind.
                if (midiOutUnprepareHeader(outputHandle, headerPointer, headerSize) == MidiErrorStillPlaying)
                {
                    dataPointer = IntPtr.Zero;
                    headerPointer = IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(dataPointer);
                Marshal.FreeHGlobal(headerPointer);
            }
        }
    }

    public void Dispose()
    {
        lock (sendGate)
        {
            if (outputHandle == IntPtr.Zero)
            {
                return;
            }
            _ = midiOutReset(outputHandle);
            _ = midiOutClose(outputHandle);
            outputHandle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MidiHeader
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public UIntPtr dwUser;
        public uint dwFlags;
        public IntPtr lpNext;
        public UIntPtr reserved;
        public uint dwOffset;
        public UIntPtr dwReserved0;
        public UIntPtr dwReserved1;
        public UIntPtr dwReserved2;
        public UIntPtr dwReserved3;
        public UIntPtr dwReserved4;
        public UIntPtr dwReserved5;
        public UIntPtr dwReserved6;
        public UIntPtr dwReserved7;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiOutCapabilitiesWide
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")]
    private static extern uint midiOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiOutGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint midiOutGetDevCaps(UIntPtr deviceIdentifier, out MidiOutCapabilitiesWide capabilities, uint capabilitiesSize);

    [DllImport("winmm.dll")]
    private static extern uint midiOutOpen(out IntPtr outputHandle, uint deviceIdentifier, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint midiOutShortMsg(IntPtr outputHandle, uint packedMessage);

    [DllImport("winmm.dll")]
    private static extern uint midiOutPrepareHeader(IntPtr outputHandle, IntPtr header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern uint midiOutLongMsg(IntPtr outputHandle, IntPtr header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern uint midiOutUnprepareHeader(IntPtr outputHandle, IntPtr header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern uint midiOutReset(IntPtr outputHandle);

    [DllImport("winmm.dll")]
    private static extern uint midiOutClose(IntPtr outputHandle);
}
