import assert from "node:assert/strict";
import { test } from "node:test";
import { DIAL_KINDS } from "../dial-logic.js";
import { renderKeyFace, PALETTE } from "../key-face.js";
import { createNotePadState, describeNote, KEY_KINDS, noteForPad } from "../keypad-logic.js";
import { composeDeskLayout, composeLaunchpadLayout, composeMixerLayout, composePadsLayout } from "../../scripts/launchpad-profile.mjs";

const state = {
    playing: true,
    bar: 9,
    bpm: 124,
    masterGain: 1,
    record: true,
    view: "session",
    unsavedChanges: true,
    arrangementOverridden: false,
    returnToArrangementQueued: false,
    selectedTrack: 1,
    scenes: ["Verse", "Drop"],
    tracks: [
        { name: "Kick", gain: 1, pan: 0, muted: false, solo: false, slots: ["playing", "stopped"] },
        { name: "Bass", gain: 0.5, pan: -0.5, muted: true, solo: true, slots: ["empty", "queued"] },
    ],
};
const noPage = () => ({ column: 0, row: 0 });

const press = (kind, column, row, page = noPage()) => KEY_KINDS[kind].press({ state, coordinates: { column, row }, page });
const view = (kind, column, row, page = noPage()) => KEY_KINDS[kind].view({ state, coordinates: { column, row }, page });

test("a stopped clip launches, and a playing or empty slot stops its track", () => {
    assert.deepEqual(press("clip-slot", 0, 1), { type: "launch_clip", track: 0, scene: 1 });
    assert.deepEqual(press("clip-slot", 0, 0), { type: "stop_track", track: 0 });
    assert.deepEqual(press("clip-slot", 1, 0), { type: "stop_track", track: 1 });
});

test("clip keys are coloured by the slot's state", () => {
    assert.equal(view("clip-slot", 0, 0).fill, PALETTE.playing);
    assert.equal(view("clip-slot", 1, 1).fill, PALETTE.queued);
    assert.equal(view("clip-slot", 0, 1).fill, PALETTE.idle);
    assert.equal(view("clip-slot", 1, 0).fill, PALETTE.background);
});

test("keys past the edge of the song stay dark and do nothing", () => {
    assert.equal(press("clip-slot", 7, 0), null);
    assert.equal(press("clip-slot", 0, 3), null);
    assert.equal(press("scene-launch", 0, 3), null);
    assert.equal(press("track-mute", 7, 0), null);
    assert.equal(view("clip-slot", 7, 0).label, undefined);
});

test("paging shifts the grid, and it stops at the first track and scene", () => {
    const page = noPage();
    KEY_KINDS["page-right"].press({ state, page });
    KEY_KINDS["page-down"].press({ state, page });
    assert.deepEqual(page, { column: 1, row: 1 });
    assert.deepEqual(press("clip-slot", 0, 0, page), { type: "launch_clip", track: 1, scene: 1 });

    KEY_KINDS["page-left"].press({ state, page });
    KEY_KINDS["page-left"].press({ state, page });
    KEY_KINDS["page-up"].press({ state, page });
    KEY_KINDS["page-up"].press({ state, page });
    assert.deepEqual(page, { column: 0, row: 0 });
});

test("paging stops once the last track and scene reach the top-left key", () => {
    const page = noPage();
    for (let pressCount = 0; pressCount < 5; pressCount++) {
        KEY_KINDS["page-right"].press({ state, page });
        KEY_KINDS["page-down"].press({ state, page });
    }
    assert.deepEqual(page, { column: 1, row: 1 });
});

test("a queued clip and its scene blink, and a playing clip does not", () => {
    const context = { state, coordinates: { column: 1, row: 1 }, page: noPage() };
    assert.equal(KEY_KINDS["clip-slot"].view({ ...context, blinkOn: true }).fill, PALETTE.queued);
    assert.equal(KEY_KINDS["clip-slot"].view({ ...context, blinkOn: false }).fill, PALETTE.idle);
    assert.equal(KEY_KINDS["scene-launch"].view({ ...context, blinkOn: true }).fill, PALETTE.queued);
    assert.notEqual(KEY_KINDS["scene-launch"].view({ ...context, blinkOn: false }).fill, PALETTE.queued);
    assert.equal(KEY_KINDS["clip-slot"].view({ state, coordinates: { column: 0, row: 0 }, page: noPage(), blinkOn: false }).fill, PALETTE.playing);
});

test("the XL Launchpad fills every key: a clip grid, a scene column and a control row", () => {
    const layout = composeLaunchpadLayout({ columns: 8, rows: 4 });
    assert.equal(Object.keys(layout).length, 32);
    assert.equal(Object.values(layout).filter((kind) => kind === "clip-slot").length, 21);
    for (const row of [0, 1, 2]) {
        assert.equal(layout[`7,${row}`], "scene-launch");
    }
    assert.equal(layout["0,3"], "play-stop");
    assert.equal(layout["2,3"], "open-pads");
    assert.equal(layout["6,3"], "page-scenes");
    assert.equal(layout["7,3"], "open-desk");
    for (const kind of Object.values(layout)) {
        assert.ok(KEY_KINDS[kind], kind);
    }
});

