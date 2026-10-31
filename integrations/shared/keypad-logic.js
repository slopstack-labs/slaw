import { PALETTE } from "./key-face.js";

/**
 * What every key kind looks like and does, as plain data with no Stream Deck in sight.
 * `view` returns a face for renderKeyFace; `press` returns the request to send, or null.
 * The clip grid reads the key's own position, so one action fills a Mini, an MK.2 or an XL.
 */

const LOWEST_BASE_NOTE = 0;
const HIGHEST_BASE_NOTE = 120;

/** Scale degrees for the note pads. Mirrors NotePadLayout.cs in the studio. */
export const SCALES = Object.freeze({
    chromatic: null,
    major: [0, 2, 4, 5, 7, 9, 11],
    minor: [0, 2, 3, 5, 7, 8, 10],
});

const NOTE_NAMES = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

/** The studio's note names: C4 is MIDI note 60. */
export function describeNote(midiNote) {
    return `${NOTE_NAMES[midiNote % 12]}${Math.floor(midiNote / 12) - 1}`;
}

/** Fresh note pad state for a plugin: C4 up, chromatic, nothing held. Held notes are kept by key id. */
export function createNotePadState() {
    return { baseNote: 60, scale: "chromatic", held: new Map() };
}

/**
 * The note under a pad, or null off the pad area. Pads fill every key but the bottom row and
 * right column, numbered from the bottom left as on an MPC, so one action suits any deck size.
 */
export function noteForPad(coordinates, deckSize, notes) {
    const padColumns = deckSize.columns - 1;
    const padRows = deckSize.rows - 1;
    if (coordinates.column >= padColumns || coordinates.row >= padRows) {
        return null;
    }
    const padIndex = (padRows - 1 - coordinates.row) * padColumns + coordinates.column;
    const degrees = SCALES[notes.scale];
    const midiNote = degrees
        ? notes.baseNote + 12 * Math.floor(padIndex / degrees.length) + degrees[padIndex % degrees.length]
        : notes.baseNote + padIndex;
    return midiNote <= 127 ? midiNote : null;
}

const commandKey = (command, glyph, label, isLit = () => false, litFill = PALETTE.playing) => ({
    view: ({ state }) => ({ glyph, label, fill: isLit(state) ? litFill : PALETTE.idle }),
    press: () => ({ type: "command", command }),
});

const pageKey = (glyph, label, columnChange, rowChange) => ({
    view: () => ({ glyph, label, foreground: PALETTE.dim }),
    // Stops with the last track or scene on the top-left key, so the grid never scrolls into the void.
    press: ({ state, page }) => {
        page.column = Math.min(Math.max(0, page.column + columnChange), Math.max(0, state.tracks.length - 1));
        page.row = Math.min(Math.max(0, page.row + rowChange), Math.max(0, state.scenes.length - 1));
        return null;
    },
});

function trackAt({ state, coordinates, page }) {
    const trackIndex = coordinates.column + page.column;
    return { trackIndex, track: state.tracks[trackIndex] };
}

function trackSwitchKey(field, glyph, litFill) {
    return {
        view: (context) => {
            const { track } = trackAt(context);
            return track
                ? { glyph, label: track.name, fill: track[field] ? litFill : PALETTE.idle }
                : { foreground: PALETTE.dim };
        },
        press: (context) => {
            const { trackIndex, track } = trackAt(context);
            return track ? { type: "set_track", track: trackIndex, [field]: !track[field] } : null;
        },
    };
}

