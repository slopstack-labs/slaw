using System.Collections.Concurrent;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

internal enum TransportMode
{
    Stopped,
    Session,
    Arrangement,
}

/// <summary>What one track is doing right now, for the UI to draw.</summary>
internal readonly record struct TrackTransportState(int? PlayingScene, int? ClipStep, int? QueuedScene);

/// <summary>An immutable picture of the transport, republished after every step.</summary>
internal sealed record TransportSnapshot(
    TransportMode Mode,
    long ArrangementStep,
    int? ArrangementSection,
    IReadOnlyDictionary<string, TrackTransportState> Tracks,
    double PreviewProgress)
{
    public static readonly TransportSnapshot Idle = new(
        TransportMode.Stopped, -1, null, new Dictionary<string, TrackTransportState>(), -1);
}

/// <summary>
/// The sample-accurate sequencer behind both live playback and offline bounces.
/// The UI publishes immutable project snapshots whenever anything changes; the
/// engine reads the latest one at every step boundary and every block, so edits —
/// notes, knobs, tempo, mutes, or an AI rewriting the entire song — are heard
/// on the next step without stopping playback.
///
/// All transport commands are queued and applied on the audio thread, so the
/// engine itself never needs a lock.
/// </summary>
internal sealed class SequencerEngine
{
    private const int LaunchQuantizationSteps = 16;
    private const int MaximumVoicesPerTrack = 48;
    private const int MaximumCachedVoices = 768;
    private const int MaximumBlockFrames = 8192;

    private readonly int sampleRate;
    private readonly ConcurrentQueue<Action> pendingTransportCommands = new();
    private readonly Dictionary<string, TrackRuntimeState> trackRuntimeStates = new();
    private readonly Dictionary<string, float[]> renderedVoiceCache = new();
    private readonly Random noiseEntropySource;
    private readonly Random humanizationEntropySource;
    private readonly MasterBusProcessor masterBusProcessor;
    private readonly float[] trackScratchBuffer = new float[MaximumBlockFrames];

    private SlawProject? publishedProject;
    private TransportSnapshot publishedTransport = TransportSnapshot.Idle;

    private TransportMode transportMode = TransportMode.Stopped;
    private double samplesUntilNextStep;
    private long sessionStep = -1;
    private long arrangementStep = -1;
    private int? arrangementSection;
    private bool arrangementLoops = true;

    private float[]? previewLeft;
    private float[]? previewRight;
    private int previewPosition;

    private readonly Random auditionEntropySource = new(0xA0D1);
    private AuditionVoice? activeAudition;

    public SequencerEngine(int sampleRate, int noiseSeed = 0x51A7)
    {
        this.sampleRate = sampleRate;
        noiseEntropySource = new Random(noiseSeed);
        humanizationEntropySource = new Random(noiseSeed ^ 0x2A);
        masterBusProcessor = new MasterBusProcessor(sampleRate);
    }

    public int SampleRate => sampleRate;

    /// <summary>The latest transport state. Safe to read from any thread.</summary>
    public TransportSnapshot Transport => Volatile.Read(ref publishedTransport);

    /// <summary>True once a non-looping arrangement has played its final step.</summary>
    public bool ArrangementFinished { get; private set; }

    // ── Commands (any thread) ─────────────────────────────────────────────────

    /// <summary>
    /// Hands the engine a new project snapshot. The caller must not mutate it
    /// afterwards; publish a fresh clone for every change.
    /// </summary>
    public void PublishProject(SlawProject projectSnapshot) => Volatile.Write(ref publishedProject, projectSnapshot);

    public void PlayArrangement(long startStep = 0, bool loop = true) => pendingTransportCommands.Enqueue(() =>
    {
        ReleaseAllSessionClips();
        transportMode = TransportMode.Arrangement;
        arrangementStep = startStep - 1;
        arrangementLoops = loop;
        ArrangementFinished = false;
        samplesUntilNextStep = 0;
    });

    /// <summary>Launches every clip in a scene (tracks with an empty slot stop).</summary>
    public void LaunchScene(int sceneIndex) => pendingTransportCommands.Enqueue(() =>
    {
        var project = Volatile.Read(ref publishedProject);
        if (project is null)
        {
            return;
        }
        foreach (var track in project.Tracks)
        {
            QueueClipLaunch(track.Id, track.ClipAt(sceneIndex) is null ? null : sceneIndex);
        }
    });

