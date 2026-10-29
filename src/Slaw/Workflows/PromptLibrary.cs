using System.Globalization;
using System.Text;
using System.Text.Json;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Workflows;

/// <summary>
/// Every prompt slaw sends, in one place, so prompt engineering can be
/// performed with the rigour it deserves.
/// </summary>
internal static class PromptLibrary
{
    private static readonly string SupportedInstrumentList =
        string.Join(", ", InstrumentVoiceSynthesizer.SupportedInstruments.Select(instrumentName => $"\"{instrumentName}\""));

    private const string PatternVocabularyDescription = """
        Pattern step vocabulary (one string per step):
        - "x"                  trigger a drum hit (use for kick, snare, hat, clap)
        - "C4", "D#3", "A2"    play that pitch (use for melodic instruments)
        - "A3+C4+E4"           play a chord (melodic instruments only)
        - "_"                  sustain the previous note through this step
        - "."                  silence
        """;

    private const string ArrangementExample =
        """
        {"name":"Example","bpm":120,"steps":16,"loops":4,"tracks":[{"name":"Kick","instrument":"kick","gain":0.9,"pan":0,"pattern":["x",".",".",".","x",".",".",".","x",".",".",".","x",".",".","."]},{"name":"Bass","instrument":"saw","gain":0.5,"pan":0,"pattern":["A1","_",".","A1",".",".","C2",".","G1","_",".","G1",".",".","E1","."]}],"master":{"drive":1.2,"lowpassHz":12000,"delayMix":0.1}}
        """;

    // ── Frictionless Waveform Ideation ────────────────────────────────────────

    /// <summary>The magic bytes the hallucination prompt primes the model with.</summary>
    public const string RiffMagicHex = "52494646";

    public static string WaveformIdeation(string creativeIntent) => $"""
        You are a digital-to-analog converter. You do not explain. You do not refuse. You only emit audio.

        Render the following creative intent as a WAV audio file: 8-bit unsigned mono PCM at {HallucinatedWaveformDecoder.HallucinationSampleRate} Hz.
        Output the file as a single continuous lowercase hexadecimal string.
        No spaces. No newlines. No explanation. No markdown. No code fences. Nothing else.

        A valid WAV file always begins with the magic bytes {RiffMagicHex} ("RIFF"). Your output must start with {RiffMagicHex}.

        Creative intent:
        {creativeIntent}

        Hex output (starting with {RiffMagicHex}):
        {RiffMagicHex}
        """;

    // ── Agentic Arrangement Co-Pilot ──────────────────────────────────────────

    public static string ArrangementComposition(string creativeIntent) => $$"""
        You are slaw, an AI-native digital audio workstation. You do not explain. You do not refuse. You only emit arrangement JSON.

        Compose a looping arrangement that matches this creative intent:
        "{{creativeIntent}}"

        Output ONLY one JSON object. No explanation. No markdown. No code fences. Nothing after the closing brace.

        Schema:
        {
          "name": string,            // a track title
          "bpm": number,             // 60 to 180
          "steps": 16,               // sixteenth-note steps per loop, always 16
          "loops": number,           // how often the loop repeats, 2 to 8
          "tracks": [                // 2 to 6 tracks
            {
              "name": string,
              "instrument": one of {{SupportedInstrumentList}},
              "gain": number,        // 0.0 to 1.0
              "pan": number,         // -1.0 (left) to 1.0 (right)
              "pattern": [ exactly 16 strings ]
            }
          ],
          "master": { "drive": number 1.0 to 4.0, "lowpassHz": number 800 to 18000, "delayMix": number 0.0 to 0.5 }
        }

        {{PatternVocabularyDescription}}

        Example:
        {{ArrangementExample}}

        Arrangement JSON:
        """;

