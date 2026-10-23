import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const json = async file => JSON.parse(await readFile(new URL(`../${file}`, import.meta.url), 'utf8'));

test('package wires Tale syntax, snippets, manifest schema, and the thin LSP client', async () => {
  const manifest = await json('package.json');
  assert.equal(manifest.main, './dist/extension.cjs');
  assert.equal(manifest.browser, undefined);
  assert.deepEqual(manifest.activationEvents, ['onLanguage:tale', 'onLanguage:yaml', 'workspaceContains:**/story.talewright.yaml']);
  assert.ok(manifest.dependencies['vscode-languageclient']);
  assert.deepEqual(Object.keys(manifest.contributes).sort(),
    ['configuration', 'configurationDefaults', 'grammars', 'languages', 'snippets', 'yamlValidation']);
  assert.deepEqual(manifest.contributes.configurationDefaults['[tale]'], {
    'editor.autoClosingBrackets': 'never',
    'editor.autoClosingQuotes': 'never',
  });
  assert.deepEqual(manifest.contributes.languages[0].extensions, ['.tale']);
  assert.equal(manifest.contributes.grammars[0].scopeName, 'text.tale');
  for (const contribution of ['grammars', 'snippets']) {
    await readFile(new URL(`../${manifest.contributes[contribution][0].path}`, import.meta.url));
  }
  assert.deepEqual(manifest.contributes.yamlValidation, [{
    fileMatch: ['*.talewright.yaml'],
    url: './schemas/story.schema.json',
  }]);
  await readFile(new URL(`../${manifest.contributes.yamlValidation[0].url}`, import.meta.url));
  const [pattern] = manifest.contributes.yamlValidation[0].fileMatch;
  const matches = name => new RegExp(`^${pattern.replace(/\./g, '\\.').replace(/\*/g, '[^/]*')}$`).test(name);
  for (const name of ['story.talewright.yaml']) assert.ok(matches(name), name);
  for (const name of ['story.yaml', 'story.talewright.yml', 'talewright.yaml', 'docker-compose.yaml']) {
    assert.ok(!matches(name), name);
  }
  const config = await json(manifest.contributes.languages[0].configuration);
  assert.equal(config.comments.lineComment, '//');
  assert.deepEqual(config.autoClosingPairs, []);
  assert.ok(!config.surroundingPairs.some(([open]) => open === '{'));
});

test('snippets have discoverable prefixes and a bounded default-expansion contract', async () => {
  const snippets = await json('snippets/tale.json');
  assert.equal(Object.keys(snippets).length, 5);
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
