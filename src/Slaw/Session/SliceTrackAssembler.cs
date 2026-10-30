using System.Text.RegularExpressions;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Turns part of a sampler's audio into a track of its own that plays that part
/// across the keyboard. The source file and the source track stay as they were:
/// the cut is one more non-destructive trim, so the new track is an ordinary
/// sampler and needs nothing the project format did not already have.
/// </summary>
internal static class SliceTrackAssembler
{
    private static readonly Regex SliceSuffix = new(@"( · slice \d+)+$", RegexOptions.CultureInvariant);

    /// <param name="startSeconds">Start of the region, in the source's edited audio (what its waveform shows).</param>
    /// <param name="endSeconds">End of the region, exclusive, in the same audio.</param>
    public static SlawTrack CreatePitchedTrack(SlawProject project, SlawTrack source, double startSeconds, double endSeconds, int sceneIndex)
    {
        var sliceTrack = AudioTakeAssembler.CreateTakeTrack(project, source);
        var originalName = SliceSuffix.Replace(source.Name, string.Empty);
        var sliceNumber = project.Tracks.Count(track => track.Name != originalName && SliceSuffix.Replace(track.Name, string.Empty) == originalName) + 1;
        sliceTrack.Name = $"{originalName} · slice {sliceNumber}";
        sliceTrack.Sample = source.Sample;

        // The source's start and end markers described the whole sample, not this piece of it.
        sliceTrack.InstrumentParameters = source.InstrumentParameters
            .Where(parameter => parameter.Key is not ("start" or "end"))
            .ToDictionary(parameter => parameter.Key, parameter => parameter.Value);

        // Operations run in order, so a trim added last cuts exactly the audio the waveform showed.
        var operations = source.SampleEditing?.Operations
            .Select(operation => new SampleEditOperation { Type = operation.Type, StartSeconds = operation.StartSeconds, EndSeconds = operation.EndSeconds, Amount = operation.Amount })
            .ToList() ?? new List<SampleEditOperation>();
        operations.Add(new SampleEditOperation { Type = "trim", StartSeconds = startSeconds, EndSeconds = endSeconds });
        sliceTrack.SampleEditing = new SampleEditSettings { PlaybackMode = SampleEditSettings.PitchedMode, Operations = operations };

        sliceTrack.Clips[sceneIndex] = SlawClip.Empty(project.Scenes[sceneIndex], project.SceneLengthInSteps(sceneIndex));
        return sliceTrack;
    }

    /// <summary>The slice a position falls in: the same boundaries the sampler plays, from the start of the audio to its end.</summary>
    public static (double Start, double End)? SliceAt(IEnumerable<double> slicePointsSeconds, double durationSeconds, double positionSeconds)
    {
        if (durationSeconds <= 0 || positionSeconds < 0 || positionSeconds > durationSeconds)
        {
            return null;
        }
        var boundaries = slicePointsSeconds
            .Where(slicePoint => slicePoint > 0 && slicePoint < durationSeconds)
            .Distinct()
            .Order()
            .Prepend(0)
            .Append(durationSeconds)
            .ToList();
        for (var boundaryIndex = 0; boundaryIndex < boundaries.Count - 1; boundaryIndex++)
        {
            if (positionSeconds < boundaries[boundaryIndex + 1])
            {
                return (boundaries[boundaryIndex], boundaries[boundaryIndex + 1]);
            }
        }
        return (boundaries[^2], boundaries[^1]);
    }
}
