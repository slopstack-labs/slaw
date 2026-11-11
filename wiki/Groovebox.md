# Groovebox mode

[← All documentation](README.md)

Some stakeholders do not want a DAW. They want a box with sixteen lit buttons per row and a pattern bank, so they can nod at it. **Groovebox mode** is that box: the same song, seen the way a drum machine sees it.

Turn it on with **Ctrl+G** (⌘G on a Mac), the **Groovebox** button between Session and Arrangement in the toolbar, **View → Groovebox**, or *View: Groovebox* in the command palette. Ctrl+G again goes back to the Session grid. slaw remembers which of the two you left it in.

## What it shows

Nothing new is stored. The Groovebox is another way to look at the Session view's clips:

- **Scenes are patterns.** The pattern pads along the top are the scenes, numbered 1 to 16 (the scene limit), with the scene's name underneath. The outlined pad is the pattern being edited (the selected scene). A green pad is playing; an orange outline is queued for the next bar. **+** adds an empty pattern and opens it.
- **Tracks are rows.** Each track is one row of steps: its clip in the edited pattern. A track with no clip there shows a faded, empty row; click a step and the clip exists, in the same undo step as the note.
- **The page is one bar.** A row shows sixteen steps at a time. When a pattern holds clips longer than a bar, **PAGE 1 2 3 4** appears on the right, and **Follow** turns the pages with the playhead. Each row shows its own page of the one in view, so a one-bar hat loop under a four-bar bass line keeps showing its only bar, which is also what is playing. Steps past the end of a shorter clip are drawn as empty outlines.

The clip and devices panel below follows the selected track, as in the Session view: the Groovebox is for patterns, the devices are where the sound gets its quarterly review.

## Playing it

| Gesture | Action |
|---------|--------|
| Click a pattern pad | Edit that pattern. While music plays it also launches, on the next bar, like a scene launch |
| Shift+click a pattern pad | Edit it without launching, so you can prepare the next pattern while this one plays |
| Right-click a pattern pad | Play / edit without launching / copy to a new pattern after this one / place in arrangement at cursor / delete |
| Drag a pattern pad | Onto the Arrangement button or into the arrangement, like a scene |
| Press a track's name pad | Hear its sound (the clip's first note, or the default note) and select the track |
| M / S on a row | Mute / solo the track, at once |
| Space | Play / stop: launches the edited pattern |
| ↑ ↓ | Select the track above / below |
| ← → | Select the previous / next pattern (without launching it; Enter or Shift+Enter launches) |

Steps take the same gestures as the clip editor's step row: **click** toggles a step, **drag a hit up or down** sets its velocity (softer hits are paler), **right-click** clears, **Ctrl+click** holds the previous note, the **mouse wheel** transposes a note (Shift: an octave). Melodic rows show the note name in each step; a new note reuses the pitch before it.

## Pattern tools

Each row ends in **◀ ▶** (shift the pattern one step earlier or later, wrapping around the loop), a **length** button and **⋯**. Right-clicking a track's name pad opens the same tools.

| Tool | What it does |
|------|--------------|
| ◀ / ▶ | Shift every step one step earlier / later. A held note that wraps past the first step becomes a rest there: a loop's first step has nothing earlier to hold |
| Length (`16 st ▾`) | 4 to 64 steps. Growing repeats what is there; a row shorter or longer than the others makes a polymeter. Envelope points past a shortened clip's end are dropped |
| Euclidean rhythm | 1 to 16 hits spread as evenly as the length allows, the first on step 1 (3 over 8 is the tresillo, 4 over 16 is four on the floor). Steps that already had a hit keep its pitch and velocity; new hits play the clip's first note |
| Roll the dice | A new random pattern at 25, 40 or 60% density. Drum hits come as accents or ghost notes; melodic hits only use pitches the clip already plays, so the dice stay in key |
| Double / halve length | Repeat the pattern once more / keep its first half |
| Clear pattern | Every step a rest; the clip and its length stay |
| Launch only this track's clip | As the ▶ on its Session slot |
| Delete this clip | Removes the clip from this pattern |

Every tool is one undo step, and a tool that would change nothing (shifting silence, a Euclid that is already there) leaves no undo step behind. Clip envelopes stay where they are when a pattern shifts: they describe motion over the loop, not a single step.

The tools are also in the **command palette** (Ctrl+Shift+P) as *Groovebox: …* entries: shift earlier / later, Euclidean rhythm with 3, 4, 5 or 7 hits, roll the dice, double / halve length and clear. From the palette they act on the selected clip in any view: the selected arrangement clip if there is one, otherwise the selected track's clip in the selected scene, created on the spot if the slot is empty.

## With a second window

Groovebox mode takes the Session view's place wherever that is. With the second window open (Ctrl+Shift+W), the Groovebox sits in one window and the Arrangement in the other, and Tab swaps them.

## For agents

Agents over [MCP](MCP.md) see what they always see: tracks, scenes and clips. A pattern made in the Groovebox is an ordinary session clip, and a clip an agent writes appears in the Groovebox on the next edit. The Groovebox has no MCP tools of its own, because the agents already had the clips and did not ask for the lights.
