import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const json = async file => JSON.parse(await readFile(new URL(`../${file}`, import.meta.url), 'utf8'));

test('snippets have discoverable prefixes and a bounded default-expansion contract', async () => {
  const snippets = await json('snippets/tale.json');
  assert.equal(Object.keys(snippets).length, 6);
  const prefixes = new Set();
  for (const [name, snippet] of Object.entries(snippets)) {
    assert.match(snippet.prefix, /^tale-/);
    assert.ok(!prefixes.has(snippet.prefix), name);
    prefixes.add(snippet.prefix);
    assert.ok(snippet.description.length > 0);
    const body = snippet.body.join('\n');
    assert.ok(body.includes('$0'), name);
    assert.ok(!body.replace(/\$\{\d+:[^{}]*\}/g, '').replace(/\$0/g, '').includes('$'),
      `${name}: update compiler/default-expansion tests if richer snippet syntax is added`);
  }
});
