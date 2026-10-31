// Generates the bundled setups (Launchpad, Pads, Mixer, Desk), so nobody has to drag 32 keys into place.
import { createHash } from "node:crypto";
import { deflateRawSync } from "node:zlib";

/** A stable GUID from a name, so rebuilding the plugin does not look like a new profile. */
function stableGuid(name) {
    const hex = createHash("sha1").update(`slaw-launchpad:${name}`).digest("hex");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-5${hex.slice(13, 16)}-a${hex.slice(17, 20)}-${hex.slice(20, 32)}`;
}

/**
 * Where every key goes on a deck: clips fill the grid, scene launches run down the right edge
 * as on a Launchpad, and the bottom row is transport and paging. Returns kind by "column,row".
 */
export function composeLaunchpadLayout({ columns, rows }) {
    const layout = {};
    for (let row = 0; row < rows - 1; row++) {
        for (let column = 0; column < columns - 1; column++) {
            layout[`${column},${row}`] = "clip-slot";
        }
        layout[`${columns - 1},${row}`] = "scene-launch";
    }
    placeRow(layout, rows - 1, ["play-stop", "record", "open-pads", "back-to-arrangement", "page-left", "page-right", "page-scenes", "open-desk"], columns);
    return layout;
}

/** Note pads in the grid's place, with octave and scale down the right edge and recording controls along the bottom. */
export function composePadsLayout({ columns, rows }) {
    const layout = {};
    const sideKeys = ["octave-up", "octave-down", "scale-toggle"];
    for (let row = 0; row < rows - 1; row++) {
        for (let column = 0; column < columns - 1; column++) {
            layout[`${column},${row}`] = "note-pad";
        }
        if (sideKeys[row]) {
            layout[`${columns - 1},${row}`] = sideKeys[row];
        }
    }
    placeRow(layout, rows - 1, ["play-stop", "record", "undo", "track-previous", "track-next", "scene-previous", "scene-next", "open-launchpad"], columns);
    return layout;
}

/** A row each of Mute, Solo and Stop, one column per track, with transport and the other setups below. */
export function composeMixerLayout({ columns, rows }) {
    const layout = {};
    ["track-mute", "track-solo", "track-stop"].forEach((kind, row) => placeRow(layout, row, Array(columns).fill(kind), columns));
    placeRow(layout, rows - 1, ["play-stop", "record", "undo", "page-left", "page-right", "open-launchpad", "open-pads", "open-desk"], columns);
    return layout;
}

/** The studio's everyday commands, for working at the computer with the deck beside the keyboard. */
export function composeDeskLayout({ columns, rows }) {
    const layout = {};
    placeRow(layout, 0, ["play-stop", "record", "back-to-arrangement", "switch-view", "undo", "redo", "save", "bounce"], columns);
    placeRow(layout, 1, ["add-track", "add-scene", "create-clip", "duplicate-clip", "copy-clip", "paste-clip", "delete-clip", "command-palette"], columns);
    placeRow(layout, 2, ["track-previous", "track-next", "scene-previous", "scene-next", "launch-selected-clip", "launch-selected-scene"], columns);
    placeRow(layout, rows - 1, ["open-launchpad", "open-pads", "open-mixer"], columns);
    return layout;
}

function placeRow(layout, row, kinds, columns) {
    kinds.slice(0, columns).forEach((kind, column) => {
        layout[`${column},${row}`] = kind;
    });
}

function pageManifest(layout, pluginUuid, actionNames, profileName) {
    const actions = {};
    for (const [position, kind] of Object.entries(layout)) {
        actions[position] = {
            ActionID: stableGuid(`${pluginUuid}:${profileName}:${position}`),
            LinkedTitle: true,
            Name: actionNames[kind],
            Resources: null,
            Settings: {},
            State: 0,
            States: [{}],
            UUID: `${pluginUuid}.${kind}`,
        };
    }
    return { Controllers: [{ Actions: actions, Type: "Keypad" }], Icon: "", Name: "" };
}

const crcTable = Array.from({ length: 256 }, (_, tableIndex) => {
    let crc = tableIndex;
    for (let bit = 0; bit < 8; bit++) {
        crc = crc & 1 ? 0xedb88320 ^ (crc >>> 1) : crc >>> 1;
    }
    return crc >>> 0;
});

function crc32(bytes) {
    let crc = 0xffffffff;
    for (const byte of bytes) {
        crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
    }
    return (crc ^ 0xffffffff) >>> 0;
}

/** A minimal zip writer: the plugins have enough dependencies. */
function zipEntries(entries) {
    const localParts = [];
    const centralParts = [];
    let offset = 0;
    for (const { path, content } of entries) {
        const nameBytes = Buffer.from(path, "utf8");
        const dataBytes = Buffer.from(content, "utf8");
        const deflated = deflateRawSync(dataBytes);
        const local = Buffer.alloc(30);
        local.writeUInt32LE(0x04034b50, 0);
        local.writeUInt16LE(20, 4);
        local.writeUInt16LE(0x0800, 6);
        local.writeUInt16LE(8, 8);
        local.writeUInt32LE(crc32(dataBytes), 14);
        local.writeUInt32LE(deflated.length, 18);
        local.writeUInt32LE(dataBytes.length, 22);
        local.writeUInt16LE(nameBytes.length, 26);
        localParts.push(local, nameBytes, deflated);

        const central = Buffer.alloc(46);
        central.writeUInt32LE(0x02014b50, 0);
        central.writeUInt16LE(20, 4);
        central.writeUInt16LE(20, 6);
        central.writeUInt16LE(0x0800, 8);
        central.writeUInt16LE(8, 10);
        central.writeUInt32LE(crc32(dataBytes), 16);
        central.writeUInt32LE(deflated.length, 20);
        central.writeUInt32LE(dataBytes.length, 24);
        central.writeUInt16LE(nameBytes.length, 28);
        central.writeUInt32LE(offset, 42);
        centralParts.push(central, nameBytes);
        offset += local.length + nameBytes.length + deflated.length;
    }
    const centralDirectory = Buffer.concat(centralParts);
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(entries.length, 8);
    end.writeUInt16LE(entries.length, 10);
    end.writeUInt32LE(centralDirectory.length, 12);
    end.writeUInt32LE(offset, 16);
    return Buffer.concat([...localParts, centralDirectory, end]);
}

/** Renders a .streamDeckProfile in the layout the Stream Deck app itself writes (format 3.0). */
export function renderProfile({ name, model, layout, pluginUuid, actionNames }) {
    const profileGuid = stableGuid(`${name}:profile`).toUpperCase();
    const mainPage = stableGuid(`${name}:page`);
    const defaultPage = stableGuid(`${name}:default-page`);
    const root = `${profileGuid}.sdProfile`;
    return zipEntries([
        {
            path: `${root}/manifest.json`,
            content: JSON.stringify({
                Device: { Model: model, UUID: "" },
                Name: name,
                Pages: { Current: mainPage, Default: defaultPage, Pages: [mainPage] },
                Version: "3.0",
            }),
        },
        {
            path: `${root}/Profiles/${mainPage.toUpperCase()}/manifest.json`,
            content: JSON.stringify(pageManifest(layout, pluginUuid, actionNames, name)),
        },
        {
            path: `${root}/Profiles/${defaultPage.toUpperCase()}/manifest.json`,
            content: JSON.stringify({ Controllers: [{ Actions: null, Type: "Keypad" }], Icon: "", Name: "" }),
        },
    ]);
}
