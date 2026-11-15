import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../src/Talewright.Player/Components/StoryPlayer.razor.js", import.meta.url));
const { revealLatest } = await import(`data:text/javascript;base64,${source.toString("base64")}`);

function setup() {
    const calls = [];
    const toolbar = { getBoundingClientRect: () => ({ bottom: 60, height: 60 }) };
    const outside = {};
    const latest = {
        isConnected: true,
        focus(options) { calls.push(["focus", options]); document.activeElement = this; },
        getBoundingClientRect: () => ({ top: 400 }),
    };
    const player = {
        contains: element => element === toolbar || element === latest,
        querySelector: () => toolbar,
    };
    const transcript = {
        querySelector: selector => selector === ".tw-latest" ? latest : null,
        closest: selector => selector === ".tw-player" ? player : null,
        addEventListener() {},
    };
    globalThis.document = {
        activeElement: outside,
        documentElement: { dataset: {}, scrollHeight: 2000 },
        addEventListener() {},
    };
    globalThis.window = {
        matchMedia: () => ({ matches: true }),
        innerHeight: 800,
        scrollY: 0,
        scrollTo: options => calls.push(["scrollTo", options]),
        addEventListener() {},
    };
    globalThis.requestAnimationFrame = () => 1;
    globalThis.cancelAnimationFrame = () => {};
    globalThis.getComputedStyle = () => ({ top: "0px" });
    return { calls, latest, outside, toolbar, transcript };
}

test("A delayed initial reveal does not steal focus from the player's toolbar", () => {
    const { calls, toolbar, transcript } = setup();
    document.activeElement = toolbar;

    revealLatest(transcript, true, true);

    assert.equal(document.activeElement, toolbar);
    assert.deepEqual(calls, []);
});

test("An initial reveal still focuses the first passage when focus is outside the player", () => {
    const { calls, latest, transcript } = setup();

    revealLatest(transcript, true, true);

    assert.equal(document.activeElement, latest);
    assert.deepEqual(calls, [
        ["scrollTo", { top: 0, behavior: "auto" }],
        ["focus", { preventScroll: true }],
    ]);
});

test("Explicit navigation still reveals and focuses a passage from the toolbar", () => {
    const { calls, latest, toolbar, transcript } = setup();
    document.activeElement = toolbar;

    revealLatest(transcript, false, false);

    assert.equal(document.activeElement, latest);
    assert.deepEqual(calls, [
        ["scrollTo", { top: 340, behavior: "auto" }],
        ["focus", { preventScroll: true }],
    ]);
});

test("Fractional scroll positions keep the passage below the toolbar", () => {
    const { calls, latest, toolbar, transcript } = setup();
    latest.getBoundingClientRect = () => ({ top: 400.7 });
    toolbar.getBoundingClientRect = () => ({ bottom: 60.1, height: 60.1 });

    revealLatest(transcript, false);

    assert.deepEqual(calls[0], ["scrollTo", { top: 340, behavior: "auto" }]);
});

for (const timing of ["unchanged layout", "layout changed", "image loaded"]) {
    test(`A scrollbar drag interrupting the initial smooth scroll prevents later image realignment (${timing})`, () => {
        const { calls, latest, transcript } = setup();
        const listeners = new Map();
        const frames = new Map();
        let frameId = 0;
        let sceneTop = 400;
        const listen = (target, type, listener, options) => listeners.set(`${target}:${type}`, { listener, signal: options.signal });
        const dispatch = (target, type, event = {}) => {
            const entry = listeners.get(`${target}:${type}`);
            if (entry && !entry.signal.aborted) entry.listener(event);
        };
        window.addEventListener = (type, listener, options) => listen("window", type, listener, options);
        document.addEventListener = (type, listener, options) => listen("document", type, listener, options);
        transcript.addEventListener = (type, listener, options) => listen("transcript", type, listener, options);
        window.matchMedia = () => ({ matches: false });
        latest.getBoundingClientRect = () => ({ top: sceneTop - window.scrollY });
        globalThis.requestAnimationFrame = callback => { frames.set(++frameId, callback); return frameId; };
        globalThis.cancelAnimationFrame = id => frames.delete(id);
        globalThis.HTMLImageElement = class {};

        revealLatest(transcript, false);
        if (timing !== "unchanged layout") document.documentElement.scrollHeight += 100;
        if (timing === "image loaded") {
            window.scrollY = 80;
            dispatch("transcript", "load", { target: new HTMLImageElement() });
        }
        window.scrollY = 120;
        dispatch("window", "scroll");
        dispatch("window", "scrollend");
        sceneTop += 100;
        document.documentElement.scrollHeight += 100;
        dispatch("transcript", "load", { target: new HTMLImageElement() });
        while (frames.size) {
            const [id, callback] = frames.entries().next().value;
            frames.delete(id);
            callback();
        }

        assert.deepEqual(calls.filter(([name]) => name === "scrollTo"), [
            ["scrollTo", { top: 340, behavior: "smooth" }],
        ]);
        assert.equal(window.scrollY, 120);
        assert.equal(document.activeElement, latest);
    });
}

