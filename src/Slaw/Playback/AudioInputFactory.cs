namespace SlopstackLabs.Slaw.Playback;

/// <summary>Picks the audio input backend for the host platform.</summary>
internal static class AudioInputFactory
{
    /// <summary>Creates (but does not start) the platform backend, or returns null with a reason when the platform has none.</summary>
    public static IAudioInputSource? TryCreate(int sampleRate, string preferredSubstrate, string? inputDeviceName, out string? unavailabilityReason)
    {
        unavailabilityReason = null;
        if (OperatingSystem.IsWindows())
        {
            return new WindowsWaveInAudioInput(sampleRate, inputDeviceName);
        }
        if (OperatingSystem.IsLinux())
        {
            return new PipedProcessAudioInput(sampleRate, preferredSubstrate, inputDeviceName);
        }
        if (OperatingSystem.IsMacOS())
        {
            return new MacAudioQueueAudioInput(sampleRate);
        }
        unavailabilityReason = "audio recording is not supported on this platform yet (CoreAudio input is scheduled for the same quarter as CoreMIDI)";
        return null;
    }

    /// <summary>Input devices the user may pick in Settings; empty means "the default device only".</summary>
    public static IReadOnlyList<string> ListInputDevices()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsWaveDeviceDirectory.ListInputDevices();
        }
        return OperatingSystem.IsLinux() ? PipedProcessAudioInput.ListInputDevices() : Array.Empty<string>();
    }
}