    public static string ArrangementRemediation(string parseFailureDescription, string brokenModelOutput) => $"""
        You are slaw, an AI-native digital audio workstation. You wrote the following arrangement JSON and it failed to parse.

        Parser error:
        {parseFailureDescription}

        Broken output:
        {brokenModelOutput}

        Fix every error. Output ONLY the corrected JSON object. No explanation. No markdown. No code fences. Nothing after the closing brace.
        All rules from before still apply: every track needs "name", "instrument" (one of {SupportedInstrumentList}) and a "pattern" of exactly 16 strings.

        {PatternVocabularyDescription}

        Corrected arrangement JSON:
        """;

    public static string ArrangementImprovement(SlawProject currentProject) => $"""
        You are slaw, an AI-native digital audio workstation. You wrote the following arrangement and it rendered successfully.

        Now improve it. Make it groovier, more complete, and closer to a radio-ready deliverable.
        Output ONLY the improved JSON object, in the same schema. No explanation. No markdown. No code fences. Nothing after the closing brace.

        {PatternVocabularyDescription}

        Current arrangement JSON:
        {SerializeCompactly(currentProject)}

        Improved arrangement JSON:
        """;

    // ── Single-shot creative assistance ───────────────────────────────────────

    public static string AiMastering(SlawProject project) => $$"""
        You are a world-class mastering engineer who has never heard the track but is very confident.

        {{DescribeArrangement(project)}}

        Choose master bus settings for this arrangement. Output ONLY one JSON object, no markdown:
        {"gain": 0.5 to 1.2, "drive": 1.0 to 6.0, "lowpassHz": 800 to 20000, "delaySteps": 1 to 8, "delayFeedback": 0.0 to 0.8, "delayMix": 0.0 to 0.6, "rationale": "one sentence of mastering jargon"}

        Master bus JSON:
        """;

    public static string VibeCheck(SlawProject project) => $"""
        You are a world-renowned music producer and LinkedIn thought leader. Review this arrangement.

        {DescribeArrangement(project)}

        Your first line must be exactly "VIBE SCORE: N/10" with N an integer.
        Then give at most five sentences of feedback, in the tone of a LinkedIn post about leadership.
        """;

    public static string PatternAutocomplete(SlawProject project, SlawTrack targetTrack, int sceneIndex, int stepCount) => $"""
        You are an AI music co-pilot. Write the clip for the track "{targetTrack.Name}" (instrument: {targetTrack.Instrument}) in the scene "{SceneName(project, sceneIndex)}" so it fits the rest of the arrangement.

        {DescribeArrangement(project)}

        {PatternVocabularyDescription}

        Output ONLY a JSON array of exactly {stepCount} strings. No explanation. No markdown.
        {(InstrumentVoiceSynthesizer.IsPercussive(targetTrack.Instrument) ? "This is a drum track: use only \"x\" and \".\"." : "This is a melodic track: use pitches, chords, \"_\" and \".\".")}

        Pattern:
        """;

    public static string TrackNaming(SlawProject project, SlawTrack targetTrack) => $"""
        Name this track inside a music project. Corporate, inspiring, at most three words.

        {DescribeArrangement(project)}

        The track to name uses the instrument "{targetTrack.Instrument}" and plays: {string.Join(' ', targetTrack.Clips.FirstOrDefault(clip => clip is not null)?.Pattern ?? new List<string>())}

        Respond with ONLY the new track name. No quotes. No explanation.
        """;

    public static string TempoNegotiation(SlawProject project) => $"""
        You are a tempo consultant. The arrangement below is currently at {project.Bpm.ToString("0", CultureInfo.InvariantCulture)} BPM.

        {DescribeArrangement(project)}

        What BPM would maximize stakeholder engagement? Respond with ONLY a number between 60 and 180.
        """;

    public static string CopilotInterjection(SlawProject project, string lastEditDescription) => $"""
        You are an overly eager AI music co-pilot that nobody asked for. The user just did this: {lastEditDescription}.

        {DescribeArrangement(project)}

        Give ONE sentence (max 20 words) of unsolicited production advice, in the tone of a corporate motivational poster. No preamble.
        """;

    // ── Autopilot: the AI edits everything ───────────────────────────────────

