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
/// A live VST3 plugin: component, audio processor and edit controller, wired
/// up and processing mono slaw blocks through its stereo buses.
///
/// Lifecycle calls (create, state, dispose) run on <see cref="PluginHostThread"/>;
/// note and process calls come from the audio thread only. Parameter values are
/// never stored here: every block re-reads the device's dictionary and sends
/// what changed, which is how knobs, AI patches and automation all reach the plugin.
/// </summary>
internal sealed unsafe class Vst3PluginInstance : IDisposable
{
    public const int MaximumBlockFrames = 8192;
    private const int StateIdle = 0;
    private const int StateProcessing = 1;
    private const int StateRetired = 2;
    private static readonly byte[] StateMagic = "SLV3"u8.ToArray();

    private readonly int sampleRate;
    private readonly bool offline;
    private readonly Vst3HostComponentHandler componentHandler = new();
    private readonly Vst3HostParameterChanges inputParameterChanges = new();
    private readonly Vst3HostParameterChanges outputParameterChanges = new();
    private readonly Vst3HostEventList inputEvents = new();
    private readonly Vst3HostEventList outputEvents = new();
    private readonly Dictionary<uint, double> lastSentValues = new();
    private readonly HashSet<int> soundingNotes = new();
    private readonly List<IntPtr> ownedAllocations = new();

    private IntPtr component;
    private IntPtr audioProcessor;
    private IntPtr editController;
    private bool controllerIsSeparate;
    private IntPtr componentConnection;
    private IntPtr controllerConnection;

    private int inputBusCount;
    private int outputBusCount;
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
    }

    public string Name { get; }
    public bool IsInstrument { get; }
    public IReadOnlyList<Vst3ParameterDescription> Parameters { get; private set; } = Array.Empty<Vst3ParameterDescription>();

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

    private void ActivateBus(int mediaType, int busDirection, int busIndex, bool shouldBeActive) =>
        ((delegate* unmanaged<IntPtr, int, int, int, byte, int>)Vst3Abi.Slot(component, 10))(component, mediaType, busDirection, busIndex, shouldBeActive ? (byte)1 : (byte)0);

    private void ConfigureBuses()
    {
        inputBusCount = BusCount(Vst3Abi.MediaTypeAudio, Vst3Abi.BusDirectionInput);
        outputBusCount = BusCount(Vst3Abi.MediaTypeAudio, Vst3Abi.BusDirectionOutput);

        // Ask for stereo main buses and leave any sidechains as the plugin likes them.
        var inputArrangements = stackalloc ulong[Math.Max(1, inputBusCount)];
        var outputArrangements = stackalloc ulong[Math.Max(1, outputBusCount)];
        for (var busIndex = 0; busIndex < inputBusCount; busIndex++)
        {
            ulong arrangement = Vst3Abi.SpeakerArrangementStereo;
            ((delegate* unmanaged<IntPtr, int, int, ulong*, int>)Vst3Abi.Slot(audioProcessor, 4))(audioProcessor, Vst3Abi.BusDirectionInput, busIndex, &arrangement);
            inputArrangements[busIndex] = busIndex == 0 ? Vst3Abi.SpeakerArrangementStereo : arrangement;
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

    // ── Audio thread ──────────────────────────────────────────────────────────

    public void SetTransport(double beatsPerMinute, bool playing)
    {
        tempo = beatsPerMinute;
        transportPlaying = playing;
    }

    public void NoteOn(int midiNote, float velocity)
    {
        if (soundingNotes.Contains(midiNote))
        {
            inputEvents.Add(Vst3Event.NoteOff(midiNote, 0));
        }
        if (inputEvents.Add(Vst3Event.NoteOn(midiNote, velocity, 0)))
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
    public void Process(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double>? parameterValues)
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
                ProcessChunk(leftBlock.Slice(blockStart, chunkLength), rightBlock.Slice(blockStart, chunkLength), parameterValues);
                parameterValues = null;
            }
        }
        finally
        {
            Volatile.Write(ref lifecycleState, StateIdle);
        }
    }

    private void ProcessChunk(Span<float> leftBlock, Span<float> rightBlock, IReadOnlyDictionary<string, double>? parameterValues)
    {
        var frameCount = leftBlock.Length;
        for (var busIndex = 0; busIndex < inputBusCount; busIndex++)
        {
            var busChannelCount = inputBuses[busIndex].NumChannels;
            for (var channelIndex = 0; channelIndex < busChannelCount; channelIndex++)
            {
                var channel = new Span<float>(inputBuses[busIndex].ChannelBuffers32[channelIndex], frameCount);
                if (busIndex != 0)
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

    /// <summary>Sends every p&lt;id&gt; value that changed since the last block (as many as fit; the rest next time).</summary>
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
        if (lastSentValues.TryGetValue(parameterId, out var lastSentValue) && Math.Abs(lastSentValue - normalizedValue) < 1e-9)
        {
            return true;
        }
        if (inputParameterChanges.QueueFor(parameterId, out _) is not { } valueQueue)
        {
            return false;
        }
        valueQueue.AddPoint(0, normalizedValue);
        lastSentValues[parameterId] = normalizedValue;
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
