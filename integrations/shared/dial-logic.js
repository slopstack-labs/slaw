/**
 * What every dial kind does on a Stream Deck +, as plain data. `view` returns the
 * $B1 layout's fields, `turn` the request for some ticks, and `push` the request for
 * pressing it. Track dials follow the studio's selected track.
 */

function selectedTrack(state) {
    return state.tracks[state.selectedTrack];
}

function gainToDecibels(gain) {
    return gain <= 0.0001 ? "-∞ dB" : `${(20 * Math.log10(gain)).toFixed(1)} dB`;
}

function describePan(pan) {
    if (Math.abs(pan) < 0.005) {
        return "C";
    }
    return `${Math.round(Math.abs(pan) * 100)}${pan < 0 ? "L" : "R"}`;
}

const NO_TRACK_VIEW = Object.freeze({ title: "no track", value: "", indicator: 0 });

export const DIAL_KINDS = Object.freeze({
    "track-gain": {
        view: (state) => {
            const track = selectedTrack(state);
            return track
                ? { title: track.name, value: track.muted ? "muted" : gainToDecibels(track.gain), indicator: (track.gain / 1.5) * 100 }
                : NO_TRACK_VIEW;
        },
        turn: (state, ticks) => (selectedTrack(state) ? { type: "adjust", target: "gain", track: state.selectedTrack, delta: ticks * 0.02 } : null),
        push: (state) => {
            const track = selectedTrack(state);
            return track ? { type: "set_track", track: state.selectedTrack, muted: !track.muted } : null;
        },
    },
    "track-pan": {
        view: (state) => {
            const track = selectedTrack(state);
            return track ? { title: track.name, value: describePan(track.pan), indicator: ((track.pan + 1) / 2) * 100 } : NO_TRACK_VIEW;
        },
        turn: (state, ticks) => (selectedTrack(state) ? { type: "adjust", target: "pan", track: state.selectedTrack, delta: ticks * 0.02 } : null),
        // Pushing recentres, which is what a pan knob is pushed for.
        push: (state) => {
            const track = selectedTrack(state);
            return track ? { type: "adjust", target: "pan", track: state.selectedTrack, delta: -track.pan } : null;
        },
    },
    tempo: {
        view: (state) => ({ title: "Tempo", value: `${state.bpm.toFixed(state.bpm % 1 === 0 ? 0 : 2)} BPM`, indicator: ((state.bpm - 40) / 260) * 100 }),
        turn: (_state, ticks) => ({ type: "adjust", target: "tempo", delta: ticks }),
        push: () => ({ type: "command", command: "TogglePlayback" }),
    },
    master: {
        view: (state) => ({ title: "Master", value: gainToDecibels(state.masterGain), indicator: (state.masterGain / 2) * 100 }),
        turn: (_state, ticks) => ({ type: "adjust", target: "master", delta: ticks * 0.02 }),
        push: () => ({ type: "command", command: "Undo" }),
    },
    "select-track": {
        view: (state) => {
            const track = selectedTrack(state);
            return { title: "Track", value: track?.name ?? "none", indicator: state.tracks.length > 1 ? (state.selectedTrack / (state.tracks.length - 1)) * 100 : 0 };
        },
        turn: (_state, ticks) => ({ type: "command", command: ticks > 0 ? "SelectNextTrack" : "SelectPreviousTrack" }),
        push: () => ({ type: "command", command: "LaunchSelection" }),
    },
});

export const OFFLINE_DIAL_VIEW = Object.freeze({ title: "slaw", value: "offline", indicator: 0 });
