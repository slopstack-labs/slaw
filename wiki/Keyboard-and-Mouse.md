# Keyboard and mouse

[← All documentation](README.md)

Every gesture the studio answers to.

## Mouse and knobs

| Gesture | Action |
|---------|--------|
| Click a clip slot's ▶ | Launch the clip on the next bar |
| Double-click an empty slot | Create a clip |
| Right-click a scene | Place in arrangement at cursor / duplicate / delete |
| Click and hold a piano roll key | Audition the note on the track's instrument |
| Drag along the piano roll keys | Glide between notes |
| Click in the piano roll | Add a note or chord (previews while the mouse is held) |
| Drag right in the piano roll | Hold the note |
| Click a note | Select it (the **Velocity** slider above the editor follows the selection) |
| Alt- or Ctrl-drag a note up/down | Change its velocity (softer notes are paler) |
| Double-click or right-click a note | Delete it |
| Drag a drum hit up/down | Change its velocity (Shift+click selects a hit without toggling it) |
| Mouse wheel (piano roll) | Scroll octaves |
| Click an arrangement clip | Select it and open it in the editor below |
| Drag an arrangement clip / its right edge | Move it / loop its pattern longer (Ctrl copies) |
| Double-click an empty arrangement lane | Create a one-bar clip there |
| Right-click an arrangement clip | Edit / duplicate / rename / delete |
| Right-click a track header | Rename / duplicate / arm / delete the track |
| Double-click a sample in the browser | Add it as a new sampler track |
| Right-click a sample in the browser | Add as new track / load into selected track / preview |
| Drop WAV files or a folder on the window | Create sampler tracks / add folder to the sample library |
| Drag a knob vertically | Change the value (Shift = fine) |
| Mouse wheel over a knob | Change the value |
| Double-click a knob | Reset to default |

## Keyboard

Shaped like the key map you already have in your fingers. **F1** (or Ctrl+/) shows the whole list in the studio.

| Shortcut | Action |
|----------|--------|
| Space | Play / stop. Session view launches the selected scene, Arrangement view plays the song from the cursor |
| Shift+Space | Relaunch the selected scene right away (Arrangement: play from the cursor) |
| Tab | Toggle Session / Arrangement (with two windows open: swap them) |
| Ctrl+Alt+B / Ctrl+Alt+L | Show / hide the browser / the clip and devices panel |
| Ctrl+F | Search the browser: instruments, effects, plugins, samples (Esc clears it) |
| Ctrl+Alt+M / Ctrl+Alt+I | Show / hide the Session view's mixer strips / the AI panel |
| Ctrl+Shift+W | Open / close the second window |
| Ctrl+Shift+P (⌘⇧P on a Mac) | Command palette: search everything the studio can do, add tracks and effects by name |
| Ctrl+Shift+I | Import sounds |
| F11 | Full screen for the focused window |
| Enter / Shift+Enter | Launch the selected clip / scene |
| ↑ ↓ ← → | Move the clip selection |
| Ctrl+D / Ctrl+Shift+D | Duplicate the selected clip into the next scene / duplicate the scene |
| Delete / Backspace | Delete the selected clip (Arrangement: the selected clip, or the track whose header you clicked last) |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste a clip into the selected slot |
| Ctrl+Shift+M | Create an empty clip in the selected slot |
| Ctrl+T / Ctrl+I | Add a track / a scene |
| M | Computer MIDI keyboard on / off |
| F9 | Arm / disarm MIDI record |
| Esc | Stop held notes and hand the keyboard back from a text field |
| Ctrl+S | Save |
| Ctrl+Shift+S | Save as |
| Ctrl+B | Bounce the arrangement: opens the bounce dialog pointed at the WAV next to the project, showing the full path and whether it replaces an existing file before anything is written (Enter bounces, Esc cancels) |
| Ctrl+Shift+B | The same dialog with your last bounce's settings: the full arrangement, one scene looped N times, or stems (one WAV per track), at 44.1/48/96 kHz and 16/24-bit, to a path of your choosing |
| Ctrl+O | Open |
| Ctrl+N | New |
| Ctrl+Shift+N | New AI chat |
| Ctrl+Z | Undo |
| Ctrl+Shift+Z / Ctrl+Y | Redo |
| Ctrl+, | Settings |

**Text fields** keep their letters, arrows and Space while you type. Enter, Esc, or a click anywhere else hands the keyboard back to the transport. Buttons never take keyboard focus, so Space and Enter cannot re-press the last one you clicked.

**Computer MIDI keyboard.** Press **M** (or **⌨ Keys** in the toolbar) and your keyboard plays the selected track, through the same path as a hardware keyboard, so **● Rec** records it too:

- **Notes:** `A S D F G H J K L ;` are the white keys from C, `W E T Y U O P` the black ones. Keys are matched by position, so QWERTZ and AZERTY play the same shape.
- **Octave / velocity:** Z / X and C / V. The toolbar shows the current octave and velocity.
- **Auto-repeat** is ignored: a held key is one held note.

**Undo/redo** covers everything, including AI edits. Especially AI edits.
