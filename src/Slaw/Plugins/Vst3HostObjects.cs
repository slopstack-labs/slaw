using System.Runtime.InteropServices;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// A managed object wearing a COM costume: unmanaged memory holding a vtable
/// pointer and a GCHandle back to the C# instance. Reference counted for real,
/// because plugins keep what they are given and release it on their own schedule.
/// The creator owns the first reference and drops it with <see cref="Release"/>.
/// </summary>
internal abstract unsafe class Vst3HostObject
{
    private int referenceCount = 1;
    private GCHandle selfHandle;

    protected Vst3HostObject(IntPtr* vtable)
    {
        selfHandle = GCHandle.Alloc(this);
        NativePointer = (IntPtr)NativeMemory.Alloc((nuint)(2 * IntPtr.Size));
        ((IntPtr*)NativePointer)[0] = (IntPtr)vtable;
        ((IntPtr*)NativePointer)[1] = GCHandle.ToIntPtr(selfHandle);
    }

    public IntPtr NativePointer { get; private set; }

    /// <summary>Interfaces this object answers to, besides FUnknown.</summary>
    protected abstract byte[][] InterfaceIds { get; }

    public void Release() => ReleaseReference();

    private uint AddReference() => (uint)Interlocked.Increment(ref referenceCount);

    private uint ReleaseReference()
    {
        var remainingReferences = Interlocked.Decrement(ref referenceCount);
        if (remainingReferences == 0)
        {
            OnFinalRelease();
            NativeMemory.Free((void*)NativePointer);
            NativePointer = IntPtr.Zero;
            selfHandle.Free();
        }
        return (uint)Math.Max(0, remainingReferences);
    }

    protected virtual void OnFinalRelease()
    {
    }

    protected static T? Resolve<T>(IntPtr self) where T : class =>
        self == IntPtr.Zero ? null : GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target as T;

