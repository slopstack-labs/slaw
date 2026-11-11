# The Studio

[← All documentation](README.md)

`slaw` (or `slaw studio [project.slaw]`) opens a **session-oriented desktop DAW** on Linux, macOS, and Windows. It is built with Avalonia 11.3 and composed entirely in C#, with no XAML. The layout will be familiar to anyone who has used Ableton Live, and unfamiliar to anyone who has used a compressor. Before any inference has occurred, the AI co-pilot greets you with a hard-coded "It looks like you're making a beat. Would you like help with that?" line.

## The live engine

A real-time, sample-accurate sequencer runs in-process and streams audio continuously (macOS via Core Audio's AudioQueue, with nothing to install; Linux via a pipe into `pw-cat`, `pacat`, `aplay`, or sox `play`; Windows via the winmm waveOut API). **Every edit is heard on the next step, without stopping or restarting playback**: a note, a knob, the BPM, a mute, or an AI rewriting the entire song. The same engine renders offline bounces, so what you hear is what you export. The **hearing protection substrate** is now a live peak limiter at about -1 dBFS.

## Window layout

- **Menu bar** (top): **File** (new, open, save, import MIDI, bounce, show the last bounce in its folder, settings), **Edit** (undo/redo naming what they undo, cut/copy/paste, duplicate, delete, the metronome with its recording count-in and "only while recording"), **Create** (tracks, audio track, scenes, clips, a new AI chat), **View** (Session / Arrangement, computer keyboard, show/hide the browser, the clip & devices panel, the Session mixer and the AI panel, second window, full screen), **Help** (shortcuts). Every item shows its shortcut; menus and keys run the same commands.
- **Panels and windows**, the way Ableton Live does them: panels stay in their places and are shown or hidden, from the View menu, the switches at the right of the status bar, or the shortcuts under [Keyboard](Keyboard-and-Mouse.md#keyboard). Drag the edge of the browser, the AI panel or the clip and devices panel to resize it. **Second Window** (Ctrl+Shift+W) puts whichever of Session and Arrangement the main window is not showing on another monitor; Tab swaps the two, and keys act on the view in the focused window, so Space there plays what you are looking at. **Clip & Devices in Second Window** sends that panel across as well. Sizes, which panels are shown and the second window's place are all remembered; if its monitor has left the organization, it reopens somewhere reachable.
- **Toolbar** (below it): Play/Stop (plus **Back to Arrangement** while session clips play over the song; Stop ends every sounding note within 10 ms instead of letting it finish its release, a decisive leadership style, while reverb and delay tails still decay), **Rec** and **Click**, the metronome (Ctrl+Shift+K; right-click it for volume, a recording count-in of none, 1 or 2 bars, and "only while recording"; see [Recording](Sounds.md#the-metronome)), BPM, **Swing** (50 straight to 75; see [Groove](Arrangement.md#groove-swing-and-timing)), project title, the **Session / Arrangement** switch, a master **AI** switch, Settings (the sliders button), and a status chip showing the inference substrate (a green dot with "llama3 via Ollama", or a hollow red one with "AI offline").
- **Session view**: tracks are columns, scenes are rows. Each cell is a clip slot with a play (launch) button; empty slots show a stop square, and double-clicking one creates a clip. Launches are **quantized to the next bar**, like a real DAW. The right-most column launches whole scenes (a play button per scene, editable scene names; right-click for *place in arrangement at cursor*, *duplicate*, *delete*) and holds the master fader and **Stop all clips**. Every track column ends in a **mixer strip**: stop button, pan knob, vertical volume fader with dB readout, Mute / Solo, a **Protect from AI** lock, delete, and **Name** (AI rename).
- **Arrangement view**: the song, as one timeline with a lane per track. Clips sit at any step, at any length. See [Arrangement](Arrangement.md).
- **Detail view** (bottom): the clip editor. Melodic tracks get a polyphonic **piano roll** (click to add notes or chords, drag right to hold, click a note to select it, double- or right-click to delete, wheel or ▲▼ to scroll octaves); drums get a step row. Ratchet steps (`x*3`) show as a hit cut into that many slices, in the step row and in the piano roll. A **Velocity** slider above the editor sets how hard the selected note or hit is struck. The keyboard strip along the left edge is playable: see [Note audition](#note-audition). Clip name, clip length (4-64 steps), Clear, **Clip** (play), and **Rewrite** sit alongside. Next to the editor is the **device chain**: a small **Groove** device (the track's own Swing, empty to follow the song's, with ↺ to hand it back, and a **Timing** humanize knob in ms), the instrument device with knobs (oscillators: Attack, Decay, Sustain, Release, Filter, Octave, Detune, Spread (unison detune), AI Humanize; noise the same without Spread; pluck: Length, Brightness, …; drums: Pitch, Decay, Tone, AI Humanize; the organic instruments their own, see [Instruments](Devices.md#instruments)), then audio effects with knobs, on/off, reorder, remove, a **Sidechain** picker where the effect listens to one, and **+ Effect**, and finally the **Master Bus** device (Drive, Lowpass, Delay time/feedback/mix, **AI master**) followed by the master chain's own effects and **+ Master FX**. Knobs the AI changed glow violet until you touch them.
- **Browser** (left): a **search box** on top (Ctrl+F; Esc clears it) that filters everything below at once, instruments (click to add a track) and effects (click to add to the selected track), your **VST3 plugins**, your **Samples** (see [Your own samples](Sounds.md#your-own-samples)), and the AI actions. Every word must match, so `909 kick` finds the kicks in your 909 folder. Click a section heading to fold it away; slaw remembers which are folded. A new track, however it got here (browser, palette, import, an agent over MCP), is selected and scrolled into sight, unless the whole song was replaced.
- **AI panel** (right, AI on): the co-pilot transcript and the Autopilot chat box. See [AI features](AI.md#ai-features).

Closing the window with unsaved changes is intercepted by the co-pilot on the first attempt ("close again to abandon your artistic vision").

## Note audition

On the piano roll's keyboard strip, click and hold a key to hear that note on the track's instrument, through its effects. Drag along the keyboard to glide between notes. Releasing fades the note over the instrument's release time (drums, plucks, mallets, congas and guitars ring out). Placing a note in the grid also previews it for as long as the mouse is held.

## Drag and drop

Almost everything can be dragged somewhere it makes sense. Drop targets glow green while they would accept what you're holding.

| Drag | Drop on | Result |
|------|---------|--------|
| Instrument (browser) | a track header, mixer strip, clip slot or arrangement lane | swaps that track's instrument |
| Instrument (browser) | empty space | new track |
| Effect (browser) | any track, or the device chain | appends the effect (drop on a device to insert before it) |
| Sample (browser) or WAV file (file manager) | a sampler track, its clip slots, or the sample editor | loads it into that sampler |
| Sample or WAV file | empty space, or a non-sampler track | new sampler track(s) |
| Folder (file manager) | anywhere | joins the sample library |
| Clip | another clip slot | moves it; hold **Ctrl** (or Alt) to copy. Drum ↔ melodic notation is re-spelled for its new track. |
| Scene grip | the **Arrangement** button | adds the scene's clips at the end of the arrangement |
| Scene grip or a clip | the arrangement lanes | places them where you drop |
| Effect device grip | another device, or another track | reorders the chain, or moves the effect to that track (**Ctrl** copies) |

Every drop is a single undoable edit.
