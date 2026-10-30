using System.Globalization;

namespace SlopstackLabs.Slaw.Session;

/// <summary>
/// The song every new user meets first (A minor, 124 BPM): a 40-bar house
/// track laid out on the arrangement timeline (intro, groove, build, drop, outro) with
/// the same material in two session scenes to jam with. Built-in instruments and effects
/// only, so it sounds identical on a fresh install with no samples, fonts or plugins.
/// Hand-written, and therefore in violation of the Contributor Excellence Framework.
/// Legal has been informed and is tapping its foot in time.
/// </summary>
internal static class StarterProjectBlueprint
{
    public const double StarterBeatsPerMinute = 124;

    private const int StepsPerBar = FreeTimeline.StepsPerBar;
    private const int PhraseSteps = 4 * StepsPerBar;

    // ── The progression: Am7 | Fmaj7 | C/E | G6/D, one chord per bar ────────
    // Close voicings whose lowest voice walks down G3 F3 E3 D3 while E4 stays on top,
    // so nothing jumps and no two notes sit a semitone apart.

    private static readonly string[] AMinorSeventhVoicing = { "G3", "A3", "C4", "E4" };
    private static readonly string[] FMajorSeventhVoicing = { "F3", "A3", "C4", "E4" };
    private static readonly string[] CMajorOverEVoicing = { "E3", "G3", "C4", "E4" };
    private static readonly string[] GSixthOverDVoicing = { "D3", "G3", "B3", "E4" };

    private static readonly string[][] ProgressionVoicings = { AMinorSeventhVoicing, FMajorSeventhVoicing, CMajorOverEVoicing, GSixthOverDVoicing };

    /// <summary>The bass plays the root of each chord: (root, root an octave up).</summary>
    private static readonly (string Root, string OctaveUp)[] ProgressionRoots = { ("A1", "A2"), ("F1", "F2"), ("C2", "C3"), ("G1", "G2") };

    /// <summary>The hook: chord tones only, one row per chord, struck on steps 0, 3, 6, 8, 10 and 13.</summary>
    private static readonly string[][] HookNotesPerChord =
    {
        new[] { "A4", "C5", "E5", "G5", "E5", "C5" },
        new[] { "A4", "C5", "E5", "F5", "E5", "C5" },
        new[] { "G4", "C5", "E5", "G5", "E5", "C5" },
        new[] { "G4", "B4", "D5", "G5", "E5", "D5" },
    };

    // ── Arrangement landmarks (0-based steps, 16 per bar) ───────────────────

    private const int IntroStartStep = 0;              // bars 1–8
    private const int GrooveStartStep = 8 * StepsPerBar;   // bars 9–16
    private const int BuildStartStep = 16 * StepsPerBar;   // bars 17–20
    private const int DropStartStep = 20 * StepsPerBar;    // bars 21–36
    private const int OutroStartStep = 36 * StepsPerBar;   // bars 37–40
    private const int SongEndStep = 40 * StepsPerBar;

