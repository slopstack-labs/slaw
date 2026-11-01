namespace SlopstackLabs.Slaw.Surfaces;

/// <summary>
/// Which note a pad plays: numbered from the bottom left, rising to the right and then
/// upward, as on an MPC. Chromatic suits sliced samples and drum kits (slice 1 is C4, slice 2
/// C#4), and the scales keep a melody in key. Mirrors integrations/shared/keypad-logic.js.
/// </summary>
internal static class NotePadLayout
{
    private static readonly string[] ScaleNames = { "chromatic", "major", "minor" };

    private static readonly int[] MajorDegrees = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly int[] MinorDegrees = { 0, 2, 3, 5, 7, 8, 10 };

    /// <summary>The MIDI note for pad number <paramref name="padIndex"/>, or null above the top of MIDI.</summary>
    public static int? NoteAt(int padIndex, int baseNote, string scaleName)
    {
        var degrees = scaleName switch
        {
            "major" => MajorDegrees,
            "minor" => MinorDegrees,
            _ => null,
        };
        var midiNote = degrees is null
            ? baseNote + padIndex
            : baseNote + 12 * (padIndex / degrees.Length) + degrees[padIndex % degrees.Length];
        return midiNote <= 127 ? midiNote : null;
    }

    public static string NextScale(string scaleName) =>
        ScaleNames[(Array.IndexOf(ScaleNames, scaleName) + 1) % ScaleNames.Length];
}
