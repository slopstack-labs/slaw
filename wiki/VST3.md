# VST3 plugins: third-party synergy

[← All documentation](README.md)

For when in-house synthesis has been deemed insufficiently enterprise. slaw hosts VST3 instruments and effects in-process, from C#, by hand-transcribing the VST3 binary interface. Steinberg was not consulted. Neither was common sense.

- **Where slaw looks:** the system folders (`~/.vst3`, `/usr/lib/vst3` and `/usr/local/lib/vst3` on Linux, `Common Files\VST3` on Windows), every folder in `VST3_PATH`, any `--plugin-folder`, and your own folders under *Settings → Plugins* (with **Rescan**). Folders are scanned recursively, so bridged plugins anywhere under `~/.vst3` turn up on their own. Bundles that ship a `moduleinfo.json` are read without being loaded.
- **Finding them:** search for a plugin in the browser (Ctrl+F) or the command palette (`Add Track: Dexed (VST3)`, `Add Effect to Bass: … (VST3)`, `Plugins: Rescan VST3 Folders`). Every rescan updates all of these, including one an agent asked for.
- **Instruments:** **VST3…** on the instrument device lists every scanned synth, grouped by vendor. Steps and chords become note-on/note-off events, and held notes (`_`) last as long as you hold them. Their stereo output (voice panning, unison spread) reaches the mix intact.
- **Effects:** *+ Effect → VST3*, anywhere in the chain. Audio goes through the plugin's main bus in stereo, left and right, so Ping Pong Pan pans and MVerb's tail spreads. A mono-only bus gets the mid; a mono-only output feeds both sides.
- **Drag and drop:** drop a `.vst3` from your file manager onto a track (instruments) or the device chain (effects).
- **Knobs:** each plugin gets a generic panel built from the parameters it declares, with a filter box once there are more than 24. Values are stored as `p<id>` = 0…1 next to the built-in knobs, so undo, AI tuning, automation and MCP all treat them like any other parameter.
- **Editor windows (Windows and Linux):** **Editor** on a VST3 device opens the plugin's own window, floating above the studio. Knobs turned there reach the plugin's processor and land in the device's `p<id>` values, one undo step per gesture, so the project, the AI and Ctrl+Z all hear about it; slaw's knobs, AI patches and automation move the plugin's GUI in turn. Loading a preset in the editor updates the values slaw already tracks. On Linux the editor is embedded in an X11 window (XWayland is fine), and slaw lends it the run loop VST3 asks Linux hosts for, so JUCE, DPF and OpenGL editors (OB-Xf, MVerb, 3 Band EQ, glBars) draw, animate and take input. Without a `DISPLAY`, the button says so and stays grey. macOS editors are on the roadmap, behind profitability.
- **Sidechain input:** if the plugin has an aux input, its device header gets a **Sidechain** picker (see [Sidechains and the master chain](Devices.md#sidechains-and-the-master-chain)). The aux bus is switched on only while a source is picked, so plugins that key themselves when nothing is routed keep doing that.
- **Tempo sync:** plugins get slaw's tempo and playhead, so sidechain pumpers (Kickstart), synced LFOs and synced delays lock to the beat of the song rather than to the moment they loaded.
- **State:** each plugin's own state is saved into the `.slaw` on save, and handed back when the project opens.
- **Bounces** wait for every plugin to load, then render in offline mode. Live playback never waits: a plugin that is still loading stays silent for a moment instead of glitching the audio.
- **Agents:** `list_plugins` (with the folder each plugin came from) and `describe_plugin` over MCP. `add_track` with `"instrument": "vst3"` and `add_effect` with `"type": "vst3"` take a `plugin` (`{"path", "classId"}` or just its name).

Plugins run inside slaw's process with slaw's privileges. A plugin that crashes takes the studio with it; slopstack labs calls this *shared accountability*. Windows `.vst3` files cannot load on Linux, so slaw lists them as unavailable instead of dlopen-ing a DLL out of optimism.