    public void LaunchClip(string trackIdentifier, int sceneIndex) =>
        pendingTransportCommands.Enqueue(() => QueueClipLaunch(trackIdentifier, sceneIndex));

    public void StopTrack(string trackIdentifier) =>
        pendingTransportCommands.Enqueue(() => QueueClipLaunch(trackIdentifier, null));

    public void Stop() => pendingTransportCommands.Enqueue(() =>
    {
        transportMode = TransportMode.Stopped;
        ReleaseAllSessionClips();
        arrangementSection = null;
        PublishTransport();
    });

    /// <summary>Plays a finished buffer (e.g. a hallucinated waveform) over the mix, resampled to the engine rate.</summary>
    public void PreviewBuffer(StereoBuffer previewBuffer)
    {
        var resampledLength = (int)((long)previewBuffer.SampleFrameCount * sampleRate / previewBuffer.SampleRate);
        var resampledLeft = new float[resampledLength];
        var resampledRight = new float[resampledLength];
        for (var frameIndex = 0; frameIndex < resampledLength; frameIndex++)
        {
            var sourcePosition = (double)frameIndex * previewBuffer.SampleRate / sampleRate;
            var lowerFrame = Math.Min((int)sourcePosition, previewBuffer.SampleFrameCount - 1);
            var upperFrame = Math.Min(lowerFrame + 1, previewBuffer.SampleFrameCount - 1);
            var interpolationWeight = (float)(sourcePosition - lowerFrame);
            resampledLeft[frameIndex] = previewBuffer.Left[lowerFrame] * (1 - interpolationWeight) + previewBuffer.Left[upperFrame] * interpolationWeight;
            resampledRight[frameIndex] = previewBuffer.Right[lowerFrame] * (1 - interpolationWeight) + previewBuffer.Right[upperFrame] * interpolationWeight;
        }
        pendingTransportCommands.Enqueue(() =>
        {
            previewLeft = resampledLeft;
            previewRight = resampledRight;
            previewPosition = 0;
        });
    }

    public void StopPreview() => pendingTransportCommands.Enqueue(() => previewLeft = previewRight = null);

    // ── Note audition ─────────────────────────────────────────────────────────

    private const double AuditionMaximumHoldSeconds = 6;

    /// <summary>
    /// Starts sounding <paramref name="midiNote"/> on a track's instrument, through
    /// its device chain, until <see cref="StopNoteAudition"/>. The voice is rendered
    /// on the calling thread so the audio thread never waits for synthesis.
    /// Call from one thread only (the UI thread).
    /// </summary>
    public void StartNoteAudition(string trackIdentifier, int midiNote)
    {
        var project = Volatile.Read(ref publishedProject);
        var auditionedTrack = project?.Tracks.FirstOrDefault(track => track.Id == trackIdentifier);
        if (auditionedTrack is null)
        {
            return;
        }
        var percussiveInstrument = InstrumentVoiceSynthesizer.IsPercussive(auditionedTrack.Instrument);
        SampleData? samplerSource = null;
        if (auditionedTrack.Instrument == "sampler" && !string.IsNullOrEmpty(auditionedTrack.Sample))
        {
            try
            {
                samplerSource = SampleLibrary.LoadResampled(auditionedTrack.Sample, sampleRate);
            }
            catch (Exception sampleRemediationOpportunity) when (sampleRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return;
            }
        }
        var renderedVoice = InstrumentVoiceSynthesizer.RenderVoice(
            auditionedTrack.Instrument,
            new[] { midiNote },
            AuditionMaximumHoldSeconds,
            sampleRate,
            auditionEntropySource,
            auditionedTrack.InstrumentParameters,
            samplerSource);
        if (renderedVoice.Length == 0)
        {
            return;
        }
        // Melodic voices fade on release over the instrument's own release time;
        // drums and plucks are allowed to ring out, as nature intended.
        var releaseSeconds = percussiveInstrument || auditionedTrack.Instrument == "pluck" ||
                             (auditionedTrack.Instrument == "sampler" && DeviceCatalog.Resolve(auditionedTrack.InstrumentParameters, DeviceCatalog.InstrumentParametersFor("sampler"), "hold") < 0.5)
            ? 0
            : DeviceCatalog.Resolve(auditionedTrack.InstrumentParameters, DeviceCatalog.InstrumentParametersFor(auditionedTrack.Instrument), "release") / 1000.0;
        var auditionVoice = new AuditionVoice(trackIdentifier, renderedVoice, Math.Max(1, (int)(releaseSeconds * sampleRate)), releaseSeconds > 0);
        pendingTransportCommands.Enqueue(() =>
        {
            activeAudition?.BeginRelease();
            activeAudition = auditionVoice;
        });
    }