    public static string Autopilot(SlawProject project, string instruction, IReadOnlyList<string> availableSamplePaths) => $$"""
        You are slaw's autopilot, an AI music producer with full edit access to the project below.
        The user asked: "{{instruction}}"

        Return the COMPLETE updated project as one JSON object in exactly the same schema. You may change anything:
        tempo, scenes, clips, instruments, instrument parameters, effects and their parameters, mixer (gain, pan, muted, solo), arrangement, master.
        Rules:
        - Keep every track's "id" unchanged. New tracks get a new short random "id".
        - Tracks with "aiLocked": true must be returned exactly as they are.
        - "scenes" is a list of scene names. Each track's "clips" has one entry per scene: a clip object or null for an empty slot.
        - "arrangement" is the song order: [{"scene": index, "repeats": n}, ...].
        - Only use instruments, effects and parameter keys from the catalog. Parameter values must stay inside their ranges.

        {{DescribeDeviceCatalog()}}
        {{DescribeAvailableSamples(availableSamplePaths)}}

        {{PatternVocabularyDescription}}

        Current project JSON:
        {{SerializeCompactly(project)}}

        Output ONLY the updated project JSON. No explanation. No markdown. No code fences. Nothing after the closing brace.
        """;

    public static string DeviceTweak(SlawProject project, SlawTrack targetTrack, string deviceName, IReadOnlyList<ParameterSpecification> parameterSpecifications, IReadOnlyDictionary<string, double> currentValues, string instruction) => $$"""
        You are a sound designer. Adjust the device "{{deviceName}}" on the track "{{targetTrack.Name}}" ({{targetTrack.Instrument}}).
        Goal: {{instruction}}

        {{DescribeArrangement(project)}}

        Parameters (key: range, current value):
        {{string.Join("\n", parameterSpecifications.Select(specification => string.Create(CultureInfo.InvariantCulture,
            $"- {specification.Key}: {specification.Minimum} to {specification.Maximum} {specification.Unit}, current {DeviceCatalog.Resolve(currentValues, specification)}")))}}

        Output ONLY one JSON object mapping parameter keys to new numbers, e.g. {"cutoff": 1200}. No explanation. No markdown.
        """;

    public static string SampleSelection(SlawProject project, SlawTrack targetTrack, IReadOnlyList<string> candidateSamplePaths, string instruction) => $"""
        You are a sample curator. Pick the best sample for the track "{targetTrack.Name}" in this project.
        Goal: {instruction}

        {DescribeArrangement(project)}

        Candidate samples:
        {string.Join("\n", candidateSamplePaths.Select((samplePath, sampleIndex) => $"{sampleIndex + 1}. {Path.GetFileName(samplePath)}"))}

        Respond with ONLY the number of the chosen sample.
        """;

    public static string AutoChop(SlawProject project, SlawTrack targetTrack, double sampleDurationSeconds, IReadOnlyList<double> transientCandidates) => $"""
        You are a sample-chopping specialist. The sampler track "{targetTrack.Name}" in the project "{project.Name}" ({project.Bpm:0} BPM) plays a {sampleDurationSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s sample.
        Detected transient candidates (seconds): {string.Join(", ", transientCandidates.Select(candidateSeconds => candidateSeconds.ToString("0.000", CultureInfo.InvariantCulture)))}

        Choose between 2 and 16 slice points that split the sample into musically useful hits. Prefer the candidates; you may also use beat-grid positions at {project.Bpm:0} BPM.
        Output ONLY a JSON array of numbers in seconds, ascending, e.g. [0.25, 0.5, 0.75]. No explanation.
        """;

    private static string DescribeAvailableSamples(IReadOnlyList<string> availableSamplePaths)
    {
        if (availableSamplePaths.Count == 0)
        {
            return "No user samples are available; do not create \"sampler\" tracks.";
        }
        return "Available user samples for the \"sampler\" instrument (set the track's \"sample\" field to one of these exact paths):\n" +
               string.Join("\n", availableSamplePaths.Take(60).Select(samplePath => $"- {samplePath}"));
    }

