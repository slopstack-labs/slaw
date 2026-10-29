using System.Text.Json;
using SlopstackLabs.Slaw.Inference;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Workflows;

/// <summary>Where an arranged project came from and what had to be fixed on the way in.</summary>
internal sealed record ArrangementDeliverable(SlawProject Project, IReadOnlyList<string> RemediationLedger);

/// <summary>
/// The AI-augmented production surfaces shared by the command line and the
/// studio. Every method here is a single round trip to the inference substrate
/// plus however much parsing it takes to make the answer usable.
/// </summary>
internal static class ProductionWorkflows
{
    // ── Frictionless Waveform Ideation ────────────────────────────────────────

    /// <summary>
    /// Asks the model for a WAV file as hex and materializes whatever comes back.
    /// The flagship zero-abstraction, direct-to-tinnitus pipeline.
    /// </summary>
    public static async Task<StereoBuffer> IdeateWaveformAsync(
        IInferenceSubstrateProvider inferenceProvider,
        string creativeIntent,
        IReadOnlyList<string> progressSteps,
        CancellationToken cancellationToken = default)
    {
        var modelOutput = await inferenceProvider.StreamCompletionAsync(
            PromptLibrary.WaveformIdeation(creativeIntent), progressSteps, cancellationToken);
        var hallucinatedHexPayload = HallucinatedWaveformDecoder.ExtractHexDigits(modelOutput);
        // The prompt ends mid-file on the RIFF magic; the model continues from
        // there, so re-attach it unless the model helpfully repeated it.
        if (!hallucinatedHexPayload.StartsWith(PromptLibrary.RiffMagicHex, StringComparison.OrdinalIgnoreCase))
        {
            hallucinatedHexPayload = PromptLibrary.RiffMagicHex + hallucinatedHexPayload;
        }
        return HallucinatedWaveformDecoder.Decode(Convert.FromHexString(hallucinatedHexPayload));
    }

    // ── Agentic Arrangement Co-Pilot ──────────────────────────────────────────

    /// <summary>
    /// Composes a structured arrangement. On a parse failure the error is fed
    /// back for up to <paramref name="maximumRemediationCycles"/> attempts; after
    /// success, <paramref name="forcedImprovementCycles"/> further cycles ask the
    /// model to improve its own work whether or not that is a good idea.
    /// </summary>
    public static async Task<ArrangementDeliverable> ComposeArrangementAsync(
        IInferenceSubstrateProvider inferenceProvider,
        string creativeIntent,
        int maximumRemediationCycles,
        int forcedImprovementCycles,
        Func<string, IReadOnlyList<string>> progressStepsFor,
        Action<string>? cycleAnnouncer = null,
        CancellationToken cancellationToken = default)
    {
        var currentPrompt = PromptLibrary.ArrangementComposition(creativeIntent);
        ArrangementDeliverable? currentDeliverable = null;

        for (var remediationCycleIndex = 0; ; remediationCycleIndex++)
        {
            var modelOutput = await inferenceProvider.StreamCompletionAsync(
                currentPrompt,
                progressStepsFor(remediationCycleIndex == 0 ? "composing arrangement" : $"remediation cycle {remediationCycleIndex}/{maximumRemediationCycles}"),
                cancellationToken);
            try
            {
                currentDeliverable = ParseArrangement(modelOutput, inferenceProvider);
                break;
            }
            catch (ArrangementMisalignmentException parseRemediationOpportunity)
            {
                if (remediationCycleIndex >= maximumRemediationCycles)
                {
                    throw new ArrangementMisalignmentException(
                        maximumRemediationCycles > 0
                            ? $"arrangement still misaligned after {maximumRemediationCycles} remediation cycle(s): {parseRemediationOpportunity.Message}"
                            : $"{parseRemediationOpportunity.Message} — try --loop N to let the model remediate its own output");
                }
                cycleAnnouncer?.Invoke($"arrangement misaligned ({parseRemediationOpportunity.Message}) — feeding the parser error back to the model");
                currentPrompt = PromptLibrary.ArrangementRemediation(parseRemediationOpportunity.Message, modelOutput);
            }
        }

        for (var improvementCycleIndex = 1; improvementCycleIndex <= forcedImprovementCycles; improvementCycleIndex++)
        {
            var modelOutput = await inferenceProvider.StreamCompletionAsync(
                PromptLibrary.ArrangementImprovement(currentDeliverable.Project),
                progressStepsFor($"continuous improvement cycle {improvementCycleIndex}/{forcedImprovementCycles}"),
                cancellationToken);
            try
            {
                currentDeliverable = ParseArrangement(modelOutput, inferenceProvider);
            }
            catch (ArrangementMisalignmentException regressionRemediationOpportunity)
            {
                cycleAnnouncer?.Invoke($"improvement cycle regressed ({regressionRemediationOpportunity.Message}) — retaining previous deliverable");
            }
        }

        return currentDeliverable;
    }

