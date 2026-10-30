using System.Runtime.InteropServices;
using System.Text;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// The parts of the VST3 binary interface slaw speaks, transcribed from the
/// SDK's pluginterfaces by hand: interface IDs, the structs that cross the
/// boundary, and vtable slot numbers. VST3 is COM without the registry, so a
/// C# host is "just" pointer arithmetic performed with conviction.
/// </summary>
internal static unsafe class Vst3Abi
{
    // ── tresult ───────────────────────────────────────────────────────────────

    public const int ResultOk = 0;
    public const int ResultFalse = 1;
    public static readonly int NoInterface = OperatingSystem.IsWindows() ? unchecked((int)0x80004002) : -1;
    public static readonly int InvalidArgument = OperatingSystem.IsWindows() ? unchecked((int)0x80070057) : 2;
    public static readonly int NotImplemented = OperatingSystem.IsWindows() ? unchecked((int)0x80004001) : 3;

    // ── Enumerations ──────────────────────────────────────────────────────────

    public const int MediaTypeAudio = 0;
    public const int MediaTypeEvent = 1;
    public const int BusDirectionInput = 0;
    public const int BusDirectionOutput = 1;
    public const int ProcessModeRealtime = 0;
    public const int ProcessModeOffline = 2;
    public const int SampleSize32 = 0;
    public const ulong SpeakerArrangementStereo = 0x3;
    public const ushort EventTypeNoteOn = 0;
    public const ushort EventTypeNoteOff = 1;
    public const int ParameterFlagCanAutomate = 1 << 0;
    public const int ParameterFlagReadOnly = 1 << 1;
    public const int ParameterFlagHidden = 1 << 4;
    public const int ParameterFlagProgramChange = 1 << 15;
    public const int ParameterFlagBypass = 1 << 16;

    public const int RestartParameterValuesChanged = 1 << 2;

    /// <summary>FIDStrings for IEditController::createView and IPlugView::isPlatformTypeSupported (ASCII, NUL-terminated).</summary>
    public static readonly byte[] EditorViewType = "editor\0"u8.ToArray();
    public static readonly byte[] PlatformTypeHwnd = "HWND\0"u8.ToArray();

    public const uint ProcessContextPlaying = 1 << 1;
    public const uint ProcessContextProjectTimeMusicValid = 1 << 9;
    public const uint ProcessContextTempoValid = 1 << 10;
    public const uint ProcessContextTimeSignatureValid = 1 << 13;

    public const string AudioModuleClassCategory = "Audio Module Class";

    // ── Interface IDs ─────────────────────────────────────────────────────────

    public static readonly byte[] FUnknownIid = Uid(0x00000000, 0x00000000, 0xC0000000, 0x00000046);
    public static readonly byte[] PluginBaseIid = Uid(0x22888DDB, 0x156E45AE, 0x8358B348, 0x08190625);
    public static readonly byte[] PluginFactoryIid = Uid(0x7A4D811C, 0x52114A1F, 0xAED9D2EE, 0x0B43BF9F);
    public static readonly byte[] PluginFactory2Iid = Uid(0x0007B650, 0xF24B4C0B, 0xA464EDB9, 0xF00B2ABB);
    public static readonly byte[] PluginFactory3Iid = Uid(0x4555A2AB, 0xC1234E57, 0x9B122910, 0x36878931);
    public static readonly byte[] ComponentIid = Uid(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802);
    public static readonly byte[] AudioProcessorIid = Uid(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D);
    public static readonly byte[] EditControllerIid = Uid(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E);
    public static readonly byte[] ConnectionPointIid = Uid(0x70A4156F, 0x6E6E4026, 0x989148BF, 0xAA60D8D1);
    public static readonly byte[] HostApplicationIid = Uid(0x58E595CC, 0xDB2D4969, 0x8B6AAF8C, 0x36A664E5);
    public static readonly byte[] ComponentHandlerIid = Uid(0x93A0BEA3, 0x0BD045DB, 0x8E890B0C, 0xC1E46AC6);
    public static readonly byte[] ParameterChangesIid = Uid(0xA4779663, 0x0BB64A56, 0xB44384A8, 0x466FEB9D);
    public static readonly byte[] ParamValueQueueIid = Uid(0x01263A18, 0xED074F6F, 0x98C9D356, 0x4686F9BA);
    public static readonly byte[] EventListIid = Uid(0x3A2C4214, 0x346349FE, 0xB2C4F397, 0xB9695A44);
    public static readonly byte[] StreamIid = Uid(0xC3BF6EA2, 0x30994752, 0x9B6BF990, 0x1EE33E9B);
    public static readonly byte[] MessageIid = Uid(0x936F033B, 0xC6C047DB, 0xBB0882F8, 0x13C1E613);
    public static readonly byte[] AttributeListIid = Uid(0x1E5F0AEB, 0xCC7F4533, 0xA2544011, 0x38AD5EE4);
    public static readonly byte[] PlugViewIid = Uid(0x5BC32507, 0xD06049EA, 0xA6151B52, 0x2B5571EF);
    public static readonly byte[] PlugFrameIid = Uid(0x367FAF01, 0xAFA94693, 0x8D4DA2A0, 0xED0882A3);
    public static readonly byte[] PlugViewContentScaleSupportIid = Uid(0x65ED9690, 0x8AC44525, 0x8AADEF7A, 0x72EA703F);