test("A delayed adjustment scroll does not cancel later image realignment", () => {
    const { calls, latest, transcript } = setup();
    const listeners = new Map();
    const frames = new Map();
    let frameId = 0;
    let sceneTop = 400;
    const listen = (target, type, listener, options) => listeners.set(`${target}:${type}`, { listener, signal: options.signal });
    const dispatch = (target, type, event = {}) => {
        const entry = listeners.get(`${target}:${type}`);
        if (entry && !entry.signal.aborted) entry.listener(event);
    };
    const runFrame = () => {
        const [id, callback] = frames.entries().next().value;
        frames.delete(id);
        callback();
    };
    window.addEventListener = (type, listener, options) => listen("window", type, listener, options);
    document.addEventListener = (type, listener, options) => listen("document", type, listener, options);
    transcript.addEventListener = (type, listener, options) => listen("transcript", type, listener, options);
    window.scrollTo = options => { calls.push(["scrollTo", options]); window.scrollY = options.top; };
    latest.getBoundingClientRect = () => ({ top: sceneTop - window.scrollY });
    globalThis.requestAnimationFrame = callback => { frames.set(++frameId, callback); return frameId; };
    globalThis.cancelAnimationFrame = id => frames.delete(id);
    globalThis.HTMLImageElement = class {};

    revealLatest(transcript, false);
    runFrame();
    sceneTop += 100;
    document.documentElement.scrollHeight += 100;
    dispatch("transcript", "load", { target: new HTMLImageElement() });
    runFrame();
    dispatch("window", "scrollend");
    runFrame();
    dispatch("window", "scroll");
    sceneTop += 100;
    document.documentElement.scrollHeight += 100;
    dispatch("transcript", "load", { target: new HTMLImageElement() });
    assert.equal(frames.size, 1);
    runFrame();

    assert.deepEqual(calls.filter(([name]) => name === "scrollTo").map(([, options]) => options.top), [340, 440, 540]);
});

test("Restart still focuses the first passage from the toolbar", () => {
    const { calls, latest, toolbar, transcript } = setup();
    document.activeElement = toolbar;

    revealLatest(transcript, true, false);

    assert.equal(document.activeElement, latest);
    assert.deepEqual(calls, [
        ["scrollTo", { top: 0, behavior: "auto" }],
        ["focus", { preventScroll: true }],
    ]);
});

for (const { preference, systemReducesMotion, behavior } of [
    { preference: "system", systemReducesMotion: false, behavior: "smooth" },
    { preference: "system", systemReducesMotion: true, behavior: "auto" },
    { preference: "reduce", systemReducesMotion: false, behavior: "auto" },
    { preference: "reduce", systemReducesMotion: true, behavior: "auto" },
]) {
    for (const isFirst of [true, false]) {
        test(`${preference} preference with OS reduction ${systemReducesMotion} uses ${behavior} for ${isFirst ? "first" : "later"} passages`, () => {
            const { calls, latest, transcript } = setup();
            document.documentElement.dataset.twReducedMotion = preference;
            window.matchMedia = query => {
                assert.equal(query, "(prefers-reduced-motion: reduce)");
                return { matches: systemReducesMotion };
            };

            revealLatest(transcript, isFirst);

            assert.equal(document.activeElement, latest);
            assert.deepEqual(calls, [
                isFirst
                    ? ["scrollTo", { top: 0, behavior }]
                    : ["scrollTo", { top: 340, behavior }],
                ["focus", { preventScroll: true }],
            ]);
        });
    }
}
