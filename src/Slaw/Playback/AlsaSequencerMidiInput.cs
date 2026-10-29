using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// MIDI input through the ALSA sequencer. slaw registers itself as a sequencer
/// client with one input port, subscribes to every readable MIDI source on the
/// system, and listens to the system announce port so keyboards plugged in
/// later are connected too — onboarding new stakeholders without a restart.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class AlsaSequencerMidiInput : IMidiInputSource
{
    private const string AlsaLibrary = "libasound.so.2";

    private const int SequencerOpenInput = 2;
    private const int SequencerNonBlocking = 1;
    private const int TryAgainErrorCode = -11;

    private const uint PortCapabilityRead = 1 << 0;
    private const uint PortCapabilityWrite = 1 << 1;
    private const uint PortCapabilitySubscribeRead = 1 << 5;
    private const uint PortCapabilitySubscribeWrite = 1 << 6;
    private const uint PortCapabilityNoExport = 1 << 7;
    private const uint PortTypeMidiGeneric = 1 << 1;
    private const uint PortTypeApplication = 1 << 20;

    private const int SystemClient = 0;
    private const int SystemAnnouncePort = 1;

    private const byte EventNoteOn = 6;
    private const byte EventNoteOff = 7;
    private const byte EventController = 10;
    private const byte EventPortStart = 63;
    private const byte EventPortExit = 64;

    private readonly Dictionary<(int Client, int Port), string> connectedSources = new();
    private readonly object connectedSourcesGate = new();
    private IntPtr sequencerHandle;
    private int inputPortNumber;
    private int ownClientNumber;
    private Thread? eventLoopThread;
    private volatile bool shutdownRequested;

    public string BackendName => "ALSA sequencer";

    public IReadOnlyList<string> ConnectedSourceNames
    {
        get
        {
            lock (connectedSourcesGate)
            {
                return connectedSources.Values.OrderBy(sourceName => sourceName).ToList();
            }
        }
    }

    /// <summary>The client:port other programs can send to, e.g. "128:0".</summary>
    public string InputAddress => $"{ownClientNumber}:{inputPortNumber}";

    public event Action<MidiMessage>? MessageReceived;

    public event Action? SourcesChanged;

    public void Start()
    {
        int openResult;
        try
        {
            openResult = snd_seq_open(out sequencerHandle, "default", SequencerOpenInput, SequencerNonBlocking);
        }
        catch (Exception loadRemediationOpportunity) when (loadRemediationOpportunity is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("MIDI input needs libasound (ALSA) — install alsa-lib", loadRemediationOpportunity);
        }
        if (openResult < 0)
        {
            throw new InvalidOperationException($"could not open the ALSA sequencer (error {openResult})");
        }

        snd_seq_set_client_name(sequencerHandle, "slaw");
        ownClientNumber = snd_seq_client_id(sequencerHandle);
        inputPortNumber = snd_seq_create_simple_port(sequencerHandle, "slaw MIDI in",
            PortCapabilityWrite | PortCapabilitySubscribeWrite, PortTypeMidiGeneric | PortTypeApplication);
        if (inputPortNumber < 0)
        {
            snd_seq_close(sequencerHandle);
            sequencerHandle = IntPtr.Zero;
            throw new InvalidOperationException($"could not create the slaw MIDI input port (error {inputPortNumber})");
        }

        // Hot-plug: the system announce port reports ports appearing and disappearing.
        snd_seq_connect_from(sequencerHandle, inputPortNumber, SystemClient, SystemAnnouncePort);
        ConnectAllReadableSources();

        shutdownRequested = false;
        eventLoopThread = new Thread(RunEventLoop) { IsBackground = true, Name = "slaw MIDI input" };
        eventLoopThread.Start();
        SourcesChanged?.Invoke();
    }

    private void ConnectAllReadableSources()
    {
        snd_seq_client_info_malloc(out var clientInfo);
        snd_seq_port_info_malloc(out var portInfo);
        try
        {
            snd_seq_client_info_set_client(clientInfo, -1);
            while (snd_seq_query_next_client(sequencerHandle, clientInfo) >= 0)
            {
                var clientNumber = snd_seq_client_info_get_client(clientInfo);
                snd_seq_port_info_set_client(portInfo, clientNumber);
                snd_seq_port_info_set_port(portInfo, -1);
                while (snd_seq_query_next_port(sequencerHandle, portInfo) >= 0)
                {
                    TryConnectSource(clientNumber, snd_seq_port_info_get_port(portInfo), clientInfo, portInfo);
                }
            }
        }
        finally
        {
            snd_seq_port_info_free(portInfo);
            snd_seq_client_info_free(clientInfo);
        }
    }

    private void ConnectSingleSource(int clientNumber, int portNumber)
    {
        snd_seq_client_info_malloc(out var clientInfo);
        snd_seq_port_info_malloc(out var portInfo);
        try
        {
            if (snd_seq_get_any_client_info(sequencerHandle, clientNumber, clientInfo) >= 0 &&
                snd_seq_get_any_port_info(sequencerHandle, clientNumber, portNumber, portInfo) >= 0)
            {
                TryConnectSource(clientNumber, portNumber, clientInfo, portInfo);
            }
        }
        finally
        {
            snd_seq_port_info_free(portInfo);
            snd_seq_client_info_free(clientInfo);
        }
    }

    private void TryConnectSource(int clientNumber, int portNumber, IntPtr clientInfo, IntPtr portInfo)
    {
        if (clientNumber == SystemClient || clientNumber == ownClientNumber)
        {
            return;
        }
        var portCapabilities = snd_seq_port_info_get_capability(portInfo);
        var readableBySubscription = (portCapabilities & (PortCapabilityRead | PortCapabilitySubscribeRead)) == (PortCapabilityRead | PortCapabilitySubscribeRead);
        if (!readableBySubscription || (portCapabilities & PortCapabilityNoExport) != 0)
        {
            return;
        }
        var clientName = Marshal.PtrToStringUTF8(snd_seq_client_info_get_name(clientInfo)) ?? $"client {clientNumber}";
        var portName = Marshal.PtrToStringUTF8(snd_seq_port_info_get_name(portInfo)) ?? $"port {portNumber}";
        // The kernel's loopback port only echoes what others send; connecting it invites feedback.
        if (clientName.StartsWith("Midi Through", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (snd_seq_connect_from(sequencerHandle, inputPortNumber, clientNumber, portNumber) < 0)
        {
            return;
        }
        lock (connectedSourcesGate)
        {
            connectedSources[(clientNumber, portNumber)] = portName.StartsWith(clientName, StringComparison.Ordinal) ? portName : $"{clientName}: {portName}";
        }
    }

    private void RunEventLoop()
    {
        while (!shutdownRequested)
        {
            var inputResult = snd_seq_event_input(sequencerHandle, out var sequencerEvent);
            if (inputResult == TryAgainErrorCode || sequencerEvent == IntPtr.Zero)
            {
                Thread.Sleep(1);
                continue;
            }
            if (inputResult < 0)
            {
                // Overrun or similar: drop what was lost, keep listening.
                Thread.Sleep(1);
                continue;
            }
            try
            {
                DispatchEvent(sequencerEvent);
            }
            catch (Exception handlerRemediationOpportunity) when (handlerRemediationOpportunity is not OutOfMemoryException)
            {
                // A listener misbehaved; the MIDI thread must outlive it.
            }
        }
    }

    /// <summary>
    /// snd_seq_event_t: type@0, flags@1, tag@2, queue@3, time@4..11,
    /// source client@12 / port@13, dest@14..15, data from @16.
    /// </summary>
    private void DispatchEvent(IntPtr sequencerEvent)
    {
        var eventType = Marshal.ReadByte(sequencerEvent, 0);
        var sourceClient = Marshal.ReadByte(sequencerEvent, 12);
        var sourcePort = Marshal.ReadByte(sequencerEvent, 13);
        switch (eventType)
        {
            case EventNoteOn or EventNoteOff:
                {
                    var midiChannel = Marshal.ReadByte(sequencerEvent, 16);
                    var noteNumber = Marshal.ReadByte(sequencerEvent, 17);
                    var noteVelocity = Marshal.ReadByte(sequencerEvent, 18);
                    var messageKind = eventType == EventNoteOn && noteVelocity > 0 ? MidiMessageKind.NoteOn : MidiMessageKind.NoteOff;
                    MessageReceived?.Invoke(new MidiMessage(messageKind, midiChannel, noteNumber, noteVelocity, SourceNameOf(sourceClient, sourcePort)));
                    break;
                }
            case EventController:
                {
                    var midiChannel = Marshal.ReadByte(sequencerEvent, 16);
                    var controllerNumber = Marshal.ReadInt32(sequencerEvent, 20);
                    var controllerValue = Marshal.ReadInt32(sequencerEvent, 24);
                    MessageReceived?.Invoke(new MidiMessage(MidiMessageKind.ControlChange, midiChannel, controllerNumber, controllerValue, SourceNameOf(sourceClient, sourcePort)));
                    break;
                }
            case EventPortStart:
                ConnectSingleSource(Marshal.ReadByte(sequencerEvent, 16), Marshal.ReadByte(sequencerEvent, 17));
                SourcesChanged?.Invoke();
                break;
            case EventPortExit:
                lock (connectedSourcesGate)
                {
                    connectedSources.Remove((Marshal.ReadByte(sequencerEvent, 16), Marshal.ReadByte(sequencerEvent, 17)));
                }
                SourcesChanged?.Invoke();
                break;
        }
    }

    private string SourceNameOf(int clientNumber, int portNumber)
    {
        lock (connectedSourcesGate)
        {
            return connectedSources.TryGetValue((clientNumber, portNumber), out var sourceName) ? sourceName : $"{clientNumber}:{portNumber}";
        }
    }

    public void Dispose()
    {
        shutdownRequested = true;
        eventLoopThread?.Join(TimeSpan.FromSeconds(1));
        if (sequencerHandle != IntPtr.Zero)
        {
            snd_seq_close(sequencerHandle);
            sequencerHandle = IntPtr.Zero;
        }
    }

    // ── libasound ─────────────────────────────────────────────────────────────

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_open(out IntPtr sequencerHandle, string deviceName, int streams, int mode);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_close(IntPtr sequencerHandle);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_set_client_name(IntPtr sequencerHandle, string clientName);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_client_id(IntPtr sequencerHandle);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_create_simple_port(IntPtr sequencerHandle, string portName, uint capabilities, uint portType);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_connect_from(IntPtr sequencerHandle, int ownPort, int sourceClient, int sourcePort);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_event_input(IntPtr sequencerHandle, out IntPtr sequencerEvent);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_client_info_malloc(out IntPtr clientInfo);

    [DllImport(AlsaLibrary)]
    private static extern void snd_seq_client_info_free(IntPtr clientInfo);

    [DllImport(AlsaLibrary)]
    private static extern void snd_seq_client_info_set_client(IntPtr clientInfo, int clientNumber);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_client_info_get_client(IntPtr clientInfo);

    [DllImport(AlsaLibrary)]
    private static extern IntPtr snd_seq_client_info_get_name(IntPtr clientInfo);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_query_next_client(IntPtr sequencerHandle, IntPtr clientInfo);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_get_any_client_info(IntPtr sequencerHandle, int clientNumber, IntPtr clientInfo);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_port_info_malloc(out IntPtr portInfo);

    [DllImport(AlsaLibrary)]
    private static extern void snd_seq_port_info_free(IntPtr portInfo);

    [DllImport(AlsaLibrary)]
    private static extern void snd_seq_port_info_set_client(IntPtr portInfo, int clientNumber);

    [DllImport(AlsaLibrary)]
    private static extern void snd_seq_port_info_set_port(IntPtr portInfo, int portNumber);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_port_info_get_port(IntPtr portInfo);

    [DllImport(AlsaLibrary)]
    private static extern uint snd_seq_port_info_get_capability(IntPtr portInfo);

    [DllImport(AlsaLibrary)]
    private static extern IntPtr snd_seq_port_info_get_name(IntPtr portInfo);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_query_next_port(IntPtr sequencerHandle, IntPtr portInfo);

    [DllImport(AlsaLibrary)]
    private static extern int snd_seq_get_any_port_info(IntPtr sequencerHandle, int clientNumber, int portNumber, IntPtr portInfo);
}