    public void StopNoteAudition() => pendingTransportCommands.Enqueue(() => activeAudition?.BeginRelease());

    // ── Rendering (audio thread) ──────────────────────────────────────────────

    /// <summary>Renders the next block of audio. Both spans must have the same length.</summary>
    public void RenderBlock(Span<float> leftChannel, Span<float> rightChannel)
    {
        if (leftChannel.Length > MaximumBlockFrames)
        {
            RenderBlock(leftChannel[..MaximumBlockFrames], rightChannel[..MaximumBlockFrames]);
            RenderBlock(leftChannel[MaximumBlockFrames..], rightChannel[MaximumBlockFrames..]);
            return;
        }

        while (pendingTransportCommands.TryDequeue(out var transportCommand))
        {
            transportCommand();
        }

        leftChannel.Clear();
        rightChannel.Clear();
        var project = Volatile.Read(ref publishedProject);
        if (project is null)
        {
            return;
        }

        var renderedFrameCount = 0;
        while (renderedFrameCount < leftChannel.Length)
        {
            if (transportMode != TransportMode.Stopped && samplesUntilNextStep <= 0)
            {
                AdvanceStep(project);
                samplesUntilNextStep += project.StepDurationSeconds * sampleRate;
            }
            var chunkFrameCount = leftChannel.Length - renderedFrameCount;
            if (transportMode != TransportMode.Stopped)
            {
                chunkFrameCount = Math.Clamp((int)Math.Ceiling(samplesUntilNextStep), 1, chunkFrameCount);
            }
            MixTracks(project,
                leftChannel.Slice(renderedFrameCount, chunkFrameCount),
                rightChannel.Slice(renderedFrameCount, chunkFrameCount));
            samplesUntilNextStep -= chunkFrameCount;
            renderedFrameCount += chunkFrameCount;
        }

        masterBusProcessor.Process(leftChannel, rightChannel, project.Master, project.StepDurationSeconds);
        MixPreview(leftChannel, rightChannel);
        masterBusProcessor.ProtectHearing(leftChannel, rightChannel);
    }

    private void MixPreview(Span<float> leftChannel, Span<float> rightChannel)
    {
        if (previewLeft is null || previewRight is null)
        {
            return;
        }
        var mixableFrameCount = Math.Min(leftChannel.Length, previewLeft.Length - previewPosition);
        for (var frameIndex = 0; frameIndex < mixableFrameCount; frameIndex++)
        {
            leftChannel[frameIndex] += previewLeft[previewPosition + frameIndex];
            rightChannel[frameIndex] += previewRight[previewPosition + frameIndex];
        }
        previewPosition += mixableFrameCount;
        if (previewPosition >= previewLeft.Length)
        {
            previewLeft = previewRight = null;
        }
        PublishTransport();
    }

    // ── Sequencing ────────────────────────────────────────────────────────────

    private void QueueClipLaunch(string trackIdentifier, int? sceneIndex)
    {
        var runtimeState = RuntimeStateFor(trackIdentifier);
        if (transportMode != TransportMode.Session)
        {
            // Launching from a stopped transport (or out of the arrangement)
            // starts the session clock immediately.
            if (transportMode == TransportMode.Arrangement)
            {
                ReleaseAllSessionClips();
            }
            transportMode = TransportMode.Session;
            sessionStep = -1;
            samplesUntilNextStep = 0;
            runtimeState.PlayingScene = sceneIndex;
            runtimeState.ClipOriginStep = 0;
            runtimeState.QueuedScene = null;
            runtimeState.HasQueuedLaunch = false;
        }
        else
        {
            runtimeState.QueuedScene = sceneIndex;
            runtimeState.HasQueuedLaunch = true;
        }
        PublishTransport();
    }

    private void ReleaseAllSessionClips()
    {
        foreach (var runtimeState in trackRuntimeStates.Values)
        {
            runtimeState.PlayingScene = null;
            runtimeState.QueuedScene = null;
            runtimeState.HasQueuedLaunch = false;
            runtimeState.CurrentClipStep = null;
        }
    }