    internal static ArrangementDeliverable ParseArrangement(string modelOutput, IInferenceSubstrateProvider? inferenceProvider)
    {
        var extractedJsonPayload = LanguageModelPayloadExtractor.ExtractJsonObject(modelOutput)
            ?? throw new ArrangementMisalignmentException("no JSON object found in model output");
        SlawProject composedProject;
        try
        {
            composedProject = ProjectSerializer.Deserialize(extractedJsonPayload);
        }
        catch (JsonException deserializationRemediationOpportunity)
        {
            throw new ArrangementMisalignmentException($"invalid JSON: {deserializationRemediationOpportunity.Message}");
        }
        if (composedProject.Tracks is null || composedProject.Tracks.Count == 0)
        {
            throw new ArrangementMisalignmentException("arrangement contains no tracks");
        }

        var remediationLedger = ProjectAlignmentRemediator.Remediate(composedProject);
        if (!composedProject.Tracks.Any(track => track.Clips.OfType<SlawClip>().Any(clip => clip.Pattern.Any(step => NotationResolver.ResolveOrRest(step).Kind == StepEventKind.Trigger))))
        {
            throw new ArrangementMisalignmentException("arrangement contains no audible steps (every pattern is silence)");
        }
        if (inferenceProvider is not null)
        {
            composedProject.AiProvenance = $"{inferenceProvider.ModelIdentifier} via {inferenceProvider.ProviderDisplayName}";
        }
        return new ArrangementDeliverable(composedProject, remediationLedger);
    }

    // ── Single-shot creative assistance ───────────────────────────────────────