    /// <summary>Allocates a vtable whose first three slots are FUnknown's, followed by <paramref name="interfaceSlots"/>.</summary>
    protected static IntPtr* BuildVtable(params IntPtr[] interfaceSlots)
    {
        var vtable = (IntPtr*)NativeMemory.Alloc((nuint)((3 + interfaceSlots.Length) * IntPtr.Size));
        vtable[0] = (IntPtr)(delegate* unmanaged<IntPtr, byte*, IntPtr*, int>)&QueryInterfaceThunk;
        vtable[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&AddReferenceThunk;
        vtable[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&ReleaseThunk;
        for (var slotIndex = 0; slotIndex < interfaceSlots.Length; slotIndex++)
        {
            vtable[3 + slotIndex] = interfaceSlots[slotIndex];
        }
        return vtable;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterfaceThunk(IntPtr self, byte* interfaceId, IntPtr* queriedObject)
    {
        if (queriedObject is null)
        {
            return Vst3Abi.InvalidArgument;
        }
        *queriedObject = IntPtr.Zero;
        if (Resolve<Vst3HostObject>(self) is not { } hostObject)
        {
            return Vst3Abi.NoInterface;
        }
        if (Vst3Abi.UidEquals(interfaceId, Vst3Abi.FUnknownIid) || hostObject.InterfaceIds.Any(knownIid => Vst3Abi.UidEquals(interfaceId, knownIid)))
        {
            hostObject.AddReference();
            *queriedObject = self;
            return Vst3Abi.ResultOk;
        }
        return Vst3Abi.NoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint AddReferenceThunk(IntPtr self) => Resolve<Vst3HostObject>(self)?.AddReference() ?? 0;

    [UnmanagedCallersOnly]
    private static uint ReleaseThunk(IntPtr self) => Resolve<Vst3HostObject>(self)?.ReleaseReference() ?? 0;
}

/// <summary>IHostApplication: our name, and a factory for the messages plugins send themselves.</summary>
internal sealed unsafe class Vst3HostApplication : Vst3HostObject
{
    private static readonly IntPtr* SharedVtable = BuildVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, char*, int>)&GetNameThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, byte*, IntPtr*, int>)&CreateInstanceThunk);

    /// <summary>One host context for every plugin, alive for the whole process: plugins may cache it forever.</summary>
    public static readonly Vst3HostApplication Shared = new();

    private Vst3HostApplication()
        : base(SharedVtable)
    {
    }

    protected override byte[][] InterfaceIds { get; } = { Vst3Abi.HostApplicationIid };

    [UnmanagedCallersOnly]
    private static int GetNameThunk(IntPtr self, char* hostName)
    {
        if (hostName is null)
        {
            return Vst3Abi.InvalidArgument;
        }
        Vst3Abi.WriteUtf16(hostName, 128, "slaw (AI-native DAW)");
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int CreateInstanceThunk(IntPtr self, byte* classId, byte* interfaceId, IntPtr* createdObject)
    {
        if (createdObject is null)
        {
            return Vst3Abi.InvalidArgument;
        }
        *createdObject = IntPtr.Zero;
        if (Vst3Abi.UidEquals(classId, Vst3Abi.MessageIid) || Vst3Abi.UidEquals(interfaceId, Vst3Abi.MessageIid))
        {
            *createdObject = new Vst3HostMessage().NativePointer;
            return Vst3Abi.ResultOk;
        }
        if (Vst3Abi.UidEquals(classId, Vst3Abi.AttributeListIid) || Vst3Abi.UidEquals(interfaceId, Vst3Abi.AttributeListIid))
        {
            *createdObject = new Vst3HostAttributeList().NativePointer;
            return Vst3Abi.ResultOk;
        }
        return Vst3Abi.NoInterface;
    }
}

/// <summary>Where a component handler delivers what the plugin's controller reports.</summary>
internal interface IVst3ComponentHandlerTarget
{
    void BeginEdit(uint parameterId);
    void PerformEdit(uint parameterId, double normalizedValue);
    void EndEdit(uint parameterId);
    void RestartComponent(int restartFlags);
}

/// <summary>IComponentHandler: the plugin's controller reporting knob edits, mostly from its own editor.</summary>
internal sealed unsafe class Vst3HostComponentHandler : Vst3HostObject
{
    private static readonly IntPtr* SharedVtable = BuildVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, uint, int>)&BeginEditThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, uint, double, int>)&PerformEditThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, uint, int>)&EndEditThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&RestartComponentThunk);

    private readonly IVst3ComponentHandlerTarget target;

    public Vst3HostComponentHandler(IVst3ComponentHandlerTarget target)
        : base(SharedVtable)
    {
        this.target = target;
    }

    protected override byte[][] InterfaceIds { get; } = { Vst3Abi.ComponentHandlerIid };

    /// <summary>Runs a report against the target; exceptions must not unwind into the plugin.</summary>
    private static int Deliver(IntPtr self, Action<IVst3ComponentHandlerTarget> report)
    {
        try
        {
            if (Resolve<Vst3HostComponentHandler>(self) is { } handler)
            {
                report(handler.target);
            }
        }
        catch (Exception reportRemediationOpportunity)
        {
            Console.Error.WriteLine($"[plugin host] component handler: {reportRemediationOpportunity.Message}");
        }
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int BeginEditThunk(IntPtr self, uint parameterId) => Deliver(self, target => target.BeginEdit(parameterId));

    [UnmanagedCallersOnly]
    private static int PerformEditThunk(IntPtr self, uint parameterId, double normalizedValue) =>
        Deliver(self, target => target.PerformEdit(parameterId, normalizedValue));

    [UnmanagedCallersOnly]
    private static int EndEditThunk(IntPtr self, uint parameterId) => Deliver(self, target => target.EndEdit(parameterId));

    [UnmanagedCallersOnly]
    private static int RestartComponentThunk(IntPtr self, int restartFlags) => Deliver(self, target => target.RestartComponent(restartFlags));
}

/// <summary>IParamValueQueue with preallocated points: it lives on the audio thread.</summary>
internal sealed unsafe class Vst3HostParamValueQueue : Vst3HostObject
{
    private const int PointCapacity = 16;

