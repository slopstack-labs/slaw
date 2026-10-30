# slaw Roadmap
### *The Circle of Fifths of Inevitability*

slaw follows a structured release cadence. Each milestone modulates up a perfect fifth from the last, representing a distinct phase in our journey toward full inference-layer music production supremacy. The path is clear. The destination is certain. The mix may or may not be audible.

| Version | Key | Codename | Status |
|---------|-----|----------|--------|
| `v0.1.0` | C | **Cognitive Composition** | ✅ shipped |
| `v0.2.0` | G | **Generative Groove** | ✅ shipped |
| `v0.3.0` | D | **Determinism Dissolution** | 🔄 in progress |
| `v0.4.0` | A | **Agentic Amplitude** | 🗺️ planned |
| `v0.5.0` | E | **Emergent Equalization** | 🗺️ planned |
| `v0.6.0` | B | **Boundless Bounce** | 🗺️ planned |
| `v0.7.0` | F# | **Fortissimo Fulfillment** | 🗺️ planned |
| `v0.8.0` | C#/Db | **Cacophony Calibration** | 🗺️ planned |
| `v0.9.0` | Ab | **Autotune Abdication** | 🗺️ planned |
| `v0.10.0` | Eb | **Echo Enlightenment** | 🗺️ planned |
| `v0.11.0` | Bb | **Bitrate Benevolence** | 🗺️ planned |
| `v0.12.0` | F | **Frequency Fluency** | 🗺️ planned |
| `v1.0.0` | C | **Complete Convergence** | 🗺️ planned |

---

*Complete Convergence is the final release. The circle is closed. There is nowhere left to modulate. The mix has fully dissolved into the inference layer.*

---

## Open work: co-pilot parity (`v0.4.0` **Agentic Amplitude**)

The MCP server can now edit everything slaw does; the in-app co-pilot (Autopilot, ✨ buttons) cannot. Agentic Amplitude ships when the co-pilot can edit everything a human can, measured against the default local model (`qwen2.5:3b`) with a harness that runs the real workflow and reports what each patch changed (see CLAUDE.md, *LLM prompts*).

- [ ] **Restore what the arrangement-first prompt (`37d8a4e`) dropped.** The patch applier still accepts these, but the Autopilot prompt no longer mentions them: clip envelopes (`automation` on session and arrangement clips: filter sweeps, pitch, gain), sidechain ducking (`duck` with `sidechain`), the master effect chain (`master.effects`), and `removeScenes`. Each needs a labelled example and an eval request ("sweep the filter into the chorus", "duck the pad under the kick", "put a reverb on the master").
- [ ] **Arrangement editing beyond notes:** move, resize and remove arrangement clips (`start`/`length`/`remove` in `timeline`), duplicate sections, extend the song.
- [ ] **Tracks:** remove and duplicate tracks, rename, mute/solo, pan, from a request ("drop the piano in the verses").
- [ ] **VST3 plugins:** tell the co-pilot which plugins are installed and their roles, and let it load instrument/effect plugins and set their `p<id>` parameters (today it only sees built-in devices; the AI sound designer handles a plugin's first 32 knobs).
- [ ] **SoundFonts:** list available `.sf2` presets and let the co-pilot create `soundfont` tracks (`soundfont {path, bank, program}`) and change presets.
- [ ] **Audio files:** a patch way to place a WAV on the arrangement (the `arrangement_place_audio` slicing), so "put my vocal in at bar 1" works from the chat.
- [ ] **Per-note velocity and note lengths** in generated parts (`@0.6`, `C4:8`), with the per-note binding rule.
- [ ] **✨ Generate / Rewrite for arrangement clips:** the clip editor hides ✨ Rewrite for arrangement clips; generate and rewrite them in place, aware of the surrounding bars and chords.
- [ ] **Stereo toolkit:** widen, ping-pong delay, reverb width, pan, when asked about space and width.
- [ ] **Long songs on small models:** the project listing of a 100-bar song strains a 3B context; send a focused window (the bars and tracks the request is about) plus a compact overview.
- [ ] **Commit the Autopilot eval harness** (opt-in, skipped without Ollama) with one request per capability above, and track the "changed the right thing" rate per model.

*Every item is done when the eval request for it changes the song correctly on `qwen2.5:3b` in at least 4 of 6 attempts.*
