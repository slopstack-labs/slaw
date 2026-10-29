namespace SlopstackLabs.Slaw.Playback;

/// <summary>Picks the MIDI input backend for the host platform.</summary>
internal static class MidiInputFactory
{
    /// <summary>Creates the platform backend, or returns null with a reason when the platform has none.</summary>
    public static IMidiInputSource? TryCreate(out string? unavailabilityReason)
    {
        unavailabilityReason = null;
        if (OperatingSystem.IsLinux())
        {
            return new AlsaSequencerMidiInput();
        }
        if (OperatingSystem.IsWindows())
        {
            return new WindowsWinMmMidiInput();
        }
        unavailabilityReason = "MIDI input is not supported on this platform yet (CoreMIDI support is on the roadmap, between Neural Nexus and Ontological Optimization)";
        return null;
    }
}