    private static readonly IntPtr* SharedVtable = BuildVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, uint>)&GetParameterIdThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, int>)&GetPointCountThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, int, int*, double*, int>)&GetPointThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, int, double, int*, int>)&AddPointThunk);

    private readonly int[] pointOffsets = new int[PointCapacity];
    private readonly double[] pointValues = new double[PointCapacity];

    public Vst3HostParamValueQueue()
        : base(SharedVtable)
    {
    }

    protected override byte[][] InterfaceIds { get; } = { Vst3Abi.ParamValueQueueIid };

    public uint ParameterId { get; private set; }

    public int PointCount { get; private set; }

    public void Reset(uint parameterId)
    {
        ParameterId = parameterId;
        PointCount = 0;
    }

    public int AddPoint(int sampleOffset, double normalizedValue)
    {
        if (PointCount >= PointCapacity)
        {
            return -1;
        }
        pointOffsets[PointCount] = sampleOffset;
        pointValues[PointCount] = normalizedValue;
        return PointCount++;
    }

    public (int SampleOffset, double Value) PointAt(int pointIndex) => (pointOffsets[pointIndex], pointValues[pointIndex]);

    [UnmanagedCallersOnly]
    private static uint GetParameterIdThunk(IntPtr self) => Resolve<Vst3HostParamValueQueue>(self)?.ParameterId ?? 0;

    [UnmanagedCallersOnly]
    private static int GetPointCountThunk(IntPtr self) => Resolve<Vst3HostParamValueQueue>(self)?.PointCount ?? 0;

    [UnmanagedCallersOnly]
    private static int GetPointThunk(IntPtr self, int pointIndex, int* sampleOffset, double* normalizedValue)
    {
        if (Resolve<Vst3HostParamValueQueue>(self) is not { } queue || pointIndex < 0 || pointIndex >= queue.PointCount)
        {
            return Vst3Abi.InvalidArgument;
        }
        if (sampleOffset is not null)
        {
            *sampleOffset = queue.pointOffsets[pointIndex];
        }
        if (normalizedValue is not null)
        {
            *normalizedValue = queue.pointValues[pointIndex];
        }
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int AddPointThunk(IntPtr self, int sampleOffset, double normalizedValue, int* pointIndex)
    {
        var addedIndex = Resolve<Vst3HostParamValueQueue>(self)?.AddPoint(sampleOffset, normalizedValue) ?? -1;
        if (pointIndex is not null)
        {
            *pointIndex = addedIndex;
        }
        return addedIndex >= 0 ? Vst3Abi.ResultOk : Vst3Abi.ResultFalse;
    }
}

/// <summary>
/// IParameterChanges over a fixed pool of queues. Changes beyond the pool's
/// capacity are refused; the caller simply sends them next block.
/// </summary>
internal sealed unsafe class Vst3HostParameterChanges : Vst3HostObject
{
    public const int QueueCapacity = 64;

