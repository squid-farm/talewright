import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../src/Talewright.Player/Components/StoryPlayer.razor.js", import.meta.url));
const { revealLatest } = await import(`data:text/javascript;base64,${source.toString("base64")}`);

function setup() {
    const calls = [];
    const toolbar = {};
    const outside = {};
    const latest = {
        isConnected: true,
        focus(options) { calls.push(["focus", options]); document.activeElement = this; },
        scrollIntoView(options) { calls.push(["scrollIntoView", options]); },
    };
    const player = { contains: element => element === toolbar || element === latest };
    const transcript = {
        querySelector: selector => selector === ".tw-latest" ? latest : null,
        closest: selector => selector === ".tw-player" ? player : null,
    };
    globalThis.document = { activeElement: outside };
    globalThis.window = {
        matchMedia: () => ({ matches: true }),
        scrollTo: options => calls.push(["scrollTo", options]),
    };
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
        ["scrollIntoView", { block: "start", behavior: "auto" }],
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
            document.documentElement = { dataset: { twReducedMotion: preference } };
            window.matchMedia = query => {
                assert.equal(query, "(prefers-reduced-motion: reduce)");
                return { matches: systemReducesMotion };
            };

            revealLatest(transcript, isFirst);

            assert.equal(document.activeElement, latest);
            assert.deepEqual(calls, [
                isFirst
                    ? ["scrollTo", { top: 0, behavior }]
                    : ["scrollIntoView", { block: "start", behavior }],
                ["focus", { preventScroll: true }],
            ]);
        });
    }
}
