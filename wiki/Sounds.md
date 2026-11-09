# Samples, SoundFonts, MIDI and recording

[← All documentation](README.md)

Getting sounds and notes in from the outside world.

## Your own samples

The `sampler` instrument ("Sampler") plays your own WAV files, pitched across the piano roll. C4 is the root note by default, i.e. original pitch. Knobs: Root Note, Transpose, Start, End (trim), Note Length (off = one-shot, on = follows the note length), Reverse, Attack, Release, Gain, Filter, AI Humanize.

- **Format**: WAV, AIFF/AIFF-C (uncompressed) and FLAC. 8/16/24/32-bit PCM and 32/64-bit float, any channel count (mixed to mono), any sample rate. An MP3 in a WAV container is rejected with "format tag 0x55 looks like MP3 wearing a WAV costume".
- **Import sounds** (**File → Import Sounds…**, **⤓ Import…** in the browser, or Ctrl+Shift+I): drop a folder or some files on the window. It lists every sound it finds, all the way down the folder, guesses what each one is from its name, and plays whichever you land on, so the arrow keys audition a whole pack. Space ticks and unticks. Then pick how they arrive: **Drum kit** (one track, one sound per key from C4, up to 64, joined into one WAV in `imports/` next to the project and played in Slice mode), **Separate tracks** (one pitched sampler per sound), or **Sample library** (the folder joins Browser → Samples and nothing touches the song). Tracks undo as one step with Ctrl+Z. **Ableton preset** appears when the folder holds `.adg` or `.adv` files: each becomes one `multisampler` track with its keys, velocities, tuning and round robin intact, so a soft note picks the soft recording. Simpler and Sampler devices work, in Instrument and Drum Racks at any depth (a Drum Rack's pads land on their own MIDI notes). **Operator** presets become one `operator` track with its oscillators, algorithm, envelopes, filter, LFO and pitch envelope. It is slaw's own synth playing Live's settings, so expect the same patch in a similar voice rather than the same bytes. Effects that are really doing something become slaw's nearest (Delay, Ping Pong Delay, Simple Delay, Reverb, Overdrive, Saturator, Dynamic Tube, Amp, Redux, Auto Filter, Utility's gain and width, and Simpler's low-pass and high-pass filters, taken from a rack's loudest layer, since a track has one effect chain); ones switched off or at 0% wet change nothing, so they are skipped. Layers keep their own envelopes. Macro positions are already in the settings, so only the knobs are missing. What audibly stays behind, such as EQ Eight, Compressor, Chorus or Live's other synths, is named in the window. The track points at the original files, so keep the pack where it is. Dropping a preset, a Live song or a folder holding presets anywhere on the studio opens this window with them ready. A chain's fader and speaker button are respected, and the loudest recording is brought to a usable level. `.als` songs can't be imported, though the presets and samples beside them can; a packed `.alp` needs installing in Live first, and its folder can then be dropped here.
- **Browser → Samples**: **+ Folder…** adds a sample folder, ⟳ rescans, and a filter box narrows the list by file or folder name ("909" keeps the whole pack). Samples are grouped by the folder they live in, labelled relative to the sample folder you added (*BPB Cassette 909 / clean*), so a pack's three `bd01.wav`s are no longer a single undifferentiated synergy. Folders start folded; slaw remembers which ones you opened. Click a sample to preview it; double-click to add it as a new sampler track (with a starter note so it is audible). Right-click for *add as new track*, *load into selected track*, or *preview*. Sample folders are also managed in Settings → Samples.
- **Drag and drop**: drop WAV files onto the window to create sampler tracks. One WAV dropped while an empty sampler track is selected loads into that track. Drop a folder to add it to the sample library.
- **Sampler device**: shows the sample's waveform, with **Load…**, ▶ preview, and **✨ Pick**, where an AI "sample curator" picks a sample from your folders for the track.
- **Autopilot**: is shown your sample library and may build sampler tracks from it. Hallucinated paths are matched by file name, or reverted.
- **Portability**: samples stored next to (or below) the project file are saved as relative paths, so project folders can be moved. Others stay absolute.

slaw itself still ships zero samples. These are yours, and therefore your responsibility.

## Sample editor

Open it from the sampler device (**✎ Edit**, or **⤢** for its own resizable window), from the **Sample** tab next to **Clip** in the detail panel, or with `slaw studio --view sample`. Every edit is non-destructive: it is stored in the project as data, undoable with Ctrl+Z, and the source WAV is never touched.

- **Waveform**: zoom with Ctrl+wheel, `+` / `−`, **Fit** or **Zoom to selection**, and scroll with the wheel or the overview strip. Drag to select. The green **S** and red **E** markers set the playable region.
- **Edits**: **Trim**, **Delete**, **Silence**, **Reverse**, **Normalize**, **−3 dB / +3 dB**, **Fade in / Fade out** and **Duplicate** apply to the selection (some apply to the whole sample if nothing is selected). **Revert to original** discards them.
- **Autotune**: pick a key and a style in the **Autotune** row, then **Tune** moves the pitch of the selection (or the whole sample) onto that key's notes. The key starts on the one the song's written notes fit (**Analyze: Check Song Key** finds the same); **chromatic** snaps to every semitone. Styles set the retune time: **Robot** (0 ms, the hard stepped sound), **Tight** (20 ms), **Natural** (60 ms, keeps the start of each note and most vibrato) and **Gentle** (150 ms). Length and timing do not change, and breaths, consonants and silence pass through untouched. It hears one voice or one lead line at a time; chords and full mixes confuse it, as they would a junior engineer. It works on loaded samples and recorded takes alike, and stacks with the other edits like any of them. The command palette offers **Autotune *track* to *key*: Robot / Tight / Natural / Gentle** for the selected sampler track's whole sample, and MCP agents have the `autotune` tool. Long takes take a few seconds to process the first time; that happens in the background, the waveform shows **autotuning…** meanwhile, and the other edit buttons wait until it lands.
- **Pitched mode**: the sample is repitched across the keyboard. Tick **Sustain loop** and drag the amber **L** markers: while a note is held (Note Length on), the loop repeats, then the release plays out.
- **Play a piece on the keys**: drag across any part of the waveform and a small toolbar appears over the selection: **▶ Listen**, **🎹 Play on keys** and **Trim**. **Play on keys** puts that piece on a new track (`Lead · slice 1`, same mixer and effects), selects it, turns on the computer keyboard and opens the piano roll, so the piece plays pitched from the keyboard, a MIDI keyboard or the grid, with C4 at its original pitch. Pitch changes speed as well, as on a classic sampler; there is no time-stretching. The source track and the file stay as they were, and Ctrl+Z removes the new track. In Slice mode, right-click a slice for the same thing, or to remove a slice line.
- **Slice mode**: chop the sample into slices. Slice 1 plays on the root note (C4), slice 2 on C#4, and so on. Use **÷ 4 / ÷ 8 / ÷ 16**, **Detect transients** (with a sensitivity slider), double-click the waveform to add a slice, drag slices to move them, and right-click a slice to remove it. **✨ Auto-chop** lets the model choose slice points from the detected transients. It considers them carefully, then chops on the beat anyway.
- **Preview / export**: **▶ Selection** and **▶ All** play through the live engine with a playhead. **Save as WAV…** exports the edited sample. **Bake…** saves it and swaps it in as the track's sample.

## SoundFonts

The `soundfont` instrument ("SoundFont Player") plays one preset of a SoundFont 2 bank (`.sf2`): General MIDI sets, ringtone-grade phone fonts, sampled orchestras, whatever compliance has signed off on.

- **Loading:** **SoundFont… (.sf2)** in the browser adds a track; **Load…** in the device swaps the font on a track; or drop a `.sf2` onto a track (loads into it) or the window (new track). The device's preset picker lists every preset as `bank:program name`. Bank 128 is the drum kit: each MIDI note plays a different drum.
- **Knobs:** Gain, Transpose (semitones), Release (scales every zone's release), Filter, AI Humanize. Velocity, clip envelopes (including `pitch`, bent tape-style like the sampler) and the device chain work as on any track.
- **What is honoured:** key and velocity ranges, root key (and overridingRootKey), coarse/fine/scale tuning and pitch correction, loop modes (continuous and until-release) with every address offset, the delay/attack/hold/decay/sustain/release volume envelope (with keynum-to-hold/decay), initialAttenuation (scaled 0.4, as E-mu hardware and FluidSynth do), the zone's lowpass filter, and preset-level generators added on top of instrument ones.
- **What is not (yet):** the modulation envelope and both LFOs, modulators, chorus/reverb sends, exclusive classes, and stereo: zones render mono, and a stereo pair is summed at half level each. Zones are chosen at velocity 100; how hard a note is struck is applied as gain, as on the oscillators, drums and pluck.
- **Loading behaviour:** fonts parse in the background; a note that arrives while its font is still loading is skipped, exactly like an undecoded sample. Bounces wait for the parse.
- **Portability:** a font next to (or below) the project file is saved as a relative path.

## MIDI

**MIDI input.** Every connected keyboard or controller plays the selected track:

- **Playing:** polyphonic and velocity-sensitive, through the track's own effects.
- **Knobs:** the mod wheel sweeps the instrument filter, CC7 sets volume, CC10 sets pan.
- **Platforms:**
  - **Linux:** the ALSA sequencer. slaw auto-connects to every MIDI source, including ones plugged in later, and appears as a `slaw` client so other software can play it (`aplaymidi -p slaw:0 song.mid`).
  - **Windows:** winmm.
  - **macOS:** not yet.
- **Turning it off:** Settings → Integrations.

**● Rec** records what you play into the selected clip:

- **While playing:** notes land on the step you hear, latency-compensated and quantized to sixteenths. Takes overdub, and a held key writes sustains. If the session is playing and the selected track is not, engaging ● Rec starts the selected clip on the next bar, creating it if the slot is empty.
- **While stopped:** step input. Each note fills the next step, and notes struck together become a chord.
- **Count-in:** with a [metronome count-in](#the-metronome) set, pressing Play with ● Rec engaged clicks one or two bars before the transport starts; notes played during it are heard, not recorded.
- **Velocity:** each note keeps how hard you struck it.
- **Undo:** a whole take is one undo step.

**MIDI import.** Open a `.mid` with **File → Import MIDI…** (or **Open…**), drop it on the window, or run `slaw import song.mid`:

- **With a SoundFont:** if a `.sf2` came with the file (same name, or the only one in its folder), or you pick one (**File → Import MIDI with SoundFont…**, drop the `.mid` and `.sf2` together, `--soundfont`), every channel plays its General MIDI program from the font and channel 10 plays the font's drum kit as one track, note for note. The status bar and the import notes say which font was used.
- **Without one:** notes are quantized to sixteenths, channel 10 drums are split into kick, snare, clap, shaker and hat tracks, and General MIDI programs pick slaw instruments (pianos → epiano, chromatic percussion → mallet, organs → organ, guitars → guitar, pipes → flute, basses → saw, pads → triangle…).
- **Pitch bends:** imported as clip `pitch` envelopes, in semitones, honouring each channel's bend range (RPN 0, ±2 by default). Quarter-tone ornaments and quarter-tone-flat parts survive the import intact.
- **Song structure:** the song is cut into 4-bar scenes, and the arrangement plays them back in the file's order (a repeated section becomes one longer arrangement clip).
- **Single clip:** drop a `.mid` on a clip slot to fill just that clip.
- **Agents:** the `import_midi` MCP tool does the same for agents.

slaw's notation is monophonic per step and polyphonic per chord, so imports are lossy by design. Velocities and that one grace note are remediated away.

## Recording

After years of generating vocals, slaw now also accepts them from humans. A pilot programme.

1. **Arm a track.** Click **🎙 Audio Track** in the browser, which adds an empty track and arms it, or press **●** on any sampler track's mixer strip. One track can be armed at a time, because there is one microphone. While a track is armed, the 🎙 meter next to **● Rec** shows the input level, and it turns red when you clip.
2. **Press ● Rec (F9).**
   - If the transport is stopped, slaw launches the selected scene and the take starts with it (after the [metronome's count-in](#the-metronome), if one is set). In the Arrangement view it plays the arrangement from the cursor instead, and the take lands on the arrangement where you sang it (long takes are sliced into consecutive 4-bar clips).
   - If the transport is already playing, the take punches in on the next step. A clip that wasn't playing yet joins on the next bar.
3. **Stop the transport, or press ● Rec again,** to end the take. A bar above the clip panel offers **✂ Cut & play it →**, which opens the take in the sample editor (see **Play a piece on the keys**). Ignore it and it leaves after 30 seconds.

The take is saved as a 24-bit WAV in `recordings/` next to the project (or next to `settings.json` if the project hasn't been saved yet). It's loaded into the armed track with a sampler that plays it unaltered, triggered on the step it was sung on. The clip grows in whole bars, up to 64 steps, so the take fits before the loop comes around. Ctrl+Z removes the whole take.

- **Takes don't overwrite each other.** A track holds one sample. If other scenes still play the old take, the new one goes on a fresh track (*Vocal Stakeholder 1 · take 2*) with the same effects and mix.
- **Rec stays engaged after you stop,** so pressing play again records the next take. Disengage it when the session wraps.
- **Latency.** slaw places each take where you *heard* the beat, so output latency is already compensated. **Settings → Audio → Input latency** (default 20 ms) is trimmed from the head of every take to cover the microphone and driver. If takes land late, raise it.
- **Recording tools.** On Linux, slaw records through `pw-record`, then `parec`, then `arecord`, whichever is installed, and Settings picks the input device. On Windows it uses waveIn, and Settings picks the input device there too. On macOS it uses Core Audio with nothing to install, always from the default input in System Settings → Sound; the first time you arm a track macOS asks for microphone permission, and if it was refused, slaw stops and says where to allow it.
- **No input monitoring.** slaw's output runs through a pipe with tens of milliseconds of buffering, so hearing yourself through it would be a slapback delay with a feedback roadmap. Use your interface's direct monitoring, or PipeWire's.
- **Autotune a take** in the sample editor (**Autotune** row) or from the command palette (**Autotune *track* to *key*…**). The take's WAV stays as sung; the correction is an edit in the project, so Ctrl+Z or **Revert to original** undoes it.
- **Agents can't record.** They can't sing either. They see takes like any other sample in `get_project`.

### The metronome

A click on every beat, so takes land where the grid expects them. It has been told it is the most important person in the room and believes it.

- **Click** next to **● Rec** in the toolbar (or Ctrl+Shift+K, **Edit → Metronome**, or the palette's **Transport: Metronome On / Off**) turns it on. Each beat gets a short 1 kHz blip; each bar's downbeat a higher (1.5 kHz) and louder one. It follows the straight grid: swing moves your notes, never the click.
- **Live output only.** The click is added after the master chain, so master effects never compress or echo it, and it never reaches a bounce, a stem, an MCP `render` or anything else rendered offline. Those stay byte-for-byte what they were.
- **Right-click Click** for its settings: **volume**, **count-in** and **only while recording** (clicks only while ● Rec is engaged, so plain playback stays quiet). The same settings live in **Settings → Audio** and in the **Edit** menu; the palette has **Metronome: Count-In 1 Bar / 2 Bars Before Recording** and **Metronome: No Count-In**. They are studio preferences, not part of the project, so they follow you from song to song.
- **Count-in.** With a count-in of 1 or 2 bars, starting a recording from a stopped transport (● Rec on an armed audio track, or ● Rec engaged and then Play, Space or a clip or scene launch) clicks those bars first, at the song's tempo, and only then starts the transport. The count-in clicks even with the metronome off, and nothing is recorded during it: an audio take is trimmed to the first step, and MIDI notes you play are heard but not written (not even as step input). Pressing Stop (or Space) during the count-in cancels it. If something is already playing, there is no count-in; the take punches in as usual.