    public static SlawProject Create(string projectName)
    {
        var synergyKick = Track("Synergy Kick", "kick", 0.7, 0);
        var stakeholderClap = Track("Stakeholder Clap", "clap", 1.2, 0.05,
            Effect("reverb", ("size", 0.35), ("mix", 0.18), ("damping", 0.5)));
        var agileHats = Track("Agile Hats", "hat", 1.2, -0.25,
            Effect("highpass", ("cutoff", 2000)));
        var bottomLineBass = Track("Bottom-Line Bass", "saw", 1.1, 0,
            Effect("lowpass", ("cutoff", 900), ("resonance", 0.35)),
            Effect("vibe", ("synergy", 0.3)),
            Effect(SidechainRouting.DuckEffectType, ("amount", 0.6), ("threshold", -24), ("attack", 1), ("release", 140)));
        var thoughtLeaderChords = Track("Thought Leader Chords", "triangle", 0.9, -0.1,
            Effect("lowpass", ("cutoff", 9000), ("resonance", 0.15)),
            Effect("reverb", ("size", 0.75), ("mix", 0.3), ("width", 0.9)),
            Effect(SidechainRouting.DuckEffectType, ("amount", 0.55), ("threshold", -24), ("attack", 2), ("release", 170)));
        var visionaryPluck = Track("Visionary Pluck", "pluck", 1.4, 0.3,
            Effect("delay", ("steps", 3), ("feedback", 0.35), ("mix", 0.25), ("pingpong", 0.6)),
            Effect("reverb", ("size", 0.5), ("mix", 0.2)),
            Effect(SidechainRouting.DuckEffectType, ("amount", 0.25), ("threshold", -24), ("attack", 2), ("release", 150)));
        var escalationSnare = Track("Escalation Snare", "snare", 1.2, 0,
            Effect("highpass", ("cutoff", 300)),
            Effect("reverb", ("size", 0.6), ("mix", 0.3)));
        var runwayRiser = Track("Runway Riser", "noise", 1.0, 0,
            Effect("highpass", ("cutoff", 200), ("resonance", 0.35)),
            Effect("reverb", ("size", 0.7), ("mix", 0.3)));

        // Keyed by the kick: bass, chords and hook make room on every beat.
        foreach (var duckedTrack in new[] { bottomLineBass, thoughtLeaderChords, visionaryPluck })
        {
            duckedTrack.Effects[^1].SidechainSource = synergyKick.Id;
        }

        SetInstrument(bottomLineBass, ("attack", 2), ("decay", 180), ("sustain", 0.55), ("release", 50));
        SetInstrument(thoughtLeaderChords, ("attack", 3), ("decay", 260), ("sustain", 0.3), ("release", 180));
        SetInstrument(visionaryPluck, ("length", 1200), ("brightness", 0.55));
        SetInstrument(runwayRiser, ("attack", 2000), ("decay", 5), ("sustain", 1), ("release", 300));

        // ── Patterns ────────────────────────────────────────────────────────
        var fourOnTheFloor = Bars("x . . . x . . . x . . . x . . .");
        var fourOnTheFloorWithFill = Bars(
            "x . . . x . . . x . . . x . . .",
            "x . . . x . . . x . . . x . . .",
            "x . . . x . . . x . . . x . . .",
            "x . . . x . . . x . . . x . x@0.5 .");
        var backbeatClap = Bars(". . . . x . . . . . . . x . . .");
        var offbeatHats = Bars(". . x . . . x . . . x . . . x x@0.4");
        var grooveHats = Bars("x@0.4 x@0.3 x x@0.3 x@0.4 x@0.3 x x@0.3 x@0.4 x@0.3 x x@0.3 x@0.4 x@0.3 x x@0.45");
        var dropHats = Bars("x@0.5 x@0.35 x x@0.35 x@0.5 x@0.35 x x@0.4 x@0.5 x@0.35 x x@0.35 x@0.5 x@0.4 x x@0.55");
        var grooveBass = Bars(ProgressionRoots.Select(chordRoot =>
            $". . {chordRoot.Root}@0.9 _ . . {chordRoot.Root}@0.8 _ . . {chordRoot.Root}@0.9 _ . . {chordRoot.Root}@0.8 _").ToArray());
        var dropBass = Bars(ProgressionRoots.Select(chordRoot =>
            $". . {chordRoot.Root} _ . . {chordRoot.Root}@0.85 _ . . {chordRoot.Root}@0.95 _ . {chordRoot.OctaveUp}@0.6 {chordRoot.Root}@0.85 _").ToArray());
        var chordPads = Bars(ProgressionVoicings.Select(chordVoicing =>
            $"{Chord(chordVoicing, 0.8)} {Sustain(15)}").ToArray());
        var offbeatStabs = Bars(ProgressionVoicings.Select(chordVoicing =>
            $". . {Chord(chordVoicing, 0.9)} _ . . {Chord(chordVoicing, 0.7)} _ . . {Chord(chordVoicing, 0.85)} _ . . {Chord(chordVoicing, 0.7)} _").ToArray());
        var hook = Bars(HookNotesPerChord.Select(hookNotes =>
            $"{hookNotes[0]} . . {hookNotes[1]}@0.8 . . {hookNotes[2]}@0.9 . {hookNotes[3]}@0.85 . {hookNotes[4]}@0.75 . . {hookNotes[5]}@0.7 . .").ToArray());
        var snareRoll = Bars(
            "x@0.4 . . . x@0.45 . . . x@0.5 . . . x@0.5 . . .",
            "x@0.5 . x@0.5 . x@0.55 . x@0.55 . x@0.6 . x@0.6 . x@0.6 . x@0.6 .",
            "x@0.6 . x@0.6 . x@0.65 . x@0.65 . x@0.7 x@0.7 x@0.72 x@0.74 x@0.76 x@0.78 x@0.8 x@0.82",
            "x@0.8 x@0.81 x@0.82 x@0.83 x@0.84 x@0.85 x@0.86 x@0.87 x@0.9 x@0.9 x@0.9 x@0.9 . . . .");
        var snareBackbeat = Bars(". . . . x@0.45 . . . . . . . x@0.45 . . .");
        var riserSwell = Bars($"C4 {Sustain(63)}");
        var shortSwell = Bars($"C4 {Sustain(31)}");

        // Pads: the chords' own envelope is a short stab; these clips open it up per note.
        var padShape = Envelopes(
            ("inst.attack", new[] { (0.0, 250.0) }),
            ("inst.sustain", new[] { (0.0, 0.75) }),
            ("inst.release", new[] { (0.0, 400.0) }));

        // ── Session: two scenes to jam with ─────────────────────────────────
        synergyKick.Clips.AddRange(new[] { SessionClip("Onboarding", fourOnTheFloor), SessionClip("Drop", fourOnTheFloorWithFill) });
        stakeholderClap.Clips.AddRange(new[] { SessionClip("Onboarding", backbeatClap), SessionClip("Drop", backbeatClap) });
        agileHats.Clips.AddRange(new[] { SessionClip("Onboarding", grooveHats), SessionClip("Drop", dropHats) });
        bottomLineBass.Clips.AddRange(new[] { SessionClip("Onboarding", grooveBass), SessionClip("Drop", dropBass) });
        thoughtLeaderChords.Clips.AddRange(new[] { SessionClip("Onboarding", chordPads, padShape), SessionClip("Drop", offbeatStabs) });
        visionaryPluck.Clips.AddRange(new[] { null, SessionClip("Drop", hook) });
        escalationSnare.Clips.AddRange(new[] { null, SessionClip("Drop", snareBackbeat) });
        runwayRiser.Clips.AddRange(new SlawClip?[] { null, null });

        // ── Arrangement: the song ───────────────────────────────────────────
        var timeline = new List<TimelineClip>
        {
            // Intro, bars 1–8: kick, offbeat hats and pads opening up; clap from bar 5, a short riser into bar 9.
            Place(synergyKick, IntroStartStep, 8, "Intro", fourOnTheFloor),
            Place(agileHats, IntroStartStep, 8, "Intro", offbeatHats),
            Place(stakeholderClap, IntroStartStep + PhraseSteps, 4, "Intro", backbeatClap),
            Place(runwayRiser, IntroStartStep + 6 * StepsPerBar, 2, "Intro riser", shortSwell,
                Envelopes(
                    ("fx.0.cutoff", new[] { (0.0, 300.0), (32.0, 4000.0) }),
                    ("gain", new[] { (0.0, 0.05), (28.0, 0.75), (31.0, 0.8), (32.0, 0.0) }))),
            Place(thoughtLeaderChords, IntroStartStep, 8, "Intro pads", chordPads,
                MergeEnvelopes(padShape, Envelopes(("fx.0.cutoff", new[] { (0.0, 250.0), (128.0, 2200.0) })))),

            // Groove, bars 9–16: bass and sixteenth hats join; a muffled hook teases from bar 13.
            Place(synergyKick, GrooveStartStep, 8, "Groove", fourOnTheFloor),
            Place(stakeholderClap, GrooveStartStep, 8, "Groove", backbeatClap),
            Place(agileHats, GrooveStartStep, 8, "Groove", grooveHats),
            Place(bottomLineBass, GrooveStartStep, 8, "Groove", grooveBass),
            Place(thoughtLeaderChords, GrooveStartStep, 8, "Groove pads", chordPads,
                MergeEnvelopes(padShape, Envelopes(("fx.0.cutoff", new[] { (0.0, 2200.0), (128.0, 4500.0) })))),
            Place(visionaryPluck, GrooveStartStep + PhraseSteps, 4, "Hook teaser", hook,
                Envelopes(("inst.cutoff", new[] { (0.0, 1200.0), (64.0, 2500.0) }))),

            // Build, bars 17–20: no kick or bass; stabs and hook behind a rising filter, a snare roll and a riser.
            Place(agileHats, BuildStartStep, 4, "Build", offbeatHats),
            Place(thoughtLeaderChords, BuildStartStep, 4, "Build stabs", offbeatStabs,
                Envelopes(("fx.0.cutoff", new[] { (0.0, 400.0), (64.0, 9000.0) }))),
            Place(visionaryPluck, BuildStartStep, 4, "Build hook", hook,
                Envelopes(("inst.cutoff", new[] { (0.0, 500.0), (64.0, 12000.0) }))),
            Place(escalationSnare, BuildStartStep, 4, "Snare roll", snareRoll,
                Envelopes(("inst.pitch", new[] { (0.0, 0.0), (64.0, 7.0) }))),
            Place(runwayRiser, BuildStartStep, 4, "Riser", riserSwell,
                Envelopes(
                    ("fx.0.cutoff", new[] { (0.0, 150.0), (64.0, 6000.0) }),
                    ("gain", new[] { (0.0, 0.08), (56.0, 0.8), (63.0, 1.0), (64.0, 0.0) }))),

            // Drop, bars 21–36: everything, offbeat stabs and the hook; a snare doubles the clap and a downlifter clears the air.
            Place(synergyKick, DropStartStep, 16, "Drop", fourOnTheFloorWithFill),
            Place(stakeholderClap, DropStartStep, 16, "Drop", backbeatClap),
            Place(agileHats, DropStartStep, 16, "Drop", dropHats),
            Place(bottomLineBass, DropStartStep, 16, "Drop", dropBass),
            Place(thoughtLeaderChords, DropStartStep, 16, "Drop stabs", offbeatStabs),
            Place(visionaryPluck, DropStartStep, 16, "Drop hook", hook),
            Place(escalationSnare, DropStartStep, 16, "Drop backbeat", snareBackbeat),
            Place(runwayRiser, DropStartStep, 2, "Downlifter", shortSwell,
                Envelopes(
                    ("inst.attack", new[] { (0.0, 5.0) }),
                    ("fx.0.cutoff", new[] { (0.0, 5000.0), (32.0, 150.0) }),
                    ("gain", new[] { (0.0, 1.0), (32.0, 0.0) }))),

            // Outro, bars 37–40: the groove again while the bass closes and the pads fade out.
            Place(synergyKick, OutroStartStep, 4, "Outro", fourOnTheFloor),
            Place(stakeholderClap, OutroStartStep, 4, "Outro", backbeatClap),
            Place(agileHats, OutroStartStep, 4, "Outro", offbeatHats),
            Place(bottomLineBass, OutroStartStep, 4, "Outro", grooveBass,
                Envelopes(("fx.0.cutoff", new[] { (0.0, 900.0), (64.0, 150.0) }))),
            Place(thoughtLeaderChords, OutroStartStep, 4, "Outro pads", chordPads,
                MergeEnvelopes(padShape, Envelopes(
                    ("fx.0.cutoff", new[] { (0.0, 4000.0), (64.0, 300.0) }),
                    ("gain", new[] { (0.0, thoughtLeaderChords.Gain), (64.0, 0.0) })))),
        };

        var starterProject = new SlawProject
        {
            Name = projectName,
            Bpm = StarterBeatsPerMinute,
            Steps = StepsPerBar,
            Scenes = { "Onboarding", "Q4 Drop" },
            Tracks = { synergyKick, stakeholderClap, agileHats, bottomLineBass, thoughtLeaderChords, visionaryPluck, escalationSnare, runwayRiser },
            Timeline = timeline,
            Master = new MasterChainParameters { Gain = 0.75, Drive = 1.0, LowpassHz = 18000, DelayMix = 0 },
            AiProvenance = "none (legacy human authorship)",
        };
        System.Diagnostics.Debug.Assert(FreeTimeline.LengthInSteps(starterProject) == SongEndStep, "the starter song should be exactly 40 bars");
        return starterProject;
    }

