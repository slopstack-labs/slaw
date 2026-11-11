# Arrangement, automation and groove

[← All documentation](README.md)

The song itself, and how it moves.

## Arrangement

The song is one timeline. Scenes stay in the session, where they belong: a grid of loops to jam with, approved for creative exploration but no longer responsible for the song order. Tab toggles **Session / Arrangement** (or start with `--view arrangement`).

- **Make clips:** double-click a lane to create a one-bar clip, drag a session clip in to place a copy (drag a whole scene in to place its whole row), use **Scene at cursor** in the toolbar, or right-click a session clip or scene for *Place in arrangement at cursor*.
- **Edit them:** drag to move, even onto another track. Moves snap to beats; hold Shift for bars or Alt for single steps. Drag a clip's right edge to make it longer: the pattern loops to fill the length, and dashed lines mark each pass. Ctrl+drag copies. Right-click to duplicate, rename, loop or delete.
- **Same editor:** a selected clip opens in the detail view like any other clip, with the piano roll, step row and envelopes.
- **Overlaps:** where two clips overlap on a track, the one that starts later plays. Nothing is trimmed, so moving it away reveals the clip underneath, intact. The later clip is also the one drawn on top.
- **Ruler:** click to set the cursor, double-click to play from there, and drag to set the loop brace (**Loop** toggles it; right-click the ruler to clear it).
- **Tracks:** hover a header for its delete button, or right-click it to rename, duplicate (with its session and arrangement clips), arm for recording or delete. Click a header and press Del to delete that track; Ctrl+Z brings it back, clips and all. Double-click a name to rename it in place.
- **Keys:** Space plays and stops from the cursor. Enter plays from the selected clip. Del, Ctrl+D (duplicate right after itself), Ctrl+C/X/V (paste at the cursor) act on arrangement clips. Ctrl+wheel zooms; Shift+wheel scrolls.

**Jamming over the song.** While the arrangement plays, launch any session clip or scene: on the next bar that track follows the session instead (its lane dims and its header shows an orange **session** tag). Other tracks keep playing the arrangement. Stopping a track's clip silences it until you return. **Back to Arrangement** hands every track back on the next bar. Stopping the transport forgets the detour, so the next play is the arrangement as written. Launching a scene over the song only takes over the tracks that have a clip in it.

Projects from before the arrangement was a timeline (a list of scenes with repeats) open with that list laid out on the timeline, one clip per track per section, sounding exactly as before. The list is not written back. Agents and the autopilot may still send one in a patch, and it replaces the arrangement with those scenes end to end.

## Automation (clip envelopes)

Every clip can carry envelopes: parameters that change while the clip plays, in session and arrangement alike. Strategic pivots, on a schedule.

- Open **Envelopes** in the clip editor, pick a target, and draw. Click to add a breakpoint, drag it to move it (it snaps to quarter steps; hold Shift for none), and right-click or double-click to remove it. **Clear lane** hands control back to the knob.
- Values hold before the first breakpoint and after the last, and move in a straight line between them. Envelopes are evaluated every 64 samples, so a sweep sounds like a sweep, not a staircase.
- **Arrangement clips:** an envelope spans the whole clip, from its start to its end, while the pattern loops underneath. An 8-bar clip of a 1-bar groove can open its filter across all 8 bars. **Session clips:** the envelope spans the pattern and loops with it. When session clips or scenes are placed in the arrangement, their envelopes are repeated across the new clip, so they sound exactly as they looped.
- While the clip isn't playing, the knob's own value applies.

| Target | Moves |
|--------|-------|
| `gain`, `pan` | The track's mixer, continuously. Absolute values: a gain envelope (0–1.5) replaces the fader while the clip plays, it does not scale it |
| `pitch` | Semitones (±24). Oscillators bend in place and keep their length; everything else (samples, plucks, drums, the organic instruments) bends tape-style |
| `fx.<index>.<param>` | One device's parameter, continuously: `fx.0.cutoff` is the classic filter sweep |
| `inst.<param>` | An instrument parameter, read as each note starts (voices are rendered whole). `inst.spread` sweeps a Reese bass from one oscillator to a full beating pair, one note at a time |

Moving, removing or inserting devices takes their envelopes along. Agents write envelopes with the `set_automation` MCP tool or `"automation"` in a patch.

## Groove: swing and timing

The grid is a strong default, not a performance review. Two settings loosen it without touching a single pattern:

| Setting | Where | Units | What it does |
|---|---|---|---|
| **Swing** | project `swing` (toolbar), overridable per track (`tracks[].swing`, the Groove device) | percent of an eighth note, 50–75 | MPC-style: every odd sixteenth (steps 1, 3, 5 … counted from the start of the song, not of the clip: the second of each eighth-note pair) starts (swing − 50) / 50 of a step late. 50 is straight, 58 a light shuffle, 66.67 a triplet shuffle (⅓ step late), 75 a dotted-sixteenth limp (½ step). A track's own swing wins, so the hats can shuffle over a four-on-the-floor kick that stays straight; `null` (an empty box) follows the song |
| **Timing** | `tracks[].timing`, the Groove device's knob | milliseconds, 0–50 | Every step the track plays starts a random 0..timing ms late (never early). The randomness is seeded, so a bounce of the same project is the same bytes every time; 8–15 ms reads as "played", 30 as "late for standup" |

Swing applies identically to session clips, arrangement clips, bounces and VST3 instruments (their note-ons and note-offs go out at sample offsets). Ratchet strikes spread evenly across their step's swung length, and a held note ends where the step after it really starts, so legato lines stay legato. Envelopes stay on the straight grid. Timing is a track setting rather than an instrument knob next to **AI Humanize** (which only humanizes loudness) because it belongs with swing: it is the track's feel, it survives switching instruments, and it works the same for samplers, SoundFonts and plugins. A project with swing 50 and no timing renders exactly the bytes it did before groove existed.
