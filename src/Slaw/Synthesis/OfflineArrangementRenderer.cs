using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Synthesis;

/// <summary>
/// Bounces a project's arrangement to a stereo buffer by running the same
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

    public static StereoBuffer Render(SlawProject project, int sampleRate = DefaultSampleRate) =>
        RenderWith(project, sampleRate, project.ArrangementLengthInSteps(), bounceEngine => bounceEngine.PlayArrangement(loop: false));

    /// <summary>Renders the free timeline from its first step to the end of its last clip.</summary>
    public static StereoBuffer RenderTimeline(SlawProject project, int sampleRate = DefaultSampleRate) =>
        RenderWith(project, sampleRate, FreeTimeline.LengthInSteps(project), bounceEngine => bounceEngine.PlayTimeline(loop: false));

    private static StereoBuffer RenderWith(SlawProject project, int sampleRate, long lengthInSteps, Action<SequencerEngine> startTransport)
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

        MasterChainProcessor.ApplyHearingProtection(renderedBuffer);
        return renderedBuffer;
    }

    /// <summary>Renders one scene looped <paramref name="loopCount"/> times, as if it were the whole song.</summary>
    public static StereoBuffer RenderScene(SlawProject project, int sceneIndex, int loopCount, int sampleRate = DefaultSampleRate)
    {
        var sceneProject = project.DeepClone();
        sceneProject.Arrangement = new List<ArrangementSection> { new() { Scene = sceneIndex, Repeats = Math.Max(1, loopCount) } };
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
            var playsAnything = track.Clips.OfType<SlawClip>().Any(clip =>
                clip.Pattern.Any(step => NotationResolver.ResolveOrRest(step).Kind == StepEventKind.Trigger));
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
