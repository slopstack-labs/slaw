import streamDeck, { SingletonAction } from "@elgato/streamdeck";
import WebSocket from "ws";
import { DIAL_KINDS, OFFLINE_DIAL_VIEW } from "../../shared/dial-logic.js";
import { SurfaceClient } from "../../shared/surface-client.js";

const PLUGIN_UUID = "com.slopstacklabs.slaw-plus";

const surface = new SurfaceClient({ webSocketClass: WebSocket, url: process.env.SLAW_SURFACE_URL });
surface.onError = (errorMessage) => streamDeck.logger.warn(`slaw refused a request: ${errorMessage}`);

/** Drawn feedback by action id: the studio republishes often, and the touch strip should only hear about changes. */
const drawnFeedback = new Map();

const registeredActions = [];

/** A dial can tick far faster than a mix should be rebuilt, so ticks are summed and sent at this pace. */
const TICK_FLUSH_MILLISECONDS = 60;
const pendingTicks = new Map();

setInterval(() => {
    for (const [dialAction, { deckAction, ticks }] of pendingTicks) {
        if (ticks === 0) {
            continue;
        }
        dialAction.perform(deckAction, (state) => dialAction.dialKind.turn(state, ticks));
    }
    pendingTicks.clear();
}, TICK_FLUSH_MILLISECONDS);

class SlawDialAction extends SingletonAction {
    constructor(dialKind) {
        super();
        this.dialKind = DIAL_KINDS[dialKind];
        this.manifestId = `${PLUGIN_UUID}.${dialKind}`;
    }

    onWillAppear(event) {
        drawnFeedback.delete(event.action.id);
        this.draw(event.action);
    }

    onWillDisappear(event) {
        drawnFeedback.delete(event.action.id);
    }

    onDialRotate(event) {
        const pending = pendingTicks.get(this) ?? { deckAction: event.action, ticks: 0 };
        pending.ticks += event.payload.ticks;
        pendingTicks.set(this, pending);
    }

    onDialDown(event) {
        this.perform(event.action, (state) => this.dialKind.push(state));
    }

    onTouchTap(event) {
        this.perform(event.action, (state) => this.dialKind.push(state));
    }

    perform(deckAction, composeRequest) {
        if (!surface.connected) {
            deckAction.showAlert();
            return;
        }
        const request = composeRequest(surface.state);
        if (request && !surface.send(request)) {
            deckAction.showAlert();
        }
    }

    draw(deckAction) {
        if (!deckAction.isDial()) {
            return;
        }
        const view = surface.connected ? this.dialKind.view(surface.state) : OFFLINE_DIAL_VIEW;
        const feedback = JSON.stringify(view);
        if (drawnFeedback.get(deckAction.id) !== feedback) {
            drawnFeedback.set(deckAction.id, feedback);
            deckAction.setFeedback({ title: view.title, value: view.value, indicator: Math.round(view.indicator) });
        }
    }
}

for (const dialKind of Object.keys(DIAL_KINDS)) {
    const registeredAction = new SlawDialAction(dialKind);
    registeredActions.push(registeredAction);
    streamDeck.actions.registerAction(registeredAction);
}

surface.subscribe(() => {
    for (const registeredAction of registeredActions) {
        for (const deckAction of registeredAction.actions) {
            registeredAction.draw(deckAction);
        }
    }
});
surface.start();
// No action stores settings, and the newer behaviour would refuse to start on Stream Deck before 7.1.
streamDeck.settings.useLegacySettingsBehavior = true;
streamDeck.connect();