    /// <summary>
    /// INLINE_UID: plain big-endian everywhere except Windows, where the SDK
    /// lays the first eight bytes out like a COM GUID for old times' sake.
    /// </summary>
    public static byte[] Uid(uint firstLong, uint secondLong, uint thirdLong, uint fourthLong)
    {
        var uidBytes = new byte[16];
        if (OperatingSystem.IsWindows())
        {
            uidBytes[0] = (byte)firstLong;
            uidBytes[1] = (byte)(firstLong >> 8);
            uidBytes[2] = (byte)(firstLong >> 16);
            uidBytes[3] = (byte)(firstLong >> 24);
            uidBytes[4] = (byte)(secondLong >> 16);
            uidBytes[5] = (byte)(secondLong >> 24);
            uidBytes[6] = (byte)secondLong;
            uidBytes[7] = (byte)(secondLong >> 8);
        }
        else
        {
            WriteBigEndian(uidBytes, 0, firstLong);
            WriteBigEndian(uidBytes, 4, secondLong);
        }
        WriteBigEndian(uidBytes, 8, thirdLong);
        WriteBigEndian(uidBytes, 12, fourthLong);
        return uidBytes;
    }

    private static void WriteBigEndian(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)(value >> 16);
        destination[offset + 2] = (byte)(value >> 8);
        destination[offset + 3] = (byte)value;
    }

    public static bool UidEquals(byte* candidateUid, byte[] knownUid) =>
        candidateUid is not null && new ReadOnlySpan<byte>(candidateUid, 16).SequenceEqual(knownUid);

    public static string FormatUid(ReadOnlySpan<byte> uidBytes) => Convert.ToHexString(uidBytes);

    public static byte[]? ParseUid(string? classIdentifier)
    {
        if (classIdentifier is not { Length: 32 })
        {
            return null;
        }
        try
        {
            return Convert.FromHexString(classIdentifier);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ── String helpers ────────────────────────────────────────────────────────

    public static string ReadAscii(byte* text, int capacity)
    {
        var length = 0;
        while (length < capacity && text[length] != 0)
        {
            length++;
        }
        return Encoding.UTF8.GetString(text, length);
    }

    public static string ReadUtf16(char* text, int capacityInCharacters)
    {
        var length = 0;
        while (length < capacityInCharacters && text[length] != 0)
        {
            length++;
        }
        return new string(text, 0, length);
    }

    public static void WriteUtf16(char* destination, int capacityInCharacters, string text)
    {
        var copiedLength = Math.Min(text.Length, capacityInCharacters - 1);
        for (var characterIndex = 0; characterIndex < copiedLength; characterIndex++)
        {
            destination[characterIndex] = text[characterIndex];
        }
        destination[copiedLength] = '\0';
    }

    // ── Calling into plugin objects ───────────────────────────────────────────

    /// <summary>Returns vtable slot <paramref name="slotIndex"/> of a COM-style object.</summary>
    public static IntPtr Slot(IntPtr comObject, int slotIndex) => (*(IntPtr**)comObject)[slotIndex];

    public static IntPtr QueryInterface(IntPtr comObject, byte[] interfaceId)
    {
        if (comObject == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        IntPtr queriedInterface = IntPtr.Zero;
        fixed (byte* interfaceIdBytes = interfaceId)
        {
            var queryResult = ((delegate* unmanaged<IntPtr, byte*, IntPtr*, int>)Slot(comObject, 0))(comObject, interfaceIdBytes, &queriedInterface);
            return queryResult == ResultOk ? queriedInterface : IntPtr.Zero;
        }
    }

    public static void Release(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, uint>)Slot(comObject, 2))(comObject);
        }
    }
}

// ── Structs that cross the boundary (natural alignment on every platform) ────

[StructLayout(LayoutKind.Explicit, Size = 116)]
internal unsafe struct Vst3ClassInfo
{
    [FieldOffset(0)] public fixed byte ClassId[16];
    [FieldOffset(16)] public int Cardinality;
    [FieldOffset(20)] public fixed byte Category[32];
    [FieldOffset(52)] public fixed byte Name[64];
}

[StructLayout(LayoutKind.Explicit, Size = 440)]
internal unsafe struct Vst3ClassInfo2
{
    [FieldOffset(0)] public fixed byte ClassId[16];
    [FieldOffset(16)] public int Cardinality;
    [FieldOffset(20)] public fixed byte Category[32];
    [FieldOffset(52)] public fixed byte Name[64];
    [FieldOffset(116)] public uint ClassFlags;
    [FieldOffset(120)] public fixed byte SubCategories[128];
    [FieldOffset(248)] public fixed byte Vendor[64];
    [FieldOffset(312)] public fixed byte Version[64];
    [FieldOffset(376)] public fixed byte SdkVersion[64];
}

