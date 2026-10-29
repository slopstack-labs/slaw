using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// Receives MIDI through winmm's midiIn API, a 1991 interface with which
/// negotiations are conducted exclusively through a single callback, a device
/// handle, and the quiet understanding that nothing will ever be pushed to us
/// proactively about new hardware. Consequently, devices connected after
/// <see cref="Start"/> require a studio restart; the stakeholder does not
/// issue hotplug notifications and has declined our change request.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWinMmMidiInput : IMidiInputSource
{
    private const uint CallbackFunctionFlag = 0x00030000;
    private const uint MidiInputDataMessage = 0x3C3;
    private const uint MmSystemNoError = 0;

    private readonly object lifecycleSynchronizationGate = new();
    private readonly List<IntPtr> openedDeviceHandles = new();
    private readonly List<string> openedDeviceNames = new();
    private readonly Dictionary<int, string> deviceNamesByDeviceIdentifier = new();
    private readonly MidiInProc retainedCallbackDelegate;

    private bool started;
    private bool disposed;

    public WindowsWinMmMidiInput()
    {
        retainedCallbackDelegate = HandleMidiInputCallback;
    }

    public string BackendName => "winmm";

    public IReadOnlyList<string> ConnectedSourceNames
    {
        get
        {
            lock (lifecycleSynchronizationGate)
            {
                return openedDeviceNames.ToArray();
            }
        }
    }

    public event Action<MidiMessage>? MessageReceived;

    public event Action? SourcesChanged;

    public void Start()
    {
        lock (lifecycleSynchronizationGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return;
            }

            var attachedDeviceCount = midiInGetNumDevs();
            for (var deviceIdentifier = 0; deviceIdentifier < attachedDeviceCount; deviceIdentifier++)
            {
                TryOpenSingleDevice(deviceIdentifier);
            }

            if (openedDeviceHandles.Count == 0)
            {
                throw new InvalidOperationException("no MIDI input devices found — plug in a keyboard and restart the studio");
            }

            started = true;
        }

        try
        {
            SourcesChanged?.Invoke();
        }
        catch (Exception remediationOpportunity)
        {
            _ = remediationOpportunity;
        }
    }

    public void Dispose()
    {
        lock (lifecycleSynchronizationGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var deviceHandle in openedDeviceHandles)
            {
                _ = midiInStop(deviceHandle);
                _ = midiInReset(deviceHandle);
                _ = midiInClose(deviceHandle);
            }

            openedDeviceHandles.Clear();
            openedDeviceNames.Clear();
            deviceNamesByDeviceIdentifier.Clear();
        }
    }

    private void TryOpenSingleDevice(int deviceIdentifier)
    {
        var capabilitiesResult = midiInGetDevCaps((UIntPtr)(uint)deviceIdentifier, out var deviceCapabilities, (uint)Marshal.SizeOf<MidiInCapabilitiesWide>());
        if (capabilitiesResult != MmSystemNoError)
        {
            return;
        }

        var deviceName = deviceCapabilities.szPname ?? $"MIDI input {deviceIdentifier}";
        deviceNamesByDeviceIdentifier[deviceIdentifier] = deviceName;

        var openResult = midiInOpen(out var deviceHandle, (uint)deviceIdentifier, retainedCallbackDelegate, (IntPtr)deviceIdentifier, CallbackFunctionFlag);
        if (openResult != MmSystemNoError)
        {
            deviceNamesByDeviceIdentifier.Remove(deviceIdentifier);
            return;
        }

        var startResult = midiInStart(deviceHandle);
        if (startResult != MmSystemNoError)
        {
            _ = midiInClose(deviceHandle);
            deviceNamesByDeviceIdentifier.Remove(deviceIdentifier);
            return;
        }

        openedDeviceHandles.Add(deviceHandle);
        openedDeviceNames.Add(deviceName);
    }

    private void HandleMidiInputCallback(IntPtr deviceHandle, uint message, IntPtr instance, IntPtr param1, IntPtr param2)
    {
        try
        {
            if (message != MidiInputDataMessage)
            {
                return;
            }

            var packedMessage = unchecked((long)param1);
            var status = (int)(packedMessage & 0xFF);
            var data1 = (int)((packedMessage >> 8) & 0x7F);
            var data2 = (int)((packedMessage >> 16) & 0x7F);
            var channel = status & 0x0F;

            MidiMessageKind kind;
            switch (status & 0xF0)
            {
                case 0x90 when data2 > 0:
                    kind = MidiMessageKind.NoteOn;
                    break;
                case 0x90:
                case 0x80:
                    kind = MidiMessageKind.NoteOff;
                    break;
                case 0xB0:
                    kind = MidiMessageKind.ControlChange;
                    break;
                default:
                    return;
            }

            string sourceName;
            lock (lifecycleSynchronizationGate)
            {
                if (!deviceNamesByDeviceIdentifier.TryGetValue((int)instance, out var resolvedName))
                {
                    resolvedName = "winmm";
                }

                sourceName = resolvedName;
            }

            MessageReceived?.Invoke(new MidiMessage(kind, channel, data1, data2, sourceName));
        }
        catch (Exception remediationOpportunity)
        {
            _ = remediationOpportunity;
        }
    }

    private delegate void MidiInProc(IntPtr midiInHandle, uint message, IntPtr instance, IntPtr param1, IntPtr param2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCapabilitiesWide
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;

        public uint dwSupport;
    }

    [DllImport("winmm.dll")]
    private static extern uint midiInGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern uint midiInGetDevCaps(UIntPtr deviceIdentifier, out MidiInCapabilitiesWide capabilities, uint capabilitiesByteCount);

    [DllImport("winmm.dll")]
    private static extern uint midiInOpen(out IntPtr midiInHandle, uint deviceIdentifier, MidiInProc callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint midiInStart(IntPtr midiInHandle);

    [DllImport("winmm.dll")]
    private static extern uint midiInStop(IntPtr midiInHandle);

    [DllImport("winmm.dll")]
    private static extern uint midiInReset(IntPtr midiInHandle);

    [DllImport("winmm.dll")]
    private static extern uint midiInClose(IntPtr midiInHandle);
}
