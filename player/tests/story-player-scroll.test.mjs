import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../src/Talewright.Player/Components/StoryPlayer.razor.js", import.meta.url));
const { initialize, dispose } = await import(`data:text/javascript;base64,${source.toString("base64")}`);

function setup() {
    const calls = [];
    const listeners = new Map();
    const transcript = {
        addEventListener(type, listener, options) {
            calls.push(["add", type, options]);
            listeners.set(type, listener);
        },
        removeEventListener(type, listener, options) {
            calls.push(["remove", type, options]);
            assert.equal(listeners.get(type), listener);
            listeners.delete(type);
        },
    };
    globalThis.window = {
        scrollX: 12,
        scrollY: 1500,
        scrollTo: options => calls.push(["scrollTo", options]),
    };
    return { calls, listeners, transcript };
}

test("Pressing a passage control does not consume the input", () => {
    const { listeners, transcript } = setup();
    initialize(transcript);

    const event = new Event("pointerdown", { cancelable: true });
    listeners.get("pointerdown")(event);

    assert.equal(event.defaultPrevented, false);
});

test("Repeated passage reveals do not register duplicate input listeners", () => {
    const { calls, transcript } = setup();
    initialize(transcript);
    initialize(transcript);

    assert.deepEqual(calls, [["add", "pointerdown", { capture: true }]]);
});

test("Disposing a player removes its input listener and permits reinitialization", () => {
    const { calls, listeners, transcript } = setup();
    initialize(transcript);
    dispose(transcript);
    assert.equal(listeners.size, 0);
    dispose(transcript);
    initialize(transcript);

    assert.deepEqual(calls, [
        ["add", "pointerdown", { capture: true }],
        ["remove", "pointerdown", { capture: true }],
        ["add", "pointerdown", { capture: true }],
    ]);
});

test("A transcript removed during asynchronous initialization needs no listener", () => {
    const { calls } = setup();
    initialize(null);
    dispose(null);

    assert.deepEqual(calls, []);
});
