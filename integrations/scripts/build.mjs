// Builds each deck plugin into its own .sdPlugin folder: generated manifest, icons and bundled code.
// `--validate` also runs Elgato's validator; `--pack` also makes the installable .streamDeckPlugin files.
import { execFileSync } from "node:child_process";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { deflateSync } from "node:zlib";
import { build } from "esbuild";
import { DIAL_KINDS } from "../shared/dial-logic.js";
import { KEY_KINDS } from "../shared/keypad-logic.js";
import { composeLaunchpadLayout, composePadsLayout, renderProfile } from "./launchpad-profile.mjs";

const integrationsRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const pluginVersion = "0.2.0.0";

const KEY_ACTION_DETAILS = {
    "play-stop": ["Play / Stop", "Starts or stops whichever view is showing. Lights up while playing."],
    record: ["Record", "Engages or releases record. Lights up while engaged."],
    undo: ["Undo", "Undoes the last edit."],
    redo: ["Redo", "Redoes the last undone edit."],
    save: ["Save", "Saves the project. Lights up while there are unsaved changes."],
    "switch-view": ["Session / Arrangement", "Switches between the two views."],
    "back-to-arrangement": ["Back to Arrangement", "Hands every track back to the arrangement on the next bar. Lights up while session clips play over it."],
    "clip-slot": ["Clip Slot", "Launches one session clip. The key's column is the track and its row is the scene, so fill the deck with these to get a clip grid. Pressing a playing clip stops its track."],
    "scene-launch": ["Scene Launch", "Launches a whole scene. The key's row picks the scene."],
    "track-mute": ["Track Mute", "Mutes a track. The key's column picks the track."],
    "track-solo": ["Track Solo", "Solos a track. The key's column picks the track."],
    "note-pad": ["Note Pad", "Plays the selected track while held, and records with Record on. The key's position picks the note, rising from the bottom left."],
    "octave-up": ["Pads: Octave Up", "Moves the note pads up an octave."],
    "octave-down": ["Pads: Octave Down", "Moves the note pads down an octave."],
    "scale-toggle": ["Pads: Scale", "Switches the note pads between chromatic, major and minor."],
    "track-previous": ["Previous Track", "Selects the previous track."],
    "track-next": ["Next Track", "Selects the next track."],
    "scene-previous": ["Previous Scene", "Selects the previous scene."],
    "scene-next": ["Next Scene", "Selects the next scene."],
    "open-pads": ["Open Pads Profile", "Switches the deck to the slaw Pads XL profile."],
    "open-launchpad": ["Open Launchpad Profile", "Switches the deck to the slaw Launchpad XL profile."],
    "page-left": ["Grid: Previous Tracks", "Scrolls the clip grid one track left."],
    "page-right": ["Grid: Next Tracks", "Scrolls the clip grid one track right."],
    "page-up": ["Grid: Previous Scenes", "Scrolls the clip grid one scene up."],
    "page-down": ["Grid: Next Scenes", "Scrolls the clip grid one scene down."],
};

const DIAL_ACTION_DETAILS = {
    "track-gain": ["Track Volume", "Volume of the selected track.", "Volume", "Mute"],
    "track-pan": ["Track Pan", "Pan of the selected track.", "Pan", "Centre"],
    tempo: ["Tempo", "Project tempo, one BPM per tick.", "Tempo", "Play / Stop"],
    master: ["Master Volume", "Master fader.", "Master volume", "Undo"],
    "select-track": ["Select Track", "Moves the track selection that the other dials follow.", "Select track", "Launch selected clip"],
};

const baseManifest = {
    Author: "slopstack labs",
    CategoryIcon: "imgs/category",
    CodePath: "bin/plugin.cjs",
    Icon: "imgs/plugin",
    Nodejs: { Version: "20" },
    OS: [
        { Platform: "windows", MinimumVersion: "10" },
        { Platform: "mac", MinimumVersion: "12" },
    ],
    SDKVersion: 2,
    Software: { MinimumVersion: "6.4" },
    Version: pluginVersion,
};