    private static readonly IntPtr* SharedVtable = BuildVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, int>)&GetParameterCountThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr>)&GetParameterDataThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, uint*, int*, IntPtr>)&AddParameterDataThunk);

    private readonly Vst3HostParamValueQueue[] queues;

    public Vst3HostParameterChanges()
        : base(SharedVtable)
    {
        queues = Enumerable.Range(0, QueueCapacity).Select(_ => new Vst3HostParamValueQueue()).ToArray();
    }

    protected override byte[][] InterfaceIds { get; } = { Vst3Abi.ParameterChangesIid };

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public Vst3HostParamValueQueue QueueAt(int queueIndex) => queues[queueIndex];

    /// <summary>Returns the queue for <paramref name="parameterId"/>, or null when the pool is full.</summary>
    public Vst3HostParamValueQueue? QueueFor(uint parameterId, out int queueIndex)
    {
        for (queueIndex = 0; queueIndex < Count; queueIndex++)
        {
            if (queues[queueIndex].ParameterId == parameterId)
            {
                return queues[queueIndex];
            }
        }
        if (Count >= QueueCapacity)
        {
            queueIndex = -1;
            return null;
        }
        queueIndex = Count++;
        queues[queueIndex].Reset(parameterId);
        return queues[queueIndex];
    }

    protected override void OnFinalRelease()
    {
        foreach (var queue in queues)
        {
            queue.Release();
        }
    }

    [UnmanagedCallersOnly]
    private static int GetParameterCountThunk(IntPtr self) => Resolve<Vst3HostParameterChanges>(self)?.Count ?? 0;

    [UnmanagedCallersOnly]
    private static IntPtr GetParameterDataThunk(IntPtr self, int queueIndex) =>
        Resolve<Vst3HostParameterChanges>(self) is { } parameterChanges && queueIndex >= 0 && queueIndex < parameterChanges.Count
            ? parameterChanges.queues[queueIndex].NativePointer
            : IntPtr.Zero;

    [UnmanagedCallersOnly]
    private static IntPtr AddParameterDataThunk(IntPtr self, uint* parameterId, int* queueIndex)
    {
        if (Resolve<Vst3HostParameterChanges>(self) is not { } parameterChanges || parameterId is null)
        {
            return IntPtr.Zero;
        }
        var queue = parameterChanges.QueueFor(*parameterId, out var assignedIndex);
        if (queueIndex is not null)
        {
            *queueIndex = assignedIndex;
        }
        return queue?.NativePointer ?? IntPtr.Zero;
    }
}

/// <summary>IEventList over a preallocated event array.</summary>
internal sealed unsafe class Vst3HostEventList : Vst3HostObject
{
    public const int EventCapacity = 512;

    private static readonly IntPtr* SharedVtable = BuildVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, int>)&GetEventCountThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, int, Vst3Event*, int>)&GetEventThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, Vst3Event*, int>)&AddEventThunk);

    private readonly Vst3Event[] events = new Vst3Event[EventCapacity];

    public Vst3HostEventList()
        : base(SharedVtable)
    {
    }

    protected override byte[][] InterfaceIds { get; } = { Vst3Abi.EventListIid };

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public bool Add(in Vst3Event noteEvent)
    {
        if (Count >= EventCapacity)
        {
            return false;
        }
        events[Count++] = noteEvent;
        return true;
    }

    public Vst3Event EventAt(int eventIndex) => events[eventIndex];

    [UnmanagedCallersOnly]
    private static int GetEventCountThunk(IntPtr self) => Resolve<Vst3HostEventList>(self)?.Count ?? 0;

    [UnmanagedCallersOnly]
    private static int GetEventThunk(IntPtr self, int eventIndex, Vst3Event* destination)
    {
        if (Resolve<Vst3HostEventList>(self) is not { } eventList || destination is null || eventIndex < 0 || eventIndex >= eventList.Count)
        {
            return Vst3Abi.InvalidArgument;
        }
        *destination = eventList.events[eventIndex];
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int AddEventThunk(IntPtr self, Vst3Event* addedEvent) =>
        Resolve<Vst3HostEventList>(self) is { } eventList && addedEvent is not null && eventList.Add(*addedEvent)
            ? Vst3Abi.ResultOk
            : Vst3Abi.ResultFalse;
}

