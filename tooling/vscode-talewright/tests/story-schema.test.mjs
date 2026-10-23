import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { test } from 'node:test';
import Ajv from 'ajv';
import { parse } from 'yaml';

const root = new URL('../../../', import.meta.url);
const read = async path => readFile(new URL(path, root), 'utf8');
const schema = JSON.parse(await read('tooling/vscode-talewright/schemas/story.schema.json'));
const ajv = new Ajv({ allErrors: true, strict: false });
ajv.addSchema(schema);
const validateManifest = ajv.compile(schema);

function parsedYaml(source, name) {
  try {
    return parse(source, { uniqueKeys: true });
  } catch (error) {
    assert.fail(`${name} is not valid YAML: ${error.message}`);
  }
}

function assertValid(validator, value, name) {
  assert.equal(validator(value), true, `${name}: ${ajv.errorsText(validator.errors)}`);
}

function assertInvalid(validator, value, name, keyword) {
  assert.equal(validator(value), false, `${name} unexpectedly passed schema validation`);
  if (keyword) {
    assert.ok(validator.errors.some(error => error.keyword === keyword),
      `${name} should fail with ${keyword}: ${ajv.errorsText(validator.errors)}`);
  }
}

const compileSubschema = target => ajv.compile({
  $schema: schema.$schema,
  definitions: schema.definitions,
  ...target,
});

function markdownYaml(source, index) {
  const blocks = [...source.matchAll(/```yaml\r?\n([\s\S]*?)\r?\n```/g)];
  assert.ok(blocks[index], `missing documented YAML block ${index}`);
  return blocks[index][1];
}

test('schema validates every shipped story manifest', async () => {
  for (const path of [
    'samples/LostRelic/story.talewright.yaml',
    'examples/CSharpStory/Story/Content/story.talewright.yaml',
  ]) {
    assertValid(validateManifest, parsedYaml(await read(path), path), path);
  }
});

test('schema validates complete and contextual documented manifest examples', async () => {
  const fullManifests = [
    ['docs/reference/tale-syntax.md', 0],
    ['docs/guide/first-story.md', 0],
    ['docs/guide/debugger-test-plan.md', 0],
  ];
  for (const [path, index] of fullManifests) {
    const name = `${path} YAML example ${index + 1}`;
    assertValid(validateManifest, parsedYaml(markdownYaml(await read(path), index), name), name);
  }

  const fragmentExamples = [
    ['docs/reference/tale-syntax.md', 1, 'pronouns'],
    ['docs/reference/tale-syntax.md', 2, 'creation'],
    ['docs/guide/writing-stories.md', 0, 'characters'],
    ['docs/guide/writing-stories.md', 1, 'items'],
    ['docs/guide/writing-stories.md', 2, 'factions'],
    ['docs/guide/writing-stories.md', 3, 'triggers'],
    ['docs/guide/writing-stories.md', 4, 'creation'],
  ];
  for (const [path, index, property] of fragmentExamples) {
    const name = `${path} YAML example ${index + 1} (${property})`;
    const fragment = parsedYaml(markdownYaml(await read(path), index), name);
    const value = property === 'pronouns' ? fragment.pronouns : fragment[property];
    assert.ok(value !== undefined, `${name} must include ${property}`);
    const story = {
      id: 'documented-fragment',
      title: 'Documented fragment',
      start: 'beginning',
      characters: { player: { 'first-name': 'Ada' } },
      [property]: value,
    };
    if (property === 'pronouns') {
      delete story.pronouns;
      story.characters.player.pronouns = value;
    }
    assertValid(validateManifest, story, name);
  }

  const writingGuide = await read('docs/guide/writing-stories.md');
  for (const [property, snippet] of [
    ['flags', '[alarm-raised]'],
    ['vars', '{ bribes: 0 }'],
  ]) {
    const value = parsedYaml(`${property}: ${snippet}`, `documented ${property} shorthand`)[property];
    assertValid(validateManifest, {
      id: 'documented-shorthand',
      title: 'Documented shorthand',
      start: 'beginning',
      characters: { player: { 'first-name': 'Ada' } },
      [property]: value,
    }, `documented ${property} shorthand`);
  }
  assert.ok(writingGuide.includes('`flags: [alarm-raised]`'));
  assert.ok(writingGuide.includes('`vars: { bribes: 0 }`'));
});