    // ── Construction helpers ────────────────────────────────────────────────

    private static SlawTrack Track(string trackName, string instrumentName, double trackGain, double trackPan, params EffectDevice[] effectDevices) => new()
    {
        Name = trackName,
        Instrument = instrumentName,
        Gain = trackGain,
        Pan = trackPan,
        Effects = effectDevices.ToList(),
    };

    private static EffectDevice Effect(string effectType, params (string Key, double Value)[] parameterValues) => new()
    {
        Type = effectType,
        Parameters = parameterValues.ToDictionary(parameterValue => parameterValue.Key, parameterValue => parameterValue.Value),
    };

    private static void SetInstrument(SlawTrack track, params (string Key, double Value)[] parameterValues)
    {
        foreach (var (parameterKey, parameterValue) in parameterValues)
        {
            track.InstrumentParameters[parameterKey] = parameterValue;
        }
    }

    /// <summary>Space-separated bars of step notation, concatenated into one pattern.</summary>
    private static List<string> Bars(params string[] barNotations) =>
        barNotations.SelectMany(barNotation => barNotation.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();

    /// <summary>A chord with the same velocity on every note (a suffix on the last note alone would only soften that one).</summary>
    private static string Chord(IEnumerable<string> chordVoicing, double noteVelocity) =>
        string.Join('+', chordVoicing.Select(chordNote => string.Create(CultureInfo.InvariantCulture, $"{chordNote}@{noteVelocity:0.##}")));

    private static string Sustain(int sustainedStepCount) =>
        string.Join(' ', Enumerable.Repeat(NotationResolver.SustainSymbol, sustainedStepCount));

    private static Dictionary<string, List<AutomationPoint>> Envelopes(params (string TargetKey, (double Step, double Value)[] Points)[] envelopeDefinitions) =>
        envelopeDefinitions.ToDictionary(
            envelopeDefinition => envelopeDefinition.TargetKey,
            envelopeDefinition => envelopeDefinition.Points.Select(point => new AutomationPoint { Step = point.Step, Value = point.Value }).ToList());

    private static Dictionary<string, List<AutomationPoint>> MergeEnvelopes(params Dictionary<string, List<AutomationPoint>>[] envelopeSets)
    {
        var mergedEnvelopes = new Dictionary<string, List<AutomationPoint>>();
        foreach (var (targetKey, points) in envelopeSets.SelectMany(envelopeSet => envelopeSet))
        {
            mergedEnvelopes[targetKey] = points.Select(point => new AutomationPoint { Step = point.Step, Value = point.Value }).ToList();
        }
        return mergedEnvelopes;
    }

    private static SlawClip SessionClip(string clipName, List<string> pattern, Dictionary<string, List<AutomationPoint>>? envelopes = null) => new()
    {
        Name = clipName,
        Pattern = pattern.ToList(),
        Automation = envelopes is null ? null : MergeEnvelopes(envelopes),
    };

    /// <summary>An arrangement clip on <paramref name="track"/> from <paramref name="startStep"/> for <paramref name="barCount"/> bars; the pattern loops to fill it.</summary>
    private static TimelineClip Place(SlawTrack track, int startStep, int barCount, string clipName, List<string> pattern, Dictionary<string, List<AutomationPoint>>? envelopes = null) => new()
    {
        TrackId = track.Id,
        StartStep = startStep,
        LengthSteps = barCount * StepsPerBar,
        Clip = SessionClip(clipName, pattern, envelopes),
    };
}
