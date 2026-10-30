namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// Which recordings a note plays on a "multisampler" track. Pure, so the engine, the
/// audition path and the tests all agree: every zone covering the key and velocity
/// sounds (that is what layers are), except that a round-robin group contributes one
/// member per note. The pick comes from the caller's seeded random, so bounces of the
/// same project stay the same bytes.
/// </summary>
internal static class SampleZoneMap
{
    public static int MidiVelocity(double noteVelocity) =>
        Math.Clamp((int)Math.Round(NotationResolver.ClampVelocity(noteVelocity) * 127), 1, 127);

    public static List<SampleZone> Select(IReadOnlyList<SampleZone>? zones, int midiNote, double noteVelocity, Random roundRobinPicker)
    {
        var selected = new List<SampleZone>();
        if (zones is null)
        {
            return selected;
        }
        var velocity = MidiVelocity(noteVelocity);
        List<(int Group, List<SampleZone> Members)>? roundRobinGroups = null;
        foreach (var zone in zones)
        {
            if (midiNote < zone.KeyLow || midiNote > zone.KeyHigh || velocity < zone.VelocityLow || velocity > zone.VelocityHigh)
            {
                continue;
            }
            if (zone.RoundRobinGroup is not { } group)
            {
                selected.Add(zone);
                continue;
            }
            roundRobinGroups ??= new List<(int, List<SampleZone>)>();
            var existingGroup = roundRobinGroups.FindIndex(candidate => candidate.Group == group);
            if (existingGroup >= 0)
            {
                roundRobinGroups[existingGroup].Members.Add(zone);
            }
            else
            {
                roundRobinGroups.Add((group, new List<SampleZone> { zone }));
            }
        }
        if (roundRobinGroups is not null)
        {
            foreach (var (_, members) in roundRobinGroups)
            {
                selected.Add(members[roundRobinPicker.Next(members.Count)]);
            }
        }
        return selected;
    }

    /// <summary>"51 recordings, C2 to G9", for device captions and listings.</summary>
    public static string Describe(IReadOnlyList<SampleZone>? zones)
    {
        if (zones is not { Count: > 0 })
        {
            return "no recordings";
        }
        return $"{zones.Count} recording{(zones.Count == 1 ? string.Empty : "s")}, {NotationResolver.FormatMidiNote(zones.Min(zone => zone.KeyLow))} to {NotationResolver.FormatMidiNote(zones.Max(zone => zone.KeyHigh))}";
    }
}
