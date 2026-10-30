using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Playback;

/// <summary>
/// The waveOut and waveIn devices Windows will admit to. Devices are remembered by
/// name because their numbers change whenever something is plugged in. winmm cuts
/// names off at 31 characters, a limit it considers generous.
/// </summary>
internal static class WindowsWaveDeviceDirectory
{
    public const int QueuedBufferRingLength = 4;

    /// <summary>The lowest latency worth offering: measured with four buffers, 40 ms still ran dry in most runs and 48 ms never did.</summary>
    public const int MinimumLatencyMilliseconds = 48;

    /// <summary>Splits the latency setting across waveOut's ring, never below the minimum.</summary>
    public static int FramesPerQueuedBufferFor(int sampleRate, int latencyMilliseconds) =>
        (int)(sampleRate * (long)Math.Max(MinimumLatencyMilliseconds, latencyMilliseconds) / 1000 / QueuedBufferRingLength);

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> ListOutputDevices()
    {
        var deviceCount = waveOutGetNumDevs();
        var deviceNames = new List<string>();
        for (uint deviceIdentifier = 0; deviceIdentifier < deviceCount; deviceIdentifier++)
        {
            deviceNames.Add(waveOutGetDevCapsW(new UIntPtr(deviceIdentifier), out var capabilities, Marshal.SizeOf<WaveOutCapabilitiesRecord>()) == 0
                ? capabilities.szPname.TrimEnd()
                : $"output {deviceIdentifier}");
        }
        return NumberDuplicateNames(deviceNames);
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> ListInputDevices()
    {
        var deviceCount = waveInGetNumDevs();
        var deviceNames = new List<string>();
        for (uint deviceIdentifier = 0; deviceIdentifier < deviceCount; deviceIdentifier++)
        {
            deviceNames.Add(waveInGetDevCapsW(new UIntPtr(deviceIdentifier), out var capabilities, Marshal.SizeOf<WaveInCapabilitiesRecord>()) == 0
                ? capabilities.szPname.TrimEnd()
                : $"input {deviceIdentifier}");
        }
        return NumberDuplicateNames(deviceNames);
    }

    /// <summary>The device number currently behind a listed name, or null when it is not connected.</summary>
    [SupportedOSPlatform("windows")]
    public static uint? FindOutputDevice(string deviceName) => IdentifierOf(ListOutputDevices(), deviceName);

    /// <summary>The device number currently behind a listed name, or null when it is not connected.</summary>
    [SupportedOSPlatform("windows")]
    public static uint? FindInputDevice(string deviceName) => IdentifierOf(ListInputDevices(), deviceName);

    /// <summary>Two identical interfaces report one name between them; the second becomes "Name (2)" so both can be picked.</summary>
    internal static IReadOnlyList<string> NumberDuplicateNames(IReadOnlyList<string> deviceNames)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var numberedNames = new List<string>(deviceNames.Count);
        foreach (var deviceName in deviceNames)
        {
            occurrences[deviceName] = occurrences.GetValueOrDefault(deviceName) + 1;
            numberedNames.Add(occurrences[deviceName] == 1 ? deviceName : $"{deviceName} ({occurrences[deviceName]})");
        }
        return numberedNames;
    }

    private static uint? IdentifierOf(IReadOnlyList<string> deviceNames, string deviceName)
    {
        for (var deviceIndex = 0; deviceIndex < deviceNames.Count; deviceIndex++)
        {
            if (string.Equals(deviceNames[deviceIndex], deviceName, StringComparison.Ordinal))
            {
                return (uint)deviceIndex;
            }
        }
        return null;
    }

    // ── winmm interop ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCapabilitiesRecord
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCapabilitiesRecord
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetDevCapsW(UIntPtr uDeviceID, out WaveOutCapabilitiesRecord pwoc, int cbwoc);

    [DllImport("winmm.dll")]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveInGetDevCapsW(UIntPtr uDeviceID, out WaveInCapabilitiesRecord pwic, int cbwic);
}