const xl = { columns: 8, rows: 4 };

test("pads rise from the bottom left: 21 chromatic notes above the control row", () => {
    const notes = createNotePadState();
    assert.equal(noteForPad({ column: 0, row: 2 }, xl, notes), 60);
    assert.equal(noteForPad({ column: 1, row: 2 }, xl, notes), 61);
    assert.equal(noteForPad({ column: 0, row: 1 }, xl, notes), 67);
    assert.equal(noteForPad({ column: 6, row: 0 }, xl, notes), 80);
    assert.equal(noteForPad({ column: 7, row: 0 }, xl, notes), null);
    assert.equal(noteForPad({ column: 0, row: 3 }, xl, notes), null);
    assert.equal(describeNote(60), "C4");
    assert.equal(describeNote(61), "C#4");
});

test("scales keep a melody in key and match the studio's table", () => {
    const notes = { ...createNotePadState(), scale: "major" };
    assert.equal(noteForPad({ column: 1, row: 2 }, xl, notes), 62);
    assert.equal(noteForPad({ column: 2, row: 2 }, xl, notes), 64);
    assert.equal(noteForPad({ column: 0, row: 1 }, xl, notes), 72);
    notes.scale = "minor";
    assert.equal(noteForPad({ column: 2, row: 2 }, xl, notes), 63);
});

test("a pad sounds while held, and ends the note it began after an octave change", () => {
    const notes = createNotePadState();
    const pad = { state, coordinates: { column: 0, row: 2 }, deckSize: xl, notes, keyId: "pad-1" };
    assert.deepEqual(KEY_KINDS["note-pad"].press(pad), { type: "note_on", note: 60 });
    assert.equal(KEY_KINDS["note-pad"].view(pad).fill, PALETTE.playing);

    KEY_KINDS["octave-up"].press({ notes });
    assert.deepEqual(KEY_KINDS["note-pad"].release(pad), { type: "note_off", note: 60 });
    assert.equal(KEY_KINDS["note-pad"].release(pad), null);
    assert.equal(KEY_KINDS["note-pad"].view(pad).label, "C5");
    assert.equal(KEY_KINDS["note-pad"].view(pad).fill, PALETTE.solo);
});

test("octaves stop at the ends of MIDI, and scales cycle", () => {
    const notes = createNotePadState();
    for (let pressCount = 0; pressCount < 12; pressCount++) {
        KEY_KINDS["octave-up"].press({ notes });
    }
    assert.equal(notes.baseNote, 120);
    assert.equal(noteForPad({ column: 6, row: 0 }, xl, notes), null);
    for (let pressCount = 0; pressCount < 12; pressCount++) {
        KEY_KINDS["octave-down"].press({ notes });
    }
    assert.equal(notes.baseNote, 0);
    KEY_KINDS["scale-toggle"].press({ notes });
    assert.equal(notes.scale, "major");
    KEY_KINDS["scale-toggle"].press({ notes });
    KEY_KINDS["scale-toggle"].press({ notes });
    assert.equal(notes.scale, "chromatic");
});

test("the XL Pads profile has every key, and the two profiles lead to each other", () => {
    const layout = composePadsLayout(xl);
    assert.equal(Object.keys(layout).length, 32);
    assert.equal(Object.values(layout).filter((kind) => kind === "note-pad").length, 21);
    assert.equal(layout["1,3"], "record");
    assert.equal(layout["7,0"], "octave-up");
    for (const kind of Object.values(layout)) {
        assert.ok(KEY_KINDS[kind], kind);
    }
    assert.equal(KEY_KINDS[layout["7,3"]].opensProfile, "profiles/slaw Launchpad XL");
    assert.equal(KEY_KINDS[composeLaunchpadLayout(xl)["2,3"]].opensProfile, "profiles/slaw Pads XL");
});

test("the Mixer setup gives each track a Mute, Solo and Stop key", () => {
    const layout = composeMixerLayout(xl);
    assert.equal(Object.keys(layout).length, 32);
    assert.equal(layout["0,0"], "track-mute");
    assert.equal(layout["7,1"], "track-solo");
    assert.equal(layout["3,2"], "track-stop");
    assert.deepEqual(KEY_KINDS["track-stop"].press({ state, coordinates: { column: 1, row: 2 }, page: noPage() }), { type: "stop_track", track: 1 });
    assert.equal(KEY_KINDS["track-stop"].press({ state, coordinates: { column: 7, row: 2 }, page: noPage() }), null);
    assert.equal(KEY_KINDS["track-stop"].view({ state, coordinates: { column: 0, row: 2 }, page: noPage() }).fill, PALETTE.playing);
});

