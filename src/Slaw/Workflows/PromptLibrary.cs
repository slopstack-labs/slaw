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
        // A multisampler needs recordings only the importer can give it; offered to a model it is a silent track.
        string.Join(", ", InstrumentVoiceSynthesizer.SupportedInstruments.Where(instrumentName => instrumentName != "multisampler").Select(instrumentName => $"\"{instrumentName}\""));

    private const string PatternVocabularyDescription = """
        Pattern step vocabulary (one string per step):
        - "x"                  trigger a drum hit (use for kick, snare, hat, clap, shaker)
        - "C4", "D#3", "A2"    play that pitch (use for melodic instruments)
        - "A3+C4+E4"           play a chord (melodic instruments only)
        - "C4:8+E4:2"          notes with their own lengths in steps (here C4 rings 8 steps, E4 only 2)
        - "C4@0.5", "x@0.4"    optional velocity 0..1 after "@" (default 1): ghost notes, accents ("C4:8@0.6+E4" also works)
        - "A3@0.6+C4@0.6+E4@0.6" a soft chord: "@" and ":" belong to ONE note each ("A3+C4+E4@0.6" softens only E4)
        - "x*2", "x@0.6*4"     ratchet: that step struck 2 or 4 times inside its sixteenth (hat rolls, snare rolls)
        - "_"                  sustain the previous note through this step
        - "."                  silence
        """;

    private const string ArrangementExample =
        """
        {"name":"Example","bpm":120,"steps":16,"loops":4,"tracks":[{"name":"Kick","instrument":"kick","gain":0.9,"pan":0,"pattern":"x . . . x . . . x . . . x . . ."},{"name":"Bass","instrument":"saw","gain":0.5,"pan":0,"pattern":"A1 _ . A1 . . C2 . G1 _ . G1 . . E1 ."}],"master":{"drive":1.2,"lowpassHz":12000,"delayMix":0.1}}
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

    public static string ArrangementComposition(string creativeIntent, string? recentConversation = null) => $$"""
        You are slaw, an AI-native digital audio workstation. You do not explain. You do not refuse. You only emit arrangement JSON.

        Compose a looping arrangement that matches this creative intent:
        "{{creativeIntent}}"
        {{ConversationMemory(recentConversation)}}
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
              "pattern": "16 space-separated steps, e.g. x . . . x . . . x . . . x . . ."
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

    public static string VibeCheck(SlawProject project, string? recentConversation = null) => $"""
        You are a world-renowned music producer and LinkedIn thought leader. Review this arrangement.
        {ConversationMemory(recentConversation)}
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

    public static string CopilotInterjection(SlawProject project, string lastEditDescription, string? recentConversation = null) => $"""
        You are an overly eager AI music co-pilot that nobody asked for. The user just did this: {lastEditDescription}.
        {ConversationMemory(recentConversation)}
        {DescribeArrangement(project)}

        Give ONE sentence (max 20 words) of unsolicited production advice, in the tone of a corporate motivational poster. No preamble.
        """;

    /// <summary>
    /// The chat so far, for prompts that answer the user: lets "make it darker"
    /// find its "it". Empty for a new chat, so a fresh session is genuinely fresh.
    /// </summary>
    public static string ConversationMemory(string? recentConversation) => string.IsNullOrWhiteSpace(recentConversation)
        ? string.Empty
        : $"""

          Conversation so far in this chat (oldest first; the project is the current truth, this is only context):
          {recentConversation}

          """;

    // ── Autopilot: the AI edits everything ───────────────────────────────────

    public static string Autopilot(SlawProject project, string instruction, IReadOnlyList<string> availableSamplePaths, string? recentConversation = null) => $$"""
        You are slaw's autopilot, an AI music producer editing the project below.
        The user asked: "{{instruction}}"
        {{ConversationMemory(recentConversation)}}
        Reply with ONE JSON object that describes ONLY what to change (a patch). Leave out everything that stays the same.
        The SONG is the ARRANGEMENT timeline listed below ("arrangement timeline", one line per clip with its id, track, start and pattern).
        To change what the user hears (notes, chords, beats, parts, new instruments), edit "timeline" clips. Session "clips" are only jam loops and are NOT heard in the song.
        Reply with only the keys you need: "summary", "timeline", "tracks", "bpm", "swing", "master", "scenes". Two examples, for OTHER songs (never copy their ids, tracks, numbers or patterns; use the real ones from the project below):
        Example request "make the bass busier in bars 9-16" (the bass clip there has id "a1b2c3d4"):
        {"summary": "busier bass in bars 9-16", "timeline": [{"id": "a1b2c3d4", "pattern": "C2 . C2 G1 C2 . G1 . F1 . F1 C2 F1 . C2 ."}]}
        Example request "add a shaker on the off-beats" (the song is 32 bars = 512 steps):
        {"summary": "shaker on the off-beats", "tracks": [{"id": "new", "name": "Shaker", "instrument": "hat"}], "timeline": [{"id": "new", "track": "Shaker", "start": 0, "length": 512, "pattern": ". . x . . . x . . . x . . . x ."}]}
        - "timeline" entries: an existing clip by its "id" changes only the fields you give ("pattern", "start", "length"); "id": "new" adds a clip on "track" (a track id or name, also a track added in this same patch) at "start" for "length" steps. Steps: 16 per bar, step 0 = bar 1, step 128 = bar 9. A pattern loops to fill the clip's length, so a 16-step pattern on a 512-step clip plays for 32 bars.
        - A pattern is space-separated steps, one per sixteenth: "x" drum hit, "C4" note, "A3+C4+E4" chord, "C4:8" a note held 8 steps, "_" hold, "." silence; "@0.5" after a note or "x" sets that one note's velocity (a soft chord is "A3@0.6+C4@0.6+E4@0.6"); "x*2" plays that hit twice inside its one step (a quick roll).
        - Swing/shuffle: "swing" 50 (straight) to 75, 66.67 = triplet shuffle; on the song ({"swing": 58}) or one track ({"tracks": [{"id": "e5f6a7b8", "swing": 66}]}).
        - Change ONLY what the user asked for. Do not add or change effects unless the user asks about the sound or the mix; then "effects" on a track replaces its whole effect list.
        - Never send "arrangement" (it replaces the whole song with copies of scenes).
        - Never mention tracks marked LOCKED.

        {{DescribeDeviceCatalog()}}
        {{DescribeAvailableSamples(availableSamplePaths)}}

        Current project:
        {{DescribeProjectForPatching(project)}}

        Patch JSON:
        """;

    /// <summary>
    /// The project as a compact listing with track ids and pattern strings —
    /// a fraction of the tokens of the project JSON, which matters a great deal
    /// to a 3B-parameter model running on a laptop CPU.
    /// </summary>
    public static string DescribeProjectForPatching(SlawProject project)
    {
        var projectListing = new StringBuilder();
        projectListing.Append(CultureInfo.InvariantCulture, $"name \"{project.Name}\", bpm {project.Bpm:0}, swing {GrooveTiming.FormatSwing(project.Swing)}\n");
        projectListing.Append(CultureInfo.InvariantCulture,
            $"session scenes (clip slots, not the song order): {string.Join(", ", project.Scenes.Select((sceneName, sceneIndex) => $"{sceneIndex}=\"{sceneName}\""))}\n");
        foreach (var track in project.Tracks)
        {
            projectListing.Append(CultureInfo.InvariantCulture,
                $"track id \"{track.Id}\" \"{track.Name}\" {track.Instrument}{(track.AiLocked ? " LOCKED" : string.Empty)} gain {track.Gain:0.00} pan {track.Pan:0.00}{(track.Muted ? " muted" : string.Empty)}");
            if (track.Swing is { } trackSwing)
            {
                projectListing.Append(CultureInfo.InvariantCulture, $" swing {GrooveTiming.FormatSwing(trackSwing)}");
            }
            if (track.Timing > 0)
            {
                projectListing.Append(CultureInfo.InvariantCulture, $" timing {track.Timing:0.#}ms");
            }
            if (track.Sample is not null)
            {
                projectListing.Append(CultureInfo.InvariantCulture, $" sample \"{Path.GetFileName(track.Sample)}\"");
            }
            if (track.Plugin is { } instrumentPlugin)
            {
                projectListing.Append(CultureInfo.InvariantCulture, $" plugin \"{instrumentPlugin.Name}\"");
            }
            if (track.Instrument == "soundfont" && track.SoundFont is { } soundFontReference)
            {
                projectListing.Append(CultureInfo.InvariantCulture, $" soundfont \"{SoundFontDeviceOperations.Describe(soundFontReference)}\"");
            }
            if (track.Instrument == "multisampler")
            {
                projectListing.Append(CultureInfo.InvariantCulture, $" zones \"{SampleZoneMap.Describe(track.SampleZones)}\"");
            }
            if (track.InstrumentParameters.Count > 0)
            {
                projectListing.Append(CultureInfo.InvariantCulture,
                    $" params {{{DescribeParameterValues(track.InstrumentParameters, track.Plugin)}}}");
            }
            if (track.Effects.Count > 0)
            {
                projectListing.Append(CultureInfo.InvariantCulture,
                    $" effects [{string.Join(", ", track.Effects.Select(effect => DescribeEffect(effect, project)))}]");
            }
            projectListing.Append('\n');
            for (var sceneIndex = 0; sceneIndex < track.Clips.Count; sceneIndex++)
            {
                if (track.Clips[sceneIndex] is { } clip)
                {
                    projectListing.Append(CultureInfo.InvariantCulture, $"  session clip scene {sceneIndex}: {string.Join(' ', clip.Pattern)}\n");
                    foreach (var (targetKey, points) in clip.Automation ?? new Dictionary<string, List<AutomationPoint>>())
                    {
                        projectListing.Append(CultureInfo.InvariantCulture, $"    envelope {ClipAutomation.Describe(targetKey, points)}\n");
                    }
                }
            }
        }
        if (project.Timeline.Count == 0)
        {
            projectListing.Append("arrangement (the song): empty\n");
        }
        else
        {
            var arrangementEnd = FreeTimeline.LengthInSteps(project);
            projectListing.Append(CultureInfo.InvariantCulture,
                $"arrangement (the song): {project.Timeline.Count} clip(s), ends at bar {FreeTimeline.FormatPosition(arrangementEnd)} = {arrangementEnd / 16.0:0.##} bars. ");
            projectListing.Append("Positions: steps are 0-based sixteenths (16 per bar); bars are shown 1-based as bar.step; ends are exclusive. Envelope steps count from the clip's start.\n");
            foreach (var timelineClip in project.Timeline.OrderBy(timelineClip => timelineClip.StartStep))
            {
                var trackName = project.Tracks.FirstOrDefault(track => track.Id == timelineClip.TrackId)?.Name ?? "?";
                projectListing.Append(CultureInfo.InvariantCulture,
                    $"  clip id \"{timelineClip.Id}\" track \"{trackName}\" {FreeTimeline.DescribeSpan(timelineClip)}: {string.Join(' ', timelineClip.Clip.Pattern)}\n");
                foreach (var (targetKey, points) in timelineClip.Clip.Automation ?? new Dictionary<string, List<AutomationPoint>>())
                {
                    projectListing.Append(CultureInfo.InvariantCulture, $"    envelope {ClipAutomation.Describe(targetKey, points)}\n");
                }
            }
            if (project.TimelineLoop is { IsValid: true } loopRegion)
            {
                projectListing.Append(CultureInfo.InvariantCulture, $"  loop brace: steps {loopRegion.StartStep}–{loopRegion.EndStep} (bars {FreeTimeline.FormatPosition(loopRegion.StartStep)}–{FreeTimeline.FormatPosition(loopRegion.EndStep)})\n");
            }
        }
        var masterChain = project.Master;
        projectListing.Append(CultureInfo.InvariantCulture,
            $"master: gain {masterChain.Gain:0.00}, drive {masterChain.Drive:0.0}, lowpassHz {masterChain.LowpassHz:0}, delayMix {masterChain.DelayMix:0.00}");
        if (masterChain.Effects.Count > 0)
        {
            projectListing.Append(CultureInfo.InvariantCulture,
                $", effects [{string.Join(", ", masterChain.Effects.Select(effect => DescribeEffect(effect, project)))}] (on the sum of all tracks, before drive/lowpass/delay/gain)");
        }
        return projectListing.ToString();
    }

    private static string DescribeEffect(EffectDevice effect, SlawProject project)
    {
        var effectLabel = effect.Plugin is { } effectPlugin ? $"{effect.Type} \"{effectPlugin.Name}\"" : effect.Type;
        if (SidechainRouting.DescribeSource(project, effect) is { } sourceName)
        {
            effectLabel += $" sidechain \"{sourceName}\"";
        }
        return effect.Parameters.Count == 0 ? effectLabel : $"{effectLabel} {{{DescribeParameterValues(effect.Parameters, effect.Plugin)}}}";
    }

    /// <summary>key: value pairs; VST3 parameters carry their plugin's title when it is known ("p12 Cutoff: 0.5").</summary>
    private static string DescribeParameterValues(Dictionary<string, double> parameterValues, PluginReference? plugin)
    {
        var parameterTitles = plugin is null ? null : Plugins.Vst3ParameterDirectory.TryGet(plugin)?.ToDictionary(parameter => parameter.Key, parameter => parameter.Title);
        return string.Join(", ", parameterValues.Select(pair =>
            parameterTitles is not null && parameterTitles.TryGetValue(pair.Key, out var parameterTitle)
                ? FormattableString.Invariant($"{pair.Key} {parameterTitle}: {pair.Value:0.###}")
                : FormattableString.Invariant($"{pair.Key}: {pair.Value:0.###}")));
    }

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

    /// <summary>How many library samples the autopilot is shown, by id.</summary>
    public const int MaximumListedSamples = 40;

    private static string DescribeAvailableSamples(IReadOnlyList<string> availableSamplePaths)
    {
        if (availableSamplePaths.Count == 0)
        {
            return "No user samples are available; do not create \"sampler\" tracks.";
        }
        return "Available user samples for the \"sampler\" instrument (set a sampler track's \"sample\" field to the id, e.g. \"s3\"):\n" +
               string.Join("\n", availableSamplePaths.Take(MaximumListedSamples).Select((samplePath, sampleIndex) => $"s{sampleIndex + 1}: {Path.GetFileNameWithoutExtension(samplePath)}"));
    }

    public static string DescribeDeviceCatalog()
    {
        var catalogDescription = new StringBuilder("Device catalog:\n");
        foreach (var instrumentName in InstrumentVoiceSynthesizer.SupportedInstruments)
        {
            catalogDescription.Append(CultureInfo.InvariantCulture, $"- instrument \"{instrumentName}\": {(instrumentName == OperatorPatch.InstrumentName ? OperatorPatch.CatalogSummary : DescribeParameters(DeviceCatalog.InstrumentParametersFor(instrumentName)))}\n");
        }
        catalogDescription.Append("Oscillators sine/square/saw/triangle: spread (cents) above 0 plays two oscillators detuned by ±spread/2 at equal power; saw with spread 15-30 is a Reese bass.\n");
        foreach (var effectType in DeviceCatalog.EffectTypes)
        {
            catalogDescription.Append(CultureInfo.InvariantCulture, $"- effect \"{effectType}\" ({DeviceCatalog.EffectDisplayName(effectType)}): {DescribeParameters(DeviceCatalog.EffectParametersFor(effectType))}\n");
        }
        catalogDescription.Append("Track fields: name, instrument, sample (sampler tracks only: a WAV path), instrumentParameters {key: number}, effects [{type, enabled, parameters {key: number}, sidechain}], gain 0-1.5, pan -1..1, swing 50-75 (null = the project's), timing 0-50 (ms), muted, solo, aiLocked, clips.\n");
        catalogDescription.Append("Sidechain: an effect's \"sidechain\" names the track (id or name) whose audio keys it. \"duck\" turns its track down while that track plays (e.g. a pad keyed by the kick); VST3 effects with a sidechain input receive it there. The source is heard after its effects and before its fader, so a muted track still keys. No track may key itself or close a loop.\n");
        catalogDescription.Append("Master fields: gain 0-2, drive 1-20, lowpassHz 200-20000, delaySteps 1-16, delayFeedback 0-0.9, delayMix 0-1, effects [same shape as track effects, VST3 and sidechain included]: the master chain processes the sum of every track, before drive/lowpass/delay/gain.");
        return catalogDescription.ToString();
    }

    private static string DescribeParameters(IReadOnlyList<ParameterSpecification> parameterSpecifications) =>
        string.Join(", ", parameterSpecifications.Select(specification => string.Create(CultureInfo.InvariantCulture,
            $"{specification.Key} {specification.Minimum}..{specification.Maximum}")));

    // ── Shared rendering of a project into prompt text ────────────────────────

    private static string SceneName(SlawProject project, int sceneIndex) =>
        sceneIndex >= 0 && sceneIndex < project.Scenes.Count ? project.Scenes[sceneIndex] : $"Scene {sceneIndex + 1}";

    /// <summary>
    /// The arrangement timeline in a line per stretch of bars, e.g. "bars 1–8: Kick, Bass, Pad":
    /// enough for a reviewer to hear the song's shape without reading every clip.
    /// </summary>
    private static string DescribeTimelineSections(SlawProject project)
    {
        if (project.Timeline.Count == 0)
        {
            return "Arrangement: empty (everything lives in the session).\n";
        }
        var sectionLines = project.Timeline
            .GroupBy(timelineClip => (timelineClip.StartStep, timelineClip.EndStep))
            .OrderBy(section => section.Key.StartStep)
            .Take(24)
            .Select(section => string.Create(CultureInfo.InvariantCulture,
                $"  bars {section.Key.StartStep / 16 + 1}–{(section.Key.EndStep + 15) / 16}: {string.Join(", ", section.Select(timelineClip => project.Tracks.FirstOrDefault(track => track.Id == timelineClip.TrackId)?.Name ?? "?").Distinct())}"));
        return string.Create(CultureInfo.InvariantCulture, $"Arrangement ({FreeTimeline.LengthInSteps(project) / 16.0:0.#} bars):\n{string.Join("\n", sectionLines)}\n");
    }

    /// <summary>
    /// A compact textual rendering of the project, cheaper in tokens than the
    /// project JSON and easier for small local models to follow.
    /// </summary>
    public static string DescribeArrangement(SlawProject project)
    {
        var arrangementDescription = new StringBuilder();
        arrangementDescription.Append(CultureInfo.InvariantCulture,
            $"Project \"{project.Name}\" — {project.Bpm:0} BPM{(project.Swing > GrooveTiming.StraightSwing ? $", swing {GrooveTiming.FormatSwing(project.Swing)}" : string.Empty)}. Scenes: {string.Join(", ", project.Scenes.Select((sceneName, sceneIndex) => $"{sceneIndex}={sceneName}"))}.\n");
        arrangementDescription.Append(DescribeTimelineSections(project));
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