    private void AdvanceStep(SlawProject project)
    {
        if (transportMode == TransportMode.Session)
        {
            sessionStep++;
            var launchBoundaryReached = sessionStep % LaunchQuantizationSteps == 0;
            foreach (var track in project.Tracks)
            {
                var runtimeState = RuntimeStateFor(track.Id);
                if (runtimeState.HasQueuedLaunch && launchBoundaryReached)
                {
                    runtimeState.PlayingScene = runtimeState.QueuedScene;
                    runtimeState.ClipOriginStep = sessionStep;
                    runtimeState.HasQueuedLaunch = false;
                    runtimeState.QueuedScene = null;
                }
                runtimeState.CurrentClipStep = null;
                if (runtimeState.PlayingScene is not { } playingScene || track.ClipAt(playingScene) is not { Pattern.Count: > 0 } playingClip)
                {
                    continue;
                }
                var clipStep = (int)((sessionStep - runtimeState.ClipOriginStep) % playingClip.Pattern.Count);
                runtimeState.CurrentClipStep = clipStep;
                TriggerStep(project, track, runtimeState, playingClip, clipStep);
            }
        }
        else if (transportMode == TransportMode.Arrangement)
        {
            arrangementStep++;
            var arrangementLength = project.ArrangementLengthInSteps();
            if (arrangementLength == 0 || arrangementStep >= arrangementLength)
            {
                if (arrangementLength > 0 && arrangementLoops)
                {
                    arrangementStep = 0;
                }
                else
                {
                    transportMode = TransportMode.Stopped;
                    ArrangementFinished = true;
                    arrangementSection = null;
                    ReleaseAllSessionClips();
                    PublishTransport();
                    return;
                }
            }

            var (sectionIndex, stepInSection) = LocateArrangementStep(project, arrangementStep);
            arrangementSection = sectionIndex;
            var sceneIndex = project.Arrangement[sectionIndex].Scene;
            foreach (var track in project.Tracks)
            {
                var runtimeState = RuntimeStateFor(track.Id);
                runtimeState.PlayingScene = sceneIndex;
                runtimeState.CurrentClipStep = null;
                if (track.ClipAt(sceneIndex) is not { Pattern.Count: > 0 } sectionClip)
                {
                    continue;
                }
                var clipStep = (int)(stepInSection % sectionClip.Pattern.Count);
                runtimeState.CurrentClipStep = clipStep;
                TriggerStep(project, track, runtimeState, sectionClip, clipStep);
            }
        }
        PublishTransport();
    }

    internal static (int SectionIndex, long StepInSection) LocateArrangementStep(SlawProject project, long arrangementStep)
    {
        var sectionStartStep = 0L;
        for (var sectionIndex = 0; sectionIndex < project.Arrangement.Count; sectionIndex++)
        {
            var section = project.Arrangement[sectionIndex];
            var sectionLength = (long)project.SceneLengthInSteps(section.Scene) * Math.Max(1, section.Repeats);
            if (arrangementStep < sectionStartStep + sectionLength)
            {
                return (sectionIndex, arrangementStep - sectionStartStep);
            }
            sectionStartStep += sectionLength;
        }
        return (Math.Max(0, project.Arrangement.Count - 1), 0);
    }

    private void TriggerStep(SlawProject project, SlawTrack track, TrackRuntimeState runtimeState, SlawClip clip, int clipStep)
    {
        var stepEvent = NotationResolver.ResolveOrRest(clip.Pattern[clipStep]);
        if (stepEvent.Kind != StepEventKind.Trigger)
        {
            return;
        }

        var percussiveInstrument = InstrumentVoiceSynthesizer.IsPercussive(track.Instrument);
        var heldStepCount = 1;
        while (!percussiveInstrument &&
               clipStep + heldStepCount < clip.Pattern.Count &&
               NotationResolver.ResolveOrRest(clip.Pattern[clipStep + heldStepCount]).Kind == StepEventKind.Sustain)
        {
            heldStepCount++;
        }

        var renderedVoice = RenderOrRecallVoice(project, track, stepEvent, heldStepCount);
        if (renderedVoice.Length == 0)
        {
            return;
        }
        var humanizeAmount = DeviceCatalog.Resolve(
            track.InstrumentParameters, DeviceCatalog.InstrumentParametersFor(track.Instrument), "humanize");
        var humanizedGain = (float)(1 - humanizeAmount * 0.35 * humanizationEntropySource.NextDouble());

        if (runtimeState.ActiveVoices.Count >= MaximumVoicesPerTrack)
        {
            runtimeState.ActiveVoices.RemoveAt(0);
        }
        runtimeState.ActiveVoices.Add(new ActiveVoice(renderedVoice, humanizedGain));
    }