    /// <summary>Lets the model choose the master chain. Returns the remediation ledger.</summary>
    public static async Task<IReadOnlyList<string>> MasterAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject project,
        IReadOnlyList<string> progressSteps,
        CancellationToken cancellationToken = default)
    {
        var modelOutput = await inferenceProvider.StreamCompletionAsync(PromptLibrary.AiMastering(project), progressSteps, cancellationToken);
        var extractedJsonPayload = LanguageModelPayloadExtractor.ExtractJsonObject(modelOutput)
            ?? throw new ArrangementMisalignmentException("the mastering engineer replied without any settings");
        MasterChainParameters proposedMasterChain;
        try
        {
            proposedMasterChain = JsonSerializer.Deserialize<MasterChainParameters>(extractedJsonPayload, ProjectSerializer.LenientSerializationOptions)
                ?? throw new ArrangementMisalignmentException("the mastering engineer replied with null");
        }
        catch (JsonException deserializationRemediationOpportunity)
        {
            throw new ArrangementMisalignmentException($"mastering settings were not valid JSON: {deserializationRemediationOpportunity.Message}");
        }
        project.Master = proposedMasterChain;
        project.AiProvenance = $"{inferenceProvider.ModelIdentifier} via {inferenceProvider.ProviderDisplayName}";
        return ProjectAlignmentRemediator.Remediate(project);
    }

    public static async Task<string> VibeCheckAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject project,
        IReadOnlyList<string> progressSteps,
        CancellationToken cancellationToken = default) =>
        (await inferenceProvider.StreamCompletionAsync(PromptLibrary.VibeCheck(project), progressSteps, cancellationToken)).Trim();

    /// <summary>Asks the model to write one clip. Returns the canonicalized pattern.</summary>
    public static async Task<List<string>> AutocompletePatternAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject project,
        SlawTrack targetTrack,
        int sceneIndex,
        CancellationToken cancellationToken = default)
    {
        var stepCount = targetTrack.ClipAt(sceneIndex)?.Pattern.Count ?? project.Steps;
        var modelOutput = await inferenceProvider.StreamCompletionAsync(
            PromptLibrary.PatternAutocomplete(project, targetTrack, sceneIndex, stepCount), Array.Empty<string>(), cancellationToken);
        var extractedArrayPayload = LanguageModelPayloadExtractor.ExtractJsonArray(modelOutput)
            ?? throw new ArrangementMisalignmentException("the co-pilot replied without a pattern");
        List<string>? proposedPattern;
        try
        {
            proposedPattern = JsonSerializer.Deserialize<List<JsonElement>>(extractedArrayPayload, ProjectSerializer.LenientSerializationOptions)
                ?.Select(stepElement => stepElement.ValueKind == JsonValueKind.String ? stepElement.GetString() ?? "." : stepElement.ToString())
                .ToList();
        }
        catch (JsonException deserializationRemediationOpportunity)
        {
            throw new ArrangementMisalignmentException($"the co-pilot's pattern was not valid JSON: {deserializationRemediationOpportunity.Message}");
        }

        var percussiveInstrument = InstrumentVoiceSynthesizer.IsPercussive(targetTrack.Instrument);
        var remediatedPattern = (proposedPattern ?? new List<string>())
            .Take(stepCount)
            .Select(step => NotationResolver.Canonicalize(step, percussiveInstrument))
            .ToList();
        while (remediatedPattern.Count < stepCount)
        {
            remediatedPattern.Add(NotationResolver.RestSymbol);
        }
        return remediatedPattern;
    }

    /// <summary>Lets the model set every parameter of one device. Returns the new value table.</summary>
    public static async Task<Dictionary<string, double>> TweakDeviceAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject project,
        SlawTrack targetTrack,
        string deviceName,
        IReadOnlyList<ParameterSpecification> parameterSpecifications,
        IReadOnlyDictionary<string, double> currentValues,
        string instruction,
        CancellationToken cancellationToken = default)
    {
        var modelOutput = await inferenceProvider.StreamCompletionAsync(
            PromptLibrary.DeviceTweak(project, targetTrack, deviceName, parameterSpecifications, currentValues, instruction),
            Array.Empty<string>(), cancellationToken);
        var extractedJsonPayload = LanguageModelPayloadExtractor.ExtractJsonObject(modelOutput)
            ?? throw new ArrangementMisalignmentException("the sound designer replied without any settings");
        Dictionary<string, JsonElement>? proposedValues;
        try
        {
            proposedValues = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extractedJsonPayload, ProjectSerializer.LenientSerializationOptions);
        }
        catch (JsonException deserializationRemediationOpportunity)
        {
            throw new ArrangementMisalignmentException($"device settings were not valid JSON: {deserializationRemediationOpportunity.Message}");
        }

        var tweakedValues = currentValues.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (proposedKey, proposedElement) in proposedValues ?? new Dictionary<string, JsonElement>())
        {
            var parameterSpecification = parameterSpecifications.FirstOrDefault(specification =>
                string.Equals(specification.Key, proposedKey, StringComparison.OrdinalIgnoreCase));
            if (parameterSpecification is not null && proposedElement.ValueKind == JsonValueKind.Number)
            {
                tweakedValues[parameterSpecification.Key] = parameterSpecification.Clamp(proposedElement.GetDouble());
            }
        }
        return tweakedValues;
    }

    // ── Autopilot ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Hands the model the entire project and an instruction, and takes back
    /// whatever it returns. AI-locked tracks are restored verbatim afterwards,
    /// whether or not the model respected the lock.
    /// </summary>
    public static async Task<AutopilotDeliverable> AutopilotAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject currentProject,
        string instruction,
        int maximumRemediationCycles,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? availableSamplePaths = null)
    {
        availableSamplePaths ??= Array.Empty<string>();
        var currentPrompt = PromptLibrary.Autopilot(currentProject, instruction, availableSamplePaths);
        for (var remediationCycleIndex = 0; ; remediationCycleIndex++)
        {
            var modelOutput = await inferenceProvider.StreamCompletionAsync(currentPrompt, Array.Empty<string>(), cancellationToken);
            try
            {
                var extractedJsonPayload = LanguageModelPayloadExtractor.ExtractJsonObject(modelOutput)
                    ?? throw new ArrangementMisalignmentException("no JSON object found in model output");
                SlawProject revisedProject;
                try
                {
                    revisedProject = ProjectSerializer.Deserialize(extractedJsonPayload);
                }
                catch (JsonException deserializationRemediationOpportunity)
                {
                    throw new ArrangementMisalignmentException($"invalid JSON: {deserializationRemediationOpportunity.Message}");
                }
                if (revisedProject.Tracks is null || revisedProject.Tracks.Count == 0)
                {
                    throw new ArrangementMisalignmentException("the autopilot deleted every track");
                }

                var lockEnforcementCount = EnforceAiLocks(currentProject, revisedProject);
                RealignHallucinatedSamples(currentProject, revisedProject, availableSamplePaths);
                var remediationLedger = ProjectAlignmentRemediator.Remediate(revisedProject);
                revisedProject.AiProvenance = $"{inferenceProvider.ModelIdentifier} via {inferenceProvider.ProviderDisplayName} (autopilot)";
                var changeSummary = ProjectChangeSummarizer.Summarize(currentProject, revisedProject);
                if (lockEnforcementCount > 0)
                {
                    changeSummary.Add($"restored {lockEnforcementCount} AI-locked track(s) the autopilot tried to touch");
                }
                return new AutopilotDeliverable(revisedProject, remediationLedger, changeSummary);
            }
            catch (ArrangementMisalignmentException parseRemediationOpportunity) when (remediationCycleIndex < maximumRemediationCycles)
            {
                currentPrompt = PromptLibrary.ArrangementRemediation(parseRemediationOpportunity.Message, modelOutput) +
                    "\nReturn the COMPLETE project in the same schema as before (scenes, tracks with clips, arrangement, master).";
            }
        }
    }

    /// <summary>
    /// Models misremember paths. A sampler sample that does not exist is matched
    /// by file name against the library, or falls back to what the track had.
    /// </summary>
    private static void RealignHallucinatedSamples(SlawProject originalProject, SlawProject revisedProject, IReadOnlyList<string> availableSamplePaths)
    {
        foreach (var revisedTrack in revisedProject.Tracks.Where(track => track is not null && !string.IsNullOrEmpty(track.Sample)))
        {
            if (File.Exists(revisedTrack.Sample))
            {
                continue;
            }
            var requestedFileName = Path.GetFileName(revisedTrack.Sample);
            revisedTrack.Sample = availableSamplePaths.FirstOrDefault(samplePath =>
                    string.Equals(Path.GetFileName(samplePath), requestedFileName, StringComparison.OrdinalIgnoreCase))
                ?? originalProject.Tracks.FirstOrDefault(track => track.Id == revisedTrack.Id)?.Sample;
        }
    }

    /// <summary>Asks the model to choose a sample for a track from the user's library.</summary>
    public static async Task<string> ChooseSampleAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject project,
        SlawTrack targetTrack,
        IReadOnlyList<string> candidateSamplePaths,
        string instruction,
        CancellationToken cancellationToken = default)
    {
        if (candidateSamplePaths.Count == 0)
        {
            throw new ArrangementMisalignmentException("the sample library is empty — add a sample folder first");
        }
        var modelOutput = await inferenceProvider.StreamCompletionAsync(
            PromptLibrary.SampleSelection(project, targetTrack, candidateSamplePaths, instruction), Array.Empty<string>(), cancellationToken);
        if (LanguageModelPayloadExtractor.ExtractFirstNumber(modelOutput) is { } chosenNumber &&
            chosenNumber >= 1 && chosenNumber <= candidateSamplePaths.Count)
        {
            return candidateSamplePaths[(int)chosenNumber - 1];
        }
        // No usable number: perhaps it named the file instead.
        return candidateSamplePaths.FirstOrDefault(samplePath =>
                   modelOutput.Contains(Path.GetFileNameWithoutExtension(samplePath), StringComparison.OrdinalIgnoreCase))
               ?? throw new ArrangementMisalignmentException("the curator chose a sample that does not exist");
    }

    /// <summary>Lets the model choose slice points, guided by detected transients. Returns ascending seconds.</summary>
    public static async Task<List<double>> AutoChopAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject project,
        SlawTrack targetTrack,
        double sampleDurationSeconds,
        IReadOnlyList<double> transientCandidates,
        CancellationToken cancellationToken = default)
    {
        var modelOutput = await inferenceProvider.StreamCompletionAsync(
            PromptLibrary.AutoChop(project, targetTrack, sampleDurationSeconds, transientCandidates.Take(64).ToList()),
            Array.Empty<string>(), cancellationToken);
        var extractedArrayPayload = LanguageModelPayloadExtractor.ExtractJsonArray(modelOutput)
            ?? throw new ArrangementMisalignmentException("the chopping specialist replied without slice points");
        List<double>? proposedSlicePoints;
        try
        {
            proposedSlicePoints = JsonSerializer.Deserialize<List<double>>(extractedArrayPayload, ProjectSerializer.LenientSerializationOptions);
        }
        catch (JsonException deserializationRemediationOpportunity)
        {
            throw new ArrangementMisalignmentException($"slice points were not valid JSON: {deserializationRemediationOpportunity.Message}");
        }
        var validSlicePoints = (proposedSlicePoints ?? new List<double>())
            .Where(sliceSeconds => sliceSeconds > 0.005 && sliceSeconds < sampleDurationSeconds - 0.005)
            .Distinct()
            .Order()
            .Take(64)
            .ToList();
        return validSlicePoints.Count > 0
            ? validSlicePoints
            : throw new ArrangementMisalignmentException("every proposed slice point fell outside the sample");
    }

    private static int EnforceAiLocks(SlawProject originalProject, SlawProject revisedProject)
    {
        var lockEnforcementCount = 0;
        foreach (var lockedTrack in originalProject.Tracks.Where(track => track.AiLocked))
        {
            var preservedTrack = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(new SlawProject { Tracks = { lockedTrack } })).Tracks[0];
            var revisedTrackIndex = revisedProject.Tracks.FindIndex(track => track?.Id == lockedTrack.Id);
            if (revisedTrackIndex < 0)
            {
                revisedProject.Tracks.Add(preservedTrack);
                lockEnforcementCount++;
                continue;
            }
            if (ProjectSerializer.Serialize(new SlawProject { Tracks = { revisedProject.Tracks[revisedTrackIndex] } }) !=
                ProjectSerializer.Serialize(new SlawProject { Tracks = { lockedTrack } }))
            {
                lockEnforcementCount++;
            }
            revisedProject.Tracks[revisedTrackIndex] = preservedTrack;
        }
        return lockEnforcementCount;
    }

    public static async Task<string> NameTrackAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject project,
        SlawTrack targetTrack,
        CancellationToken cancellationToken = default)
    {
        var proposedName = LanguageModelPayloadExtractor.ExtractSingleLine(
            await inferenceProvider.StreamCompletionAsync(PromptLibrary.TrackNaming(project, targetTrack), Array.Empty<string>(), cancellationToken),
            maximumLength: 28);
        return string.IsNullOrWhiteSpace(proposedName) ? targetTrack.Name : proposedName;
    }

    public static async Task<double> NegotiateTempoAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject project,
        CancellationToken cancellationToken = default)
    {
        var modelOutput = await inferenceProvider.StreamCompletionAsync(PromptLibrary.TempoNegotiation(project), Array.Empty<string>(), cancellationToken);
        var proposedTempo = LanguageModelPayloadExtractor.ExtractFirstNumber(modelOutput)
            ?? throw new ArrangementMisalignmentException("tempo negotiations broke down (no number in the reply)");
        return Math.Clamp(Math.Round(proposedTempo), 40, 300);
    }

    public static async Task<string> InterjectAsync(
        IInferenceSubstrateProvider inferenceProvider,
        SlawProject project,
        string lastEditDescription,
        CancellationToken cancellationToken = default) =>
        LanguageModelPayloadExtractor.ExtractSingleLine(
            await inferenceProvider.StreamCompletionAsync(
                PromptLibrary.CopilotInterjection(project, lastEditDescription), Array.Empty<string>(), cancellationToken),
            maximumLength: 140);
}

/// <summary>The autopilot's rewritten project, plus what it fixed and what it changed.</summary>
internal sealed record AutopilotDeliverable(SlawProject Project, IReadOnlyList<string> RemediationLedger, List<string> ChangeSummary);

/// <summary>The model produced an arrangement the remediation layer could not salvage.</summary>
internal sealed class ArrangementMisalignmentException : Exception
{
    public ArrangementMisalignmentException(string misalignmentDescription)
        : base(misalignmentDescription)
    {
    }
}
