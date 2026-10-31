import streamDeck, { DeviceType, SingletonAction } from "@elgato/streamdeck";
import WebSocket from "ws";
import { renderKeyFace } from "../../shared/key-face.js";
import { createNotePadState, KEY_KINDS, OFFLINE_FACE } from "../../shared/keypad-logic.js";
import { SurfaceClient } from "../../shared/surface-client.js";

const PLUGIN_UUID = "com.slopstacklabs.slaw-keypad";

const surface = new SurfaceClient({ webSocketClass: WebSocket, url: process.env.SLAW_SURFACE_URL });
surface.onError = (errorMessage) => streamDeck.logger.warn(`slaw refused a request: ${errorMessage}`);

/** The grid's scroll position, shared by every key on the deck. */
const page = { column: 0, row: 0 };

/** The note pads' octave, scale and held notes, shared by every key on the deck. */
const notes = createNotePadState();

/** Drawn faces by action id: the studio republishes often, and the deck should only hear about changes. */
const drawnFaces = new Map();

const registeredActions = [];

const QUEUED_BLINK_MILLISECONDS = 300;
let blinkOn = true;

/** Everything a key kind may ask about itself. */
function describeKey(deckAction) {
    return {
        state: surface.state,
        coordinates: deckAction.coordinates ?? { column: 0, row: 0 },
        deckSize: deckAction.device.size,
        page,
        notes,
        blinkOn,
        keyId: deckAction.id,
    };
}

class SlawKeyAction extends SingletonAction {
    constructor(keyKind) {
        super();
        this.keyKind = KEY_KINDS[keyKind];
        this.manifestId = `${PLUGIN_UUID}.${keyKind}`;
    }

    onWillAppear(event) {
        drawnFaces.delete(event.action.id);
        this.draw(event.action);
    }

    onWillDisappear(event) {
        drawnFaces.delete(event.action.id);
        // Switching profiles with a pad held must not leave its note ringing.
        this.release(event.action);
    }

    onKeyUp(event) {
        this.release(event.action);
        redrawEverything();
    }

    release(deckAction) {
        const request = this.keyKind.release?.({ notes, keyId: deckAction.id });
        if (request) {
            surface.send(request);
        }
    }

    onKeyDown(event) {
        if (this.keyKind.opensProfile) {
            streamDeck.profiles.switchToProfile(event.action.device.id, this.keyKind.opensProfile);
            return;
        }
        if (!surface.connected) {
            event.action.showAlert();
            return;
        }
        const request = this.keyKind.press(describeKey(event.action));
        if (request && !surface.send(request)) {
            event.action.showAlert();
        }
        // Paging changes what every grid key shows, with no message from the studio to trigger it.
        redrawEverything();
    }

    draw(deckAction) {
        if (!deckAction.isKey()) {
            return;
        }
        const face = surface.connected
            ? this.keyKind.view(describeKey(deckAction))
            : OFFLINE_FACE;
        const image = renderKeyFace(face);
        if (drawnFaces.get(deckAction.id) !== image) {
            drawnFaces.set(deckAction.id, image);
            deckAction.setImage(image);
        }
    }
}

function redrawEverything() {
    for (const registeredAction of registeredActions) {
        for (const deckAction of registeredAction.actions) {
            registeredAction.draw(deckAction);
        }
    }
}

for (const keyKind of Object.keys(KEY_KINDS)) {
    const registeredAction = new SlawKeyAction(keyKind);
    registeredActions.push(registeredAction);
    streamDeck.actions.registerAction(registeredAction);
}

surface.subscribe(redrawEverything);
setInterval(() => {
    if (surface.state?.tracks.some((track) => track.slots.includes("queued"))) {
        blinkOn = !blinkOn;
        redrawEverything();
    } else {
        blinkOn = true;
    }
}, QUEUED_BLINK_MILLISECONDS);
surface.start();
// No action stores settings, and the newer behaviour would refuse to start on Stream Deck before 7.1.
streamDeck.settings.useLegacySettingsBehavior = true;

// streamdeck://plugins/message/<uuid>/open-pads puts the deck on a profile from outside the deck, and makes
// the app offer to install a profile an upgrade did not bring. Only the XL has these profiles so far.
const DEEP_LINK_PROFILES = { "open-pads": "profiles/slaw Pads XL", "open-launchpad": "profiles/slaw Launchpad XL" };
streamDeck.system.onDidReceiveDeepLink((event) => {
    const profile = DEEP_LINK_PROFILES[event.url.path.replace(/^\/+|\/+$/g, "")];
    if (!profile) {
        streamDeck.logger.warn(`unknown deep link: ${event.url.path}`);
        return;
    }
    streamDeck.devices.forEach((device) => {
        if (device.type === DeviceType.StreamDeckXL) {
            streamDeck.profiles.switchToProfile(device.id, profile);
        }
    });
});
streamDeck.connect();
