using System.Text.Json.Serialization;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// A slaw project, organized the way a session-oriented DAW is: tracks are
/// columns, scenes are rows, every cell may hold a clip, and the arrangement is
/// an ordered list of scenes to play. Persisted as JSON in a <c>.slaw</c> file —
/// a format deliberately simple enough for a language model to rewrite wholesale.
/// </summary>
internal sealed class SlawProject
{
    public const string CurrentFormatIdentifier = "slaw-project/v2";

    public string Format { get; set; } = CurrentFormatIdentifier;
    public string Name { get; set; } = "untitled slop";
    public double Bpm { get; set; } = 120;

    /// <summary>Default length, in sixteenth-note steps, of newly created clips.</summary>
    public int Steps { get; set; } = 16;

    /// <summary>
    /// Legacy v1 field: how often the single pattern repeated. Migrated into the
    /// arrangement on load and never written back.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Loops { get; set; }

    public List<string> Scenes { get; set; } = new();
    public List<SlawTrack> Tracks { get; set; } = new();
    /// <summary>
    /// Legacy input only: the scene sequence older files (and models) write. Remediation
    /// flattens it onto <see cref="Timeline"/> — the arrangement — and empties it, so it is
    /// read but never written back.
    /// </summary>
    [JsonIgnore]
    public List<ArrangementSection> Arrangement { get; set; } = new();