/// <summary>IBStream backed by a MemoryStream, for getState/setState.</summary>
internal sealed unsafe class Vst3HostStream : Vst3HostObject
{
    private static readonly IntPtr* SharedVtable = BuildVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, int*, int>)&ReadThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, int*, int>)&WriteThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, long, int, long*, int>)&SeekThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, long*, int>)&TellThunk);

    private readonly MemoryStream backingStream;

    public Vst3HostStream(byte[]? initialContents = null)
        : base(SharedVtable)
    {
        backingStream = new MemoryStream();
        if (initialContents is not null)
        {
            backingStream.Write(initialContents);
            backingStream.Position = 0;
        }
    }

    protected override byte[][] InterfaceIds { get; } = { Vst3Abi.StreamIid };

    public byte[] ToArray() => backingStream.ToArray();

    public void Rewind() => backingStream.Position = 0;

    [UnmanagedCallersOnly]
    private static int ReadThunk(IntPtr self, byte* buffer, int requestedByteCount, int* readByteCount)
    {
        if (Resolve<Vst3HostStream>(self) is not { } stream || buffer is null || requestedByteCount < 0)
        {
            return Vst3Abi.InvalidArgument;
        }
        var actualByteCount = stream.backingStream.Read(new Span<byte>(buffer, requestedByteCount));
        if (readByteCount is not null)
        {
            *readByteCount = actualByteCount;
        }
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int WriteThunk(IntPtr self, byte* buffer, int byteCount, int* writtenByteCount)
    {
        if (Resolve<Vst3HostStream>(self) is not { } stream || buffer is null || byteCount < 0)
        {
            return Vst3Abi.InvalidArgument;
        }
        stream.backingStream.Write(new ReadOnlySpan<byte>(buffer, byteCount));
        if (writtenByteCount is not null)
        {
            *writtenByteCount = byteCount;
        }
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int SeekThunk(IntPtr self, long position, int seekMode, long* resultingPosition)
    {
        if (Resolve<Vst3HostStream>(self) is not { } stream)
        {
            return Vst3Abi.InvalidArgument;
        }
        var origin = seekMode switch
        {
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin,
        };
        var newPosition = stream.backingStream.Seek(position, origin);
        if (resultingPosition is not null)
        {
            *resultingPosition = newPosition;
        }
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int TellThunk(IntPtr self, long* position)
    {
        if (Resolve<Vst3HostStream>(self) is not { } stream || position is null)
        {
            return Vst3Abi.InvalidArgument;
        }
        *position = stream.backingStream.Position;
        return Vst3Abi.ResultOk;
    }
}

/// <summary>IMessage: an id and an attribute list, for plugins whose halves talk to each other through the host.</summary>
internal sealed unsafe class Vst3HostMessage : Vst3HostObject
{
    private static readonly IntPtr* SharedVtable = BuildVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, byte*>)&GetMessageIdThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, void>)&SetMessageIdThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr>)&GetAttributesThunk);

    private readonly Vst3HostAttributeList attributes = new();
    private byte* messageId;

    public Vst3HostMessage()
        : base(SharedVtable)
    {
    }

    protected override byte[][] InterfaceIds { get; } = { Vst3Abi.MessageIid };

    protected override void OnFinalRelease()
    {
        NativeMemory.Free(messageId);
        attributes.Release();
    }

    [UnmanagedCallersOnly]
    private static byte* GetMessageIdThunk(IntPtr self) => Resolve<Vst3HostMessage>(self) is { } message ? message.messageId : null;

    [UnmanagedCallersOnly]
    private static void SetMessageIdThunk(IntPtr self, byte* newMessageId)
    {
        if (Resolve<Vst3HostMessage>(self) is not { } message)
        {
            return;
        }
        NativeMemory.Free(message.messageId);
        message.messageId = null;
        if (newMessageId is null)
        {
            return;
        }
        var idLength = 0;
        while (newMessageId[idLength] != 0)
        {
            idLength++;
        }
        message.messageId = (byte*)NativeMemory.Alloc((nuint)idLength + 1);
        Buffer.MemoryCopy(newMessageId, message.messageId, idLength + 1, idLength + 1);
    }

    [UnmanagedCallersOnly]
    private static IntPtr GetAttributesThunk(IntPtr self) => Resolve<Vst3HostMessage>(self)?.attributes.NativePointer ?? IntPtr.Zero;
}

