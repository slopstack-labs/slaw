namespace SlopstackLabs.Slaw.Playback;

internal enum MidiMessageKind
{
    NoteOn,
    NoteOff,
    ControlChange,
}

/// <summary>
/// One incoming MIDI message, already decoded. A note-on with velocity 0 is
/// delivered as <see cref="MidiMessageKind.NoteOff"/>, as the spec intended and
/// half the keyboards on earth implement.
/// </summary>
internal readonly record struct MidiMessage(MidiMessageKind Kind, int Channel, int Data1, int Data2, string SourceName);

/// <summary>
/// A platform MIDI input backend. Implementations connect to every available
/// MIDI source (and, where the platform allows, ones plugged in later) and
/// raise <see cref="MessageReceived"/> on a background thread.
/// </summary>
internal interface IMidiInputSource : IDisposable
{
    /// <summary>Human-facing backend name, e.g. "ALSA sequencer" or "winmm".</summary>
    string BackendName { get; }

    /// <summary>Names of the sources currently connected.</summary>
    IReadOnlyList<string> ConnectedSourceNames { get; }

    /// <summary>Raised on a background thread for every decoded message.</summary>
    event Action<MidiMessage>? MessageReceived;

    /// <summary>Raised on a background thread when the set of connected sources changes.</summary>
    event Action? SourcesChanged;

    /// <summary>Opens the backend and connects sources. Throws <see cref="InvalidOperationException"/> when unavailable.</summary>
    void Start();
}
