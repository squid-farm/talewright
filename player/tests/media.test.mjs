import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../src/Talewright.Player/Components/MediaView.razor.js", import.meta.url));
const { observe, load, release, createUrl, revokeUrl, display } = await import(`data:text/javascript;base64,${source.toString("base64")}`);
let observer;
globalThis.IntersectionObserver = class {
    constructor(callback, options) { this.callback = callback; this.options = options; observer = this; }
    observe(image) { this.image = image; }
    disconnect() { this.disconnected = true; }
};
const active = new Set();
let created = 0;
URL.createObjectURL = blob => {
    assert.ok(blob.size > 0);
    const url = `blob:test-${++created}`;
    active.add(url);
    return url;
};
URL.revokeObjectURL = url => {
    assert.ok(active.delete(url), "Each URL is revoked exactly once");
};
const image = () => ({
    src: null,
    removeAttribute(name) { assert.equal(name, "src"); this.src = null; },
});
const receiver = () => ({ calls: 0, async invokeMethodAsync(name) { assert.equal(name, "LoadAsync"); this.calls++; } });
const stream = { async arrayBuffer() { return new Uint8Array([1, 2, 3]).buffer; } };

test("Loading is deferred until viewport proximity, with no persistent observer after request", () => {
    const target = image();
    const dotnet = receiver();
    const id = observe(target, dotnet);
    assert.equal(dotnet.calls, 0);
    assert.equal(target.src, null);
    assert.equal(observer.options.rootMargin, "200px");
    observer.callback([{ isIntersecting: false }]);
    assert.equal(dotnet.calls, 0);
    observer.callback([{ isIntersecting: true }]);
    assert.equal(dotnet.calls, 1);
    assert.equal(observer.disconnected, true);
    release(id);
});

test("A decoded mounted/history image keeps its URL until its owner releases it", async () => {
    const target = image();
    const id = observe(target, receiver());
    const loading = load(id, stream, "image/jpeg");
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    const url = target.src;
    assert.ok(active.has(url));
    target.onload();
    assert.equal(await loading, true);
    assert.ok(active.has(url));
    assert.equal(target.src, url);
    release(id);
    assert.equal(target.src, null);
    assert.equal(active.has(url), false);
    release(id);
});

test("A stale stream completion immediately revokes its URL without updating a removed image", async () => {
    const target = image();
    const id = observe(target, receiver());
    let complete;
    const before = created;
    const loading = load(id, { arrayBuffer: () => new Promise(resolve => { complete = resolve; }) }, "image/png");
    release(id);
    complete(new Uint8Array([1]).buffer);
    assert.equal(await loading, false);
    assert.equal(target.src, null);
    assert.equal(created, before + 1);
    assert.equal(active.size, 0);
});

test("Disposal during decoding resolves the pending request and revokes the URL", async () => {
    const target = image();
    const id = observe(target, receiver());
    const loading = load(id, stream, "image/webp");
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    const url = target.src;
    release(id);
    assert.equal(await loading, false);
    assert.equal(target.src, null);
    assert.equal(active.has(url), false);
    assert.equal(target.onerror, null);
});

test("Decode failure removes the broken src and releases its URL", async () => {
    const target = image();
    const id = observe(target, receiver());
    const loading = load(id, stream, "image/png");
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    const url = target.src;
    target.onerror();
    assert.equal(await loading, false);
    assert.equal(target.src, null);
    assert.equal(active.has(url), false);
    assert.equal(active.size, 0);
});

test("Shared URL presentation is independent; partial occurrence release does not revoke owner URL", async () => {
    const url = await createUrl(stream, "image/png");
    const first = image(), second = image();
    const a = observe(first, receiver()), b = observe(second, receiver());
    const loadingA = display(a, url), loadingB = display(b, url);
    release(a);
    assert.equal(await loadingA, false);
    assert.ok(active.has(url));
    assert.equal(second.src, url);
    second.onload();
    assert.equal(await loadingB, true);
    release(b);
    assert.ok(active.has(url));
    revokeUrl(url);
    assert.equal(active.size, 0);
});

test("Observer initialization failure leaves no unreachable component state", () => {
    const id = observe(image(), receiver());
    release(id);
    const Observer = globalThis.IntersectionObserver;
    globalThis.IntersectionObserver = class { constructor() { throw new Error("Unavailable observer"); } };
    try {
        assert.throws(() => observe(image(), receiver()), /Unavailable observer/);
        assert.doesNotThrow(() => release(id + 1));
    } finally {
        globalThis.IntersectionObserver = Observer;
    }
});
