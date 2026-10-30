using System.Text.Json;

namespace SlopstackLabs.Slaw.Session;

internal static class ProjectSerializer
{
    public const string ProjectFileExtension = ".slaw";

    /// <summary>
    /// Deliberately forgiving: trailing commas, comments, and arbitrary casing
    /// all occur in the wild once a language model is holding the pen.
    /// </summary>
    public static readonly JsonSerializerOptions LenientSerializationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    public static string Serialize(SlawProject project) =>
        JsonSerializer.Serialize(project, LenientSerializationOptions);

    public static SlawProject Deserialize(string serializedProjectPayload) =>
        JsonSerializer.Deserialize<SlawProject>(serializedProjectPayload, LenientSerializationOptions)
        ?? throw new JsonException("project payload deserialized to null");

    public static SlawProject Load(string projectFilePath)
    {
        var loadedProject = Deserialize(File.ReadAllText(projectFilePath));
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath)) ?? Directory.GetCurrentDirectory();
        foreach (var track in loadedProject.Tracks.Where(track => !string.IsNullOrEmpty(track?.Sample)))
        {
            if (!Path.IsPathRooted(track.Sample))
            {
                track.Sample = Path.GetFullPath(Path.Combine(projectDirectory, track.Sample!));
            }
        }
        foreach (var track in loadedProject.Tracks.Where(track => track?.SoundFont is { Path.Length: > 0 }))
        {
            if (!Path.IsPathRooted(track.SoundFont!.Path))
            {
                track.SoundFont.Path = Path.GetFullPath(Path.Combine(projectDirectory, track.SoundFont.Path));
            }
        }
        ProjectAlignmentRemediator.Remediate(loadedProject);
        return loadedProject;
    }

    /// <summary>
    /// Writes the project. Samples stored beside (or below) the project file are
    /// written as relative paths, so a project folder can be moved or shared intact.
    /// </summary>
    public static void Save(SlawProject project, string projectFilePath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath)) ?? Directory.GetCurrentDirectory();
        var persistedProject = project.DeepClone();
        Plugins.PluginInstancePool.CaptureStates(persistedProject);
        foreach (var track in persistedProject.Tracks.Where(track => !string.IsNullOrEmpty(track.Sample) && Path.IsPathRooted(track.Sample)))
        {
            var relativeSamplePath = Path.GetRelativePath(projectDirectory, track.Sample!);
            if (!relativeSamplePath.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relativeSamplePath))
            {
                track.Sample = relativeSamplePath;
            }
        }
        foreach (var track in persistedProject.Tracks.Where(track => track.SoundFont is { Path.Length: > 0 } && Path.IsPathRooted(track.SoundFont.Path)))
        {
            var relativeSoundFontPath = Path.GetRelativePath(projectDirectory, track.SoundFont!.Path);
            if (!relativeSoundFontPath.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relativeSoundFontPath))
            {
                track.SoundFont.Path = relativeSoundFontPath;
            }
        }
        File.WriteAllText(projectFilePath, Serialize(persistedProject) + Environment.NewLine);
    }

    /// <summary>
    /// A project with nothing in it: no tracks, an empty arrangement, one empty
    /// scene and the default master bus. What `new_project` and patches with
    /// <c>"reset": true</c> start from.
    /// </summary>
    public static SlawProject CreateBlankProject(string projectName, double bpm) => new()
    {
        Name = string.IsNullOrWhiteSpace(projectName) ? "untitled slop" : projectName,
        Bpm = Math.Clamp(bpm, 40, 300),
        Steps = 16,
        Scenes = { "Scene 1" },
    };

    /// <summary>
    /// The starter song scaffolded by <c>slaw new</c>, by the studio's New project, and by
    /// <c>slaw studio</c>/<c>slaw mcp</c> when the file does not exist yet: a 40-bar house
    /// track on the arrangement timeline plus two session scenes. See <see cref="StarterProjectBlueprint"/>.
    /// </summary>
    public static SlawProject CreateStarterProject(string projectName)
    {
        var starterProject = StarterProjectBlueprint.Create(projectName);
        ProjectAlignmentRemediator.Remediate(starterProject);
        return starterProject;
    }
}