const plugins = [
    {
        folder: "streamdeck-keypad",
        uuid: "com.slopstacklabs.slaw-keypad",
        // Device type 2 is the XL. Other decks can get theirs here, once one is on a desk to test with.
        profiles: [
            { name: "slaw Launchpad XL", deviceType: 2, model: "20GAT9901", layout: composeLaunchpadLayout({ columns: 8, rows: 4 }) },
            { name: "slaw Pads XL", deviceType: 2, model: "20GAT9901", layout: composePadsLayout({ columns: 8, rows: 4 }) },
        ],
        manifest: {
            Name: "slaw Keys",
            Description: "Transport keys, a clip grid and track switches for the slaw studio, on any Stream Deck with keys.",
            Actions: Object.keys(KEY_KINDS).map((kind) => ({
                Name: KEY_ACTION_DETAILS[kind][0],
                Tooltip: KEY_ACTION_DETAILS[kind][1],
                UUID: `com.slopstacklabs.slaw-keypad.${kind}`,
                Icon: "imgs/action",
                Controllers: ["Keypad"],
                States: [{ Image: "imgs/state", ShowTitle: false }],
            })),
        },
    },
    {
        folder: "streamdeck-plus",
        uuid: "com.slopstacklabs.slaw-plus",
        manifest: {
            Name: "slaw Dials",
            Description: "Volume, pan, tempo and track selection for the slaw studio, on the Stream Deck + dials and touch strip.",
            Actions: Object.keys(DIAL_KINDS).map((kind) => ({
                Name: DIAL_ACTION_DETAILS[kind][0],
                Tooltip: DIAL_ACTION_DETAILS[kind][1],
                UUID: `com.slopstacklabs.slaw-plus.${kind}`,
                Icon: "imgs/action",
                Controllers: ["Encoder"],
                Encoder: {
                    layout: "$B1",
                    TriggerDescription: { Rotate: DIAL_ACTION_DETAILS[kind][2], Push: DIAL_ACTION_DETAILS[kind][3], Touch: DIAL_ACTION_DETAILS[kind][3] },
                },
                States: [{ Image: "imgs/state", ShowTitle: false }],
            })),
        },
    },
];

const monochromeTurntable = (size) =>
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 20 20"><circle cx="10" cy="10" r="8" fill="none" stroke="#FFFFFF" stroke-width="2"/><circle cx="10" cy="10" r="2.5" fill="#FFFFFF"/></svg>`;

const stateImage = (size) =>
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 72 72"><rect width="72" height="72" fill="#16181d"/><circle cx="36" cy="36" r="24" fill="none" stroke="#7a7f8a" stroke-width="5"/><circle cx="36" cy="36" r="7" fill="#7a7f8a"/></svg>`;

/** The plugin icon must be a PNG, so it is drawn here: a turntable, because slaw has a brand. */
function renderTurntablePng(size) {
    const center = size / 2;
    const raster = Buffer.alloc((size * 4 + 1) * size);
    for (let row = 0; row < size; row++) {
        const rowOffset = row * (size * 4 + 1);
        raster[rowOffset] = 0;
        for (let column = 0; column < size; column++) {
            const distance = Math.hypot(column + 0.5 - center, row + 0.5 - center) / center;
            const inRing = distance > 0.62 && distance < 0.8;
            const inHub = distance < 0.2;
            const [red, green, blue] = inHub ? [47, 191, 113] : inRing ? [242, 243, 245] : [22, 24, 29];
            raster.set([red, green, blue, 255], rowOffset + 1 + column * 4);
        }
    }
    const crcTable = Array.from({ length: 256 }, (_, tableIndex) => {
        let crc = tableIndex;
        for (let bit = 0; bit < 8; bit++) {
            crc = crc & 1 ? 0xedb88320 ^ (crc >>> 1) : crc >>> 1;
        }
        return crc >>> 0;
    });
    const crcOf = (bytes) => {
        let crc = 0xffffffff;
        for (const byte of bytes) {
            crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
        }
        return (crc ^ 0xffffffff) >>> 0;
    };
    const chunk = (chunkType, chunkData) => {
        const typedData = Buffer.concat([Buffer.from(chunkType, "ascii"), chunkData]);
        const lengthField = Buffer.alloc(4);
        lengthField.writeUInt32BE(chunkData.length);
        const crcField = Buffer.alloc(4);
        crcField.writeUInt32BE(crcOf(typedData));
        return Buffer.concat([lengthField, typedData, crcField]);
    };
    const header = Buffer.alloc(13);
    header.writeUInt32BE(size, 0);
    header.writeUInt32BE(size, 4);
    header[8] = 8;
    header[9] = 6;
    return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", header), chunk("IDAT", deflateSync(raster)), chunk("IEND", Buffer.alloc(0))]);
}

