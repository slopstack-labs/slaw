/** The studio's default deck port: Settings → Integrations → Deck control. */
export const DEFAULT_SURFACE_URL = "ws://127.0.0.1:47116/surface";

const MINIMUM_RETRY_MILLISECONDS = 500;
const MAXIMUM_RETRY_MILLISECONDS = 5000;

/**
 * One connection to the studio, shared by every action in a plugin. It reconnects
 * for as long as it lives, because the studio is opened and closed all day and a
 * deck is plugged in all week.
 */
export class SurfaceClient {
    #url;
    #webSocketClass;
    #retryMilliseconds = MINIMUM_RETRY_MILLISECONDS;
    #retryTimer = null;
    #socket = null;
    #stopped = false;
    #listeners = new Set();

    /** The latest state message from the studio, or null while disconnected. */
    state = null;

    /** Command ids and titles, from the studio's own table. */
    commands = [];

    /** Called with the text of any request the studio refused. */
    onError = () => {};

    /** @param {{webSocketClass: typeof WebSocket, url?: string}} options */
    constructor({ webSocketClass, url = DEFAULT_SURFACE_URL }) {
        this.#webSocketClass = webSocketClass;
        this.#url = url;
    }

    get connected() {
        return this.state !== null;
    }

    /** Registers a listener for state changes and returns the function that removes it. */
    subscribe(listener) {
        this.#listeners.add(listener);
        return () => this.#listeners.delete(listener);
    }

    start() {
        this.#stopped = false;
        this.#connect();
    }

    stop() {
        this.#stopped = true;
        clearTimeout(this.#retryTimer);
        this.#socket?.close();
    }

    /** Sends a request. Returns false when the studio is not there to hear it. */
    send(request) {
        if (!this.connected || this.#socket?.readyState !== this.#webSocketClass.OPEN) {
            return false;
        }
        this.#socket.send(JSON.stringify(request));
        return true;
    }

    #connect() {
        let socket;
        try {
            socket = new this.#webSocketClass(this.#url);
        } catch {
            this.#scheduleRetry();
            return;
        }
        this.#socket = socket;
        socket.addEventListener("message", (event) => this.#receive(String(event.data)));
        socket.addEventListener("close", () => this.#handleClosed(socket));
        // A refused connection also fires close, which does the retrying.
        socket.addEventListener("error", () => {});
    }

    #receive(messageText) {
        let message;
        try {
            message = JSON.parse(messageText);
        } catch {
            return;
        }
        switch (message.type) {
            case "commands":
                this.commands = message.commands;
                this.#retryMilliseconds = MINIMUM_RETRY_MILLISECONDS;
                break;
            case "state":
                this.state = message;
                this.#notify();
                break;
            case "error":
                this.onError(message.message);
                break;
        }
    }

    #handleClosed(closedSocket) {
        if (closedSocket !== this.#socket) {
            return;
        }
        this.#socket = null;
        if (this.state !== null) {
            this.state = null;
            this.#notify();
        }
        if (!this.#stopped) {
            this.#scheduleRetry();
        }
    }

    #scheduleRetry() {
        this.#retryTimer = setTimeout(() => this.#connect(), this.#retryMilliseconds);
        this.#retryMilliseconds = Math.min(this.#retryMilliseconds * 2, MAXIMUM_RETRY_MILLISECONDS);
    }

    #notify() {
        for (const listener of this.#listeners) {
            listener(this.state);
        }
    }
}
