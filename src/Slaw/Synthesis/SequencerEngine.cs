using System.Collections.Concurrent;
using SlopstackLabs.Slaw.Plugins;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// What is playing. <see cref="Timeline"/> is the arrangement: the linear
/// timeline of clips. <see cref="Session"/> is clips launched from the grid
/// with no arrangement underneath.
/// </summary>
internal enum TransportMode
{
    Stopped,
    Session,
    Timeline,
}

/// <summary>
/// What one track is doing right now, for the UI to draw. <paramref name="TimelineClipId"/> is set
/// while an arrangement clip plays. <paramref name="OverridesArrangement"/> is set while the
/// arrangement plays but this track follows a launched session clip (or a stop) instead.
/// </summary>
/// <param name="EnvelopeStep">Where the playing clip's envelopes are: the pattern step in the session, steps since the clip's start in the arrangement.</param>
internal readonly record struct TrackTransportState(int? PlayingScene, int? ClipStep, int? QueuedScene, string? TimelineClipId = null, bool OverridesArrangement = false, int? EnvelopeStep = null);

/// <summary>An immutable picture of the transport, republished after every step.</summary>
internal sealed record TransportSnapshot(
    TransportMode Mode,
    IReadOnlyDictionary<string, TrackTransportState> Tracks,
    double PreviewProgress,
    long PublishedTimestamp = 0,
    double StepDurationSeconds = 0,
    long TimelineStep = -1,
    bool ReturnToArrangementQueued = false)
{
    public static readonly TransportSnapshot Idle = new(
        TransportMode.Stopped, new Dictionary<string, TrackTransportState>(), -1);

    /// <summary>True while any track plays session material over the arrangement (Back to Arrangement lights up).</summary>
    public bool ArrangementOverridden => Mode == TransportMode.Timeline && Tracks.Values.Any(trackState => trackState.OverridesArrangement);
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
    private const int AutomationChunkFrames = 64;

    /// <summary>Step notes reach plugins at a polite mezzo-forte (MIDI ~100), humanized downwards from there.</summary>
    private const float PluginNoteVelocity = 0.8f;

    private readonly int sampleRate;
    private readonly ConcurrentQueue<Action> pendingTransportCommands = new();
    private readonly Dictionary<string, TrackRuntimeState> trackRuntimeStates = new();
    private readonly Dictionary<string, float[]> renderedVoiceCache = new();
    private readonly Random noiseEntropySource;
    private readonly Random humanizationEntropySource;
    private readonly MasterBusProcessor masterBusProcessor;
    // Each track renders into a stereo pair. While a track is still mono (built-in voices
    // through mono-keeping effects) only the left buffer is used; see EffectProcessor.
    private readonly float[] trackLeftScratchBuffer = new float[MaximumBlockFrames];
    private readonly float[] trackRightScratchBuffer = new float[MaximumBlockFrames];
    private readonly float[] voiceScratchBuffer = new float[MaximumBlockFrames];
    private readonly PluginInstancePool pluginPool;

    private SlawProject? publishedProject;
    private TransportSnapshot publishedTransport = TransportSnapshot.Idle;
    private SlawProject? automationScannedProject;
    private bool automationScanResult;

    private TransportMode transportMode = TransportMode.Stopped;
    private double samplesUntilNextStep;
    private long sessionStep = -1;
    private long timelineStep = -1;
    private bool timelineLoops = true;

    // Session launches over the arrangement are quantized to the bar on a clock
    // that never wraps, so a loop brace cannot confuse a launched clip's position.
    private long timelineTickCount = -1;
    private bool returnToArrangementQueued;
    private SlawProject? timelineIndexedProject;
    private readonly Dictionary<string, List<TimelineClip>> timelineClipsByTrack = new();

    private float[]? previewLeft;
    private float[]? previewRight;
    private int previewPosition;

    private readonly Random auditionEntropySource = new(0xA0D1);
    private readonly List<AuditionVoice> activeAuditions = new();
    private readonly Dictionary<string, HashSet<int>> pluginAuditionNotes = new();
    private const int MaximumAuditionVoices = 32;

    /// <param name="offlinePluginHosting">
    /// Bounces: VST3 plugins load synchronously and process in offline mode, so no note is skipped while one loads.
    /// </param>
    public SequencerEngine(int sampleRate, int noiseSeed = 0x51A7, bool offlinePluginHosting = false)
    {
        this.sampleRate = sampleRate;
        noiseEntropySource = new Random(noiseSeed);
        humanizationEntropySource = new Random(noiseSeed ^ 0x2A);
        masterBusProcessor = new MasterBusProcessor(sampleRate);
        pluginPool = new PluginInstancePool(sampleRate, offlinePluginHosting);
    }

    public int SampleRate => sampleRate;

    /// <summary>How much the master limiter has worked since this engine started (bounces read it when they finish).</summary>
    public (int LimitedFrames, double DeepestGainReduction) LimiterActivity =>
        (masterBusProcessor.LimitedFrameCount, masterBusProcessor.DeepestGainReduction);

    /// <summary>The VST3 instances this engine plays (for the device view's knobs and load errors).</summary>
    public PluginInstancePool Plugins => pluginPool;

    /// <summary>Tears down every plugin instance. Call once the engine will render no more.</summary>
    public void ReleasePlugins() => pluginPool.ReleaseAll();

    /// <summary>The latest transport state. Safe to read from any thread.</summary>
    public TransportSnapshot Transport => Volatile.Read(ref publishedTransport);

    /// <summary>True once a non-looping arrangement (a bounce) has played its final step.</summary>
    public bool ArrangementFinished { get; private set; }

    // ── Commands (any thread) ─────────────────────────────────────────────────

    /// <summary>
    /// Hands the engine a new project snapshot. The caller must not mutate it
    /// afterwards; publish a fresh clone for every change.
    /// </summary>
    public void PublishProject(SlawProject projectSnapshot) => Volatile.Write(ref publishedProject, projectSnapshot);

    /// <summary>
    /// Plays the arrangement (the timeline) from <paramref name="startStep"/>. Live playback
    /// wraps at the loop brace (or the end); bounces pass <paramref name="loop"/> false and stop
    /// at the end. Every track starts out following the arrangement.
    /// </summary>
    public void PlayTimeline(long startStep = 0, bool loop = true) => pendingTransportCommands.Enqueue(() =>
    {
        ReleaseAllSessionClips();
        transportMode = TransportMode.Timeline;
        timelineStep = Math.Max(0, startStep) - 1;
        timelineTickCount = -1;
        timelineLoops = loop;
        ArrangementFinished = false;
        samplesUntilNextStep = 0;
    });

    /// <summary>
    /// Launches every clip in a scene. Without an arrangement playing, tracks with an
    /// empty slot stop; over the arrangement, only the scene's clips take over their
    /// tracks and the rest keep following the arrangement.
    /// </summary>
    public void LaunchScene(int sceneIndex) => pendingTransportCommands.Enqueue(() =>
    {
        var project = Volatile.Read(ref publishedProject);
        if (project is null)
        {
            return;
        }
        foreach (var track in project.Tracks)
        {
            var sceneClip = track.ClipAt(sceneIndex);
            if (sceneClip is null && transportMode == TransportMode.Timeline)
            {
                continue;
            }
            QueueClipLaunch(track.Id, sceneClip is null ? null : sceneIndex);
        }
    });

    /// <summary>
    /// Hands every track that a session launch took over back to the arrangement,
    /// on the next bar. Without the arrangement playing it simply forgets the overrides.
    /// </summary>
    public void BackToArrangement() => pendingTransportCommands.Enqueue(() =>
    {
        if (transportMode == TransportMode.Timeline)
        {
            returnToArrangementQueued = trackRuntimeStates.Values.Any(runtimeState => runtimeState.OverridesArrangement || runtimeState.HasQueuedLaunch);
        }
        else
        {
            foreach (var runtimeState in trackRuntimeStates.Values)
            {
                runtimeState.OverridesArrangement = false;
            }
        }
        PublishTransport();
    });

    public void LaunchClip(string trackIdentifier, int sceneIndex) =>
        pendingTransportCommands.Enqueue(() => QueueClipLaunch(trackIdentifier, sceneIndex));

    public void StopTrack(string trackIdentifier) =>
        pendingTransportCommands.Enqueue(() => QueueClipLaunch(trackIdentifier, null));

    public void Stop() => pendingTransportCommands.Enqueue(() =>
    {
        transportMode = TransportMode.Stopped;
        ReleaseAllSessionClips();
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
    public void StartNoteAudition(string trackIdentifier, int midiNote, double velocity = 1.0)
    {
        var project = Volatile.Read(ref publishedProject);
        var auditionedTrack = project?.Tracks.FirstOrDefault(track => track.Id == trackIdentifier);
        if (auditionedTrack is null)
        {
            return;
        }
        if (auditionedTrack.Instrument == DeviceTypes.Vst3)
        {
            var pluginVelocity = (float)Math.Clamp(velocity, 0.05, 1.0);
            pendingTransportCommands.Enqueue(() =>
            {
                if (pluginPool.TryGet(PluginInstancePool.InstrumentSlot(trackIdentifier), auditionedTrack.Plugin) is not { } auditionInstance)
                {
                    return;
                }
                auditionInstance.NoteOn(midiNote, pluginVelocity);
                if (!pluginAuditionNotes.TryGetValue(trackIdentifier, out var heldNotes))
                {
                    pluginAuditionNotes[trackIdentifier] = heldNotes = new HashSet<int>();
                }
                heldNotes.Add(midiNote);
            });
            return;
        }
        var percussiveInstrument = InstrumentVoiceSynthesizer.IsPercussive(auditionedTrack.Instrument);
        SampleData? samplerSource = null;
        if (auditionedTrack.Instrument == "sampler" && !string.IsNullOrEmpty(auditionedTrack.Sample))
        {
            try
            {
                samplerSource = SampleEditProcessor.LoadProcessed(auditionedTrack.Sample, auditionedTrack.SampleEditing, sampleRate);
            }
            catch (Exception sampleRemediationOpportunity) when (sampleRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return;
            }
        }
        var auditionedSoundFont = auditionedTrack.Instrument == "soundfont" ? SoundFonts.SoundFontLibrary.LoadPreset(auditionedTrack.SoundFont) : null;
        var renderedVoice = InstrumentVoiceSynthesizer.RenderVoice(
            auditionedTrack.Instrument,
            new[] { midiNote },
            AuditionMaximumHoldSeconds,
            sampleRate,
            auditionEntropySource,
            auditionedTrack.InstrumentParameters,
            samplerSource,
            auditionedTrack.SampleEditing,
            soundFontPreset: auditionedSoundFont);
        if (renderedVoice.Length == 0)
        {
            return;
        }
        // Melodic voices fade on release over the instrument's own release time;
        // drums and plucks are allowed to ring out, as nature intended.
        var releaseSeconds = percussiveInstrument || auditionedTrack.Instrument == "pluck" ||
                             (auditionedTrack.Instrument == "sampler" && DeviceCatalog.Resolve(auditionedTrack.InstrumentParameters, DeviceCatalog.InstrumentParametersFor("sampler"), "hold") < 0.5)
            ? 0
            : auditionedTrack.Instrument == "soundfont"
                // The font's release knob is a multiplier of each zone's own release, not milliseconds.
                ? 0.25 * DeviceCatalog.Resolve(auditionedTrack.InstrumentParameters, DeviceCatalog.InstrumentParametersFor("soundfont"), "release")
                : DeviceCatalog.Resolve(auditionedTrack.InstrumentParameters, DeviceCatalog.InstrumentParametersFor(auditionedTrack.Instrument), "release") / 1000.0;
        // Same curve as recorded notes, so a take plays back as loud as it was played.
        var auditionVoice = new AuditionVoice(trackIdentifier, midiNote, (float)NotationResolver.VelocityToGain(Math.Max(0.05, velocity)),
            renderedVoice, Math.Max(1, (int)(releaseSeconds * sampleRate)), releaseSeconds > 0);
        pendingTransportCommands.Enqueue(() =>
        {
            // Re-striking a held key releases the previous strike rather than stacking it.
            foreach (var sameNoteVoice in activeAuditions.Where(voice => voice.MidiNote == midiNote))
            {
                sameNoteVoice.BeginRelease();
            }
            activeAuditions.Add(auditionVoice);
            if (activeAuditions.Count > MaximumAuditionVoices)
            {
                activeAuditions.RemoveAt(0);
            }
        });
    }

    /// <summary>Releases every sounding audition note (the piano roll's mouse-up).</summary>
    public void StopNoteAudition() => pendingTransportCommands.Enqueue(() =>
    {
        foreach (var auditionVoice in activeAuditions)
        {
            auditionVoice.BeginRelease();
        }
        foreach (var (trackIdentifier, heldNotes) in pluginAuditionNotes)
        {
            var auditionInstance = pluginPool.Current(PluginInstancePool.InstrumentSlot(trackIdentifier));
            foreach (var heldNote in heldNotes)
            {
                auditionInstance?.NoteOff(heldNote);
            }
            heldNotes.Clear();
        }
    });

    /// <summary>Releases one note (a MIDI key being let go), leaving the rest of the chord ringing.</summary>
    public void StopNoteAudition(int midiNote) => pendingTransportCommands.Enqueue(() =>
    {
        foreach (var auditionVoice in activeAuditions.Where(voice => voice.MidiNote == midiNote))
        {
            auditionVoice.BeginRelease();
        }
        foreach (var (trackIdentifier, heldNotes) in pluginAuditionNotes)
        {
            if (heldNotes.Remove(midiNote))
            {
                pluginPool.Current(PluginInstancePool.InstrumentSlot(trackIdentifier))?.NoteOff(midiNote);
            }
        }
    });

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
                // Envelopes are re-evaluated per chunk; short chunks keep sweeps free of zipper noise.
                if (ProjectHasAutomation(project))
                {
                    chunkFrameCount = Math.Min(chunkFrameCount, AutomationChunkFrames);
                }
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
        if (transportMode == TransportMode.Timeline)
        {
            // Over the arrangement a launch (or a stop) takes this track over on the next
            // bar; the other tracks keep following the arrangement until Back to Arrangement.
            runtimeState.QueuedScene = sceneIndex;
            runtimeState.HasQueuedLaunch = true;
            returnToArrangementQueued = false;
        }
        else if (transportMode != TransportMode.Session)
        {
            // Launching from a stopped transport starts the session clock immediately.
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
        foreach (var (trackIdentifier, runtimeState) in trackRuntimeStates)
        {
            if (runtimeState.HostsPluginInstrument)
            {
                pluginPool.Current(PluginInstancePool.InstrumentSlot(trackIdentifier))?.AllNotesOff();
                Array.Clear(runtimeState.PluginNoteCountdowns);
            }
            runtimeState.PlayingScene = null;
            runtimeState.QueuedScene = null;
            runtimeState.HasQueuedLaunch = false;
            runtimeState.CurrentClipStep = null;
            runtimeState.EnvelopeStep = null;
            runtimeState.PlayingClip = null;
            runtimeState.PlayingTimelineClipId = null;
            runtimeState.OverridesArrangement = false;
        }
        returnToArrangementQueued = false;
    }

    private void AdvanceStep(SlawProject project)
    {
        AdvancePluginNoteCountdowns();
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
                runtimeState.EnvelopeStep = null;
                runtimeState.PlayingClip = null;
                if (runtimeState.PlayingScene is not { } playingScene || track.ClipAt(playingScene) is not { Pattern.Count: > 0 } playingClip)
                {
                    continue;
                }
                var clipStep = (int)((sessionStep - runtimeState.ClipOriginStep) % playingClip.Pattern.Count);
                runtimeState.CurrentClipStep = clipStep;
                runtimeState.EnvelopeStep = clipStep;
                runtimeState.PlayingClip = playingClip;
                TriggerStep(project, track, runtimeState, playingClip, clipStep, clipStep, playingClip.Pattern.Count);
            }
        }
        else if (transportMode == TransportMode.Timeline && !AdvanceTimelineStep(project))
        {
            return;
        }
        PublishTransport();
    }

    /// <summary>Plugin instruments hear about note ends as events: one step closer for every held note, note-off at zero.</summary>
    private void AdvancePluginNoteCountdowns()
    {
        foreach (var (trackIdentifier, runtimeState) in trackRuntimeStates)
        {
            if (!runtimeState.HostsPluginInstrument)
            {
                continue;
            }
            Vst3PluginInstance? pluginInstrument = null;
            var countdowns = runtimeState.PluginNoteCountdowns;
            for (var midiNote = 0; midiNote < countdowns.Length; midiNote++)
            {
                if (countdowns[midiNote] > 0 && --countdowns[midiNote] == 0)
                {
                    pluginInstrument ??= pluginPool.Current(PluginInstancePool.InstrumentSlot(trackIdentifier));
                    pluginInstrument?.NoteOff(midiNote);
                }
            }
        }
    }

    private void TriggerPluginStep(SlawTrack track, TrackRuntimeState runtimeState, SlawClip clip, int clipStep, float humanizedGain)
    {
        runtimeState.HostsPluginInstrument = true;
        if (pluginPool.TryGet(PluginInstancePool.InstrumentSlot(track.Id), track.Plugin) is not { } pluginInstrument)
        {
            return;
        }
        foreach (var placedNote in ClipNoteEditor.NotesStartingAt(clip.Pattern, clipStep))
        {
            pluginInstrument.NoteOn(placedNote.MidiNote, PluginNoteVelocity * humanizedGain);
            runtimeState.PluginNoteCountdowns[Math.Clamp(placedNote.MidiNote, 0, 127)] = Math.Max(1, placedNote.Length);
        }
    }

    /// <summary>
    /// Plays one step of the arrangement. Tracks a session launch took over play their
    /// session clip (or nothing, after a stop) instead. Returns false when playback ran
    /// off the end and stopped.
    /// </summary>
    private bool AdvanceTimelineStep(SlawProject project)
    {
        timelineStep++;
        timelineTickCount++;
        if (project.TimelineLoop is { IsValid: true } loopRegion && timelineLoops && timelineStep == loopRegion.EndStep)
        {
            timelineStep = loopRegion.StartStep;
        }
        var timelineLength = FreeTimeline.LengthInSteps(project);
        if (timelineStep >= timelineLength && !(project.TimelineLoop is { IsValid: true } && timelineLoops && timelineStep < project.TimelineLoop.EndStep))
        {
            if (timelineLength > 0 && timelineLoops)
            {
                timelineStep = 0;
            }
            else
            {
                transportMode = TransportMode.Stopped;
                ArrangementFinished = true;
                ReleaseAllSessionClips();
                PublishTransport();
                return false;
            }
        }

        if (!ReferenceEquals(project, timelineIndexedProject))
        {
            timelineIndexedProject = project;
            timelineClipsByTrack.Clear();
            foreach (var track in project.Tracks)
            {
                timelineClipsByTrack[track.Id] = FreeTimeline.ClipsForTrack(project, track.Id);
            }
        }
        var launchBoundaryReached = timelineTickCount % LaunchQuantizationSteps == 0;
        if (returnToArrangementQueued && launchBoundaryReached)
        {
            returnToArrangementQueued = false;
            foreach (var runtimeState in trackRuntimeStates.Values)
            {
                runtimeState.OverridesArrangement = false;
                runtimeState.HasQueuedLaunch = false;
                runtimeState.QueuedScene = null;
            }
        }
        foreach (var track in project.Tracks)
        {
            var runtimeState = RuntimeStateFor(track.Id);
            if (runtimeState.HasQueuedLaunch && launchBoundaryReached)
            {
                runtimeState.OverridesArrangement = true;
                runtimeState.PlayingScene = runtimeState.QueuedScene;
                runtimeState.ClipOriginStep = timelineTickCount;
                runtimeState.HasQueuedLaunch = false;
                runtimeState.QueuedScene = null;
            }
            runtimeState.CurrentClipStep = null;
            runtimeState.EnvelopeStep = null;
            runtimeState.PlayingClip = null;
            runtimeState.PlayingTimelineClipId = null;
            if (runtimeState.OverridesArrangement)
            {
                if (runtimeState.PlayingScene is { } overridingScene && track.ClipAt(overridingScene) is { Pattern.Count: > 0 } sessionClip)
                {
                    var sessionClipStep = (int)((timelineTickCount - runtimeState.ClipOriginStep) % sessionClip.Pattern.Count);
                    runtimeState.CurrentClipStep = sessionClipStep;
                    runtimeState.EnvelopeStep = sessionClipStep;
                    runtimeState.PlayingClip = sessionClip;
                    TriggerStep(project, track, runtimeState, sessionClip, sessionClipStep, sessionClipStep, sessionClip.Pattern.Count);
                }
                continue;
            }
            runtimeState.PlayingScene = null;
            if (FreeTimeline.ClipAt(timelineClipsByTrack[track.Id], timelineStep) is not { Clip.Pattern.Count: > 0 } timelineClip)
            {
                continue;
            }
            // The pattern loops inside the clip; its envelopes do not: they span the whole clip.
            var clipStep = FreeTimeline.PatternStepAt(timelineClip, timelineStep);
            var stepsIntoClip = (int)(timelineStep - timelineClip.StartStep);
            runtimeState.CurrentClipStep = clipStep;
            runtimeState.EnvelopeStep = stepsIntoClip;
            runtimeState.PlayingClip = timelineClip.Clip;
            runtimeState.PlayingTimelineClipId = timelineClip.Id;
            TriggerStep(project, track, runtimeState, timelineClip.Clip, clipStep, stepsIntoClip, timelineClip.LengthSteps);
        }
        return true;
    }

    /// <param name="envelopeStep">Where the clip's envelopes are: the pattern step for session clips, steps since the clip's start for arrangement clips.</param>
    /// <param name="envelopeLength">How long the envelopes run before they come around again.</param>
    private void TriggerStep(SlawProject project, SlawTrack track, TrackRuntimeState runtimeState, SlawClip clip, int clipStep, int envelopeStep, int envelopeLength)
    {
        var stepEvent = NotationResolver.ResolveOrRest(clip.Pattern[clipStep]);
        if (stepEvent.Kind != StepEventKind.Trigger)
        {
            return;
        }

        // Voices are rendered whole, so instrument envelopes are sampled as each note
        // starts and an oscillator's pitch envelope is baked into the note itself.
        IReadOnlyDictionary<string, double> instrumentParameters = track.InstrumentParameters;
        var bendsAtRender = InstrumentVoiceSynthesizer.BendsAtRender(track.Instrument);
        RenderedPitchBend? pitchBend = null;
        if (clip.Automation is { Count: > 0 })
        {
            var envelopes = runtimeState.EnvelopesFor(clip);
            if (envelopes.InstrumentEnvelopes.Count > 0)
            {
                var automatedParameters = new Dictionary<string, double>(track.InstrumentParameters);
                foreach (var (parameterKey, points) in envelopes.InstrumentEnvelopes)
                {
                    automatedParameters[parameterKey] = ClipAutomation.Evaluate(points, envelopeStep);
                }
                instrumentParameters = automatedParameters;
            }
            if (bendsAtRender && envelopes.Pitch is { } pitchEnvelope)
            {
                var envelopeWrap = Math.Max(1, envelopeLength);
                var stepSeconds = project.StepDurationSeconds;
                pitchBend = new RenderedPitchBend(
                    elapsedSeconds => Math.Clamp(ClipAutomation.Evaluate(pitchEnvelope, (envelopeStep + elapsedSeconds / stepSeconds) % envelopeWrap), -24, 24),
                    FormattableString.Invariant($"{envelopeStep}/{envelopeWrap}@{envelopes.PitchFingerprint}"));
            }
        }

        var percussiveInstrument = InstrumentVoiceSynthesizer.IsPercussive(track.Instrument);
        var humanizeAmount = DeviceCatalog.Resolve(
            instrumentParameters, DeviceCatalog.InstrumentParametersFor(track.Instrument), "humanize");
        var humanizedGain = (float)(1 - humanizeAmount * 0.35 * humanizationEntropySource.NextDouble());
        if (track.Instrument == DeviceTypes.Vst3)
        {
            TriggerPluginStep(track, runtimeState, clip, clipStep, humanizedGain);
            return;
        }

        // Notes struck together may be held for different lengths and struck at
        // different velocities: render one voice per distinct (length, velocity),
        // each with its share of the chord. Velocity is only a gain, so it never splits the
        // voice cache.
        var noteGroups = percussiveInstrument
            ? new[] { (Length: 1, Velocity: stepEvent.PeakVelocity, Notes: stepEvent.MidiNotes.ToArray()) }
            : ClipNoteEditor.NotesStartingAt(clip.Pattern, clipStep)
                .GroupBy(placedNote => (placedNote.Length, placedNote.Velocity))
                .Select(noteGroup => (noteGroup.Key.Length, noteGroup.Key.Velocity, Notes: noteGroup.Select(placedNote => placedNote.MidiNote).ToArray()))
                .ToArray();
        foreach (var (heldStepCount, noteVelocity, chordNotes) in noteGroups)
        {
            var renderedVoice = RenderOrRecallVoice(project, track, instrumentParameters, pitchBend, StepEvent.Trigger(chordNotes), heldStepCount);
            if (renderedVoice.Length == 0)
            {
                continue;
            }
            if (runtimeState.ActiveVoices.Count >= MaximumVoicesPerTrack)
            {
                runtimeState.ActiveVoices.RemoveAt(0);
            }
            // Each voice normalizes its own notes by 1/sqrt(k); rescale so every
            // note in the full chord sits at 1/sqrt(n), as if rendered together.
            var velocityGain = (float)NotationResolver.VelocityToGain(noteVelocity);
            runtimeState.ActiveVoices.Add(new ActiveVoice(renderedVoice, velocityGain * humanizedGain * (float)Math.Sqrt(chordNotes.Length / (double)Math.Max(1, stepEvent.MidiNotes.Count)), followsPitchEnvelope: !bendsAtRender));
        }
    }

    /// <summary>A pitch envelope rendered into a voice, with a signature for the voice cache.</summary>
    private sealed record RenderedPitchBend(Func<double, double> SemitonesAt, string Signature);

    private float[] RenderOrRecallVoice(SlawProject project, SlawTrack track, IReadOnlyDictionary<string, double> instrumentParameters, RenderedPitchBend? pitchBend, StepEvent stepEvent, int heldStepCount)
    {
        var parameterFingerprint = string.Join(',', instrumentParameters.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value:R}"));
        var voiceCacheKey = $"{track.Instrument}|{track.Sample}|{SampleEditingFingerprint(track.SampleEditing)}|{track.SoundFont?.Fingerprint}|{string.Join('+', stepEvent.MidiNotes)}|{heldStepCount}|{project.Bpm:R}|{parameterFingerprint}|{pitchBend?.Signature}";
        if (renderedVoiceCache.TryGetValue(voiceCacheKey, out var cachedVoice))
        {
            return cachedVoice;
        }
        SampleData? samplerSource = null;
        if (track.Instrument == "sampler")
        {
            // Never touch the disk from the audio thread: a sample that is not
            // decoded yet is requested in the background and skipped this time.
            samplerSource = string.IsNullOrEmpty(track.Sample) ? null : SampleEditProcessor.TryGetProcessed(track.Sample, track.SampleEditing, sampleRate);
            if (samplerSource is null)
            {
                return Array.Empty<float>();
            }
        }
        SoundFonts.SoundFontPreset? soundFontPreset = null;
        if (track.Instrument == "soundfont")
        {
            // Same rule as samples: a font still parsing in the background skips this note.
            soundFontPreset = SoundFonts.SoundFontLibrary.TryGetPreset(track.SoundFont);
            if (soundFontPreset is null)
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
            instrumentParameters,
            samplerSource,
            track.SampleEditing,
            pitchBend?.SemitonesAt,
            soundFontPreset);
        renderedVoiceCache[voiceCacheKey] = renderedVoice;
        return renderedVoice;
    }

    private static string SampleEditingFingerprint(SampleEditSettings? sampleEditing) =>
        sampleEditing is null
            ? "-"
            : FormattableString.Invariant(
                $"{SampleEditProcessor.Fingerprint(sampleEditing)}|{sampleEditing.PlaybackMode}|{string.Join(',', sampleEditing.SlicePointsSeconds)}|{sampleEditing.LoopEnabled}|{sampleEditing.LoopStartSeconds:R}|{sampleEditing.LoopEndSeconds:R}");

    // ── Mixing ────────────────────────────────────────────────────────────────

    private void MixTracks(SlawProject project, Span<float> leftChannel, Span<float> rightChannel)
    {
        var soloEngaged = project.Tracks.Any(track => track.Solo);
        var processingContext = new EffectProcessingContext(sampleRate, project.StepDurationSeconds);
        var trackLeftBlock = trackLeftScratchBuffer.AsSpan(0, leftChannel.Length);
        var trackRightBlock = trackRightScratchBuffer.AsSpan(0, leftChannel.Length);
        var voiceBlock = voiceScratchBuffer.AsSpan(0, leftChannel.Length);

        var stepFrameCount = project.StepDurationSeconds * sampleRate;
        var stepProgress = transportMode == TransportMode.Stopped || stepFrameCount <= 0
            ? 0
            : Math.Clamp(1 - samplesUntilNextStep / stepFrameCount, 0, 1);

        foreach (var track in project.Tracks)
        {
            var runtimeState = RuntimeStateFor(track.Id);
            var trackAudible = !track.Muted && (!soloEngaged || track.Solo);
            trackLeftBlock.Clear();
            trackRightBlock.Clear();

            // Clip envelopes only move while their clip plays; otherwise the knobs rule.
            CompiledEnvelopes? envelopes = null;
            var clipPosition = 0.0;
            if (transportMode != TransportMode.Stopped &&
                runtimeState.EnvelopeStep is { } envelopeStep &&
                runtimeState.PlayingClip is { Automation.Count: > 0 } automatedClip)
            {
                envelopes = runtimeState.EnvelopesFor(automatedClip);
                clipPosition = envelopeStep + stepProgress;
            }
            var playbackRate = envelopes?.Pitch is { } pitchEnvelope
                ? Math.Pow(2, Math.Clamp(ClipAutomation.Evaluate(pitchEnvelope, clipPosition), -24, 24) / 12)
                : 1.0;

            // A plugin instrument is stereo from the start; built-in voices are mono, so a
            // track without one stays mono (left buffer only) until an effect widens it.
            var trackIsMono = !RenderPluginInstrument(project, track, runtimeState, trackLeftBlock, trackRightBlock, envelopes, clipPosition);
            var monoVoiceBlock = trackIsMono ? trackLeftBlock : voiceBlock;
            var voicesSounded = runtimeState.ActiveVoices.Count > 0;
            if (voicesSounded && !trackIsMono)
            {
                voiceBlock.Clear();
            }
            for (var voiceIndex = runtimeState.ActiveVoices.Count - 1; voiceIndex >= 0; voiceIndex--)
            {
                var activeVoice = runtimeState.ActiveVoices[voiceIndex];
                // Muted voices still advance, so unmuting lands mid-note rather than restarting it.
                if (activeVoice.MixInto(monoVoiceBlock, playbackRate, trackAudible))
                {
                    runtimeState.ActiveVoices.RemoveAt(voiceIndex);
                }
            }

            // An auditioned note is heard even on a muted track: you asked for it explicitly.
            var auditionOnThisTrack = false;
            for (var auditionIndex = activeAuditions.Count - 1; auditionIndex >= 0; auditionIndex--)
            {
                var auditionVoice = activeAuditions[auditionIndex];
                if (auditionVoice.TrackIdentifier != track.Id)
                {
                    continue;
                }
                if (!voicesSounded && !trackIsMono)
                {
                    voiceBlock.Clear();
                }
                voicesSounded = true;
                auditionOnThisTrack = true;
                if (auditionVoice.MixInto(monoVoiceBlock))
                {
                    activeAuditions.RemoveAt(auditionIndex);
                }
            }
            if (voicesSounded && !trackIsMono)
            {
                for (var frameIndex = 0; frameIndex < voiceBlock.Length; frameIndex++)
                {
                    trackLeftBlock[frameIndex] += voiceBlock[frameIndex];
                    trackRightBlock[frameIndex] += voiceBlock[frameIndex];
                }
            }

            auditionOnThisTrack |= pluginAuditionNotes.TryGetValue(track.Id, out var heldPluginNotes) && heldPluginNotes.Count > 0;
            runtimeState.SynchronizeEffectChain(track.Id, track.Effects, sampleRate, pluginPool);
            for (var effectIndex = 0; effectIndex < track.Effects.Count; effectIndex++)
            {
                if (track.Effects[effectIndex].Enabled)
                {
                    var effectParameters = envelopes is null
                        ? track.Effects[effectIndex].Parameters
                        : runtimeState.AutomatedEffectParameters(effectIndex, track.Effects[effectIndex].Parameters, envelopes, clipPosition);
                    trackIsMono = runtimeState.EffectChain[effectIndex].Process(trackLeftBlock, trackRightBlock, effectParameters,
                        processingContext with { MonoInput = trackIsMono });
                }
            }

            if (!trackAudible && !auditionOnThisTrack)
            {
                continue;
            }
            var trackGain = envelopes?.Gain is { } gainEnvelope ? Math.Clamp(ClipAutomation.Evaluate(gainEnvelope, clipPosition), 0, 1.5) : track.Gain;
            var trackPan = envelopes?.Pan is { } panEnvelope ? ClipAutomation.Evaluate(panEnvelope, clipPosition) : track.Pan;
            // Constant-power balance: a mono source (equal channels) pans exactly as it always
            // has, and a stereo source keeps its image, tilted toward the side it is panned to.
            var panAngle = (Math.Clamp(trackPan, -1, 1) + 1) * Math.PI / 4;
            var leftGain = (float)(trackGain * Math.Cos(panAngle));
            var rightGain = (float)(trackGain * Math.Sin(panAngle));
            var trackRightSource = trackIsMono ? trackLeftBlock : trackRightBlock;
            for (var frameIndex = 0; frameIndex < trackLeftBlock.Length; frameIndex++)
            {
                leftChannel[frameIndex] += trackLeftBlock[frameIndex] * leftGain;
                rightChannel[frameIndex] += trackRightSource[frameIndex] * rightGain;
            }
        }

        // Forget tracks that no longer exist, along with their reverb tails.
        if (trackRuntimeStates.Count > project.Tracks.Count)
        {
            var survivingIdentifiers = project.Tracks.Select(track => track.Id).ToHashSet();
            foreach (var orphanedIdentifier in trackRuntimeStates.Keys.Where(identifier => !survivingIdentifiers.Contains(identifier)).ToList())
            {
                trackRuntimeStates.Remove(orphanedIdentifier);
                pluginPool.ReleaseTrack(orphanedIdentifier);
                pluginAuditionNotes.Remove(orphanedIdentifier);
            }
        }
    }

    /// <summary>
    /// A VST3 instrument's block is its voice: rendered first, then through the track's
    /// device chain like any other. Returns true if a plugin wrote the (stereo) block.
    /// </summary>
    private bool RenderPluginInstrument(SlawProject project, SlawTrack track, TrackRuntimeState runtimeState, Span<float> trackLeftBlock, Span<float> trackRightBlock, CompiledEnvelopes? envelopes, double clipPosition)
    {
        if (track.Instrument != DeviceTypes.Vst3)
        {
            if (runtimeState.HostsPluginInstrument)
            {
                runtimeState.HostsPluginInstrument = false;
                Array.Clear(runtimeState.PluginNoteCountdowns);
                pluginPool.Release(PluginInstancePool.InstrumentSlot(track.Id));
            }
            return false;
        }
        runtimeState.HostsPluginInstrument = true;
        if (pluginPool.TryGet(PluginInstancePool.InstrumentSlot(track.Id), track.Plugin) is not { } pluginInstrument)
        {
            return false;
        }
        pluginInstrument.SetTransport(project.Bpm, transportMode != TransportMode.Stopped);
        // A plugin plays continuously, so its instrument envelopes move per chunk rather than per note.
        pluginInstrument.Process(trackLeftBlock, trackRightBlock, envelopes is { InstrumentEnvelopes.Count: > 0 }
            ? runtimeState.AutomatedInstrumentParameters(track.InstrumentParameters, envelopes, clipPosition)
            : track.InstrumentParameters);
        return true;
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
            pair => new TrackTransportState(pair.Value.PlayingScene, pair.Value.CurrentClipStep, pair.Value.HasQueuedLaunch ? pair.Value.QueuedScene ?? -1 : null,
                pair.Value.PlayingTimelineClipId, pair.Value.OverridesArrangement, pair.Value.EnvelopeStep));
        var previewProgress = previewLeft is null ? -1 : (double)previewPosition / previewLeft.Length;
        var project = Volatile.Read(ref publishedProject);
        Volatile.Write(ref publishedTransport, new TransportSnapshot(
            transportMode, trackStates, previewProgress,
            System.Diagnostics.Stopwatch.GetTimestamp(), project?.StepDurationSeconds ?? 0,
            transportMode == TransportMode.Timeline ? timelineStep : -1,
            returnToArrangementQueued));
    }

    // ── Runtime state ─────────────────────────────────────────────────────────

    private sealed class ActiveVoice
    {
        private readonly float[] samples;
        private readonly float gain;
        private readonly bool followsPitchEnvelope;
        private double position;

        public ActiveVoice(float[] samples, float gain, bool followsPitchEnvelope)
        {
            this.samples = samples;
            this.gain = gain;
            this.followsPitchEnvelope = followsPitchEnvelope;
        }

        /// <summary>
        /// Adds the next block. Voices that did not bake the pitch envelope in play at
        /// <paramref name="playbackRate"/>, tape-style. Returns true once the voice has finished.
        /// </summary>
        public bool MixInto(Span<float> trackBlock, double playbackRate, bool audible)
        {
            if (!followsPitchEnvelope)
            {
                playbackRate = 1.0;
            }
            if (playbackRate == 1.0 && position == Math.Floor(position))
            {
                var integerPosition = (int)position;
                var mixableFrameCount = Math.Min(trackBlock.Length, samples.Length - integerPosition);
                for (var frameIndex = 0; audible && frameIndex < mixableFrameCount; frameIndex++)
                {
                    trackBlock[frameIndex] += samples[integerPosition + frameIndex] * gain;
                }
                position += mixableFrameCount;
                return position >= samples.Length;
            }
            for (var frameIndex = 0; frameIndex < trackBlock.Length; frameIndex++)
            {
                var lowerFrame = (int)position;
                if (lowerFrame >= samples.Length - 1)
                {
                    position = samples.Length;
                    return true;
                }
                if (audible)
                {
                    var interpolationWeight = (float)(position - lowerFrame);
                    trackBlock[frameIndex] += (samples[lowerFrame] + (samples[lowerFrame + 1] - samples[lowerFrame]) * interpolationWeight) * gain;
                }
                position += playbackRate;
            }
            return position >= samples.Length;
        }
    }

    /// <summary>A clip's envelopes, parsed once per project snapshot so the audio thread never parses target keys.</summary>
    private sealed class CompiledEnvelopes
    {
        public CompiledEnvelopes(SlawClip clip)
        {
            foreach (var (targetKey, points) in clip.Automation ?? new Dictionary<string, List<AutomationPoint>>())
            {
                if (points.Count == 0)
                {
                    continue;
                }
                var sortedPoints = points.OrderBy(point => point.Step).ToList();
                switch (targetKey)
                {
                    case ClipAutomation.GainTarget:
                        Gain = sortedPoints;
                        break;
                    case ClipAutomation.PanTarget:
                        Pan = sortedPoints;
                        break;
                    case ClipAutomation.PitchTarget:
                        Pitch = sortedPoints;
                        PitchFingerprint = string.Join(';', sortedPoints.Select(point => FormattableString.Invariant($"{point.Step:R}:{point.Value:R}"))) + FormattableString.Invariant($"/{clip.Pattern.Count}");
                        break;
                    default:
                        if (ClipAutomation.TryParseEffectTarget(targetKey, out var effectIndex, out var effectParameterKey))
                        {
                            EffectEnvelopes.Add((effectIndex, effectParameterKey, sortedPoints));
                        }
                        else if (targetKey.StartsWith(ClipAutomation.InstrumentTargetPrefix, StringComparison.Ordinal))
                        {
                            InstrumentEnvelopes.Add((targetKey[ClipAutomation.InstrumentTargetPrefix.Length..], sortedPoints));
                        }
                        break;
                }
            }
        }

        public List<AutomationPoint>? Gain { get; }
        public List<AutomationPoint>? Pan { get; }
        public List<AutomationPoint>? Pitch { get; }
        public string PitchFingerprint { get; } = string.Empty;
        public List<(int EffectIndex, string ParameterKey, List<AutomationPoint> Points)> EffectEnvelopes { get; } = new();
        public List<(string ParameterKey, List<AutomationPoint> Points)> InstrumentEnvelopes { get; } = new();
    }

    private bool ProjectHasAutomation(SlawProject project)
    {
        if (!ReferenceEquals(project, automationScannedProject))
        {
            automationScannedProject = project;
            automationScanResult = project.Tracks.Any(track => track.Clips.Any(clip => clip?.Automation is { Count: > 0 })) ||
                                   project.Timeline.Any(timelineClip => timelineClip.Clip.Automation is { Count: > 0 });
        }
        return automationScanResult;
    }

    /// <summary>A held preview note: plays until released, then fades over its release time.</summary>
    private sealed class AuditionVoice
    {
        private readonly float[] samples;
        private readonly int releaseFrameCount;
        private readonly bool fadesOnRelease;
        private int position;
        private int releaseFramesRemaining = -1;

        private readonly float velocityGain;

        public AuditionVoice(string trackIdentifier, int midiNote, float velocityGain, float[] samples, int releaseFrameCount, bool fadesOnRelease)
        {
            TrackIdentifier = trackIdentifier;
            MidiNote = midiNote;
            this.velocityGain = velocityGain;
            this.samples = samples;
            this.releaseFrameCount = releaseFrameCount;
            this.fadesOnRelease = fadesOnRelease;
        }

        public string TrackIdentifier { get; }

        public int MidiNote { get; }

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
                trackBlock[frameIndex] += samples[position++] * releaseGain * velocityGain;
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

        /// <summary>The playing clip's envelope position: equals <see cref="CurrentClipStep"/> in the session, steps since the start of an arrangement clip.</summary>
        public int? EnvelopeStep { get; set; }

        /// <summary>The clip whose step <see cref="CurrentClipStep"/> is, in any transport mode; envelopes follow it.</summary>
        public SlawClip? PlayingClip { get; set; }

        /// <summary>The arrangement clip playing on this track, for the UI.</summary>
        public string? PlayingTimelineClipId { get; set; }

        /// <summary>While the arrangement plays, this track follows a session launch (or a stop) instead of its timeline clips.</summary>
        public bool OverridesArrangement { get; set; }

        private readonly Dictionary<SlawClip, CompiledEnvelopes> compiledEnvelopes = new(ReferenceEqualityComparer.Instance);
        private readonly List<Dictionary<string, double>> automatedEffectParameters = new();

        /// <summary>The clip's parsed envelopes. Snapshots are immutable, so a clip object's envelopes never go stale.</summary>
        public CompiledEnvelopes EnvelopesFor(SlawClip clip)
        {
            if (!compiledEnvelopes.TryGetValue(clip, out var envelopes))
            {
                // Each edit publishes fresh clip objects; forget the previous snapshot's.
                if (compiledEnvelopes.Count > 64)
                {
                    compiledEnvelopes.Clear();
                }
                envelopes = new CompiledEnvelopes(clip);
                compiledEnvelopes[clip] = envelopes;
            }
            return envelopes;
        }

        private readonly Dictionary<string, double> automatedInstrumentParameters = new();

        /// <summary>The instrument's knob values with its envelopes applied, in a dictionary reused every block (plugin instruments).</summary>
        public IReadOnlyDictionary<string, double> AutomatedInstrumentParameters(Dictionary<string, double> knobValues, CompiledEnvelopes envelopes, double clipPosition)
        {
            automatedInstrumentParameters.Clear();
            foreach (var (knobKey, knobValue) in knobValues)
            {
                automatedInstrumentParameters[knobKey] = knobValue;
            }
            foreach (var (parameterKey, points) in envelopes.InstrumentEnvelopes)
            {
                automatedInstrumentParameters[parameterKey] = ClipAutomation.Evaluate(points, clipPosition);
            }
            return automatedInstrumentParameters;
        }

        /// <summary>An effect slot's knob values with its envelopes applied, in a dictionary reused every block.</summary>
        public IReadOnlyDictionary<string, double> AutomatedEffectParameters(int effectIndex, Dictionary<string, double> knobValues, CompiledEnvelopes envelopes, double clipPosition)
        {
            Dictionary<string, double>? automatedValues = null;
            foreach (var (envelopeEffectIndex, parameterKey, points) in envelopes.EffectEnvelopes)
            {
                if (envelopeEffectIndex != effectIndex)
                {
                    continue;
                }
                if (automatedValues is null)
                {
                    while (automatedEffectParameters.Count <= effectIndex)
                    {
                        automatedEffectParameters.Add(new Dictionary<string, double>());
                    }
                    automatedValues = automatedEffectParameters[effectIndex];
                    automatedValues.Clear();
                    foreach (var (knobKey, knobValue) in knobValues)
                    {
                        automatedValues[knobKey] = knobValue;
                    }
                }
                automatedValues[parameterKey] = ClipAutomation.Evaluate(points, clipPosition);
            }
            return automatedValues ?? (IReadOnlyDictionary<string, double>)knobValues;
        }

        /// <summary>True while the track's instrument is a VST3 plugin (so its slot is released when that changes).</summary>
        public bool HostsPluginInstrument { get; set; }

        /// <summary>Steps until each MIDI note's note-off, for plugin instruments; zero is not sounding.</summary>
        public int[] PluginNoteCountdowns { get; } = new int[128];

        /// <summary>
        /// Keeps one processor per effect slot. A slot is only rebuilt when its
        /// type changes, so turning a knob never resets a delay line or a reverb tail.
        /// </summary>
        public void SynchronizeEffectChain(string trackIdentifier, IReadOnlyList<EffectDevice> effectDevices, int sampleRate, PluginInstancePool pluginPool)
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
                    if (EffectChain[effectIndex] is Vst3EffectProcessor)
                    {
                        pluginPool.Release(PluginInstancePool.EffectSlot(trackIdentifier, effectIndex));
                    }
                    EffectChain[effectIndex] = EffectProcessor.Create(requestedType, sampleRate);
                }
                if (EffectChain[effectIndex] is Vst3EffectProcessor pluginEffect)
                {
                    pluginEffect.Bind(pluginPool, PluginInstancePool.EffectSlot(trackIdentifier, effectIndex), effectDevices[effectIndex].Plugin);
                }
            }
            if (EffectChain.Count > effectDevices.Count)
            {
                for (var removedIndex = effectDevices.Count; removedIndex < EffectChain.Count; removedIndex++)
                {
                    if (EffectChain[removedIndex] is Vst3EffectProcessor)
                    {
                        pluginPool.Release(PluginInstancePool.EffectSlot(trackIdentifier, removedIndex));
                    }
                }
                EffectChain.RemoveRange(effectDevices.Count, EffectChain.Count - effectDevices.Count);
            }
        }
    }
}
