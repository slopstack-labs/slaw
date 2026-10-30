using System.Globalization;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Swing and timing humanize: the two ways slaw lets a groove stop sounding like a spreadsheet.
///
/// Swing is MPC-style, in percent of an eighth note: of each pair of sixteenths, the first lasts
/// <c>swing</c>% of the eighth and the second (every odd step: 1, 3, 5 … counted on the song's grid)
/// starts that much later. In steps, an odd step starts <see cref="SwingDelaySteps"/> = (swing - 50) / 50
/// late: 50 is straight, 66.67 is a triplet shuffle (1/3 step late), 75 a dotted-sixteenth limp (1/2 step).
/// Timing humanize is per track, in milliseconds: every step the track strikes starts a seeded random
/// 0..timing ms late (never early), so offline renders stay deterministic.
/// </summary>
internal static class GrooveTiming
{
    public const double StraightSwing = 50;
    public const double MaximumSwing = 75;
    public const double TripletSwing = 200.0 / 3;
    public const double MaximumTimingMilliseconds = 50;

    /// <summary>The swing a track plays with: its own override, or the project's.</summary>
    public static double EffectiveSwing(SlawProject project, SlawTrack track) => track.Swing ?? project.Swing;

    /// <summary>How late an odd sixteenth starts, in steps: (swing - 50) / 50, from 0 (straight) to 0.5 (swing 75).</summary>
    public static double SwingDelaySteps(double swingPercent) =>
        double.IsNaN(swingPercent) ? 0 : (Math.Clamp(swingPercent, StraightSwing, MaximumSwing) - StraightSwing) / 50;

    /// <summary>True for the second sixteenth of each eighth-note pair (odd grid steps), the ones swing delays.</summary>
    public static bool IsSwungStep(long gridStep) => (gridStep & 1) == 1;

    /// <summary>"swing 66 (odd sixteenths 0.32 step late)", or "straight" for 50.</summary>
    public static string DescribeSwing(double swingPercent) =>
        swingPercent <= StraightSwing
            ? "straight"
            : string.Create(CultureInfo.InvariantCulture, $"swing {swingPercent:0.##} (odd sixteenths {SwingDelaySteps(swingPercent):0.###} step late)");

    /// <summary>"swing 66" / "swing 50 (straight)": compact form for listings and change summaries.</summary>
    public static string FormatSwing(double swingPercent) =>
        string.Create(CultureInfo.InvariantCulture, $"{swingPercent:0.##}{(swingPercent <= StraightSwing ? " (straight)" : string.Empty)}");

    /// <summary>
    /// Clamps a swing value into 50..75 and says so in the ledger. Agents sometimes write a fraction
    /// (0.6) or a 0..100 "amount"; anything below 50 is clamped to straight with a note on the units.
    /// </summary>
    public static double RemediateSwing(double swingPercent, string label, List<string> remediationLedger)
    {
        if (double.IsNaN(swingPercent) || double.IsInfinity(swingPercent))
        {
            remediationLedger.Add($"{label}: {swingPercent} replaced with 50 (straight)");
            return StraightSwing;
        }
        if (swingPercent < StraightSwing)
        {
            remediationLedger.Add(string.Create(CultureInfo.InvariantCulture,
                $"{label}: {swingPercent} clamped to 50 (straight); swing is a percentage from 50 (straight) to 75, 66 is a triplet shuffle"));
            return StraightSwing;
        }
        if (swingPercent > MaximumSwing)
        {
            remediationLedger.Add(string.Create(CultureInfo.InvariantCulture, $"{label}: {swingPercent} clamped to 75 (the maximum swing)"));
            return MaximumSwing;
        }
        return swingPercent;
    }
}
