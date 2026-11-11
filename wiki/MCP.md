# MCP: let other AIs produce your song

[← All documentation](README.md)

An AI-native DAW that only one AI can use would be a silo. slaw speaks the [Model Context Protocol](https://modelcontextprotocol.io), so agents such as Claude Code can read and edit your project directly.

**Live studio.** Enable *Settings → Integrations → Expose the open project over MCP*, or start with `slaw studio --mcp`. slaw then listens on `http://127.0.0.1:47115/mcp` (change it with `--mcp-port`). Agents edit the song you have open, while it plays:

- edits are heard on the next step, show up in the UI, and undo with Ctrl+Z like any other edit
- the co-pilot announces each one ("I see we've brought in a consultant")

```sh
claude mcp add --transport http slaw-studio http://127.0.0.1:47115/mcp
```

**Headless.** `slaw mcp song.slaw` serves a project file over stdio and autosaves every edit:

```sh
claude mcp add slaw -- slaw mcp /path/to/song.slaw
```

| Tool | Does |
|------|------|
| `get_project` / `get_catalog` | Describe the project (listing with every arrangement clip's id, bars and pattern loops; or the full project as pure JSON), and list every instrument, effect and parameter range |
| `arrangement_place_audio` | Put a WAV (a vocal, a stem, a whole recording) on the arrangement from a bar, at its original pitch and speed, cut into 4-bar slice clips so any length plays through. Set the tempo to the recording's first: the audio is never time-stretched |
| `arrangement_place_clip`, `arrangement_edit_clip`, `arrangement_remove_clips` | The song, one clip at a time: place a clip on a track at a bar (or step) for some bars (or steps), move or resize it by id, remove clips by id or by track and bar range. Every result names the clip's id and where it sits |
| `apply_patch` | Many edits at once, in the format the Autopilot uses: `reset` (start from an empty project first), tempo, `swing` (50–75; tracks take their own `swing` and `timing` ms), scenes (rename) and `removeScenes`, tracks (`"id": "new"` adds one; existing tracks by id or name; `remove: true` takes the track's clips with it), session clips, clip envelopes, effects, master, and `timeline` (arrangement clips by track, start step and length; `get_project`'s JSON field names work too, so its clips paste straight back in). A legacy `arrangement` list of scenes replaces the whole arrangement with those scenes end to end. Results echo every envelope written; anything ignored or adjusted comes back as a warning |
| `open_project` | Open a `.slaw` file as the current project, like File → Open (in the live studio it shows up in the window). Refuses to drop unsaved work unless told to |
| `new_project` | Start from scratch in one undoable step: no tracks, no clips, one empty scene, the default master bus, optionally a name and tempo |
| `set_clip`, `set_automation`, `remove_scene`, `add_track`, `update_track`, `add_effect`, `set_tempo` | Focused edits. `set_clip` writes a session clip; `update_track` also sets a track's `swing` (null follows the project) and `timing`, and reports anything it clamped; `set_automation` writes an envelope on an arrangement clip (by id) or a session clip; `add_effect` takes `"track": "master"` for the master chain and a `sidechain` track |
| `transport` | `play_arrangement` (from a 1-based bar), `play_scene` (over a playing arrangement it takes those tracks over), `back_to_arrangement`, `stop` (live studio only) |
| `render`, `save_project`, `undo`, `redo` | Bounce the arrangement to WAV (relative paths land next to the project) with its length in bars and seconds and how hard hearing protection worked, plus LUFS loudness (integrated and loudest 3 s), true peak, stereo correlation overall and below 120 Hz, the level change in mono, and dB per frequency band, with warnings for true peaks above -1 dBTP, a mono loss over 3 dB and a wide low end. With `levels: true`, also every track's level overall and per bar range, the mix's level and bands per bar range, a named warning for any track 12 dB or more away from the median, and likely masking: pairs of tracks both within 6 dB of the loudest in one band at the same moments, louder track first. Measured window by window, so a bass ducked by the kick does not count where the ducking keeps them apart. Measuring never changes the WAV. Save, history |
| `autotune` | Pitch-correct a sampler track's audio (a recorded take, a placed WAV, a loaded sample) to a key: `key` (default: the key the song's notes fit, else `chromatic`), `style` Robot / Tight / Natural / Gentle or `retuneMs` 0..400, optional `startSeconds`/`endSeconds`, or `remove: true`. Adds a non-destructive edit; the WAV and the timing stay as they were, the result measures the pitched audio's average and largest distance from the key in cents before and after, and `get_project` lists it after the sample as `[autotune A minor, retune 60 ms, 0.00–12.50 s]`. One voice or lead line at a time |
| `check_notes` | The key the written notes best fit, the next best, and every note outside the scale with its track and bar.step; pass `key` to check against the key you meant. Reads notes, not audio, so it is exact, and names the drums, kits and recordings it cannot read. Also in the command palette as **Analyze: Check Song Key and Out-of-Key Notes** |
| `list_samples` | Your sample library, for sampler tracks, grouped by folder; `filter` matches file or folder names |
| `describe_soundfont` | A `.sf2` file's presets as `bank:program name` (bank 128 = drum kit), for `add_track` `{"instrument": "soundfont", "soundfont": {"path", "bank", "program"}}` or `apply_patch` `tracks[].soundfont`; `import_midi` takes `soundfont` (a path, or `"none"`) and otherwise uses the `.sf2` next to the `.mid` |
| `list_plugins` / `describe_plugin` | Installed VST3 plugins with the role they declare (synth, sampler, reverb…) and the folder each came from; one plugin's parameters as `p<id>` keys with titles |
| `ask_copilot` | Ask slaw's own configured model a question about the song (or for a short review); changes nothing. An AI consulting an AI about music |

Positions are sixteenth-note steps from 0 (16 per bar) wherever an argument says `start` or `length`, and 1-based bars wherever it says `bar`; listings print both. Velocity (`@0.7`) and length (`:8`) belong to the one note they follow, so a soft chord is `A3@0.7+C4@0.7+E4@0.7`; a chord with a suffix on its last note only gets a warning. A ratchet (`*N`, 2 to 8) is the one suffix that belongs to the whole step and goes at its very end: `x*4` strikes the step four times inside its sixteenth. Writing it on an inner chord note, on several notes, or above 8 gets a warning too. Outside an envelope's clip the knob's own value applies. `get_catalog` lists what each built-in instrument puts out at gain 1, with a suggested starting gain. The server's instructions teach agents all this before they touch anything, along with a worked example, because the alternative was watching them guess.

Tracks with the AI lock on are refused to every MCP client, too. The HTTP endpoint binds to 127.0.0.1 only and rejects requests whose `Origin` is not local, so a web page cannot quietly remix your song.
