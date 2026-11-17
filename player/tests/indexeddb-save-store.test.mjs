import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";

const source = await readFile(new URL("../src/Talewright.Player/wwwroot/Saves/IndexedDbSaveStore.js", import.meta.url));
const store = await import(`data:text/javascript;base64,${source.toString("base64")}`);

test("save operations reject with the configured-storage error before configuration", async () => {
    const expected = { name: "Error", message: "Save storage is not configured." };
    const operations = [
        store.read("story", "slot"),
        store.write("story", "slot", new Uint8Array([1]), false),
        store.readTrigger("story"),
        store.writeTrigger("story", "trigger", new Uint8Array([1])),
    ];

    for (const operation of operations) {
        await assert.rejects(operation, expected);
    }
});
