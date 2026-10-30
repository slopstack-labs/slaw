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
    /// The starter arrangement scaffolded by <c>slaw new</c> and by the studio
    /// when it opens a file that does not exist yet. Hand-written, and therefore
    /// in violation of the Contributor Excellence Framework. We are working on it.
    /// </summary>
    public static SlawProject CreateStarterProject(string projectName)
    {
        var starterProject = new SlawProject
        {
            Name = projectName,
            Bpm = 124,
            Steps = 16,
            Scenes = { "Onboarding", "Q4 Drop" },
            Tracks =
            {
                StarterTrack("Synergy Kick", "kick", 0.95, 0,
                    "x . . . x . . . x . . . x . . .",
                    "x . . . x . . . x . . . x . x ."),
                StarterTrack("Stakeholder Clap", "clap", 0.55, 0.1,
                    null,
                    ". . . . x . . . . . . . x . . ."),
                StarterTrack("Agile Hats", "hat", 0.35, -0.3,
                    ". . x . . . x . . . x . . . x x",
                    "x x x . x x x . x x x . x x x x",
                    new EffectDevice { Type = "highpass", Parameters = { ["cutoff"] = 3000 } }),
                StarterTrack("Bottom-Line Bass", "saw", 0.45, 0,
                    "A1 _ . A1 . . A2 . F1 _ . F1 . . G1 G2",
                    "A1 _ . A1 . . A2 . F1 _ . F1 . . G1 G2",
                    new EffectDevice { Type = "lowpass", Parameters = { ["cutoff"] = 900, ["resonance"] = 0.45 } },
                    new EffectDevice { Type = "vibe", Parameters = { ["synergy"] = 0.4 } }),
                StarterTrack("Thought Leader Chords", "triangle", 0.3, -0.2,
                    "A3+C4+E4 _ _ _ . . . . F3+A3+C4 _ _ _ G3+B3+D4 _ _ _",
                    "A3+C4+E4 _ . A3+C4+E4 . . F3+A3+C4 _ . F3+A3+C4 . . G3+B3+D4 _ G3+B3+D4 .",
                    new EffectDevice { Type = "reverb", Parameters = { ["size"] = 0.8, ["mix"] = 0.35 } }),
                StarterTrack("Visionary Pluck", "pluck", 0.5, 0.35,
                    null,
                    "A4 . C5 . E5 . . A4 . . C5 . G4 . E4 .",
                    new EffectDevice { Type = "delay", Parameters = { ["steps"] = 3, ["feedback"] = 0.4, ["mix"] = 0.3 } }),
            },
            Arrangement =
            {
                new ArrangementSection { Scene = 0, Repeats = 2 },
                new ArrangementSection { Scene = 1, Repeats = 4 },
                new ArrangementSection { Scene = 0, Repeats = 1 },
            },
            Master = new MasterChainParameters { DelayMix = 0.1 },
            AiProvenance = "none (legacy human authorship)",
        };
        ProjectAlignmentRemediator.Remediate(starterProject);
        return starterProject;
    }

    private static SlawTrack StarterTrack(
        string trackName,
        string instrumentName,
        double trackGain,
        double trackPan,
        string? onboardingPattern,
        string? dropPattern,
        params EffectDevice[] effectDevices) => new()
        {
            Name = trackName,
            Instrument = instrumentName,
            Gain = trackGain,
            Pan = trackPan,
            Effects = effectDevices.ToList(),
            Clips =
            {
                onboardingPattern is null ? null : new SlawClip { Name = "Onboarding", Pattern = Steps(onboardingPattern) },
                dropPattern is null ? null : new SlawClip { Name = "Drop", Pattern = Steps(dropPattern) },
            },
        };

    private static List<string> Steps(string spaceSeparatedStepNotation) =>
        spaceSeparatedStepNotation.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
}