[StructLayout(LayoutKind.Explicit, Size = 452)]
internal unsafe struct Vst3FactoryInfo
{
    [FieldOffset(0)] public fixed byte Vendor[64];
    [FieldOffset(64)] public fixed byte Url[256];
    [FieldOffset(320)] public fixed byte Email[128];
    [FieldOffset(448)] public int Flags;
}

[StructLayout(LayoutKind.Explicit, Size = 276)]
internal unsafe struct Vst3BusInfo
{
    [FieldOffset(0)] public int MediaType;
    [FieldOffset(4)] public int Direction;
    [FieldOffset(8)] public int ChannelCount;
    [FieldOffset(12)] public fixed char Name[128];
    [FieldOffset(268)] public int BusType;
    [FieldOffset(272)] public uint Flags;
}

[StructLayout(LayoutKind.Explicit, Size = 792)]
internal unsafe struct Vst3ParameterInfo
{
    [FieldOffset(0)] public uint Id;
    [FieldOffset(4)] public fixed char Title[128];
    [FieldOffset(260)] public fixed char ShortTitle[128];
    [FieldOffset(516)] public fixed char Units[128];
    [FieldOffset(772)] public int StepCount;
    [FieldOffset(776)] public double DefaultNormalizedValue;
    [FieldOffset(784)] public int UnitId;
    [FieldOffset(788)] public int Flags;
}

/// <summary>ViewRect: an editor's size in pixels, as left/top/right/bottom.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Vst3ViewRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Vst3ProcessSetup
{
    public int ProcessMode;
    public int SymbolicSampleSize;
    public int MaxSamplesPerBlock;
    public double SampleRate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Vst3AudioBusBuffers
{
    public int NumChannels;
    public ulong SilenceFlags;
    public float** ChannelBuffers32;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Vst3ProcessData
{
    public int ProcessMode;
    public int SymbolicSampleSize;
    public int NumSamples;
    public int NumInputs;
    public int NumOutputs;
    public Vst3AudioBusBuffers* Inputs;
    public Vst3AudioBusBuffers* Outputs;
    public IntPtr InputParameterChanges;
    public IntPtr OutputParameterChanges;
    public IntPtr InputEvents;
    public IntPtr OutputEvents;
    public Vst3ProcessContext* ProcessContext;
}

[StructLayout(LayoutKind.Explicit, Size = 112)]
internal struct Vst3ProcessContext
{
    [FieldOffset(0)] public uint State;
    [FieldOffset(8)] public double SampleRate;
    [FieldOffset(16)] public long ProjectTimeSamples;
    [FieldOffset(24)] public long SystemTime;
    [FieldOffset(32)] public long ContinuousTimeSamples;
    [FieldOffset(40)] public double ProjectTimeMusic;
    [FieldOffset(48)] public double BarPositionMusic;
    [FieldOffset(56)] public double CycleStartMusic;
    [FieldOffset(64)] public double CycleEndMusic;
    [FieldOffset(72)] public double Tempo;
    [FieldOffset(80)] public int TimeSignatureNumerator;
    [FieldOffset(84)] public int TimeSignatureDenominator;
}

/// <summary>Vst::Event with the note-on/note-off members of its union spelled out.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct Vst3Event
{
    [FieldOffset(0)] public int BusIndex;
    [FieldOffset(4)] public int SampleOffset;
    [FieldOffset(8)] public double PpqPosition;
    [FieldOffset(16)] public ushort Flags;
    [FieldOffset(18)] public ushort Type;

    // NoteOnEvent
    [FieldOffset(24)] public short Channel;
    [FieldOffset(26)] public short Pitch;
    [FieldOffset(28)] public float NoteOnTuning;
    [FieldOffset(32)] public float NoteOnVelocity;
    [FieldOffset(36)] public int NoteOnLength;
    [FieldOffset(40)] public int NoteOnNoteId;

    // NoteOffEvent (shares Channel and Pitch)
    [FieldOffset(28)] public float NoteOffVelocity;
    [FieldOffset(32)] public int NoteOffNoteId;
    [FieldOffset(36)] public float NoteOffTuning;

    public static Vst3Event NoteOn(int midiNote, float velocity, int sampleOffset) => new()
    {
        Type = Vst3Abi.EventTypeNoteOn,
        SampleOffset = sampleOffset,
        Pitch = (short)Math.Clamp(midiNote, 0, 127),
        NoteOnVelocity = Math.Clamp(velocity, 0f, 1f),
        NoteOnNoteId = -1,
    };

    public static Vst3Event NoteOff(int midiNote, int sampleOffset) => new()
    {
        Type = Vst3Abi.EventTypeNoteOff,
        SampleOffset = sampleOffset,
        Pitch = (short)Math.Clamp(midiNote, 0, 127),
        NoteOffVelocity = 0f,
        NoteOffNoteId = -1,
    };
}