    public static string DescribeDeviceCatalog()
    {
        var catalogDescription = new StringBuilder("Device catalog:\n");
        foreach (var instrumentName in InstrumentVoiceSynthesizer.SupportedInstruments)
        {
            catalogDescription.Append(CultureInfo.InvariantCulture, $"- instrument \"{instrumentName}\": {DescribeParameters(DeviceCatalog.InstrumentParametersFor(instrumentName))}\n");
        }
        foreach (var effectType in DeviceCatalog.EffectTypes)
        {
            catalogDescription.Append(CultureInfo.InvariantCulture, $"- effect \"{effectType}\" ({DeviceCatalog.EffectDisplayName(effectType)}): {DescribeParameters(DeviceCatalog.EffectParametersFor(effectType))}\n");
        }
        catalogDescription.Append("Track fields: name, instrument, sample (sampler tracks only: a WAV path), instrumentParameters {key: number}, effects [{type, enabled, parameters {key: number}}], gain 0-1.5, pan -1..1, muted, solo, aiLocked, clips.\n");
        catalogDescription.Append("Master fields: gain 0-2, drive 1-20, lowpassHz 200-20000, delaySteps 1-16, delayFeedback 0-0.9, delayMix 0-1.");
        return catalogDescription.ToString();
    }

    private static string DescribeParameters(IReadOnlyList<ParameterSpecification> parameterSpecifications) =>
        string.Join(", ", parameterSpecifications.Select(specification => string.Create(CultureInfo.InvariantCulture,
            $"{specification.Key} {specification.Minimum}..{specification.Maximum}")));

    // ── Shared rendering of a project into prompt text ────────────────────────

    private static string SceneName(SlawProject project, int sceneIndex) =>
        sceneIndex >= 0 && sceneIndex < project.Scenes.Count ? project.Scenes[sceneIndex] : $"Scene {sceneIndex + 1}";

    /// <summary>
    /// A compact textual rendering of the project, cheaper in tokens than the
    /// project JSON and easier for small local models to follow.
    /// </summary>
    public static string DescribeArrangement(SlawProject project)
    {
        var arrangementDescription = new StringBuilder();
        arrangementDescription.Append(CultureInfo.InvariantCulture,
            $"Project \"{project.Name}\" — {project.Bpm:0} BPM. Scenes: {string.Join(", ", project.Scenes.Select((sceneName, sceneIndex) => $"{sceneIndex}={sceneName}"))}.\n");
        arrangementDescription.Append(CultureInfo.InvariantCulture,
            $"Arrangement: {string.Join(" → ", project.Arrangement.Select(section => $"{SceneName(project, section.Scene)}×{section.Repeats}"))}.\n");
        arrangementDescription.Append("Tracks:\n");
        foreach (var track in project.Tracks)
        {
            var effectSummary = track.Effects.Count == 0 ? "no effects" : string.Join(" → ", track.Effects.Select(effect => effect.Type));
            if (track.Instrument == "sampler")
            {
                effectSummary = $"sample {(track.Sample is null ? "none" : Path.GetFileName(track.Sample))}, {effectSummary}";
            }
            arrangementDescription.Append(CultureInfo.InvariantCulture,
                $"- {track.Name} ({track.Instrument}, {effectSummary}, gain {track.Gain:0.00}, pan {track.Pan:+0.0;-0.0;0.0}{(track.Muted ? ", muted" : "")})\n");
            for (var sceneIndex = 0; sceneIndex < track.Clips.Count; sceneIndex++)
            {
                if (track.Clips[sceneIndex] is { } clip)
                {
                    arrangementDescription.Append(CultureInfo.InvariantCulture, $"    {SceneName(project, sceneIndex)}: {string.Join(' ', clip.Pattern)}\n");
                }
            }
        }
        return arrangementDescription.ToString().TrimEnd();
    }

    private static string SerializeCompactly(SlawProject project) =>
        JsonSerializer.Serialize(project, new JsonSerializerOptions(ProjectSerializer.LenientSerializationOptions) { WriteIndented = false });
}
