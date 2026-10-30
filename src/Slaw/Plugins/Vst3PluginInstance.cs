using System.Globalization;
using System.Runtime.InteropServices;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>One parameter a plugin exposes, as its edit controller describes it.</summary>
internal sealed record Vst3ParameterDescription(uint Id, string Title, string Units, int StepCount, double DefaultValue, int Flags)
{
    /// <summary>The key this parameter uses in a device's parameter dictionary.</summary>
    public string Key => ParameterKey(Id);

    /// <summary>
    /// Knobs worth showing: not hidden, read-only or program changes, and not one
    /// of the ~2000 "MIDI CC 3|74" proxies JUCE plugins publish without flagging them.
    /// </summary>
    public bool IsUserFacing =>
        (Flags & (Vst3Abi.ParameterFlagHidden | Vst3Abi.ParameterFlagReadOnly | Vst3Abi.ParameterFlagProgramChange)) == 0 &&
        !(Title.StartsWith("MIDI CC ", StringComparison.Ordinal) && Title.Contains('|'));

    public static string ParameterKey(uint parameterId) => "p" + parameterId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseKey(string parameterKey, out uint parameterId)
    {
        parameterId = 0;
        return parameterKey.Length > 1 && parameterKey[0] == 'p' &&
               uint.TryParse(parameterKey.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out parameterId);
    }
}

/// <summary>
/// What a plugin's own editor did, reported on the plugin host thread while the
/// editor is open. Implementations marshal to whichever thread they belong to.
/// </summary>
internal interface IVst3EditorObserver
{
    /// <summary>A gesture began on a parameter, which had <paramref name="normalizedValueBefore"/> until now.</summary>
    void EditGestureBegan(uint parameterId, double normalizedValueBefore);

    void ParameterEdited(uint parameterId, double normalizedValue);

    void EditGestureEnded(uint parameterId);

    /// <summary>The plugin changed many values at once (a preset load): every parameter's value now.</summary>
    void ParameterValuesReloaded(IReadOnlyDictionary<uint, double> normalizedValues);

    void EditorClosed();
}

/// <summary>
/// A live VST3 plugin: component, audio processor and edit controller, wired
/// up and processing mono slaw blocks through its stereo buses.
///
/// Lifecycle calls (create, state, editor, dispose) run on <see cref="PluginHostThread"/>;
/// note and process calls come from the audio thread only. Parameter values are
/// never stored here: every block re-reads the device's dictionary and sends
/// what changed, which is how knobs, AI patches and automation all reach the plugin.
/// What the plugin's own editor changes travels the other way: straight to the
/// processor, and to an <see cref="IVst3EditorObserver"/> that writes it back
/// into the dictionary.
/// </summary>
internal sealed unsafe class Vst3PluginInstance : IDisposable, IVst3ComponentHandlerTarget
{
    public const int MaximumBlockFrames = 8192;
    private const int StateIdle = 0;
    private const int StateProcessing = 1;
    private const int StateRetired = 2;
    private const int StateReconfiguring = 3;
    private const int BusTypeAux = 1;
    private static readonly byte[] StateMagic = "SLV3"u8.ToArray();

    private readonly int sampleRate;
    private readonly bool offline;
    private readonly Vst3HostComponentHandler componentHandler;
    private readonly Vst3HostParameterChanges inputParameterChanges = new();
    private readonly Vst3HostParameterChanges outputParameterChanges = new();
    private readonly Vst3HostEventList inputEvents = new();
    private readonly Vst3HostEventList outputEvents = new();
    private readonly Dictionary<uint, double> lastTableValues = new();
    private readonly HashSet<int> soundingNotes = new();
    private readonly List<IntPtr> ownedAllocations = new();

    /// <summary>Edits from the plugin's editor, host thread → audio thread, for the processor.</summary>
    private readonly ParameterChangeRing editorEdits = new(512);

    /// <summary>Values the processor was sent or reported, audio thread → host thread, for the controller.</summary>
    private readonly ParameterChangeRing controllerUpdates = new(2048);

    private Action? controllerUpdateTicker;
    private object? editorWindow;
    private IVst3EditorObserver? editorObserver;

    private IntPtr component;
    private IntPtr audioProcessor;
    private IntPtr editController;
    private bool controllerIsSeparate;
    private IntPtr componentConnection;
    private IntPtr controllerConnection;

    private int inputBusCount;
    private int outputBusCount;

    /// <summary>The first aux (sidechain) audio input bus, or -1 if the plugin has none.</summary>
    private int sidechainBusIndex = -1;
    private volatile bool sidechainBusActive;
    private int sidechainReconfigurationPending;
    private Vst3AudioBusBuffers* inputBuses;
    private Vst3AudioBusBuffers* outputBuses;
    private Vst3ProcessContext* processContext;
    private Vst3ProcessData* processData;
    private double tempo = 120;
    private bool transportPlaying;
    private int lifecycleState = StateIdle;
    private bool active;

    private Vst3PluginInstance(int sampleRate, bool offline, string name, bool isInstrument)
    {
        this.sampleRate = sampleRate;
        this.offline = offline;
        Name = name;
        IsInstrument = isInstrument;
        componentHandler = new Vst3HostComponentHandler(this);
    }

