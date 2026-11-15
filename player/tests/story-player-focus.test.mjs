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
