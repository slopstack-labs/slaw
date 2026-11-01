# Project format

[← All documentation](README.md)

A `.slaw` project is plain JSON, currently `slaw-project/v2`: `scenes[]`, `tracks[]` (`id`, `name`, `instrument`, `instrumentParameters`, `effects[]` (each optionally keyed by a `sidechainSource` track id), `clips[]`, `aiLocked`, plus gain, pan, mute, and the optional groove: `swing` (50–75, overriding the project's; left out to follow it) and `timing` (0–50 ms)), `swing` for the whole project (50–75; left out while straight, so older files are unchanged), the arrangement as `timeline` (clips with `trackId`, `startStep`, `lengthSteps` and the `clip` itself), `master` (with its own `effects[]` when it has any), and `aiProvenance`. A project holds up to 128 tracks (up to 16 scenes); anything past that is trimmed on load and noted in the remediation ledger. Older files with a scene list under `arrangement`, and v1 files, are migrated automatically on load. A sketch:

```json
{
  "format": "slaw-project/v2",
  "name": "Q4 Synergy",
  "bpm": 124,
  "steps": 16,
  "swing": 56,
  "scenes": ["Onboarding", "Q4 Drop"],
  "tracks": [
    {
      "id": "84fbd7b5", "name": "Bottom-Line Bass", "instrument": "saw",
      "instrumentParameters": { "cutoff": 900, "release": 120 },
      "effects": [{ "type": "vibe", "enabled": true, "parameters": { "synergy": 0.4 } }],
      "gain": 0.45, "pan": 0, "swing": 50, "timing": 6, "muted": false, "solo": false, "aiLocked": true,
      "clips": [
        { "name": "Onboarding", "pattern": ["A1", "_", ".", "A1", ".", ".", "A2", ".", "F1", "_", ".", "F1", ".", ".", "G1", "G2"] },
        null
      ]
    },
    {
      "id": "c0ffee01", "name": "Kick (BYO)", "instrument": "sampler",
      "sample": "samples/kick.wav",
      "clips": [{ "name": "Onboarding", "pattern": ["C4", ".", ".", ".", "C4", ".", ".", ".", "C4", ".", ".", ".", "C4", ".", ".", "."] }, null]
    }
  ],
  "timeline": [
    { "id": "a1b2c3d4", "trackId": "84fbd7b5", "startStep": 0, "lengthSteps": 64,
      "clip": { "name": "Verse bass", "pattern": ["A1", "_", ".", "A1", ".", ".", "A2", ".", "F1", "_", ".", "F1", ".", ".", "G1", "G2"] } }
  ]
}
```

Instruments are `operator` (four-oscillator FM, see [Instruments](Devices.md#instruments)), `kick`, `snare`, `hat`, `clap`, `sine`, `square`, `saw`, `triangle` (these four take `spread`, a unison detune of 0–50 cents: above 0 a note is two oscillators at ±spread/2 mixed at equal power, so a saw at 15–30 is a beating Reese bass and 0 sounds exactly as it always has), `pluck` (Karplus-Strong), `noise`, the organic `epiano`, `organ`, `mallet`, `flute`, `guitar`, `conga` (pitched by its note; `"x"` plays C4) and `shaker` (a drum), whose timbre also follows velocity (see [Instruments](Devices.md#instruments)), `sampler` (plays the WAV named by the track's `sample` field; paths next to or below the project are stored relative), and `soundfont` (plays the preset named by the track's `soundFont` field: `{"path", "bank", "program", "presetName"}`, stored relative like samples). Pattern steps are pitches (`"C4"`, `"D#3"`, `"Bb2"`), chords (`"A3+C4+E4"`), `"x"` to trigger, `"_"` to sustain the previous note, or `"."` for silence. A note can also carry its own length in steps: in `"C4:12+E4:2"`, C4 rings for 12 steps and E4 for 2, so chord notes can end at different times and a long note can ring under later ones. In the piano roll, drag a note to set its length. Notes and hits can also carry a velocity after `@`, from 0 to 1 (MIDI-style 2–127 is accepted and converted): `"x@0.4"` is a ghost hit, `"C4:8@0.6+E4"` a soft root under a full-strength third. Full strength (1) is never written, so existing projects and terse models stay terse. Velocity scales loudness on a velocity^1.5 curve, so 0.5 sits about 9 dB down, where ghost notes live; on the organic instruments it also shapes the timbre. A step can be a **ratchet**: `*N` (2–8) at the very end strikes the whole step, every note of a chord, N times evenly spaced inside its one sixteenth: `"x*2"` is two 32nds, `"x@0.5*3"` a soft triplet roll, `"A3+C4*2"` the chord twice. Unlike `@` and `:`, which belong to one note, `*N` always belongs to the step, so write it once, last (`"x*3@0.5"` is read too). Every strike keeps its note's velocity; each one but the last stops where the next begins, and the last is held exactly as the note would be (through `"_"`, or for its `:length`), so `"C2*2 _ _ _"` is two quick strikes and then a held note. Counts above 8 play as 8; a bare `"*"` is still a plain hit. On VST3 instruments each strike is a note-on at its own sample offset inside the step. A clip may also hold `automation`: envelopes by target, as `{"fx.0.cutoff": [{"step": 0, "value": 300}, {"step": 16, "value": 6000}]}`. `clips` holds one entry per scene (`null` is an empty slot); effect `type`s are `lowpass`, `highpass`, `delay`, `reverb`, `drive`, `bitcrush` and `vibe`.

The headless CLI commands (`compose`, `compose --optimistic`, `render`, `play`, `master`, `vibe`, `new`) are unchanged.

`slaw new` scaffolds the starter song, which is also what the studio's **New project** opens and what `slaw studio` and `slaw mcp` start from when no project file is given or the file does not exist yet. It is a 40-bar house track in A minor at 124 BPM over an Am7 – Fmaj7 – C/E – G6/D progression, made only from slaw's own instruments and effects, so it sounds the same on a fresh install with no samples, SoundFonts or plugins. The arrangement runs intro (bars 1–8), groove (9–16), build (17–20), drop (21–36) and outro (37–40). *Synergy Kick* keys a duck on *Bottom-Line Bass*, *Thought Leader Chords* and *Visionary Pluck*, and clip envelopes open the pads' filter, sweep the build, pitch up the *Escalation Snare* roll and swell the *Runway Riser*. The other tracks are *Stakeholder Clap* and *Agile Hats*, which shuffle at swing 56 while the kick, bass and stabs stay on the grid. The session holds the same material as two scenes, *Onboarding* and *Q4 Drop*, so there is something to launch before anything has been learned. Every note of it was placed by a human, which Compliance has logged as a one-time exception.
