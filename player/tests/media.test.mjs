import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../src/Talewright.Player/Components/MediaView.razor.js", import.meta.url));
const { observe, load, release } = await import(`data:text/javascript;base64,${source.toString("base64")}`);
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

test("A stale stream completion cannot create a URL or update a removed image", async () => {
    const target = image();
    const id = observe(target, receiver());
    let complete;
    const before = created;
    const loading = load(id, { arrayBuffer: () => new Promise(resolve => { complete = resolve; }) }, "image/png");
    release(id);
    complete(new Uint8Array([1]).buffer);
    assert.equal(await loading, false);
    assert.equal(target.src, null);
    assert.equal(created, before);
});

test("Disposal during decoding resolves the pending request and revokes the URL", async () => {
    const target = image();
    const id = observe(target, receiver());
    const loading = load(id, stream, "image/webp");
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
    const url = target.src;
    target.onerror();
    assert.equal(await loading, false);
    assert.equal(target.src, null);
    assert.equal(active.has(url), false);
    assert.equal(active.size, 0);
});