    [JsonPropertyName("arrangement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ArrangementSection>? SerializedArrangement
    {
        get => Arrangement.Count > 0 ? Arrangement : null;
        set => Arrangement = value ?? new List<ArrangementSection>();
    }
    public MasterChainParameters Master { get; set; } = new();

    /// <summary>The arrangement: clips placed freely in time, independent of scenes (see <see cref="FreeTimeline"/>).</summary>
    [JsonIgnore]
    public List<TimelineClip> Timeline { get; set; } = new();

    /// <summary>The timeline as written to disk: omitted while empty, so scene-only projects stay unchanged.</summary>
    [JsonPropertyName("timeline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TimelineClip>? SerializedTimeline
    {
        get => Timeline.Count > 0 ? Timeline : null;
        set => Timeline = value ?? new List<TimelineClip>();
    }

    /// <summary>The arrangement's loop brace; null plays the whole timeline.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimelineLoopRegion? TimelineLoop { get; set; }

    /// <summary>
    /// Which model touched this project last. Disclosed for transparency and to
    /// give stakeholders someone to blame.
    /// </summary>
    public string? AiProvenance { get; set; }

    /// <summary>Duration of one sixteenth-note step at the current tempo.</summary>
    public double StepDurationSeconds => 60.0 / Bpm / 4.0;

    public SlawProject DeepClone() => ProjectSerializer.Deserialize(ProjectSerializer.Serialize(this));

    public SlawClip? ClipAt(int trackIndex, int sceneIndex) =>
        trackIndex >= 0 && trackIndex < Tracks.Count ? Tracks[trackIndex].ClipAt(sceneIndex) : null;

    /// <summary>
    /// Length of a scene in steps: the longest clip in that row, so shorter clips
    /// loop inside it. An empty scene lasts one default clip.
    /// </summary>
    public int SceneLengthInSteps(int sceneIndex)
    {
        var longestClipLength = Tracks
            .Select(track => track.ClipAt(sceneIndex)?.Pattern.Count ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        return longestClipLength > 0 ? longestClipLength : Steps;
    }
}

internal sealed class SlawTrack
{
    /// <summary>Stable identity, so live playback state survives reordering and AI rewrites.</summary>
    public string Id { get; set; } = NewIdentifier();

    public string Name { get; set; } = "track";
    public string Instrument { get; set; } = "sine";
    public Dictionary<string, double> InstrumentParameters { get; set; } = new();

    /// <summary>
    /// The WAV file a "sampler" track plays. Absolute in memory; written relative
    /// to the project file when the sample lives next to it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sample { get; set; }

    /// <summary>Non-destructive edits, loop and slice points for the sampler's sample.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SampleEditSettings? SampleEditing { get; set; }

    /// <summary>
    /// The SoundFont preset a "soundfont" track plays. Its path is absolute in memory and
    /// written relative to the project file when the font lives next to (or below) it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SoundFontReference? SoundFont { get; set; }

    /// <summary>The VST3 plugin a "vst3" track plays. Its knobs live in <see cref="InstrumentParameters"/> as p&lt;id&gt;.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginReference? Plugin { get; set; }
    public List<EffectDevice> Effects { get; set; } = new();
    public double Gain { get; set; } = 0.8;
    public double Pan { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }

    /// <summary>Protected from AI edits. The autopilot must leave this track exactly as it found it.</summary>
    public bool AiLocked { get; set; }

    /// <summary>One slot per scene; null is an empty slot.</summary>
    public List<SlawClip?> Clips { get; set; } = new();

    /// <summary>Legacy v1 single pattern. Migrated into the first clip on load.</summary>
    [JsonConverter(typeof(CompactPatternJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Pattern { get; set; }

    public SlawClip? ClipAt(int sceneIndex) =>
        sceneIndex >= 0 && sceneIndex < Clips.Count ? Clips[sceneIndex] : null;

    public static string NewIdentifier() => Guid.NewGuid().ToString("N")[..8];
}

internal sealed class SlawClip
{
    public string Name { get; set; } = "clip";

    /// <summary>
    /// One entry per sixteenth-note step: a pitch ("C4"), a chord ("C4+E4+G4"),
    /// a drum trigger ("x"), a sustain of the previous step ("_"), or silence (".").
    /// </summary>
    [JsonConverter(typeof(CompactPatternJsonConverter))]
    public List<string> Pattern { get; set; } = new();

    /// <summary>Clip envelopes by target key (see <see cref="ClipAutomation"/>), each sorted by step. Null when the clip has none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<AutomationPoint>>? Automation { get; set; }

    public static SlawClip Empty(string clipName, int stepCount) => new()
    {
        Name = clipName,
        Pattern = Enumerable.Repeat(NotationResolver.RestSymbol, stepCount).ToList(),
    };
}

/// <summary>
/// Everything the sample editor does to a sampler's source file, stored as data
/// so the original WAV is never touched. Positions are in seconds of the
/// edited sample (the result of applying <see cref="Operations"/> in order).
/// </summary>
internal sealed class SampleEditSettings
{
    public const string PitchedMode = "pitched";
    public const string SliceMode = "slice";

    /// <summary>"pitched": the sample is repitched across the keyboard. "slice": each note plays its own slice.</summary>
    public string PlaybackMode { get; set; } = PitchedMode;

    /// <summary>Applied in order to the source sample; each operation's times refer to the buffer as it was at that point.</summary>
    public List<SampleEditOperation> Operations { get; set; } = new();

    /// <summary>Slice boundaries (excluding 0 and the end), ascending.</summary>
    public List<double> SlicePointsSeconds { get; set; } = new();

    /// <summary>Sustain loop, used while a note is held in Note Length mode.</summary>
    public bool LoopEnabled { get; set; }
    public double LoopStartSeconds { get; set; }
    public double LoopEndSeconds { get; set; }

    public bool IsNeutral => Operations.Count == 0 && SlicePointsSeconds.Count == 0 && !LoopEnabled && PlaybackMode == PitchedMode;
}

/// <summary>One non-destructive edit: trim, delete, silence, reverse, gain, normalize, fadeIn, fadeOut, or duplicate.</summary>
internal sealed class SampleEditOperation
{
    public string Type { get; set; } = "gain";
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }

    /// <summary>Gain in dB for "gain"; target peak in dBFS for "normalize"; unused otherwise.</summary>
    public double Amount { get; set; }
}

/// <summary>An audio effect in a track's device chain.</summary>
internal sealed class EffectDevice
{
    public string Type { get; set; } = "lowpass";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Parameters { get; set; } = new();

    /// <summary>The VST3 plugin a "vst3" effect runs. Its knobs live in <see cref="Parameters"/> as p&lt;id&gt;.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginReference? Plugin { get; set; }

    /// <summary>
    /// The track whose audio keys this effect (a sidechain), by track id; null for none.
    /// The ducker and VST3 effects with a sidechain input listen to it. The source is
    /// tapped after its effects and before its fader, mute and solo, so a muted
    /// "ghost" track can still pump the mix. See <see cref="SidechainRouting"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SidechainSource { get; set; }
}

/// <summary>
/// Which VST3 plugin a device hosts: the bundle, the factory class, a name for
/// humans, and the plugin's own opaque state (base64), captured on save.
/// </summary>
internal sealed class PluginReference
{
    public string Path { get; set; } = string.Empty;

    /// <summary>The factory class id, 32 hex characters.</summary>
    public string ClassId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }

    /// <summary>Same bundle and class: the same plugin, whatever its state.</summary>
    public bool IsSamePlugin(PluginReference? otherReference) =>
        otherReference is not null &&
        string.Equals(Path, otherReference.Path, StringComparison.Ordinal) &&
        string.Equals(ClassId, otherReference.ClassId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which SoundFont preset a track plays: the .sf2 file, a MIDI bank and program, and the preset's name for humans.</summary>
internal sealed class SoundFontReference
{
    public string Path { get; set; } = string.Empty;
    public int Bank { get; set; }
    public int Program { get; set; }
    public string PresetName { get; set; } = string.Empty;

    public SoundFontReference Copy() => new() { Path = Path, Bank = Bank, Program = Program, PresetName = PresetName };

    public string Fingerprint => $"{Path}|{Bank}:{Program}";
}

/// <summary>One entry of the arrangement: play this scene this many times.</summary>
internal sealed class ArrangementSection
{
    public int Scene { get; set; }
    public int Repeats { get; set; } = 1;
}

/// <summary>
/// The master bus. Every parameter is something an LLM can be asked to choose,
/// which is the only reason most of them exist.
/// </summary>
internal sealed class MasterChainParameters
{
    public double Gain { get; set; } = 0.9;

    /// <summary>tanh saturation drive. 1.0 is clean; anything above is "warmth".</summary>
    public double Drive { get; set; } = 1.0;

    public double LowpassHz { get; set; } = 18000;
    public int DelaySteps { get; set; } = 3;
    public double DelayFeedback { get; set; } = 0.3;
    public double DelayMix { get; set; }

    /// <summary>Why the AI mastering engineer chose these settings, verbatim.</summary>
    public string? Rationale { get; set; }

    /// <summary>
    /// The master effect chain: any effects (VST3 included) on the sum of every track,
    /// run before the fixed drive/lowpass/delay/gain stages above.
    /// </summary>
    [JsonIgnore]
    public List<EffectDevice> Effects { get; set; } = new();

    /// <summary><see cref="Effects"/> as written to disk: left out entirely while empty.</summary>
    [JsonPropertyName("effects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<EffectDevice>? SerializedEffects
    {
        get => Effects.Count > 0 ? Effects : null;
        set => Effects = value ?? new List<EffectDevice>();
    }
}
