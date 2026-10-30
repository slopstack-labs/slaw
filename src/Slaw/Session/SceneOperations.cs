namespace SlopstackLabs.Slaw.Session;

/// <summary>Scene-level edits shared by the studio, MCP tools and patches.</summary>
internal static class SceneOperations
{
    /// <summary>
    /// Deletes a session scene and every track's clip slot in it, so later scenes move up one index.
    /// Arrangement clips are copies and keep playing. The last scene cannot be deleted.
    /// Returns false (and changes nothing) for an invalid index or the last scene.
    /// </summary>
    public static bool RemoveScene(SlawProject project, int sceneIndex)
    {
        if (project.Scenes.Count <= 1 || sceneIndex < 0 || sceneIndex >= project.Scenes.Count)
        {
            return false;
        }
        project.Scenes.RemoveAt(sceneIndex);
        foreach (var track in project.Tracks)
        {
            if (sceneIndex < track.Clips.Count)
            {
                track.Clips.RemoveAt(sceneIndex);
            }
        }
        return true;
    }
}