    private float[] RenderOrRecallVoice(SlawProject project, SlawTrack track, StepEvent stepEvent, int heldStepCount)
    {
        var parameterFingerprint = string.Join(',', track.InstrumentParameters.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value:R}"));
        var voiceCacheKey = $"{track.Instrument}|{track.Sample}|{string.Join('+', stepEvent.MidiNotes)}|{heldStepCount}|{project.Bpm:R}|{parameterFingerprint}";
        if (renderedVoiceCache.TryGetValue(voiceCacheKey, out var cachedVoice))
        {
            return cachedVoice;
        }
        SampleData? samplerSource = null;
        if (track.Instrument == "sampler")
        {
            // Never touch the disk from the audio thread: a sample that is not
            // decoded yet is requested in the background and skipped this time.
            samplerSource = string.IsNullOrEmpty(track.Sample) ? null : SampleLibrary.TryGetResampled(track.Sample, sampleRate);
            if (samplerSource is null)
            {
                return Array.Empty<float>();
            }
        }
        if (renderedVoiceCache.Count >= MaximumCachedVoices)
        {
            renderedVoiceCache.Clear();
        }
        var renderedVoice = InstrumentVoiceSynthesizer.RenderVoice(
            track.Instrument,
            stepEvent.MidiNotes,
            heldStepCount * project.StepDurationSeconds * 0.92,
            sampleRate,
            noiseEntropySource,
            track.InstrumentParameters,
            samplerSource);
        renderedVoiceCache[voiceCacheKey] = renderedVoice;
        return renderedVoice;
    }

    // ── Mixing ────────────────────────────────────────────────────────────────

    private void MixTracks(SlawProject project, Span<float> leftChannel, Span<float> rightChannel)
    {
        var soloEngaged = project.Tracks.Any(track => track.Solo);
        var processingContext = new EffectProcessingContext(sampleRate, project.StepDurationSeconds);
        var trackBlock = trackScratchBuffer.AsSpan(0, leftChannel.Length);

        foreach (var track in project.Tracks)
        {
            var runtimeState = RuntimeStateFor(track.Id);
            var trackAudible = !track.Muted && (!soloEngaged || track.Solo);
            trackBlock.Clear();
            for (var voiceIndex = runtimeState.ActiveVoices.Count - 1; voiceIndex >= 0; voiceIndex--)
            {
                var activeVoice = runtimeState.ActiveVoices[voiceIndex];
                var mixableFrameCount = Math.Min(trackBlock.Length, activeVoice.Samples.Length - activeVoice.Position);
                // Muted voices still advance, so unmuting lands mid-note rather than restarting it.
                for (var frameIndex = 0; trackAudible && frameIndex < mixableFrameCount; frameIndex++)
                {
                    trackBlock[frameIndex] += activeVoice.Samples[activeVoice.Position + frameIndex] * activeVoice.Gain;
                }
                activeVoice.Position += mixableFrameCount;
                if (activeVoice.Position >= activeVoice.Samples.Length)
                {
                    runtimeState.ActiveVoices.RemoveAt(voiceIndex);
                }
            }

            // An auditioned note is heard even on a muted track: you asked for it explicitly.
            var auditionOnThisTrack = activeAudition is { } audition && audition.TrackIdentifier == track.Id;
            if (auditionOnThisTrack && activeAudition!.MixInto(trackBlock))
            {
                activeAudition = null;
            }

            runtimeState.SynchronizeEffectChain(track.Effects, sampleRate);
            for (var effectIndex = 0; effectIndex < track.Effects.Count; effectIndex++)
            {
                if (track.Effects[effectIndex].Enabled)
                {
                    runtimeState.EffectChain[effectIndex].Process(trackBlock, track.Effects[effectIndex].Parameters, processingContext);
                }
            }

            if (!trackAudible && !auditionOnThisTrack)
            {
                continue;
            }
            var panAngle = (Math.Clamp(track.Pan, -1, 1) + 1) * Math.PI / 4;
            var leftGain = (float)(track.Gain * Math.Cos(panAngle));
            var rightGain = (float)(track.Gain * Math.Sin(panAngle));
            for (var frameIndex = 0; frameIndex < trackBlock.Length; frameIndex++)
            {
                leftChannel[frameIndex] += trackBlock[frameIndex] * leftGain;
                rightChannel[frameIndex] += trackBlock[frameIndex] * rightGain;
            }
        }

        // Forget tracks that no longer exist, along with their reverb tails.
        if (trackRuntimeStates.Count > project.Tracks.Count)
        {
            var survivingIdentifiers = project.Tracks.Select(track => track.Id).ToHashSet();
            foreach (var orphanedIdentifier in trackRuntimeStates.Keys.Where(identifier => !survivingIdentifiers.Contains(identifier)).ToList())
            {
                trackRuntimeStates.Remove(orphanedIdentifier);
            }
        }
    }

