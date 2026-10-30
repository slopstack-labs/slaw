namespace SlopstackLabs.Slaw.Session;

/// <summary>Whole-track operations that touch both the session and the arrangement.</summary>
internal static class TrackOperations
{
    /// <summary>
    /// Inserts a copy of <paramref name="sourceTrack"/> right below it: instrument, devices,
    /// mixer, session clips and its arrangement clips, with a fresh track id and fresh
    /// timeline clip ids. Returns the copy. The caller checks the track budget.
    /// </summary>
    public static SlawTrack Duplicate(SlawProject project, SlawTrack sourceTrack)
    {
        var duplicatedTrack = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(new SlawProject { Tracks = { sourceTrack } })).Tracks[0];
        duplicatedTrack.Id = SlawTrack.NewIdentifier();
        duplicatedTrack.Name = $"{sourceTrack.Name} (copy)";
        duplicatedTrack.AiLocked = false;
        duplicatedTrack.Solo = false;
        project.Tracks.Insert(project.Tracks.IndexOf(sourceTrack) + 1, duplicatedTrack);
        project.Timeline.AddRange(project.Timeline
            .Where(timelineClip => timelineClip.TrackId == sourceTrack.Id)
            .ToList()
            .Select(timelineClip => FreeTimeline.Copy(timelineClip, timelineClip.StartStep, duplicatedTrack.Id)));
        return duplicatedTrack;
    }
}
