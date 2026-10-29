using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Brings a project — usually one a language model just rewrote — back into
/// alignment with what the engine can actually do. Out-of-range numbers are
/// clamped, hallucinated instruments and effects are mapped onto real ones,
/// clip slots are squared up with the scene list, and v1 single-pattern projects
/// are migrated into the scene/clip model.
/// </summary>
internal static class ProjectAlignmentRemediator
{
    public const int MaximumTrackCount = 16;
    public const int MaximumSceneCount = 16;
    public const int MaximumStepCount = 64;
    public const int MaximumEffectsPerTrack = 8;
    public const int MaximumArrangementSections = 64;

    /// <summary>What a model may plausibly call an instrument, and what we will actually play.</summary>
    private static readonly Dictionary<string, string> InstrumentSynonymResolutionTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bd"] = "kick",
        ["bass drum"] = "kick",
        ["kickdrum"] = "kick",
        ["808"] = "kick",
        ["drums"] = "kick",
        ["sd"] = "snare",
        ["rim"] = "snare",
        ["snaredrum"] = "snare",
        ["hh"] = "hat",
        ["hihat"] = "hat",
        ["hi-hat"] = "hat",
        ["hi hat"] = "hat",
        ["cymbal"] = "hat",
        ["ride"] = "hat",
        ["shaker"] = "hat",
        ["handclap"] = "clap",
        ["claps"] = "clap",
        ["bass"] = "saw",
        ["sub"] = "sine",
        ["sub bass"] = "sine",
        ["synth"] = "saw",
        ["lead"] = "square",
        ["chiptune"] = "square",
        ["pulse"] = "square",
        ["flute"] = "triangle",
        ["pad"] = "triangle",
        ["piano"] = "pluck",
        ["keys"] = "pluck",
        ["guitar"] = "pluck",
        ["harp"] = "pluck",
        ["bell"] = "pluck",
        ["strings"] = "saw",
        ["vocals"] = "triangle",
        ["vocal"] = "triangle",
        ["choir"] = "triangle",
        ["fx"] = "noise",
        ["riser"] = "noise",
        ["ambience"] = "noise",
        ["vinyl"] = "noise",
        ["sample"] = "sampler",
        ["sampled"] = "sampler",
        ["one-shot"] = "sampler",
    };

    private static readonly Dictionary<string, string> EffectSynonymResolutionTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["filter"] = "lowpass",
        ["lpf"] = "lowpass",
        ["low-pass"] = "lowpass",
        ["autofilter"] = "lowpass",
        ["hpf"] = "highpass",
        ["high-pass"] = "highpass",
        ["echo"] = "delay",
        ["pingpong"] = "delay",
        ["hall"] = "reverb",
        ["room"] = "reverb",
        ["space"] = "reverb",
        ["distortion"] = "drive",
        ["saturation"] = "drive",
        ["saturator"] = "drive",
        ["overdrive"] = "drive",
        ["lofi"] = "bitcrush",
        ["lo-fi"] = "bitcrush",
        ["crusher"] = "bitcrush",
        ["redux"] = "bitcrush",
        ["ai"] = "vibe",
        ["enhancer"] = "vibe",
        ["neural"] = "vibe",
    };

    /// <summary>
    /// Remediates <paramref name="project"/> in place and returns a human-readable
    /// list of every misalignment that was corrected along the way.
    /// </summary>
    public static List<string> Remediate(SlawProject project)
    {
        var remediationLedger = new List<string>();

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            project.Name = "untitled slop";
        }
        project.Format = SlawProject.CurrentFormatIdentifier;
        project.Bpm = ClampWithLedger(project.Bpm, 40, 300, "bpm", remediationLedger);
        project.Steps = (int)ClampWithLedger(project.Steps <= 0 ? 16 : project.Steps, 1, MaximumStepCount, "steps", remediationLedger);
        project.Tracks ??= new List<SlawTrack>();
        project.Scenes ??= new List<string>();
        project.Arrangement ??= new List<ArrangementSection>();
        project.Master ??= new MasterChainParameters();

        if (project.Tracks.Count > MaximumTrackCount)
        {
            remediationLedger.Add($"truncated {project.Tracks.Count} tracks to {MaximumTrackCount}");
            project.Tracks.RemoveRange(MaximumTrackCount, project.Tracks.Count - MaximumTrackCount);
        }
        project.Tracks.RemoveAll(track => track is null);

        // v1 → v2: a single pattern per track becomes the first clip of scene one.
        foreach (var track in project.Tracks)
        {
            track.Clips ??= new List<SlawClip?>();
            if (track.Pattern is { Count: > 0 } legacyPattern && track.Clips.All(clip => clip is null))
            {
                track.Clips.Clear();
                track.Clips.Add(new SlawClip { Name = track.Name, Pattern = legacyPattern });
            }
            track.Pattern = null;
        }

        var sceneCount = Math.Clamp(
            Math.Max(project.Scenes.Count, project.Tracks.Select(track => track.Clips.Count).DefaultIfEmpty(0).Max()),
            1, MaximumSceneCount);
        if (project.Scenes.Count > sceneCount)
        {
            project.Scenes.RemoveRange(sceneCount, project.Scenes.Count - sceneCount);
        }
        for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
        {
            if (sceneIndex >= project.Scenes.Count)
            {
                project.Scenes.Add($"Scene {sceneIndex + 1}");
            }
            else if (string.IsNullOrWhiteSpace(project.Scenes[sceneIndex]))
            {
                project.Scenes[sceneIndex] = $"Scene {sceneIndex + 1}";
            }
        }

        var claimedTrackIdentifiers = new HashSet<string>();
        foreach (var track in project.Tracks)
        {
            if (string.IsNullOrWhiteSpace(track.Id) || !claimedTrackIdentifiers.Add(track.Id))
            {
                track.Id = SlawTrack.NewIdentifier();
                claimedTrackIdentifiers.Add(track.Id);
            }
            RemediateTrack(track, sceneCount, remediationLedger);
        }

        RemediateArrangement(project, sceneCount, remediationLedger);

        var masterChain = project.Master;
        masterChain.Gain = ClampWithLedger(masterChain.Gain, 0, 2, "master.gain", remediationLedger);
        masterChain.Drive = ClampWithLedger(masterChain.Drive, 1, 20, "master.drive", remediationLedger);
        masterChain.LowpassHz = ClampWithLedger(masterChain.LowpassHz, 200, 20000, "master.lowpassHz", remediationLedger);
        masterChain.DelaySteps = (int)ClampWithLedger(masterChain.DelaySteps, 1, 16, "master.delaySteps", remediationLedger);
        masterChain.DelayFeedback = ClampWithLedger(masterChain.DelayFeedback, 0, 0.9, "master.delayFeedback", remediationLedger);
        masterChain.DelayMix = ClampWithLedger(masterChain.DelayMix, 0, 1, "master.delayMix", remediationLedger);

        return remediationLedger;
    }

    /// <summary>Maps any instrument name a model might emit onto a supported one.</summary>
    public static string ResolveInstrument(string? requestedInstrumentName) =>
        ResolveFromVocabulary(requestedInstrumentName, InstrumentVoiceSynthesizer.SupportedInstruments, InstrumentSynonymResolutionTable) ?? "sine";

    /// <summary>Maps any effect name a model might emit onto a supported one, or null if hopeless.</summary>
    public static string? ResolveEffect(string? requestedEffectType) =>
        ResolveFromVocabulary(requestedEffectType, DeviceCatalog.EffectTypes, EffectSynonymResolutionTable);

    private static string? ResolveFromVocabulary(string? requestedName, IReadOnlyList<string> supportedNames, Dictionary<string, string> synonymTable)
    {
        var normalizedName = requestedName?.Trim().ToLowerInvariant() ?? string.Empty;
        if (supportedNames.Contains(normalizedName))
        {
            return normalizedName;
        }
        if (synonymTable.TryGetValue(normalizedName, out var resolvedName))
        {
            return resolvedName;
        }
        // Substring fallback catches "Deep House Kick", "warm analog bass", "tape echo", etc.
        foreach (var (synonymCandidate, synonymTarget) in synonymTable)
        {
            if (normalizedName.Contains(synonymCandidate, StringComparison.OrdinalIgnoreCase))
            {
                return synonymTarget;
            }
        }
        return supportedNames.FirstOrDefault(supportedName => normalizedName.Contains(supportedName, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemediateTrack(SlawTrack track, int sceneCount, List<string> remediationLedger)
    {
        if (string.IsNullOrWhiteSpace(track.Name))
        {
            track.Name = "unnamed track";
        }

        if (track.Instrument == "sampler" && string.IsNullOrWhiteSpace(track.Sample))
        {
            remediationLedger.Add($"{track.Name}: sampler has no sample loaded yet");
        }
        if (track.SampleEditing is { } sampleEditing)
        {
            sampleEditing.Operations ??= new List<SampleEditOperation>();
            sampleEditing.SlicePointsSeconds ??= new List<double>();
            var hallucinatedOperationCount = sampleEditing.Operations.RemoveAll(operation =>
                operation is null || !SampleEditProcessor.OperationTypes.Contains(operation.Type) ||
                double.IsNaN(operation.StartSeconds) || double.IsNaN(operation.EndSeconds));
            if (hallucinatedOperationCount > 0)
            {
                remediationLedger.Add($"{track.Name}: {hallucinatedOperationCount} unknown sample edit(s) discarded");
            }
            if (sampleEditing.PlaybackMode is not (SampleEditSettings.PitchedMode or SampleEditSettings.SliceMode))
            {
                sampleEditing.PlaybackMode = SampleEditSettings.PitchedMode;
            }
            sampleEditing.SlicePointsSeconds = sampleEditing.SlicePointsSeconds
                .Where(sliceSeconds => sliceSeconds > 0 && !double.IsNaN(sliceSeconds))
                .Distinct()
                .Order()
                .Take(128)
                .ToList();
            sampleEditing.LoopStartSeconds = Math.Max(0, sampleEditing.LoopStartSeconds);
            sampleEditing.LoopEndSeconds = Math.Max(sampleEditing.LoopStartSeconds, sampleEditing.LoopEndSeconds);
        }

        var resolvedInstrumentName = ResolveInstrument(track.Instrument);
        if (!string.Equals(resolvedInstrumentName, track.Instrument, StringComparison.Ordinal))
        {
            remediationLedger.Add($"{track.Name}: instrument \"{track.Instrument}\" realigned to \"{resolvedInstrumentName}\"");
            track.Instrument = resolvedInstrumentName;
        }

        track.Gain = ClampWithLedger(track.Gain, 0, 1.5, $"{track.Name}.gain", remediationLedger);
        track.Pan = ClampWithLedger(track.Pan, -1, 1, $"{track.Name}.pan", remediationLedger);

        track.InstrumentParameters ??= new Dictionary<string, double>();
        var realignedInstrumentParameters = DeviceCatalog.Remediate(track.InstrumentParameters, DeviceCatalog.InstrumentParametersFor(track.Instrument));
        if (realignedInstrumentParameters > 0)
        {
            remediationLedger.Add($"{track.Name}: {realignedInstrumentParameters} instrument parameter(s) realigned");
        }

        track.Effects ??= new List<EffectDevice>();
        track.Effects.RemoveAll(effect => effect is null);
        if (track.Effects.Count > MaximumEffectsPerTrack)
        {
            remediationLedger.Add($"{track.Name}: device chain truncated to {MaximumEffectsPerTrack} effects");
            track.Effects.RemoveRange(MaximumEffectsPerTrack, track.Effects.Count - MaximumEffectsPerTrack);
        }
        for (var effectIndex = track.Effects.Count - 1; effectIndex >= 0; effectIndex--)
        {
            var effectDevice = track.Effects[effectIndex];
            var resolvedEffectType = ResolveEffect(effectDevice.Type);
            if (resolvedEffectType is null)
            {
                remediationLedger.Add($"{track.Name}: hallucinated effect \"{effectDevice.Type}\" removed");
                track.Effects.RemoveAt(effectIndex);
                continue;
            }
            if (resolvedEffectType != effectDevice.Type)
            {
                remediationLedger.Add($"{track.Name}: effect \"{effectDevice.Type}\" realigned to \"{resolvedEffectType}\"");
                effectDevice.Type = resolvedEffectType;
            }
            effectDevice.Parameters ??= new Dictionary<string, double>();
            DeviceCatalog.Remediate(effectDevice.Parameters, DeviceCatalog.EffectParametersFor(effectDevice.Type));
        }

        if (track.Clips.Count > sceneCount)
        {
            track.Clips.RemoveRange(sceneCount, track.Clips.Count - sceneCount);
        }
        while (track.Clips.Count < sceneCount)
        {
            track.Clips.Add(null);
        }

        var percussiveInstrument = InstrumentVoiceSynthesizer.IsPercussive(track.Instrument);
        var hallucinatedStepCount = 0;
        for (var sceneIndex = 0; sceneIndex < track.Clips.Count; sceneIndex++)
        {
            if (track.Clips[sceneIndex] is not { } clip)
            {
                continue;
            }
            clip.Pattern ??= new List<string>();
            if (clip.Pattern.Count == 0)
            {
                track.Clips[sceneIndex] = null;
                continue;
            }
            if (string.IsNullOrWhiteSpace(clip.Name))
            {
                clip.Name = track.Name;
            }
            if (clip.Pattern.Count > MaximumStepCount)
            {
                remediationLedger.Add($"{track.Name}: clip \"{clip.Name}\" truncated to {MaximumStepCount} steps");
                clip.Pattern.RemoveRange(MaximumStepCount, clip.Pattern.Count - MaximumStepCount);
            }
            if (clip.Automation is not null)
            {
                // Envelopes on targets that do not exist are offboarded; out-of-range ones are clamped, not argued with.
                clip.Automation = clip.Automation
                    .Where(envelope => envelope.Value is { Count: > 0 })
                    .Select(envelope => (envelope.Key, Target: ClipAutomation.ResolveTarget(track, envelope.Key), Points: envelope.Value))
                    .Where(envelope => envelope.Target is not null)
                    .ToDictionary(envelope => envelope.Key, envelope => ClipAutomation.Tidy(envelope.Points, clip.Pattern.Count, envelope.Target!.Specification));
                if (clip.Automation.Count == 0)
                {
                    clip.Automation = null;
                }
            }
            for (var stepIndex = 0; stepIndex < clip.Pattern.Count; stepIndex++)
            {
                if (NotationResolver.TryResolve(clip.Pattern[stepIndex]) is null)
                {
                    hallucinatedStepCount++;
                }
                clip.Pattern[stepIndex] = NotationResolver.Canonicalize(clip.Pattern[stepIndex], percussiveInstrument);
            }
        }
        if (hallucinatedStepCount > 0)
        {
            remediationLedger.Add($"{track.Name}: {hallucinatedStepCount} unparseable step(s) silenced");
        }
    }

    private static void RemediateArrangement(SlawProject project, int sceneCount, List<string> remediationLedger)
    {
        project.Arrangement.RemoveAll(section => section is null);
        if (project.Arrangement.Count == 0)
        {
            // v1 projects repeated their only pattern Loops times; everything else
            // plays each scene once, top to bottom.
            if (project.Loops > 0 && sceneCount == 1)
            {
                project.Arrangement.Add(new ArrangementSection { Scene = 0, Repeats = project.Loops });
            }
            else
            {
                for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
                {
                    project.Arrangement.Add(new ArrangementSection { Scene = sceneIndex, Repeats = 2 });
                }
            }
        }
        project.Loops = 0;

        var invalidSectionCount = project.Arrangement.RemoveAll(section => section.Scene < 0 || section.Scene >= sceneCount);
        if (invalidSectionCount > 0)
        {
            remediationLedger.Add($"arrangement: {invalidSectionCount} section(s) referencing nonexistent scenes removed");
        }
        if (project.Arrangement.Count > MaximumArrangementSections)
        {
            project.Arrangement.RemoveRange(MaximumArrangementSections, project.Arrangement.Count - MaximumArrangementSections);
        }
        if (project.Arrangement.Count == 0)
        {
            project.Arrangement.Add(new ArrangementSection { Scene = 0, Repeats = 1 });
        }
        foreach (var section in project.Arrangement)
        {
            section.Repeats = Math.Clamp(section.Repeats, 1, 32);
        }
    }

    private static double ClampWithLedger(double candidateValue, double lowerBound, double upperBound, string parameterLabel, List<string> remediationLedger)
    {
        if (double.IsNaN(candidateValue))
        {
            remediationLedger.Add($"{parameterLabel}: NaN realigned to {lowerBound}");
            return lowerBound;
        }
        var clampedValue = Math.Clamp(candidateValue, lowerBound, upperBound);
        if (clampedValue != candidateValue)
        {
            remediationLedger.Add($"{parameterLabel}: {candidateValue} realigned to {clampedValue}");
        }
        return clampedValue;
    }
}