/// <summary>IAttributeList: ints, floats, strings and blobs keyed by ASCII ids.</summary>
internal sealed unsafe class Vst3HostAttributeList : Vst3HostObject
{
    private static readonly IntPtr* SharedVtable = BuildVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, long, int>)&SetIntThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, long*, int>)&GetIntThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, double, int>)&SetFloatThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, double*, int>)&GetFloatThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, char*, int>)&SetStringThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, char*, uint, int>)&GetStringThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, void*, uint, int>)&SetBinaryThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, byte*, void**, uint*, int>)&GetBinaryThunk);

    private readonly Dictionary<string, object> storedAttributes = new(StringComparer.Ordinal);
    private readonly List<IntPtr> pinnedBinaries = new();

    public Vst3HostAttributeList()
        : base(SharedVtable)
    {
    }

    protected override byte[][] InterfaceIds { get; } = { Vst3Abi.AttributeListIid };

    protected override void OnFinalRelease()
    {
        foreach (var pinnedBinary in pinnedBinaries)
        {
            NativeMemory.Free((void*)pinnedBinary);
        }
        pinnedBinaries.Clear();
    }

    private static string? AttributeKey(byte* attributeId) => attributeId is null ? null : Vst3Abi.ReadAscii(attributeId, 256);

    private static int Store(IntPtr self, byte* attributeId, object attributeValue)
    {
        if (Resolve<Vst3HostAttributeList>(self) is not { } attributeList || AttributeKey(attributeId) is not { } key)
        {
            return Vst3Abi.InvalidArgument;
        }
        attributeList.storedAttributes[key] = attributeValue;
        return Vst3Abi.ResultOk;
    }

    private static bool TryLoad<T>(IntPtr self, byte* attributeId, out T attributeValue)
    {
        attributeValue = default!;
        if (Resolve<Vst3HostAttributeList>(self) is { } attributeList && AttributeKey(attributeId) is { } key &&
            attributeList.storedAttributes.TryGetValue(key, out var storedValue) && storedValue is T typedValue)
        {
            attributeValue = typedValue;
            return true;
        }
        return false;
    }

    [UnmanagedCallersOnly]
    private static int SetIntThunk(IntPtr self, byte* attributeId, long value) => Store(self, attributeId, value);

    [UnmanagedCallersOnly]
    private static int GetIntThunk(IntPtr self, byte* attributeId, long* value)
    {
        if (value is null || !TryLoad<long>(self, attributeId, out var storedValue))
        {
            return Vst3Abi.ResultFalse;
        }
        *value = storedValue;
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int SetFloatThunk(IntPtr self, byte* attributeId, double value) => Store(self, attributeId, value);

    [UnmanagedCallersOnly]
    private static int GetFloatThunk(IntPtr self, byte* attributeId, double* value)
    {
        if (value is null || !TryLoad<double>(self, attributeId, out var storedValue))
        {
            return Vst3Abi.ResultFalse;
        }
        *value = storedValue;
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int SetStringThunk(IntPtr self, byte* attributeId, char* value) =>
        value is null ? Vst3Abi.InvalidArgument : Store(self, attributeId, Vst3Abi.ReadUtf16(value, int.MaxValue / 2));

    [UnmanagedCallersOnly]
    private static int GetStringThunk(IntPtr self, byte* attributeId, char* destination, uint destinationSizeInBytes)
    {
        if (destination is null || destinationSizeInBytes < 2 || !TryLoad<string>(self, attributeId, out var storedValue))
        {
            return Vst3Abi.ResultFalse;
        }
        Vst3Abi.WriteUtf16(destination, (int)(destinationSizeInBytes / 2), storedValue);
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int SetBinaryThunk(IntPtr self, byte* attributeId, void* data, uint byteCount) =>
        Store(self, attributeId, data is null ? Array.Empty<byte>() : new ReadOnlySpan<byte>(data, (int)byteCount).ToArray());

    [UnmanagedCallersOnly]
    private static int GetBinaryThunk(IntPtr self, byte* attributeId, void** data, uint* byteCount)
    {
        if (data is null || byteCount is null || Resolve<Vst3HostAttributeList>(self) is not { } attributeList ||
            !TryLoad<byte[]>(self, attributeId, out var storedValue))
        {
            return Vst3Abi.ResultFalse;
        }
        // The pointer must stay valid as long as the list does, so the blob is copied into memory we own.
        var pinnedCopy = NativeMemory.Alloc((nuint)Math.Max(1, storedValue.Length));
        storedValue.CopyTo(new Span<byte>(pinnedCopy, storedValue.Length));
        attributeList.pinnedBinaries.Add((IntPtr)pinnedCopy);
        *data = pinnedCopy;
        *byteCount = (uint)storedValue.Length;
        return Vst3Abi.ResultOk;
    }
}
