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

test('Play From This Scene has gated menus, a default-enabled code lens, and an optional debug task scene', async () => {
  const manifest = await json('package.json');
  assert.ok(manifest.contributes.commands.some(command =>
    command.command === 'talewright.playFromScene' && command.title === 'Talewright: Play From This Scene'));
  assert.ok(manifest.contributes.menus['editor/context'].some(menu =>
    menu.command === 'talewright.playFromScene' && /talewright\.inScene/.test(menu.when)));
  assert.ok(manifest.contributes.menus.commandPalette.some(menu =>
    menu.command === 'talewright.playFromScene' && /talewright\.inScene/.test(menu.when)));
  assert.deepEqual(manifest.contributes.configuration.properties['talewright.codeLens.playFromScene'], {
    type: 'boolean', default: true, scope: 'resource',
    description: 'Show a Play from here code lens above each scene in Talemark files.',
  });
  assert.equal(manifest.contributes.taskDefinitions[0].properties.scene.type, 'string');
  assert.ok(!manifest.contributes.taskDefinitions[0].required.includes('scene'));
});

test('Talemark semantic highlighting is enabled while its TextMate grammar remains available', async () => {
  const manifest = await json('package.json');
  assert.equal(manifest.contributes.configurationDefaults['[tale]']['editor.semanticHighlighting.enabled'], true);
  assert.ok(manifest.contributes.grammars.some(grammar =>
    grammar.language === 'tale' && grammar.scopeName === 'text.tale' &&
    grammar.path === './syntaxes/tale.tmLanguage.json'));
  const scopes = manifest.contributes.semanticTokenScopes.find(item => item.language === 'tale').scopes;
  for (const type of manifest.contributes.semanticTokenTypes) {
    assert.ok(type.description.length > 0, type.id);
    assert.ok(scopes[type.id]?.length > 0, `${type.id} must have a theme-scope mapping`);
  }
  assert.deepEqual(scopes.talemarkScene, ['entity.name.section.tale', 'entity.name.function.tale']);
  assert.deepEqual(scopes.talemarkDivert, ['keyword.control.divert.tale', 'keyword.control', 'keyword']);
  assert.deepEqual(scopes.talemarkStructure, ['keyword.control.tale', 'keyword']);
  assert.deepEqual(scopes.talemarkPunctuation, ['punctuation.section.embedded.tale', 'punctuation']);
  assert.deepEqual(scopes.talemarkFormatting, ['constant.character.escape.tale', 'constant.character']);
  assert.deepEqual(scopes.talemarkEscape, ['constant.character.escape.tale', 'constant.character']);
  assert.deepEqual(scopes.modifier, ['storage.modifier.tale', 'storage']);
});
