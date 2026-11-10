using System.Globalization;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The Groovebox's pattern tools in the command palette. They act on the selected clip, from
/// any view: an arrangement clip if one is selected, otherwise the selected track's clip in the
/// selected scene, which an empty slot gets on the spot.
/// </summary>
internal sealed partial class StudioWindow
{
    private static readonly int[] PaletteEuclideanHitCounts = { 3, 4, 5, 7 };

    private readonly Random paletteDice = new();

    private void AddGrooveboxPaletteEntries(List<CommandPaletteEntry> entries)
    {
        void AddTool(string title, Func<SlawTrack, SlawClip, bool, string?> patternOperation) =>
            entries.Add(new CommandPaletteEntry($"Groovebox: {title}", null, () => RunGrooveboxTool(patternOperation)));

        AddTool("Shift Selected Pattern One Step Earlier", (track, clip, _) => GrooveboxPatternOperations.Rotate(track, clip, -1));
        AddTool("Shift Selected Pattern One Step Later", (track, clip, _) => GrooveboxPatternOperations.Rotate(track, clip, 1));
        foreach (var hitCount in PaletteEuclideanHitCounts)
        {
            AddTool(string.Create(CultureInfo.InvariantCulture, $"Euclidean Rhythm, {hitCount} Hits"),
                (track, clip, _) => GrooveboxPatternOperations.EuclideanFill(track, clip, hitCount));
        }
        AddTool("Roll the Dice on Selected Pattern", (track, clip, _) => GrooveboxPatternOperations.Randomize(track, clip, 0.4, paletteDice));
        AddTool("Double Selected Pattern Length", (track, clip, sessionClip) =>
            clip.Pattern.Count * 2 > ProjectAlignmentRemediator.MaximumStepCount ? null : GrooveboxPatternOperations.SetLength(track, clip, clip.Pattern.Count * 2, sessionClip));
        AddTool("Halve Selected Pattern Length", (track, clip, sessionClip) =>
            clip.Pattern.Count < 2 ? null : GrooveboxPatternOperations.SetLength(track, clip, clip.Pattern.Count / 2, sessionClip));
        AddTool("Clear Selected Pattern", (track, clip, _) => GrooveboxPatternOperations.Clear(track, clip));
    }

    private void RunGrooveboxTool(Func<SlawTrack, SlawClip, bool, string?> patternOperation)
    {
        if (document.SelectedTrack is null)
        {
            SetStatus(StatusSeverity.Warning, "select a track first: the groovebox tools work on the selected clip.");
            return;
        }
        if (grooveboxView.ApplyToSelection(patternOperation) is null)
        {
            SetStatus(StatusSeverity.Informational, "nothing changed: the selected pattern already looks like that.");
        }
    }
}