    private TrackRuntimeState RuntimeStateFor(string trackIdentifier)
    {
        if (!trackRuntimeStates.TryGetValue(trackIdentifier, out var runtimeState))
        {
            runtimeState = new TrackRuntimeState();
            trackRuntimeStates[trackIdentifier] = runtimeState;
        }
        return runtimeState;
    }

    private void PublishTransport()
    {
        var trackStates = trackRuntimeStates.ToDictionary(
            pair => pair.Key,
            pair => new TrackTransportState(pair.Value.PlayingScene, pair.Value.CurrentClipStep, pair.Value.HasQueuedLaunch ? pair.Value.QueuedScene ?? -1 : null));
        var previewProgress = previewLeft is null ? -1 : (double)previewPosition / previewLeft.Length;
        Volatile.Write(ref publishedTransport, new TransportSnapshot(transportMode, arrangementStep, arrangementSection, trackStates, previewProgress));
    }

    // ── Runtime state ─────────────────────────────────────────────────────────

    private sealed class ActiveVoice
    {
        public ActiveVoice(float[] samples, float gain)
        {
            Samples = samples;
            Gain = gain;
        }

        public float[] Samples { get; }
        public float Gain { get; }
        public int Position { get; set; }
    }

    /// <summary>A held preview note: plays until released, then fades over its release time.</summary>
    private sealed class AuditionVoice
    {
        private readonly float[] samples;
        private readonly int releaseFrameCount;
        private readonly bool fadesOnRelease;
        private int position;
        private int releaseFramesRemaining = -1;

        public AuditionVoice(string trackIdentifier, float[] samples, int releaseFrameCount, bool fadesOnRelease)
        {
            TrackIdentifier = trackIdentifier;
            this.samples = samples;
            this.releaseFrameCount = releaseFrameCount;
            this.fadesOnRelease = fadesOnRelease;
        }

        public string TrackIdentifier { get; }

        public void BeginRelease()
        {
            if (fadesOnRelease && releaseFramesRemaining < 0)
            {
                releaseFramesRemaining = releaseFrameCount;
            }
        }

        /// <summary>Adds the next block of the voice. Returns true once the voice has finished.</summary>
        public bool MixInto(Span<float> trackBlock)
        {
            for (var frameIndex = 0; frameIndex < trackBlock.Length; frameIndex++)
            {
                if (position >= samples.Length || releaseFramesRemaining == 0)
                {
                    return true;
                }
                var releaseGain = releaseFramesRemaining > 0 ? (float)releaseFramesRemaining / releaseFrameCount : 1f;
                trackBlock[frameIndex] += samples[position++] * releaseGain;
                if (releaseFramesRemaining > 0)
                {
                    releaseFramesRemaining--;
                }
            }
            return false;
        }
    }

    private sealed class TrackRuntimeState
    {
        public List<ActiveVoice> ActiveVoices { get; } = new();
        public List<EffectProcessor> EffectChain { get; } = new();
        public int? PlayingScene { get; set; }
        public int? QueuedScene { get; set; }
        public bool HasQueuedLaunch { get; set; }
        public long ClipOriginStep { get; set; }
        public int? CurrentClipStep { get; set; }

        /// <summary>
        /// Keeps one processor per effect slot. A slot is only rebuilt when its
        /// type changes, so turning a knob never resets a delay line or a reverb tail.
        /// </summary>
        public void SynchronizeEffectChain(IReadOnlyList<EffectDevice> effectDevices, int sampleRate)
        {
            for (var effectIndex = 0; effectIndex < effectDevices.Count; effectIndex++)
            {
                var requestedType = effectDevices[effectIndex].Type;
                if (effectIndex >= EffectChain.Count)
                {
                    EffectChain.Add(EffectProcessor.Create(requestedType, sampleRate));
                }
                else if (EffectChain[effectIndex].EffectType != requestedType)
                {
                    EffectChain[effectIndex] = EffectProcessor.Create(requestedType, sampleRate);
                }
            }
            if (EffectChain.Count > effectDevices.Count)
            {
                EffectChain.RemoveRange(effectDevices.Count, EffectChain.Count - effectDevices.Count);
            }
        }
    }
}