export const KEY_KINDS = Object.freeze({
    "play-stop": {
        view: ({ state }) => ({
            glyph: state.playing ? "■" : "▶",
            label: state.playing ? "Stop" : "Play",
            sublabel: state.bar ? `bar ${state.bar}` : "",
            fill: state.playing ? PALETTE.playing : PALETTE.idle,
        }),
        press: () => ({ type: "command", command: "TogglePlayback" }),
    },
    record: commandKey("ToggleRecord", "●", "Rec", (state) => state.record, PALETTE.record),
    undo: commandKey("Undo", "↶", "Undo"),
    redo: commandKey("Redo", "↷", "Redo"),
    save: {
        view: ({ state }) => ({ glyph: "⤓", label: "Save", sublabel: state.unsavedChanges ? "unsaved" : "", fill: state.unsavedChanges ? PALETTE.queued : PALETTE.idle }),
        press: () => ({ type: "command", command: "Save" }),
    },
    "switch-view": {
        view: ({ state }) => ({ glyph: "⇄", label: state.view === "arrangement" ? "Arrange" : "Session" }),
        press: () => ({ type: "command", command: "SwitchView" }),
    },
    "back-to-arrangement": {
        view: ({ state }) => ({
            glyph: "⤺",
            label: "Back",
            fill: state.returnToArrangementQueued ? PALETTE.queued : state.arrangementOverridden ? PALETTE.mute : PALETTE.idle,
        }),
        press: () => ({ type: "back_to_arrangement" }),
    },
    "clip-slot": {
        view: ({ state, coordinates, page, blinkOn }) => {
            const sceneIndex = coordinates.row + page.row;
            const { track } = trackAt({ state, coordinates, page });
            const slot = track?.slots[sceneIndex];
            if (!slot) {
                return { foreground: PALETTE.dim };
            }
            // A queued clip blinks until its bar arrives, as on a Launchpad.
            const fills = { playing: PALETTE.playing, queued: blinkOn === false ? PALETTE.idle : PALETTE.queued, stopped: PALETTE.idle, empty: PALETTE.background };
            return {
                label: slot === "empty" ? "" : track.name,
                sublabel: slot === "empty" ? "" : state.scenes[sceneIndex],
                fill: fills[slot],
            };
        },
        // A playing slot stops its track; on a deck, there is no other stop button to reach for.
        press: ({ state, coordinates, page }) => {
            const sceneIndex = coordinates.row + page.row;
            const { trackIndex, track } = trackAt({ state, coordinates, page });
            const slot = track?.slots[sceneIndex];
            if (!slot) {
                return null;
            }
            return slot === "playing" || slot === "empty"
                ? { type: "stop_track", track: trackIndex }
                : { type: "launch_clip", track: trackIndex, scene: sceneIndex };
        },
    },
    "scene-launch": {
        view: ({ state, coordinates, page, blinkOn }) => {
            const sceneIndex = coordinates.row + page.row;
            const sceneName = state.scenes[sceneIndex];
            if (sceneName === undefined) {
                return { foreground: PALETTE.dim };
            }
            const isPlaying = state.tracks.some((track) => track.slots[sceneIndex] === "playing");
            const isQueued = state.tracks.some((track) => track.slots[sceneIndex] === "queued");
            return { glyph: "▶", label: sceneName, fill: isQueued && blinkOn !== false ? PALETTE.queued : isPlaying ? PALETTE.playing : PALETTE.idle };
        },
        press: ({ state, coordinates, page }) => {
            const sceneIndex = coordinates.row + page.row;
            return sceneIndex < state.scenes.length ? { type: "launch_scene", scene: sceneIndex } : null;
        },
    },
    "track-mute": trackSwitchKey("muted", "M", PALETTE.mute),
    "track-solo": trackSwitchKey("solo", "S", PALETTE.solo),
    "note-pad": {
        view: ({ coordinates, deckSize, notes, keyId }) => {
            const midiNote = noteForPad(coordinates, deckSize, notes);
            if (midiNote === null) {
                return { foreground: PALETTE.dim, fill: PALETTE.background };
            }
            const fill = notes.held.has(keyId) ? PALETTE.playing : midiNote % 12 === 0 ? PALETTE.solo : PALETTE.idle;
            return { label: describeNote(midiNote), fill };
        },
        press: ({ coordinates, deckSize, notes, keyId }) => {
            const midiNote = noteForPad(coordinates, deckSize, notes);
            if (midiNote === null) {
                return null;
            }
            notes.held.set(keyId, midiNote);
            return { type: "note_on", note: midiNote };
        },
        // Ends the note it started, even if the octave changed while it was held.
        release: ({ notes, keyId }) => {
            const heldNote = notes.held.get(keyId);
            if (heldNote === undefined) {
                return null;
            }
            notes.held.delete(keyId);
            return { type: "note_off", note: heldNote };
        },
    },
    "octave-up": {
        view: ({ notes }) => ({ glyph: "▲", label: "Octave", sublabel: describeNote(notes.baseNote) }),
        press: ({ notes }) => {
            notes.baseNote = Math.min(notes.baseNote + 12, HIGHEST_BASE_NOTE);
            return null;
        },
    },
    "octave-down": {
        view: ({ notes }) => ({ glyph: "▼", label: "Octave", sublabel: describeNote(notes.baseNote) }),
        press: ({ notes }) => {
            notes.baseNote = Math.max(notes.baseNote - 12, LOWEST_BASE_NOTE);
            return null;
        },
    },
    "scale-toggle": {
        view: ({ notes }) => ({ glyph: "♪", label: "Scale", sublabel: notes.scale, fill: notes.scale === "chromatic" ? PALETTE.idle : PALETTE.solo }),
        press: ({ notes }) => {
            const scaleNames = Object.keys(SCALES);
            notes.scale = scaleNames[(scaleNames.indexOf(notes.scale) + 1) % scaleNames.length];
            return null;
        },
    },
    "track-previous": {
        view: ({ state }) => ({ glyph: "◀", label: "Track", sublabel: state.tracks[state.selectedTrack]?.name ?? "" }),
        press: () => ({ type: "command", command: "SelectPreviousTrack" }),
    },
    "track-next": {
        view: ({ state }) => ({ glyph: "▶", label: "Track", sublabel: state.tracks[state.selectedTrack]?.name ?? "" }),
        press: () => ({ type: "command", command: "SelectNextTrack" }),
    },
    "scene-previous": {
        view: ({ state }) => ({ glyph: "▲", label: "Scene", sublabel: state.scenes[state.selectedScene] ?? "" }),
        press: () => ({ type: "command", command: "SelectPreviousScene" }),
    },
    "scene-next": {
        view: ({ state }) => ({ glyph: "▼", label: "Scene", sublabel: state.scenes[state.selectedScene] ?? "" }),
        press: () => ({ type: "command", command: "SelectNextScene" }),
    },
    "add-track": commandKey("AddTrack", "＋", "Track"),
    "add-scene": commandKey("AddScene", "＋", "Scene"),
    "create-clip": commandKey("CreateClip", "＋", "Clip"),
    "duplicate-clip": commandKey("DuplicateClip", "⧉", "Duplicate"),
    "copy-clip": commandKey("CopyClip", "⎘", "Copy"),
    "paste-clip": commandKey("PasteClip", "⎗", "Paste"),
    "delete-clip": commandKey("DeleteClip", "✕", "Delete"),
    "launch-selected-clip": commandKey("LaunchSelection", "▶", "Clip"),
    "launch-selected-scene": commandKey("LaunchSelectedScene", "▶", "Scene"),
    "command-palette": commandKey("ShowCommandPalette", "⌘", "Palette"),
    bounce: commandKey("Bounce", "⤓", "Bounce"),
    "track-stop": {
        view: (context) => {
            const { track } = trackAt(context);
            return track
                ? { glyph: "■", label: track.name, fill: track.slots.includes("playing") ? PALETTE.playing : PALETTE.idle }
                : { foreground: PALETTE.dim };
        },
        press: (context) => {
            const { trackIndex, track } = trackAt(context);
            return track ? { type: "stop_track", track: trackIndex } : null;
        },
    },
    "page-scenes": {
        view: ({ state, page }) => ({ glyph: "▼", label: "Scenes", sublabel: state.scenes[page.row] ?? "", foreground: PALETTE.dim }),
        // One key for both directions: it wraps round to the first scene.
        press: ({ state, page }) => {
            page.row = state.scenes.length > 0 ? (page.row + 1) % state.scenes.length : 0;
            return null;
        },
    },
    "open-mixer": {
        view: () => ({ glyph: "⣿", label: "Mixer", fill: PALETTE.solo }),
        press: () => null,
        opensProfile: "profiles/slaw Mixer XL",
    },
    "open-desk": {
        view: () => ({ glyph: "⣿", label: "Desk", fill: PALETTE.solo }),
        press: () => null,
        opensProfile: "profiles/slaw Desk XL",
    },
    "open-pads": {
        view: () => ({ glyph: "⣿", label: "Pads", fill: PALETTE.solo }),
        press: () => null,
        opensProfile: "profiles/slaw Pads XL",
    },
    "open-launchpad": {
        view: () => ({ glyph: "⣿", label: "Grid", fill: PALETTE.solo }),
        press: () => null,
        opensProfile: "profiles/slaw Launchpad XL",
    },
    "page-left": pageKey("◀", "Tracks", -1, 0),
    "page-right": pageKey("▶", "Tracks", 1, 0),
    "page-up": pageKey("▲", "Scenes", 0, -1),
    "page-down": pageKey("▼", "Scenes", 0, 1),
});

/** What every key shows while the studio is closed or deck control is off. */
export const OFFLINE_FACE = Object.freeze({ glyph: "○", label: "slaw", sublabel: "offline", foreground: PALETTE.dim, fill: PALETTE.background });