test("every setup uses real key kinds, every command key names a real studio command, and any setup reaches any other", () => {
    const setups = {
        "slaw Launchpad XL": composeLaunchpadLayout(xl),
        "slaw Pads XL": composePadsLayout(xl),
        "slaw Mixer XL": composeMixerLayout(xl),
        "slaw Desk XL": composeDeskLayout(xl),
    };
    const reachableFrom = {};
    for (const [name, layout] of Object.entries(setups)) {
        for (const kind of Object.values(layout)) {
            assert.ok(KEY_KINDS[kind], `${name}: ${kind}`);
        }
        reachableFrom[name] = Object.values(layout).map((kind) => KEY_KINDS[kind].opensProfile?.replace("profiles/", "")).filter(Boolean);
        for (const target of reachableFrom[name]) {
            assert.ok(setups[target], `${name} opens ${target}`);
        }
    }
    for (const start of Object.keys(setups)) {
        const seen = new Set([start]);
        const queue = [start];
        while (queue.length > 0) {
            for (const next of reachableFrom[queue.pop()]) {
                if (!seen.has(next)) {
                    seen.add(next);
                    queue.push(next);
                }
            }
        }
        assert.equal(seen.size, 4, start);
    }
});

test("the scenes key wraps round to the first scene", () => {
    const page = noPage();
    KEY_KINDS["page-scenes"].press({ state, page });
    assert.equal(page.row, 1);
    KEY_KINDS["page-scenes"].press({ state, page });
    assert.equal(page.row, 0);
});

test("switches ask for the opposite of what the track is doing", () => {
    assert.deepEqual(press("track-mute", 0, 0), { type: "set_track", track: 0, muted: true });
    assert.deepEqual(press("track-mute", 1, 0), { type: "set_track", track: 1, muted: false });
    assert.deepEqual(press("track-solo", 1, 0), { type: "set_track", track: 1, solo: false });
    assert.equal(view("track-solo", 1, 0).fill, PALETTE.solo);
});

test("transport keys show the studio's state", () => {
    assert.equal(view("play-stop", 0, 0).label, "Stop");
    assert.equal(view("play-stop", 0, 0).sublabel, "bar 9");
    assert.equal(view("record", 0, 0).fill, PALETTE.record);
    assert.equal(view("save", 0, 0).sublabel, "unsaved");
    assert.deepEqual(press("back-to-arrangement", 0, 0), { type: "back_to_arrangement" });
    assert.deepEqual(press("record", 0, 0), { type: "command", command: "ToggleRecord" });
});

test("every key view can be drawn, including hostile track names", () => {
    const hostileState = { ...state, scenes: ["<&\">", "Drop"], tracks: [{ ...state.tracks[0], name: "<script>&'\"" }, state.tracks[1]] };
    for (const [kind, keyKind] of Object.entries(KEY_KINDS)) {
        const image = renderKeyFace(keyKind.view({ state: hostileState, coordinates: { column: 0, row: 0 }, deckSize: xl, page: noPage(), notes: createNotePadState(), keyId: "key" }));
        assert.ok(image.startsWith("data:image/svg+xml,"), kind);
        assert.ok(!decodeURIComponent(image).includes("<script>"), kind);
    }
});

test("dials address the selected track with signed deltas", () => {
    assert.deepEqual(DIAL_KINDS["track-gain"].turn(state, -3), { type: "adjust", target: "gain", track: 1, delta: -0.06 });
    assert.deepEqual(DIAL_KINDS["track-pan"].turn(state, 2), { type: "adjust", target: "pan", track: 1, delta: 0.04 });
    assert.deepEqual(DIAL_KINDS.tempo.turn(state, 2), { type: "adjust", target: "tempo", delta: 2 });
    assert.deepEqual(DIAL_KINDS.master.turn(state, 1), { type: "adjust", target: "master", delta: 0.02 });
});

test("pushing a dial does its second job", () => {
    assert.deepEqual(DIAL_KINDS["track-gain"].push(state), { type: "set_track", track: 1, muted: false });
    assert.deepEqual(DIAL_KINDS["track-pan"].push(state), { type: "adjust", target: "pan", track: 1, delta: 0.5 });
    assert.deepEqual(DIAL_KINDS["select-track"].turn(state, -1), { type: "command", command: "SelectPreviousTrack" });
});

test("dial views show readable values and an in-range indicator", () => {
    assert.equal(DIAL_KINDS["track-gain"].view(state).value, "muted");
    assert.equal(DIAL_KINDS["track-pan"].view(state).value, "50L");
    assert.equal(DIAL_KINDS.tempo.view(state).value, "124 BPM");
    assert.equal(DIAL_KINDS.master.view(state).value, "0.0 dB");
    for (const [kind, dialKind] of Object.entries(DIAL_KINDS)) {
        const { indicator } = dialKind.view(state);
        assert.ok(indicator >= 0 && indicator <= 100, kind);
    }
});

test("track dials do nothing in a project with no tracks", () => {
    const emptyState = { ...state, tracks: [], selectedTrack: 0 };
    for (const kind of ["track-gain", "track-pan"]) {
        assert.equal(DIAL_KINDS[kind].turn(emptyState, 1), null);
        assert.equal(DIAL_KINDS[kind].push(emptyState), null);
        assert.equal(DIAL_KINDS[kind].view(emptyState).title, "no track");
    }
    assert.equal(DIAL_KINDS["select-track"].view(emptyState).value, "none");
});
