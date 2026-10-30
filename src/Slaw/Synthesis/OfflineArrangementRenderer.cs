using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// Bounces a project's arrangement (its timeline) to a stereo buffer by running the same
/// <see cref="SequencerEngine"/> the studio plays live, just faster than real
/// time. This is the only deterministic component in slaw — retained for
/// regulatory compliance. Noise voices draw from a fixed seed, so the same
/// project always renders the same bytes, however unpredictable its author was.
/// </summary>
internal static class OfflineArrangementRenderer
{
    public const int DefaultSampleRate = 44100;
    private const double ReleaseTailSeconds = 1.5;
    private const int RenderBlockFrames = 4096;

    /// <summary>
    /// Renders the arrangement from its first step to the end of its last clip. A project
    /// that still carries a legacy scene sequence (and nothing on its timeline) is flattened
    /// first, on a copy, so it renders exactly as it always did.
    /// </summary>
    public static StereoBuffer Render(SlawProject project, int sampleRate = DefaultSampleRate) =>
        Render(project, sampleRate, out _);

    /// <summary>As <see cref="Render(SlawProject, int)"/>, also saying how hard hearing protection had to work.</summary>
    public static StereoBuffer Render(SlawProject project, int sampleRate, out HearingProtectionReport hearingProtection)
    {
        if (project.Timeline.Count == 0 && project.Arrangement.Count > 0)
        {
            project = project.DeepClone();
            FreeTimeline.MigrateLegacyArrangement(project);
        }
        return RenderWith(project, sampleRate, FreeTimeline.LengthInSteps(project), bounceEngine => bounceEngine.PlayTimeline(loop: false), out hearingProtection);
    }

    private static StereoBuffer RenderWith(SlawProject project, int sampleRate, long lengthInSteps, Action<SequencerEngine> startTransport, out HearingProtectionReport hearingProtection)
    {
        // Bounces must not skip a sample just because it was still decoding.
        foreach (var samplerTrack in project.Tracks.Where(track => track.Instrument == "sampler" && !string.IsNullOrEmpty(track.Sample)))
        {
            try
            {
                SampleEditProcessor.LoadProcessed(samplerTrack.Sample!, samplerTrack.SampleEditing, sampleRate);
            }
            catch (Exception sampleRemediationOpportunity) when (sampleRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A missing sample renders as silence, which is at least on-brand.
            }
        }

        foreach (var zone in project.Tracks.Where(track => track.Instrument == "multisampler" && track.SampleZones is not null).SelectMany(track => track.SampleZones!))
        {
            try
            {
                SampleEditProcessor.LoadProcessed(zone.Sample, null, sampleRate);
            }
            catch (Exception sampleRemediationOpportunity) when (sampleRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // One missing recording leaves one silent key, and the bounce goes on.
            }
        }

        // SoundFonts too: a bounce waits for the parse instead of dropping the first notes.
        foreach (var soundFontTrack in project.Tracks.Where(track => track.Instrument == "soundfont"))
        {
            SoundFonts.SoundFontLibrary.LoadPreset(soundFontTrack.SoundFont);
        }

        // VST3 plugins load synchronously in a bounce: the first note waits for the synth rather than skipping it.
        var bounceEngine = new SequencerEngine(sampleRate, offlinePluginHosting: true);
        bounceEngine.PublishProject(project.DeepClone());
        startTransport(bounceEngine);

        var transportFrameCount = (long)Math.Ceiling(lengthInSteps * project.StepDurationSeconds * sampleRate);
        var totalFrameCount = (int)Math.Min(int.MaxValue / 2, transportFrameCount + (long)(ReleaseTailSeconds * sampleRate));
        var renderedBuffer = new StereoBuffer(totalFrameCount, sampleRate);

        try
        {
            for (var blockStartFrame = 0; blockStartFrame < totalFrameCount; blockStartFrame += RenderBlockFrames)
            {
                var blockFrameCount = Math.Min(RenderBlockFrames, totalFrameCount - blockStartFrame);
                bounceEngine.RenderBlock(
                    renderedBuffer.Left.AsSpan(blockStartFrame, blockFrameCount),
                    renderedBuffer.Right.AsSpan(blockStartFrame, blockFrameCount));
            }
        }
        finally
        {
            bounceEngine.ReleasePlugins();
        }

        var normalizationScale = MasterChainProcessor.ApplyHearingProtection(renderedBuffer);
        var (limitedFrames, deepestGainReduction) = bounceEngine.LimiterActivity;
        hearingProtection = new HearingProtectionReport(limitedFrames, 20 * Math.Log10(deepestGainReduction), 20 * Math.Log10(normalizationScale));
        return renderedBuffer;
    }

    /// <summary>Renders one scene looped <paramref name="loopCount"/> times, as if it were the whole song.</summary>
    public static StereoBuffer RenderScene(SlawProject project, int sceneIndex, int loopCount, int sampleRate = DefaultSampleRate)
    {
        var sceneProject = project.DeepClone();
        sceneProject.TimelineLoop = null;
        sceneProject.Timeline = FreeTimeline.PlaceScene(sceneProject, sceneIndex, startStep: 0, repeats: Math.Max(1, loopCount));
        return Render(sceneProject, sampleRate);
    }

    /// <summary>
    /// Renders one stem per track (each track soloed, through the master bus),
    /// skipping tracks that never play a note. Returns (track, buffer) pairs.
    /// </summary>
    public static IEnumerable<(SlawTrack Track, StereoBuffer Stem)> RenderStems(SlawProject project, Func<SlawProject, StereoBuffer> renderVariant)
    {
        foreach (var track in project.Tracks)
        {
            // Stems follow the arrangement, so a track counts if one of its arrangement clips plays a note.
            var playsAnything = project.Timeline
                .Where(timelineClip => timelineClip.TrackId == track.Id)
                .Any(timelineClip => timelineClip.Clip.Pattern.Any(step => NotationResolver.ResolveOrRest(step).Kind == StepEventKind.Trigger)) ||
                (project.Timeline.Count == 0 && track.Clips.OfType<SlawClip>().Any(clip =>
                    clip.Pattern.Any(step => NotationResolver.ResolveOrRest(step).Kind == StepEventKind.Trigger)));
            if (!playsAnything)
            {
                continue;
            }
            var stemProject = project.DeepClone();
            foreach (var stemTrack in stemProject.Tracks)
            {
                stemTrack.Solo = stemTrack.Id == track.Id;
                stemTrack.Muted = stemTrack.Id != track.Id;
            }
            yield return (track, renderVariant(stemProject));
        }
    }
}
