import assert from "node:assert/strict";
import { once } from "node:events";
import { test } from "node:test";
import { WebSocket, WebSocketServer } from "ws";
import { SurfaceClient } from "../surface-client.js";

async function startFakeStudio(port = 0) {
    const server = new WebSocketServer({ host: "127.0.0.1", port, path: "/surface" });
    await once(server, "listening");
    return { server, url: `ws://127.0.0.1:${server.address().port}/surface`, port: server.address().port };
}

function nextState(client, predicate) {
    return new Promise((resolve) => {
        const unsubscribe = client.subscribe((state) => {
            if (predicate(state)) {
                unsubscribe();
                resolve(state);
            }
        });
    });
}

test("the client reads commands and state, and sends requests as JSON", async () => {
    const { server, url } = await startFakeStudio();
    const received = new Promise((resolve) => {
        server.on("connection", (socket) => {
            socket.send(JSON.stringify({ type: "commands", commands: [{ id: "Undo", title: "Edit: Undo" }] }));
            socket.send(JSON.stringify({ type: "state", bpm: 124 }));
            socket.on("message", (data) => resolve(JSON.parse(String(data))));
        });
    });

    const client = new SurfaceClient({ webSocketClass: WebSocket, url });
    const connected = nextState(client, (state) => state?.bpm === 124);
    client.start();
    await connected;

    assert.equal(client.connected, true);
    assert.equal(client.commands[0].id, "Undo");
    assert.equal(client.send({ type: "command", command: "Undo" }), true);
    assert.deepEqual(await received, { type: "command", command: "Undo" });

    client.stop();
    server.close();
});

test("a refused request reaches onError", async () => {
    const { server, url } = await startFakeStudio();
    server.on("connection", (socket) => socket.send(JSON.stringify({ type: "error", message: "track 9 does not exist" })));

    const client = new SurfaceClient({ webSocketClass: WebSocket, url });
    const reported = new Promise((resolve) => {
        client.onError = resolve;
    });
    client.start();
    assert.equal(await reported, "track 9 does not exist");

    client.stop();
    server.close();
});

test("sending before the studio has spoken returns false instead of throwing", () => {
    const client = new SurfaceClient({ webSocketClass: WebSocket, url: "ws://127.0.0.1:9/surface" });
    assert.equal(client.send({ type: "command", command: "Undo" }), false);
});

test("the client goes offline when the studio closes and returns when it reopens", async () => {
    const first = await startFakeStudio();
    first.server.on("connection", (socket) => socket.send(JSON.stringify({ type: "state", bpm: 100 })));
    const client = new SurfaceClient({ webSocketClass: WebSocket, url: first.url });
    const online = nextState(client, (state) => state?.bpm === 100);
    client.start();
    await online;

    const offline = nextState(client, (state) => state === null);
    for (const socket of first.server.clients) {
        socket.terminate();
    }
    first.server.close();
    await offline;
    assert.equal(client.connected, false);
    assert.equal(client.send({ type: "command", command: "Undo" }), false);

    const second = await startFakeStudio(first.port);
    second.server.on("connection", (socket) => socket.send(JSON.stringify({ type: "state", bpm: 140 })));
    await nextState(client, (state) => state?.bpm === 140);
    assert.equal(client.connected, true);

    client.stop();
    second.server.close();
});

test("garbage from the socket is ignored", async () => {
    const { server, url } = await startFakeStudio();
    server.on("connection", (socket) => {
        socket.send("not json");
        socket.send(JSON.stringify({ type: "state", bpm: 90 }));
    });
    const client = new SurfaceClient({ webSocketClass: WebSocket, url });
    const online = nextState(client, (state) => state?.bpm === 90);
    client.start();
    await online;
    client.stop();
    server.close();
});
