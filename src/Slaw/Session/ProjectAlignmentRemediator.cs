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
    public const int MaximumTrackCount = 128;
    public const int MaximumSceneCount = 16;
    public const int MaximumStepCount = 64;
    public const int MaximumEffectsPerTrack = 8;
    public const int MaximumArrangementSections = 64;

    /// <summary>What a model may plausibly call an instrument, and what we will actually play.</summary>
    private static readonly Dictionary<string, string> InstrumentSynonymResolutionTable = new(StringComparer.OrdinalIgnoreCase)
    {
        // Organic names first: the substring fallback walks this table in order, and "marimba"
        // contains "rim", "hand drums" contains "drums", "electric piano" contains "piano".
        ["electric piano"] = "epiano",
        ["e-piano"] = "epiano",
        ["e piano"] = "epiano",
        ["rhodes"] = "epiano",
        ["wurlitzer"] = "epiano",
        ["wurli"] = "epiano",
        ["fm piano"] = "epiano",
        ["hammond"] = "organ",
        ["drawbar"] = "organ",
        ["tonewheel"] = "organ",
        ["fm"] = "operator",
        ["fm synth"] = "operator",
        ["ableton operator"] = "operator",
        ["marimba"] = "mallet",
        ["kalimba"] = "mallet",
        ["mbira"] = "mallet",
        ["vibraphone"] = "mallet",
        ["xylophone"] = "mallet",
        ["glockenspiel"] = "mallet",
        ["celesta"] = "mallet",
        ["steel drum"] = "mallet",
        ["steelpan"] = "mallet",
        ["pan flute"] = "flute",
        ["panflute"] = "flute",
        ["pan pipe"] = "flute",
        ["panpipe"] = "flute",
        ["piccolo"] = "flute",
        ["recorder"] = "flute",
        ["ocarina"] = "flute",
        ["shakuhachi"] = "flute",
        ["nylon"] = "guitar",
        ["acoustic guitar"] = "guitar",
        ["classical guitar"] = "guitar",
        ["ukulele"] = "guitar",
        ["banjo"] = "guitar",
        ["congas"] = "conga",
        ["bongo"] = "conga",
        ["tumba"] = "conga",
        ["quinto"] = "conga",
        ["djembe"] = "conga",
        ["tabla"] = "conga",
        ["hand drum"] = "conga",
        ["hand percussion"] = "conga",
        ["maraca"] = "shaker",
        ["cabasa"] = "shaker",
        ["egg shaker"] = "shaker",
        ["tambourine"] = "shaker",
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
        ["handclap"] = "clap",
        ["claps"] = "clap",
        ["bass"] = "saw",
        ["reese"] = "saw",
        ["sub"] = "sine",
        ["sub bass"] = "sine",
        ["synth"] = "saw",
        ["lead"] = "square",
        ["chiptune"] = "square",
        ["pulse"] = "square",
        ["pad"] = "triangle",
        ["piano"] = "epiano",
        ["keys"] = "epiano",
        ["guitar"] = "guitar",
        ["harp"] = "pluck",
        ["bell"] = "mallet",
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
        ["multisample"] = "multisampler",
        ["multi-sample"] = "multisampler",
        ["sf2"] = "soundfont",
        ["sound font"] = "soundfont",
        ["general midi"] = "soundfont",
        // Short or generic organic names last, so "Bb3 bass" stays a bass and "good vibes pad" a pad.
        ["b3"] = "organ",
        ["vibes"] = "mallet",
        ["percussion"] = "conga",
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
        ["stereo"] = "widen",
        ["widener"] = "widen",
        ["width"] = "widen",
        ["imager"] = "widen",
        ["haas"] = "widen",
        ["duck"] = SidechainRouting.DuckEffectType,
        ["ducker"] = SidechainRouting.DuckEffectType,
        ["ducking"] = SidechainRouting.DuckEffectType,
        ["sidechain"] = SidechainRouting.DuckEffectType,
        ["side-chain"] = SidechainRouting.DuckEffectType,
        ["pump"] = SidechainRouting.DuckEffectType,
        ["pumper"] = SidechainRouting.DuckEffectType,
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
        project.Swing = GrooveTiming.RemediateSwing(project.Swing, "swing", remediationLedger);
        project.Tracks ??= new List<SlawTrack>();
        project.Scenes ??= new List<string>();
        project.Arrangement ??= new List<ArrangementSection>();
        project.Master ??= new MasterChainParameters();

        if (project.Tracks.Count > MaximumTrackCount)
        {
            remediationLedger.Add($"kept the first {MaximumTrackCount} of {project.Tracks.Count} tracks (the limit is {MaximumTrackCount})");
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
        remediationLedger.AddRange(FreeTimeline.Remediate(project));

        var masterChain = project.Master;
        masterChain.Gain = ClampWithLedger(masterChain.Gain, 0, 2, "master.gain", remediationLedger);
        masterChain.Drive = ClampWithLedger(masterChain.Drive, 1, 20, "master.drive", remediationLedger);
        masterChain.LowpassHz = ClampWithLedger(masterChain.LowpassHz, 200, 20000, "master.lowpassHz", remediationLedger);
        masterChain.DelaySteps = (int)ClampWithLedger(masterChain.DelaySteps, 1, 16, "master.delaySteps", remediationLedger);
        masterChain.DelayFeedback = ClampWithLedger(masterChain.DelayFeedback, 0, 0.9, "master.delayFeedback", remediationLedger);
        masterChain.DelayMix = ClampWithLedger(masterChain.DelayMix, 0, 1, "master.delayMix", remediationLedger);
        masterChain.Effects ??= new List<EffectDevice>();
        RemediateEffectChain("master", masterChain.Effects, owningTrack: null, remediationLedger);
        RemediateSidechains(project, remediationLedger);

        return remediationLedger;
    }

    /// <summary>
    /// Squares up one device chain (a track's, or the master's when <paramref name="owningTrack"/>
    /// is null): the effect limit, unknown types, plugin references and parameter ranges.
    /// Removing a track effect also forgets its envelope lanes.
    /// </summary>
    private static void RemediateEffectChain(string ownerName, List<EffectDevice> effectChain, SlawTrack? owningTrack, List<string> remediationLedger)
    {
        effectChain.RemoveAll(effect => effect is null);
        if (effectChain.Count > MaximumEffectsPerTrack)
        {
            remediationLedger.Add($"{ownerName}: kept the first {MaximumEffectsPerTrack} effects (the limit per chain)");
            effectChain.RemoveRange(MaximumEffectsPerTrack, effectChain.Count - MaximumEffectsPerTrack);
        }
        for (var effectIndex = effectChain.Count - 1; effectIndex >= 0; effectIndex--)
        {
            var effectDevice = effectChain[effectIndex];
            var resolvedEffectType = ResolveEffect(effectDevice.Type);
            if (resolvedEffectType is null)
            {
                remediationLedger.Add($"{ownerName}: removed unknown effect \"{effectDevice.Type}\"");
                effectChain.RemoveAt(effectIndex);
                if (owningTrack is not null)
                {
                    ClipAutomation.ForgetEffectSlot(owningTrack, effectIndex);
                }
                continue;
            }
            if (resolvedEffectType != effectDevice.Type)
            {
                remediationLedger.Add($"{ownerName}: effect \"{effectDevice.Type}\" changed to \"{resolvedEffectType}\"");
                effectDevice.Type = resolvedEffectType;
            }
            effectDevice.Parameters ??= new Dictionary<string, double>();
            if (effectDevice.Type == Plugins.DeviceTypes.Vst3)
            {
                if (!IsUsablePluginReference(effectDevice.Plugin))
                {
                    remediationLedger.Add($"{ownerName}: VST3 effect without a plugin removed");
                    effectChain.RemoveAt(effectIndex);
                    if (owningTrack is not null)
                    {
                        ClipAutomation.ForgetEffectSlot(owningTrack, effectIndex);
                    }
                    continue;
                }
                DeviceCatalog.RemediatePluginParameters(effectDevice.Parameters);
                continue;
            }
            effectDevice.Plugin = null;
            DeviceCatalog.Remediate(effectDevice.Parameters, DeviceCatalog.EffectParametersFor(effectDevice.Type));
        }
    }

    /// <summary>
    /// Every sidechain must name a track that exists, not the effect's own track, not
    /// close a loop, and sit on an effect that listens (the ducker or a VST3 effect).
    /// Track effects are checked in project order, so of two keys that would loop the later one goes.
    /// </summary>
    private static void RemediateSidechains(SlawProject project, List<string> remediationLedger)
    {
        var trackIds = project.Tracks.Select(track => track.Id).ToHashSet(StringComparer.Ordinal);
        var acceptedKeys = new SlawProject { Tracks = project.Tracks.Select(track => new SlawTrack { Id = track.Id }).ToList() };
        var acceptedKeysByTrack = acceptedKeys.Tracks.ToDictionary(track => track.Id, StringComparer.Ordinal);
        foreach (var (ownerName, owningTrackId, effectDevice) in project.Tracks
                     .SelectMany(track => track.Effects.Select(effectDevice => (track.Name, (string?)track.Id, effectDevice)))
                     .Concat(project.Master.Effects.Select(effectDevice => ("master", (string?)null, effectDevice))))
        {
            if (effectDevice.SidechainSource is not { } sourceId)
            {
                continue;
            }
            var effectName = DeviceCatalog.EffectDisplayName(effectDevice.Type);
            string? problem = null;
            if (!SidechainRouting.AcceptsSidechain(effectDevice))
            {
                problem = $"{effectName} has no sidechain input";
            }
            else if (!trackIds.Contains(sourceId))
            {
                problem = $"no track has the id \"{sourceId}\"";
            }
            else if (SidechainRouting.WouldCreateCycle(acceptedKeys, owningTrackId, sourceId))
            {
                problem = sourceId == owningTrackId ? "a track cannot key itself" : "it would make a sidechain loop";
            }
            if (problem is not null)
            {
                remediationLedger.Add($"{ownerName}: removed the sidechain of {effectName} ({problem})");
                effectDevice.SidechainSource = null;
                continue;
            }
            if (owningTrackId is not null)
            {
                acceptedKeysByTrack[owningTrackId].Effects.Add(new EffectDevice { Type = effectDevice.Type, SidechainSource = sourceId });
            }
        }
    }

    /// <summary>Maps any instrument name a model might emit onto a supported one.</summary>
    public static string ResolveInstrument(string? requestedInstrumentName) =>
        IsPluginDeviceName(requestedInstrumentName)
            ? Plugins.DeviceTypes.Vst3
            : ResolveFromVocabulary(requestedInstrumentName, InstrumentVoiceSynthesizer.SupportedInstruments, InstrumentSynonymResolutionTable) ?? "sine";

    /// <summary>Maps any effect name a model might emit onto a supported one, or null if hopeless.</summary>
    public static string? ResolveEffect(string? requestedEffectType) =>
        IsPluginDeviceName(requestedEffectType)
            ? Plugins.DeviceTypes.Vst3
            : ResolveFromVocabulary(requestedEffectType, DeviceCatalog.EffectTypes, EffectSynonymResolutionTable);

    private static bool IsPluginDeviceName(string? requestedName) =>
        string.Equals(requestedName?.Trim(), Plugins.DeviceTypes.Vst3, StringComparison.OrdinalIgnoreCase);

    /// <summary>A plugin reference good enough to try loading: a path and a 32-hex-digit class id.</summary>
    private static bool IsUsablePluginReference(PluginReference? pluginReference) =>
        pluginReference is not null && !string.IsNullOrWhiteSpace(pluginReference.Path) && Plugins.Vst3Abi.ParseUid(pluginReference.ClassId) is not null;

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
        if (track.SoundFont is { } soundFontReference)
        {
            soundFontReference.Path ??= string.Empty;
            soundFontReference.PresetName ??= string.Empty;
            soundFontReference.Bank = Math.Clamp(soundFontReference.Bank, 0, 128);
            soundFontReference.Program = Math.Clamp(soundFontReference.Program, 0, 127);
        }
        if (track.Instrument == "soundfont" && string.IsNullOrWhiteSpace(track.SoundFont?.Path))
        {
            remediationLedger.Add($"{track.Name}: soundfont track has no .sf2 file loaded yet");
        }
        if (track.SampleZones is { } sampleZones)
        {
            var unusableZoneCount = sampleZones.RemoveAll(zone => zone is null || string.IsNullOrWhiteSpace(zone.Sample) || zone.KeyLow > zone.KeyHigh || zone.VelocityLow > zone.VelocityHigh);
            if (unusableZoneCount > 0)
            {
                remediationLedger.Add($"{track.Name}: {unusableZoneCount} unusable sample zone(s) discarded");
            }
            foreach (var zone in sampleZones)
            {
                zone.RootNote = Math.Clamp(zone.RootNote, 0, 127);
                zone.KeyLow = Math.Clamp(zone.KeyLow, 0, 127);
                zone.KeyHigh = Math.Clamp(zone.KeyHigh, 0, 127);
                zone.VelocityLow = Math.Clamp(zone.VelocityLow, 1, 127);
                zone.VelocityHigh = Math.Clamp(zone.VelocityHigh, 1, 127);
                zone.Gain = double.IsFinite(zone.Gain) ? Math.Clamp(zone.Gain, 0, 16) : 1;
                zone.TuneCents = double.IsFinite(zone.TuneCents) ? Math.Clamp(zone.TuneCents, -1200, 1200) : 0;
                zone.StartSeconds = double.IsFinite(zone.StartSeconds) ? Math.Max(0, zone.StartSeconds) : 0;
                zone.EndSeconds = double.IsFinite(zone.EndSeconds) ? Math.Max(0, zone.EndSeconds) : 0;
                zone.AttackOverride = zone.AttackOverride is { } attack && double.IsFinite(attack) ? Math.Clamp(attack, 0.5, 2000) : null;
                zone.ReleaseOverride = zone.ReleaseOverride is { } release && double.IsFinite(release) ? Math.Clamp(release, 5, 4000) : null;
            }
        }
        if (track.Instrument == "multisampler" && track.SampleZones is not { Count: > 0 })
        {
            remediationLedger.Add($"{track.Name}: multisampler track has no recordings yet");
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
            remediationLedger.Add($"{track.Name}: unknown instrument \"{track.Instrument}\" changed to \"{resolvedInstrumentName}\"");
            track.Instrument = resolvedInstrumentName;
        }
        if (track.Instrument == Plugins.DeviceTypes.Vst3 && !IsUsablePluginReference(track.Plugin))
        {
            remediationLedger.Add($"{track.Name}: VST3 instrument without a plugin changed to \"saw\"");
            track.Instrument = "saw";
        }
        if (track.Instrument != Plugins.DeviceTypes.Vst3)
        {
            track.Plugin = null;
        }

        track.Gain = ClampWithLedger(track.Gain, 0, 1.5, $"{track.Name}.gain", remediationLedger);
        track.Pan = ClampWithLedger(track.Pan, -1, 1, $"{track.Name}.pan", remediationLedger);
        if (track.Swing is { } trackSwing)
        {
            track.Swing = GrooveTiming.RemediateSwing(trackSwing, $"{track.Name}.swing", remediationLedger);
        }
        track.Timing = ClampWithLedger(track.Timing, 0, GrooveTiming.MaximumTimingMilliseconds, $"{track.Name}.timing", remediationLedger);

        track.InstrumentParameters ??= new Dictionary<string, double>();
        var realignedInstrumentParameters = track.Instrument == Plugins.DeviceTypes.Vst3
            ? DeviceCatalog.RemediatePluginParameters(track.InstrumentParameters)
            : DeviceCatalog.Remediate(track.InstrumentParameters, DeviceCatalog.InstrumentParametersFor(track.Instrument));
        if (realignedInstrumentParameters > 0)
        {
            remediationLedger.Add($"{track.Name}: {realignedInstrumentParameters} instrument parameter(s) clamped to their ranges");
        }

        track.Effects ??= new List<EffectDevice>();
        RemediateEffectChain(track.Name, track.Effects, track, remediationLedger);

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
            remediationLedger.Add($"{track.Name}: {hallucinatedStepCount} unreadable step(s) replaced with rests");
        }
    }

    /// <summary>
    /// The arrangement is the timeline. A legacy scene sequence (older files, models that
    /// still write one, v1 loop counts) is squared up and flattened onto an empty timeline,
    /// then dropped. Nothing invents sections any more: an arrangement left empty on
    /// purpose stays empty.
    /// </summary>
    private static void RemediateArrangement(SlawProject project, int sceneCount, List<string> remediationLedger)
    {
        project.Arrangement.RemoveAll(section => section is null);
        if (project.Arrangement.Count == 0 && project.Loops > 0 && project.Timeline.Count == 0)
        {
            // v1 projects repeated their only pattern Loops times.
            project.Arrangement.Add(new ArrangementSection { Scene = 0, Repeats = project.Loops });
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
        foreach (var section in project.Arrangement)
        {
            section.Repeats = Math.Clamp(section.Repeats, 1, 32);
        }
        FreeTimeline.MigrateLegacyArrangement(project);
    }

    private static double ClampWithLedger(double candidateValue, double lowerBound, double upperBound, string parameterLabel, List<string> remediationLedger)
    {
        if (double.IsNaN(candidateValue))
        {
            remediationLedger.Add($"{parameterLabel}: NaN replaced with {lowerBound}");
            return lowerBound;
        }
        var clampedValue = Math.Clamp(candidateValue, lowerBound, upperBound);
        if (clampedValue != candidateValue)
        {
            remediationLedger.Add($"{parameterLabel}: {candidateValue} clamped to {clampedValue}");
        }
        return clampedValue;
    }
}