    public string Name { get; }
    public bool IsInstrument { get; }
    public IReadOnlyList<Vst3ParameterDescription> Parameters { get; private set; } = Array.Empty<Vst3ParameterDescription>();

    /// <summary>Whether the plugin has an aux audio input a sidechain can be routed into.</summary>
    public bool HasSidechainInput => sidechainBusIndex >= 0;

    /// <summary>
    /// Activates or deactivates the sidechain input to match whether the device has a source.
    /// Called every block from the audio thread; a change is handed to the plugin host
    /// thread, which deactivates the plugin, flips the bus and reactivates it (bus activation
    /// is only allowed while inactive). Blocks pass through untouched while that happens.
    /// </summary>
    public void RequestSidechain(bool wanted)
    {
        if (sidechainBusIndex < 0 || wanted == sidechainBusActive ||
            Interlocked.CompareExchange(ref sidechainReconfigurationPending, 1, 0) != 0)
        {
            return;
        }
        if (offline)
        {
            // A bounce may wait, and must: every block of it, the first included, hears the key.
            PluginHostThread.Invoke(() =>
            {
                ReconfigureSidechainOnHostThread(wanted);
                return true;
            });
            Volatile.Write(ref sidechainReconfigurationPending, 0);
            return;
        }
        _ = PluginHostThread.InvokeAsync(() =>
        {
            try
            {
                ReconfigureSidechainOnHostThread(wanted);
            }
            finally
            {
                Volatile.Write(ref sidechainReconfigurationPending, 0);
            }
            return true;
        });
    }

    private void ReconfigureSidechainOnHostThread(bool wanted)
    {
        var spinner = new SpinWait();
        int previousState;
        while ((previousState = Interlocked.CompareExchange(ref lifecycleState, StateReconfiguring, StateIdle)) == StateProcessing)
        {
            spinner.SpinOnce();
        }
        if (previousState != StateIdle)
        {
            return;
        }
        try
        {
            ((delegate* unmanaged<IntPtr, byte, int>)Vst3Abi.Slot(audioProcessor, 8))(audioProcessor, 0);
            ((delegate* unmanaged<IntPtr, byte, int>)Vst3Abi.Slot(component, 11))(component, 0);
            ActivateBus(Vst3Abi.MediaTypeAudio, Vst3Abi.BusDirectionInput, sidechainBusIndex, wanted);
            ((delegate* unmanaged<IntPtr, byte, int>)Vst3Abi.Slot(component, 11))(component, 1);
            ((delegate* unmanaged<IntPtr, byte, int>)Vst3Abi.Slot(audioProcessor, 8))(audioProcessor, 1);
            sidechainBusActive = wanted;
        }
        finally
        {
            Volatile.Write(ref lifecycleState, StateIdle);
        }
    }

    // ── Creation (plugin host thread) ─────────────────────────────────────────

    /// <summary>Loads and activates a plugin. Blocks until the plugin host thread has done the work.</summary>
    public static Vst3PluginInstance Create(PluginReference pluginReference, int sampleRate, bool offline) =>
        PluginHostThread.Invoke(() => CreateOnHostThread(pluginReference, sampleRate, offline));

    private static Vst3PluginInstance CreateOnHostThread(PluginReference pluginReference, int sampleRate, bool offline)
    {
        var module = Vst3Module.Load(pluginReference.Path);
        var pluginClass = module.FindClass(pluginReference.ClassId, pluginReference.Name)
                          ?? throw new InvalidOperationException($"{Path.GetFileName(pluginReference.Path)} offers no class {pluginReference.ClassId}");
        var instance = new Vst3PluginInstance(sampleRate, offline, pluginClass.Name, pluginClass.IsInstrument);
        try
        {
            instance.Initialize(module, pluginClass.ClassId, pluginReference.State);
            return instance;
        }
        catch
        {
            instance.TearDown();
            throw;
        }
    }

