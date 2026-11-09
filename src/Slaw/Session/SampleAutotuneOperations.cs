using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Session;

/// <summary>A named retune time for the autotune sample edit.</summary>
internal sealed record AutotuneStyle(string Name, double RetuneMilliseconds, string Description);

/// <summary>
/// Adds the "autotune" edit to a sampler track (a loaded sample or a recorded take):
/// the choices the sample editor and the command palette offer, and the default key.
/// </summary>
internal static class SampleAutotuneOperations
{
    public static readonly IReadOnlyList<AutotuneStyle> Styles = new[]
    {
        new AutotuneStyle("Robot", 0, "snaps instantly: the hard, stepped effect"),
        new AutotuneStyle("Tight", 20, "corrects fast but keeps the slides between notes"),
        new AutotuneStyle("Natural", 60, "leaves the start of each note and most vibrato alone"),
        new AutotuneStyle("Gentle", 150, "only nudges long notes towards the key"),
    };

    /// <summary>"chromatic", then every major and minor key, as <see cref="MusicalKey.Parse"/> reads them.</summary>
    public static readonly IReadOnlyList<string> KeyChoices =
        new[] { PitchCorrector.ChromaticKey }
            .Concat(Enumerable.Range(0, 12).Select(tonic => new MusicalKey(tonic, IsMinor: false).Name))
            .Concat(Enumerable.Range(0, 12).Select(tonic => new MusicalKey(tonic, IsMinor: true).Name))
            .ToList();

    /// <summary>The key the song's written notes fit, or "chromatic" when there are none to read.</summary>
    public static string DefaultKey(SlawProject project) =>
        NoteKeyAnalysis.Analyze(project).Key is { } songKey ? songKey.Name : PitchCorrector.ChromaticKey;

    /// <summary>
    /// Appends an autotune edit over [<paramref name="startSeconds"/>, <paramref name="endSeconds"/>)
    /// of the edited sample. Positions do not move: autotune keeps the length.
    /// </summary>
    public static SampleEditOperation Add(SlawTrack track, string key, double retuneMilliseconds, double startSeconds, double endSeconds)
    {
        var autotune = new SampleEditOperation
        {
            Type = "autotune",
            StartSeconds = startSeconds,
            EndSeconds = endSeconds,
            Amount = Math.Clamp(retuneMilliseconds, 0, PitchCorrector.MaximumRetuneMilliseconds),
            Key = PitchCorrector.IsValidKey(key) && !string.IsNullOrWhiteSpace(key) ? key.Trim() : PitchCorrector.ChromaticKey,
        };
        (track.SampleEditing ??= new SampleEditSettings()).Operations.Add(autotune);
        return autotune;
    }
}