function runElgatoCli(...cliArguments) {
    const cliEntry = join(integrationsRoot, "node_modules", "@elgato", "cli", "bin", "streamdeck.mjs");
    execFileSync(process.execPath, [cliEntry, ...cliArguments], { cwd: integrationsRoot, stdio: "inherit" });
}

const shouldValidate = process.argv.includes("--validate");
const shouldPack = process.argv.includes("--pack");

for (const plugin of plugins) {
    const pluginFolder = join(integrationsRoot, plugin.folder);
    const bundleFolder = join(pluginFolder, `${plugin.uuid}.sdPlugin`);
    rmSync(bundleFolder, { recursive: true, force: true });
    mkdirSync(join(bundleFolder, "imgs"), { recursive: true });

    const manifest = { ...baseManifest, ...plugin.manifest, Category: plugin.manifest.Name, UUID: plugin.uuid };
    const profiles = plugin.profiles ?? [];
    if (profiles.length > 0) {
        // Only the first takes over the deck on install; the other waits for its key.
        manifest.Profiles = profiles.map((profile, profileIndex) => ({ Name: `profiles/${profile.name}`, DeviceType: profile.deviceType, Readonly: false, DontAutoSwitchWhenInstalled: profileIndex > 0 }));
        mkdirSync(join(bundleFolder, "profiles"), { recursive: true });
        const actionNames = Object.fromEntries(Object.entries(KEY_ACTION_DETAILS).map(([kind, details]) => [kind, details[0]]));
        for (const profile of profiles) {
            writeFileSync(join(bundleFolder, "profiles", `${profile.name}.streamDeckProfile`), renderProfile({ ...profile, pluginUuid: plugin.uuid, actionNames }));
        }
    }
    writeFileSync(join(bundleFolder, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
    writeFileSync(join(bundleFolder, "imgs", "plugin.png"), renderTurntablePng(256));
    writeFileSync(join(bundleFolder, "imgs", "plugin@2x.png"), renderTurntablePng(512));
    writeFileSync(join(bundleFolder, "imgs", "category.svg"), monochromeTurntable(28));
    writeFileSync(join(bundleFolder, "imgs", "action.svg"), monochromeTurntable(20));
    writeFileSync(join(bundleFolder, "imgs", "state.svg"), stateImage(72));

    await build({
        entryPoints: [join(pluginFolder, "src", "plugin.js")],
        bundle: true,
        platform: "node",
        target: "node20",
        format: "cjs",
        outfile: join(bundleFolder, "bin", "plugin.cjs"),
        logLevel: "warning",
    });
    console.log(`built ${plugin.uuid}.sdPlugin`);

    if (shouldValidate) {
        runElgatoCli("validate", bundleFolder, "--no-update-check");
    }
    if (shouldPack) {
        mkdirSync(join(integrationsRoot, "dist"), { recursive: true });
        runElgatoCli("pack", bundleFolder, "--output", join(integrationsRoot, "dist"), "--force", "--no-update-check");
    }
}