    private void Initialize(Vst3Module module, string classIdentifier, string? savedState)
    {
        component = module.CreateInstance(classIdentifier, Vst3Abi.ComponentIid);
        if (component == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{Name}: factory refused to create the component");
        }
        CheckResult(((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(component, 3))(component, Vst3HostApplication.Shared.NativePointer), "initialize");

        audioProcessor = Vst3Abi.QueryInterface(component, Vst3Abi.AudioProcessorIid);
        if (audioProcessor == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{Name}: no IAudioProcessor, so it cannot process audio — a bold product decision");
        }
        if (((delegate* unmanaged<IntPtr, int, int>)Vst3Abi.Slot(audioProcessor, 5))(audioProcessor, Vst3Abi.SampleSize32) != Vst3Abi.ResultOk)
        {
            throw new InvalidOperationException($"{Name}: does not process 32-bit float");
        }

        CreateEditController(module);
        ConfigureBuses();

        var processSetup = new Vst3ProcessSetup
        {
            ProcessMode = offline ? Vst3Abi.ProcessModeOffline : Vst3Abi.ProcessModeRealtime,
            SymbolicSampleSize = Vst3Abi.SampleSize32,
            MaxSamplesPerBlock = MaximumBlockFrames,
            SampleRate = sampleRate,
        };
        CheckResult(((delegate* unmanaged<IntPtr, Vst3ProcessSetup*, int>)Vst3Abi.Slot(audioProcessor, 7))(audioProcessor, &processSetup), "setupProcessing");

        if (!string.IsNullOrEmpty(savedState))
        {
            RestoreState(savedState);
        }
        else
        {
            SynchronizeControllerWithComponent();
        }
        Parameters = ReadParameters();

        AllocateProcessBuffers();
        ((delegate* unmanaged<IntPtr, byte, int>)Vst3Abi.Slot(component, 11))(component, 1);
        active = true;
        ((delegate* unmanaged<IntPtr, byte, int>)Vst3Abi.Slot(audioProcessor, 8))(audioProcessor, 1);

        if (!offline && editController != IntPtr.Zero)
        {
            controllerUpdateTicker = ForwardUpdatesToController;
            PluginHostThread.AddTicker(controllerUpdateTicker);
        }
    }

    private void CreateEditController(Vst3Module module)
    {
        editController = Vst3Abi.QueryInterface(component, Vst3Abi.EditControllerIid);
        if (editController == IntPtr.Zero)
        {
            var controllerClassId = new byte[16];
            fixed (byte* controllerClassIdPointer = controllerClassId)
            {
                if (((delegate* unmanaged<IntPtr, byte*, int>)Vst3Abi.Slot(component, 5))(component, controllerClassIdPointer) == Vst3Abi.ResultOk &&
                    controllerClassId.Any(classIdByte => classIdByte != 0))
                {
                    editController = module.CreateInstance(Vst3Abi.FormatUid(controllerClassId), Vst3Abi.EditControllerIid);
                    if (editController != IntPtr.Zero)
                    {
                        controllerIsSeparate = true;
                        ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(editController, 3))(editController, Vst3HostApplication.Shared.NativePointer);
                    }
                }
            }
        }
        if (editController == IntPtr.Zero)
        {
            // A processor without a controller still makes sound; it just has no knobs to show.
            return;
        }
        ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(editController, 16))(editController, componentHandler.NativePointer);
        if (controllerIsSeparate)
        {
            componentConnection = Vst3Abi.QueryInterface(component, Vst3Abi.ConnectionPointIid);
            controllerConnection = Vst3Abi.QueryInterface(editController, Vst3Abi.ConnectionPointIid);
            if (componentConnection != IntPtr.Zero && controllerConnection != IntPtr.Zero)
            {
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(componentConnection, 3))(componentConnection, controllerConnection);
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(controllerConnection, 3))(controllerConnection, componentConnection);
            }
        }
    }

    private int BusCount(int mediaType, int busDirection) =>
        ((delegate* unmanaged<IntPtr, int, int, int>)Vst3Abi.Slot(component, 7))(component, mediaType, busDirection);

    private int BusChannelCount(int busDirection, int busIndex)
    {
        Vst3BusInfo busInfo;
        return ((delegate* unmanaged<IntPtr, int, int, int, Vst3BusInfo*, int>)Vst3Abi.Slot(component, 8))(component, Vst3Abi.MediaTypeAudio, busDirection, busIndex, &busInfo) == Vst3Abi.ResultOk
            ? Math.Max(0, busInfo.ChannelCount)
            : 0;
    }

    private int BusType(int busDirection, int busIndex)
    {
        Vst3BusInfo busInfo;
        return ((delegate* unmanaged<IntPtr, int, int, int, Vst3BusInfo*, int>)Vst3Abi.Slot(component, 8))(component, Vst3Abi.MediaTypeAudio, busDirection, busIndex, &busInfo) == Vst3Abi.ResultOk
            ? busInfo.BusType
            : -1;
    }

    private void ActivateBus(int mediaType, int busDirection, int busIndex, bool shouldBeActive) =>
        ((delegate* unmanaged<IntPtr, int, int, int, byte, int>)Vst3Abi.Slot(component, 10))(component, mediaType, busDirection, busIndex, shouldBeActive ? (byte)1 : (byte)0);

    private void ConfigureBuses()
    {
        inputBusCount = BusCount(Vst3Abi.MediaTypeAudio, Vst3Abi.BusDirectionInput);
        outputBusCount = BusCount(Vst3Abi.MediaTypeAudio, Vst3Abi.BusDirectionOutput);

        for (var busIndex = 1; busIndex < inputBusCount && sidechainBusIndex < 0; busIndex++)
        {
            if (BusType(Vst3Abi.BusDirectionInput, busIndex) == BusTypeAux)
            {
                sidechainBusIndex = busIndex;
            }
        }

        // Ask for stereo main buses and a stereo sidechain; leave any other extra buses as the plugin likes them.
        var inputArrangements = stackalloc ulong[Math.Max(1, inputBusCount)];
        var outputArrangements = stackalloc ulong[Math.Max(1, outputBusCount)];
        for (var busIndex = 0; busIndex < inputBusCount; busIndex++)
        {
            ulong arrangement = Vst3Abi.SpeakerArrangementStereo;
            ((delegate* unmanaged<IntPtr, int, int, ulong*, int>)Vst3Abi.Slot(audioProcessor, 4))(audioProcessor, Vst3Abi.BusDirectionInput, busIndex, &arrangement);
            inputArrangements[busIndex] = busIndex == 0 || busIndex == sidechainBusIndex ? Vst3Abi.SpeakerArrangementStereo : arrangement;
        }
        for (var busIndex = 0; busIndex < outputBusCount; busIndex++)
        {
            ulong arrangement = Vst3Abi.SpeakerArrangementStereo;
            ((delegate* unmanaged<IntPtr, int, int, ulong*, int>)Vst3Abi.Slot(audioProcessor, 4))(audioProcessor, Vst3Abi.BusDirectionOutput, busIndex, &arrangement);
            outputArrangements[busIndex] = busIndex == 0 ? Vst3Abi.SpeakerArrangementStereo : arrangement;
        }
        ((delegate* unmanaged<IntPtr, ulong*, int, ulong*, int, int>)Vst3Abi.Slot(audioProcessor, 3))(
            audioProcessor, inputArrangements, inputBusCount, outputArrangements, outputBusCount);

        for (var busIndex = 0; busIndex < inputBusCount; busIndex++)
        {
            ActivateBus(Vst3Abi.MediaTypeAudio, Vst3Abi.BusDirectionInput, busIndex, busIndex == 0);
        }
        for (var busIndex = 0; busIndex < outputBusCount; busIndex++)
        {
            ActivateBus(Vst3Abi.MediaTypeAudio, Vst3Abi.BusDirectionOutput, busIndex, busIndex == 0);
        }
        var eventInputBusCount = BusCount(Vst3Abi.MediaTypeEvent, Vst3Abi.BusDirectionInput);
        for (var busIndex = 0; busIndex < eventInputBusCount; busIndex++)
        {
            ActivateBus(Vst3Abi.MediaTypeEvent, Vst3Abi.BusDirectionInput, busIndex, busIndex == 0);
        }
    }

    private void* Allocate(int byteCount)
    {
        var allocation = NativeMemory.AllocZeroed((nuint)Math.Max(1, byteCount));
        ownedAllocations.Add((IntPtr)allocation);
        return allocation;
    }

    private void AllocateProcessBuffers()
    {
        inputBuses = AllocateBuses(Vst3Abi.BusDirectionInput, inputBusCount);
        outputBuses = AllocateBuses(Vst3Abi.BusDirectionOutput, outputBusCount);
        processContext = (Vst3ProcessContext*)Allocate(sizeof(Vst3ProcessContext));
        processData = (Vst3ProcessData*)Allocate(sizeof(Vst3ProcessData));
    }

    private Vst3AudioBusBuffers* AllocateBuses(int busDirection, int busCount)
    {
        var buses = (Vst3AudioBusBuffers*)Allocate(sizeof(Vst3AudioBusBuffers) * Math.Max(1, busCount));
        for (var busIndex = 0; busIndex < busCount; busIndex++)
        {
            var channelCount = BusChannelCount(busDirection, busIndex);
            buses[busIndex].NumChannels = channelCount;
            buses[busIndex].ChannelBuffers32 = (float**)Allocate(sizeof(float*) * Math.Max(1, channelCount));
            for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                buses[busIndex].ChannelBuffers32[channelIndex] = (float*)Allocate(sizeof(float) * MaximumBlockFrames);
            }
        }
        return buses;
    }

    private IReadOnlyList<Vst3ParameterDescription> ReadParameters()
    {
        if (editController == IntPtr.Zero)
        {
            return Array.Empty<Vst3ParameterDescription>();
        }
        var parameterCount = ((delegate* unmanaged<IntPtr, int>)Vst3Abi.Slot(editController, 8))(editController);
        var parameters = new List<Vst3ParameterDescription>(Math.Max(0, parameterCount));
        for (var parameterIndex = 0; parameterIndex < parameterCount; parameterIndex++)
        {
            Vst3ParameterInfo parameterInfo;
            if (((delegate* unmanaged<IntPtr, int, Vst3ParameterInfo*, int>)Vst3Abi.Slot(editController, 9))(editController, parameterIndex, &parameterInfo) != Vst3Abi.ResultOk)
            {
                continue;
            }
            parameters.Add(new Vst3ParameterDescription(
                parameterInfo.Id,
                Vst3Abi.ReadUtf16(parameterInfo.Title, 128),
                Vst3Abi.ReadUtf16(parameterInfo.Units, 128),
                parameterInfo.StepCount,
                Math.Clamp(parameterInfo.DefaultNormalizedValue, 0, 1),
                parameterInfo.Flags));
        }
        return parameters;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>The plugin's component and controller state, packed into one base64 string for the project file.</summary>
    public string CaptureState() => PluginHostThread.Invoke(() =>
    {
        var componentState = ReadState(component, 13);
        var controllerState = controllerIsSeparate ? ReadState(editController, 7) : Array.Empty<byte>();
        using var packedState = new MemoryStream();
        using (var stateWriter = new BinaryWriter(packedState))
        {
            stateWriter.Write(StateMagic);
            stateWriter.Write(componentState.Length);
            stateWriter.Write(componentState);
            stateWriter.Write(controllerState.Length);
            stateWriter.Write(controllerState);
        }
        return Convert.ToBase64String(packedState.ToArray());
    });

    private static byte[] ReadState(IntPtr stateOwner, int getStateSlot)
    {
        if (stateOwner == IntPtr.Zero)
        {
            return Array.Empty<byte>();
        }
        var stateStream = new Vst3HostStream();
        try
        {
            return ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(stateOwner, getStateSlot))(stateOwner, stateStream.NativePointer) == Vst3Abi.ResultOk
                ? stateStream.ToArray()
                : Array.Empty<byte>();
        }
        finally
        {
            stateStream.Release();
        }
    }

    private void WriteState(IntPtr stateOwner, int setStateSlot, byte[] stateBytes)
    {
        if (stateOwner == IntPtr.Zero || stateBytes.Length == 0)
        {
            return;
        }
        var stateStream = new Vst3HostStream(stateBytes);
        try
        {
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(stateOwner, setStateSlot))(stateOwner, stateStream.NativePointer);
        }
        finally
        {
            stateStream.Release();
        }
    }

    /// <summary>Splits a packed state string. Returns false for anything slaw did not write.</summary>
    public static bool TryUnpackState(string packedState, out byte[] componentState, out byte[] controllerState)
    {
        componentState = controllerState = Array.Empty<byte>();
        try
        {
            using var stateReader = new BinaryReader(new MemoryStream(Convert.FromBase64String(packedState)));
            if (!stateReader.ReadBytes(4).AsSpan().SequenceEqual(StateMagic))
            {
                return false;
            }
            componentState = stateReader.ReadBytes(stateReader.ReadInt32());
            controllerState = stateReader.ReadBytes(stateReader.ReadInt32());
            return true;
        }
        catch (Exception stateRemediationOpportunity) when (stateRemediationOpportunity is FormatException or EndOfStreamException or ArgumentException or IOException)
        {
            return false;
        }
    }

    private void RestoreState(string packedState)
    {
        if (!TryUnpackState(packedState, out var componentState, out var controllerState))
        {
            SynchronizeControllerWithComponent();
            return;
        }
        WriteState(component, 12, componentState);
        WriteState(editController, 5, componentState);
        if (controllerIsSeparate)
        {
            WriteState(editController, 6, controllerState);
        }
    }

    /// <summary>Hands the component's state to a separate controller, so both halves agree on every knob.</summary>
    private void SynchronizeControllerWithComponent()
    {
        if (controllerIsSeparate)
        {
            WriteState(editController, 5, ReadState(component, 13));
        }
    }

    // ── The plugin's own editor (plugin host thread) ──────────────────────────

    /// <summary>
    /// Opens the plugin's editor window, or brings it forward if it is open.
    /// Returns why it could not, or null. <paramref name="observer"/> hears about
    /// edits made in the editor until it closes.
    /// </summary>
    public Task<string?> OpenEditorAsync(string title, IntPtr ownerWindow, IVst3EditorObserver observer) =>
        PluginHostThread.InvokeAsync(() => OpenEditorOnHostThread(title, ownerWindow, observer));

    private string? OpenEditorOnHostThread(string title, IntPtr ownerWindow, IVst3EditorObserver observer)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return "plugin editors open on Windows and Linux (X11) only so far — the knobs in the device view still work";
        }
        if (Volatile.Read(ref lifecycleState) == StateRetired || editController == IntPtr.Zero)
        {
            return $"{Name} has no editor of its own — its knobs are in the device view";
        }
        if (OperatingSystem.IsWindows() && editorWindow is Vst3EditorWindow openWindow)
        {
            openWindow.BringToFront();
            return null;
        }
        if (OperatingSystem.IsLinux() && editorWindow is Vst3LinuxEditorWindow openLinuxWindow)
        {
            openLinuxWindow.BringToFront();
            return null;
        }
        try
        {
            editorObserver = observer;
            if (OperatingSystem.IsWindows())
            {
                editorWindow = Vst3EditorWindow.Open(editController, Name, title, ownerWindow, EditorWindowClosed);
            }
            else if (OperatingSystem.IsLinux())
            {
                editorWindow = Vst3LinuxEditorWindow.Open(editController, Name, title, ownerWindow, EditorWindowClosed);
            }
            return null;
        }
        catch (InvalidOperationException editorRemediationOpportunity)
        {
            editorObserver = null;
            return editorRemediationOpportunity.Message;
        }
    }

    private void EditorWindowClosed()
    {
        editorWindow = null;
        var closedObserver = editorObserver;
        editorObserver = null;
        closedObserver?.EditorClosed();
    }

    private void CloseEditor()
    {
        if (OperatingSystem.IsWindows() && editorWindow is Vst3EditorWindow openWindow)
        {
            openWindow.Close();
        }
        else if (OperatingSystem.IsLinux() && editorWindow is Vst3LinuxEditorWindow openLinuxWindow)
        {
            openLinuxWindow.Close();
        }
    }

    /// <summary>The X11 window the editor lives in, or zero (Linux; tests and diagnostics). Plugin host thread.</summary>
    internal ulong LinuxEditorWindowId => OperatingSystem.IsLinux() && editorWindow is Vst3LinuxEditorWindow openLinuxWindow ? (ulong)openLinuxWindow.WindowId : 0;

    /// <summary>Ticker: tells the controller (and so the editor) what the processor was sent or reported.</summary>
    private void ForwardUpdatesToController()
    {
        if (editController == IntPtr.Zero)
        {
            return;
        }
        var setParamNormalized = (delegate* unmanaged<IntPtr, uint, double, int>)Vst3Abi.Slot(editController, 15);
        while (controllerUpdates.TryRead(out var parameterId, out var normalizedValue))
        {
            setParamNormalized(editController, parameterId, normalizedValue);
        }
    }

    private double ControllerValue(uint parameterId) =>
        ((delegate* unmanaged<IntPtr, uint, double>)Vst3Abi.Slot(editController, 14))(editController, parameterId);

    void IVst3ComponentHandlerTarget.BeginEdit(uint parameterId)
    {
        if (editorObserver is { } observer && editController != IntPtr.Zero)
        {
            observer.EditGestureBegan(parameterId, ControllerValue(parameterId));
        }
    }

    void IVst3ComponentHandlerTarget.PerformEdit(uint parameterId, double normalizedValue)
    {
        if (!double.IsFinite(normalizedValue))
        {
            return;
        }
        normalizedValue = Math.Clamp(normalizedValue, 0, 1);
        // The controller already has the value; a processor that is a separate object hears of it only from the host.
        lock (editorEdits)
        {
            editorEdits.TryWrite(parameterId, normalizedValue);
        }
        editorObserver?.ParameterEdited(parameterId, normalizedValue);
    }

    void IVst3ComponentHandlerTarget.EndEdit(uint parameterId) => editorObserver?.EditGestureEnded(parameterId);

    void IVst3ComponentHandlerTarget.RestartComponent(int restartFlags)
    {
        if ((restartFlags & Vst3Abi.RestartParameterValuesChanged) == 0)
        {
            return;
        }
        // Some plugins call this from their audio thread; the controller is only asked from the host thread.
        _ = PluginHostThread.InvokeAsync(() =>
        {
            if (editorObserver is not { } observer || editController == IntPtr.Zero)
            {
                return;
            }
            observer.ParameterValuesReloaded(Parameters.ToDictionary(parameter => parameter.Id, parameter => ControllerValue(parameter.Id)));
        });
    }

    // ── Audio thread ──────────────────────────────────────────────────────────

    /// <summary>
    /// Tempo and playhead for the next block. <paramref name="transportStep"/> is
    /// the song position in sixteenth-note steps (null while stopped), so beat-synced
    /// plugins pump on slaw's beat rather than on the moment they happened to load.
    /// </summary>
    public void SetTransport(double beatsPerMinute, double? transportStep)
    {
        tempo = beatsPerMinute;
        transportPlaying = transportStep is not null;
        if (transportStep is { } playheadStep && beatsPerMinute > 0)
        {
            processContext->ProjectTimeSamples = (long)Math.Round(playheadStep / 4 * 60.0 / beatsPerMinute * sampleRate);
        }
    }

    /// <summary>
    /// Queues a note-on for the next block, <paramref name="sampleOffset"/> frames into it (a ratchet's
    /// re-strike). A note already sounding is ended at the same offset first, so it re-strikes.
    /// </summary>
    public void NoteOn(int midiNote, float velocity, int sampleOffset = 0)
    {
        if (soundingNotes.Contains(midiNote))
        {
            inputEvents.Add(Vst3Event.NoteOff(midiNote, sampleOffset));
        }
        if (inputEvents.Add(Vst3Event.NoteOn(midiNote, velocity, sampleOffset)))
        {
            soundingNotes.Add(midiNote);
        }
    }

    public void NoteOff(int midiNote)
    {
        if (soundingNotes.Remove(midiNote))
        {
            inputEvents.Add(Vst3Event.NoteOff(midiNote, 0));
        }
    }

    public void AllNotesOff()
    {
        foreach (var soundingNote in soundingNotes)
        {
            inputEvents.Add(Vst3Event.NoteOff(soundingNote, 0));
        }
        soundingNotes.Clear();
    }

    /// <summary>
    /// Runs one stereo block in place. The blocks feed the main input bus (left
    /// and right; a mono bus gets their mid) and are replaced by the main
    /// output (a mono output goes to both sides). Instruments ignore the input:
    /// the blocks they return are their voice.
    /// </summary>
    public void Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double>? parameterValues) =>
        Process(leftBlock, rightBlock, parameterValues, null, null);

    /// <summary>
    /// As <see cref="Process(Span{float}, Span{float}, IReadOnlyDictionary{string, double}?)"/>, with a
    /// sidechain for the plugin's aux input (frames 0 .. block length; silence when null).
    /// </summary>
    public void Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double>? parameterValues, float[]? sidechainLeft, float[]? sidechainRight)
    {
        if (Interlocked.CompareExchange(ref lifecycleState, StateProcessing, StateIdle) != StateIdle)
        {
            return;
        }
        try
        {
            for (var blockStart = 0; blockStart < leftBlock.Length; blockStart += MaximumBlockFrames)
            {
                var chunkLength = Math.Min(MaximumBlockFrames, leftBlock.Length - blockStart);
                ProcessChunk(leftBlock.Slice(blockStart, chunkLength), rightBlock.Slice(blockStart, chunkLength), parameterValues,
                    sidechainLeft is null ? ReadOnlySpan<float>.Empty : sidechainLeft.AsSpan(blockStart, chunkLength),
                    (sidechainRight ?? sidechainLeft) is { } rightSource ? rightSource.AsSpan(blockStart, chunkLength) : ReadOnlySpan<float>.Empty);
                parameterValues = null;
            }
        }
        finally
        {
            Volatile.Write(ref lifecycleState, StateIdle);
        }
    }

    private void ProcessChunk(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double>? parameterValues,
        ReadOnlySpan<float> sidechainLeft, ReadOnlySpan<float> sidechainRight)
    {
        var frameCount = leftBlock.Length;
        for (var busIndex = 0; busIndex < inputBusCount; busIndex++)
        {
            var busChannelCount = inputBuses[busIndex].NumChannels;
            for (var channelIndex = 0; channelIndex < busChannelCount; channelIndex++)
            {
                var channel = new Span<float>(inputBuses[busIndex].ChannelBuffers32[channelIndex], frameCount);
                if (busIndex == sidechainBusIndex && sidechainBusActive && sidechainLeft.Length == frameCount)
                {
                    if (busChannelCount == 1)
                    {
                        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                        {
                            channel[frameIndex] = (sidechainLeft[frameIndex] + sidechainRight[frameIndex]) * 0.5f;
                        }
                    }
                    else
                    {
                        (channelIndex % 2 == 0 ? sidechainLeft : sidechainRight).CopyTo(channel);
                    }
                }
                else if (busIndex != 0)
                {
                    channel.Clear();
                }
                else if (busChannelCount == 1)
                {
                    for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    {
                        channel[frameIndex] = (leftBlock[frameIndex] + rightBlock[frameIndex]) * 0.5f;
                    }
                }
                else
                {
                    // Channels beyond a stereo pair (surround buses) alternate left and right.
                    (channelIndex % 2 == 0 ? leftBlock : rightBlock).CopyTo(channel);
                }
            }
            inputBuses[busIndex].SilenceFlags = 0;
        }
        for (var busIndex = 0; busIndex < outputBusCount; busIndex++)
        {
            for (var channelIndex = 0; channelIndex < outputBuses[busIndex].NumChannels; channelIndex++)
            {
                new Span<float>(outputBuses[busIndex].ChannelBuffers32[channelIndex], frameCount).Clear();
            }
            outputBuses[busIndex].SilenceFlags = 0;
        }

        QueueEditorEdits();
        QueueParameterChanges(parameterValues);

        var projectTimeSeconds = (double)processContext->ProjectTimeSamples / sampleRate;
        processContext->State = Vst3Abi.ProcessContextTempoValid | Vst3Abi.ProcessContextTimeSignatureValid |
                                Vst3Abi.ProcessContextProjectTimeMusicValid | (transportPlaying ? Vst3Abi.ProcessContextPlaying : 0);
        processContext->SampleRate = sampleRate;
        processContext->Tempo = tempo;
        processContext->TimeSignatureNumerator = 4;
        processContext->TimeSignatureDenominator = 4;
        processContext->ProjectTimeMusic = projectTimeSeconds * tempo / 60.0;
        processContext->BarPositionMusic = Math.Floor(processContext->ProjectTimeMusic / 4) * 4;

        processData->ProcessMode = offline ? Vst3Abi.ProcessModeOffline : Vst3Abi.ProcessModeRealtime;
        processData->SymbolicSampleSize = Vst3Abi.SampleSize32;
        processData->NumSamples = frameCount;
        processData->NumInputs = inputBusCount;
        processData->NumOutputs = outputBusCount;
        processData->Inputs = inputBusCount > 0 ? inputBuses : null;
        processData->Outputs = outputBusCount > 0 ? outputBuses : null;
        processData->InputParameterChanges = inputParameterChanges.NativePointer;
        outputParameterChanges.Clear();
        processData->OutputParameterChanges = outputParameterChanges.NativePointer;
        processData->InputEvents = inputEvents.NativePointer;
        outputEvents.Clear();
        processData->OutputEvents = outputEvents.NativePointer;
        processData->ProcessContext = processContext;

        ((delegate* unmanaged<IntPtr, Vst3ProcessData*, int>)Vst3Abi.Slot(audioProcessor, 9))(audioProcessor, processData);

        ForwardOutputParameterChanges();
        inputEvents.Clear();
        inputParameterChanges.Clear();
        processContext->ProjectTimeSamples += frameCount;
        processContext->ContinuousTimeSamples += frameCount;

        if (outputBusCount == 0 || outputBuses[0].NumChannels == 0)
        {
            if (IsInstrument)
            {
                leftBlock.Clear();
                rightBlock.Clear();
            }
            return;
        }
        var mainOutput = outputBuses[0];
        var leftOutput = mainOutput.ChannelBuffers32[0];
        var rightOutput = mainOutput.ChannelBuffers32[mainOutput.NumChannels > 1 ? 1 : 0];
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var leftSample = leftOutput[frameIndex];
            var rightSample = rightOutput[frameIndex];
            leftBlock[frameIndex] = float.IsFinite(leftSample) ? leftSample : 0f;
            rightBlock[frameIndex] = float.IsFinite(rightSample) ? rightSample : 0f;
        }
    }

    /// <summary>Hands the processor what the plugin's own editor changed since the last block.</summary>
    private void QueueEditorEdits()
    {
        while (inputParameterChanges.Count < Vst3HostParameterChanges.QueueCapacity && editorEdits.TryRead(out var parameterId, out var normalizedValue))
        {
            inputParameterChanges.QueueFor(parameterId, out _)?.AddPoint(0, normalizedValue);
        }
    }

    /// <summary>Values the processor changed on its own (meters, linked knobs) are passed on to the controller.</summary>
    private void ForwardOutputParameterChanges()
    {
        if (controllerUpdateTicker is null)
        {
            return;
        }
        for (var queueIndex = 0; queueIndex < outputParameterChanges.Count; queueIndex++)
        {
            var valueQueue = outputParameterChanges.QueueAt(queueIndex);
            if (valueQueue.PointCount > 0)
            {
                controllerUpdates.TryWrite(valueQueue.ParameterId, valueQueue.PointAt(valueQueue.PointCount - 1).Value);
            }
        }
    }

    /// <summary>
    /// Sends every p&lt;id&gt; value whose dictionary entry changed since the last
    /// block (as many as fit; the rest next time). Only changes are sent, not
    /// differences from what the plugin holds: a knob turned in the plugin's own
    /// editor stays turned until slaw's value for it actually moves.
    /// </summary>
    private void QueueParameterChanges(IReadOnlyDictionary<string, double>? parameterValues)
    {
        if (parameterValues is null)
        {
            return;
        }
        if (parameterValues is Dictionary<string, double> concreteValues)
        {
            foreach (var (parameterKey, parameterValue) in concreteValues)
            {
                if (!QueueParameterChange(parameterKey, parameterValue))
                {
                    return;
                }
            }
            return;
        }
        foreach (var (parameterKey, parameterValue) in parameterValues)
        {
            if (!QueueParameterChange(parameterKey, parameterValue))
            {
                return;
            }
        }
    }

    private bool QueueParameterChange(string parameterKey, double parameterValue)
    {
        if (!Vst3ParameterDescription.TryParseKey(parameterKey, out var parameterId) || !double.IsFinite(parameterValue))
        {
            return true;
        }
        var normalizedValue = Math.Clamp(parameterValue, 0, 1);
        if (lastTableValues.TryGetValue(parameterId, out var lastTableValue) && Math.Abs(lastTableValue - normalizedValue) < 1e-9)
        {
            return true;
        }
        if (inputParameterChanges.QueueFor(parameterId, out _) is not { } valueQueue)
        {
            return false;
        }
        valueQueue.AddPoint(0, normalizedValue);
        lastTableValues[parameterId] = normalizedValue;
        if (controllerUpdateTicker is not null)
        {
            controllerUpdates.TryWrite(parameterId, normalizedValue);
        }
        return true;
    }

    // ── Teardown ──────────────────────────────────────────────────────────────

    /// <summary>Waits for any in-flight block, then terminates the plugin on the host thread.</summary>
    public void Dispose()
    {
        var spinner = new SpinWait();
        while (Interlocked.CompareExchange(ref lifecycleState, StateRetired, StateIdle) == StateProcessing)
        {
            spinner.SpinOnce();
        }
        PluginHostThread.InvokeAsync(TearDown);
    }

    private void TearDown()
    {
        Volatile.Write(ref lifecycleState, StateRetired);
        CloseEditor();
        if (controllerUpdateTicker is not null)
        {
            PluginHostThread.RemoveTicker(controllerUpdateTicker);
        }
        if (audioProcessor != IntPtr.Zero && active)
        {
            ((delegate* unmanaged<IntPtr, byte, int>)Vst3Abi.Slot(audioProcessor, 8))(audioProcessor, 0);
        }
        if (component != IntPtr.Zero && active)
        {
            ((delegate* unmanaged<IntPtr, byte, int>)Vst3Abi.Slot(component, 11))(component, 0);
        }
        active = false;
        if (componentConnection != IntPtr.Zero && controllerConnection != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(componentConnection, 4))(componentConnection, controllerConnection);
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(controllerConnection, 4))(controllerConnection, componentConnection);
        }
        Vst3Abi.Release(componentConnection);
        Vst3Abi.Release(controllerConnection);
        componentConnection = controllerConnection = IntPtr.Zero;
        if (editController != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(editController, 16))(editController, IntPtr.Zero);
            if (controllerIsSeparate)
            {
                ((delegate* unmanaged<IntPtr, int>)Vst3Abi.Slot(editController, 4))(editController);
            }
            Vst3Abi.Release(editController);
            editController = IntPtr.Zero;
        }
        Vst3Abi.Release(audioProcessor);
        audioProcessor = IntPtr.Zero;
        if (component != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, int>)Vst3Abi.Slot(component, 4))(component);
            Vst3Abi.Release(component);
            component = IntPtr.Zero;
        }
        componentHandler.Release();
        inputParameterChanges.Release();
        outputParameterChanges.Release();
        inputEvents.Release();
        outputEvents.Release();
        foreach (var allocation in ownedAllocations)
        {
            NativeMemory.Free((void*)allocation);
        }
        ownedAllocations.Clear();
    }

    private void CheckResult(int callResult, string callName)
    {
        if (callResult != Vst3Abi.ResultOk)
        {
            throw new InvalidOperationException($"{Name}: {callName} returned {callResult}");
        }
    }
}
