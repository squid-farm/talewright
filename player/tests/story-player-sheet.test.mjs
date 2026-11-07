import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const stylesheet = await readFile(new URL("../src/Talewright.Player/wwwroot/talewright.css", import.meta.url), "utf8");

test("A player without a sheet uses one transcript grid column at every viewport width", () => {
    const rule = stylesheet.match(/\.tw-columns--full\s*\{([^}]+)\}/);

    assert.ok(rule, "Expected a full-width transcript layout rule.");
    assert.match(rule[1], /grid-template-columns:\s*minmax\(0,\s*1fr\)\s*;/);
});