test('schema rejects unknown keys at every closed reader mapping', async () => {
  const source = await read('engine/src/Talewright.Tale/Parsing/StoryYamlReader.cs');
  const readerSets = [...source.matchAll(/CheckKeys\(\w+,\s*((?:"[^"]+"\s*,?\s*)+)\);/g)]
    .map(match => [...match[1].matchAll(/"([^"]+)"/g)].map(key => key[1]).sort());
  assert.ok(readerSets.length > 0, 'reader CheckKeys lists should be discoverable');

  const closed = [
    ['root', schema, { id: 'test', title: 'Test', start: 'beginning', characters: { player: { 'first-name': 'Ada' } } }],
    ['ruleset', schema.definitions.ruleset, {}],
    ['statDefinition', schema.definitions.statDefinition, { id: 'courage' }],
    ['character', schema.definitions.character, { 'first-name': 'Ada' }],
    ['customPronouns', schema.definitions.customPronouns, {
      subject: 'xe',
      object: 'xem',
      possessive: 'xyr',
      'possessive-pronoun': 'xyrs',
      reflexive: 'xemself',
    }],
    ['item', schema.definitions.item, {}],
    ['faction', schema.definitions.faction, {}],
    ['creation', schema.definitions.creation, {}],
    ['preset', schema.definitions.preset, {}],
    ['trigger', schema.definitions.trigger, { id: 't', when: 'true', do: 'set flag:ready' }],
  ];

  const schemaSets = closed.map(([name, target]) => {
    assert.equal(target.additionalProperties, false, `${name} must close its key set`);
    const keys = Object.keys(target.properties ?? {}).sort();
    assert.ok(readerSets.some(readerKeys => JSON.stringify(readerKeys) === JSON.stringify(keys)),
      `${name} keys should match one of StoryYamlReader.CheckKeys lists`);

    const invalid = structuredClone(closed.find(entry => entry[0] === name)[2]);
    invalid.__unknown_editor_schema_test__ = 'unexpected';
    const validate = name === 'root' ? validateManifest : compileSubschema(target);
    assertInvalid(validate, invalid, `${name} unknown key`, 'additionalProperties');
    return keys;
  });

  for (const readerKeys of readerSets) {
    assert.ok(schemaSets.some(keys => JSON.stringify(keys) === JSON.stringify(readerKeys)),
      `reader key list [${readerKeys.join(', ')}] must have a closed schema mapping`);
  }
});

test('shared compiler/schema manifest fixtures cover supported forms and diagnostic differences', async () => {
  const directory = 'tooling/vscode-talewright/tests/fixtures/manifests/';
  const cases = JSON.parse(await read(`${directory}cases.json`));
  assert.deepEqual(cases.map(fixture => fixture.file).sort(),
    (await readdir(new URL(directory, root))).filter(file => file.endsWith('.yaml')).sort());
  for (const fixture of cases) {
    const manifest = parsedYaml(await read(`${directory}${fixture.file}`), fixture.file);
    if (fixture.schemaValid) {
      assertValid(validateManifest, manifest, fixture.file);
    } else {
      assertInvalid(validateManifest, manifest, fixture.file);
    }
  }
});

test('schema permits custom ruleset IDs without hard-coding registrations', async () => {
  const story = parsedYaml(await read(
    'tooling/vscode-talewright/tests/fixtures/manifests/supported-forms.yaml'), 'supported forms');
  story.ruleset.base = 'project-rules';
  assertValid(validateManifest, story, 'custom ruleset ID');
});

test('preset IDs retain open author keys within compiler structural bounds', () => {
  const validate = compileSubschema(schema.definitions.creation.properties.presets);
  for (const id of ['', ' \t ', 'x'.repeat(81)]) {
    assertInvalid(validate, { [id]: {} }, `invalid preset ID ${JSON.stringify(id)}`, 'propertyNames');
  }
  for (const id of ['x', 'x'.repeat(80), ' custom preset ']) {
    assertValid(validate, { [id]: {} }, `valid preset ID ${JSON.stringify(id)}`);
  }
});

test('schema rejects missing required fields, wrong types, and invalid closed enums', () => {
  assertInvalid(validateManifest, { id: 'missing-fields' }, 'missing root fields', 'required');
  assertInvalid(validateManifest, {
    id: 'missing-player',
    title: 'Missing player',
    start: 'beginning',
    characters: { guide: { 'first-name': 'Wick' } },
  }, 'missing player character', 'required');
  assertInvalid(validateManifest, {
    id: 'wrong-types',
    title: 'Wrong types',
    start: 'beginning',
    randomness: 'always',
    characters: { player: { 'first-name': 'Ada', inventory: 12 } },
  }, 'wrong types and randomness enum');
  assertInvalid(validateManifest, {
    id: 'wrong-trigger',
    title: 'Wrong trigger',
    start: 'beginning',
    characters: { player: { 'first-name': 'Ada' } },
    triggers: [{ id: 'bad', when: 'true', do: 'set flag:ready', once: 'sometimes' }],
  }, 'wrong trigger scalar');
  assertInvalid(validateManifest, {
    id: 'wrong-creation',
    title: 'Wrong creation',
    start: 'beginning',
    characters: { player: { 'first-name': 'Ada' } },
    creation: { pronouns: ['they', 'they'], stats: { str: [] } },
  }, 'creation value constraints');
});
