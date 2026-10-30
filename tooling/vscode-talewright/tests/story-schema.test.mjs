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

test('schema validates the organization guide settings and trigger examples', async () => {
  const guide = await read('docs/guide/organizing-your-story.md');
  for (let index = 0; index < 5; index++) {
    const name = `organization guide YAML example ${index + 1}`;
    assertValid(validateManifest, parsedYaml(markdownYaml(guide, index), name), name);
  }
});

test('schema validates every shipped story configuration', async () => {
  for (const path of [
    'samples/LostRelic/story.talewright.yaml',
    'samples/LostRelic/dream/dream.talewright.yaml',
    'examples/CSharpStory/Story/Content/story.talewright.yaml',
  ]) {
    assertValid(validateManifest, parsedYaml(await read(path), path), path);
  }
});

test('player declarations may be in the root or a supporting configuration file', () => {
  const game = { id: 'root', title: 'Root', start: 'beginning' };
  assertValid(validateManifest, { game }, 'root with declarations in supporting files');
  assertValid(validateManifest, { game, characters: {} }, 'root with player in a supporting file');
  assertValid(validateManifest, {
    game, characters: { player: { 'first-name': 'Ada' } },
  }, 'root with player');
  assertValid(validateManifest, { characters: { guide: { 'first-name': 'Bea' } } }, 'character fragment');
  assertValid(validateManifest, { items: { coin: 'Coin' } }, 'item fragment');
});

test('item grammar has explicit enum/boolean values and independent defaults', () => {
  const item = compileSubschema(schema.definitions.itemValue);
  for (const value of ['sword', {}, { name: 'armor', uncountable: true },
    { name: 'pants', 'name-agreement': 'plural' },
    { name: 'pair of boots', 'name-agreement': 'singular', uncountable: false }]) {
    assertValid(item, value, JSON.stringify(value));
  }
  for (const value of [{ 'name-agreement': 'many' }, { 'name-agreement': true },
    { 'name-agreement': ['singular'] }, { uncountable: 'sometimes' },
    { uncountable: 1 }, { uncountable: {} }]) {
    assertInvalid(item, value, JSON.stringify(value));
  }
  assert.equal(schema.definitions.item.properties['name-agreement'].default, 'singular');
  assert.equal(schema.definitions.item.properties.uncountable.default, false);
});

test('compiler, schema, and editor grammar accept the same identifiers', async () => {
  const pattern = schema.definitions.identifier.pattern;
  assert.ok(pattern.startsWith('^') && pattern.endsWith('$'), 'schema identifier pattern must be anchored');
  const grammar = await read('tooling/vscode-talewright/syntaxes/tale.tmLanguage.json');
  assert.ok(grammar.includes(pattern.slice(1, -1)),
    'editor grammar should use the same identifier body as the manifest schema');

  // EditorManifestSchemaTests runs the same fixture through the compiler.
  const fixture = JSON.parse(await read('tooling/vscode-talewright/tests/fixtures/identifiers.json'));
  const manifest = id => ({
    game: {
      id: 'identifier-test',
      title: 'Identifier test',
      start: 'beginning',
    },
    characters: { player: { 'first-name': 'Ada' }, [id]: { 'first-name': 'Bea' } },
  });
  for (const id of fixture.valid) {
    assertValid(validateManifest, manifest(id), `identifier ${JSON.stringify(id)}`);
  }

  for (const id of fixture.invalid) {
    assertInvalid(validateManifest, manifest(id), `invalid identifier ${JSON.stringify(id)}`, 'pattern');
  }

  const creationStats = compileSubschema(schema.definitions.creation.properties.stats);
  assertValid(creationStats, { str: [8] }, 'creation stat ID');
  assertInvalid(creationStats, { 'bad/id': [8] }, 'invalid creation stat ID', 'propertyNames');
});

test('schema validates complete and contextual documented manifest examples', async () => {
  const fullManifests = [
    ['docs/reference/talemark-syntax.md', 0],
    ['docs/reference/talemark-syntax.md', 1],
    ['docs/guide/first-story.md', 0],
    ['docs/guide/debugger-test-plan.md', 0],
    ['docs/guide/authoring-recipes.md', 2],
  ];
  for (const [path, index] of fullManifests) {
    const name = `${path} YAML example ${index + 1}`;
    assertValid(validateManifest, parsedYaml(markdownYaml(await read(path), index), name), name);
  }

  const fragmentExamples = [
    ['docs/reference/talemark-syntax.md', 2, 'pronouns'],
    ['docs/reference/talemark-syntax.md', 3, 'creation'],
    ['docs/reference/talemark-syntax.md', 4, 'items'],
    ['docs/reference/talemark-syntax.md', 5, 'vars'],
    ['docs/guide/writing-stories.md', 0, 'characters'],
    ['docs/guide/writing-stories.md', 1, 'items'],
    ['docs/guide/writing-stories.md', 2, 'vars'],
    ['docs/guide/writing-stories.md', 3, 'factions'],
    ['docs/guide/writing-stories.md', 4, 'triggers'],
    ['docs/guide/writing-stories.md', 5, 'creation'],
  ];
  for (const [path, index, property] of fragmentExamples) {
    const name = `${path} YAML example ${index + 1} (${property})`;
    const fragment = parsedYaml(markdownYaml(await read(path), index), name);
    const value = property === 'pronouns' ? fragment.pronouns
      : property === 'creation' ? fragment.game.creation
        : fragment[property];
    assert.ok(value !== undefined, `${name} must include ${property}`);
    const story = {
      game: {
        id: 'documented-fragment',
        title: 'Documented fragment',
        start: 'beginning',
      },
      characters: { player: { 'first-name': 'Ada' } },
    };
    if (property === 'pronouns') {
      story.characters.player.pronouns = value;
    } else if (property === 'creation') story.game.creation = value;
    else story[property] = value;
    assertValid(validateManifest, story, name);
  }

  const writingGuide = await read('docs/guide/writing-stories.md');
  for (const [property, snippet] of [
    ['flags', '[alarm-raised]'],
    ['vars', '{ bribes: 0 }'],
  ]) {
    const value = parsedYaml(`${property}: ${snippet}`, `documented ${property} shorthand`)[property];
    assertValid(validateManifest, {
      game: {
        id: 'documented-shorthand',
        title: 'Documented shorthand',
        start: 'beginning',
      },
      characters: { player: { 'first-name': 'Ada' } },
      [property]: value,
    }, `documented ${property} shorthand`);
  }
  assert.ok(writingGuide.includes('`flags: [alarm-raised]`'));
  assert.ok(writingGuide.includes('`vars: { bribes: 0 }`'));
});

test('custom possessive pronouns require nested determiner and independent values', () => {
  const validate = compileSubschema(schema.definitions.customPronouns);
  const pronouns = {
    subject: 'xe',
    object: 'xem',
    'possessive-pronoun': { determiner: 'xer', independent: 'xers' },
    reflexive: 'xemself',
    agreement: 'singular',
  };
  assertValid(validate, pronouns, 'nested possessive forms');
  for (const forms of ['xers', null, [], {}, { determiner: 'xer' }, { independent: 'xers' },
    { determiner: '', independent: 'xers' }, { determiner: 'xer', independent: [] }]) {
    assertInvalid(validate, { ...pronouns, 'possessive-pronoun': forms }, `invalid forms ${JSON.stringify(forms)}`);
  }
  const { 'possessive-pronoun': omitted, ...missing } = pronouns;
  assertInvalid(validate, missing, 'missing possessive forms', 'required');
  assertInvalid(validate, { ...pronouns, possessive: 'xer' }, 'old flat possessive', 'additionalProperties');
});

test('pronoun and displayed-name agreement are independent, explicit grammar fields', () => {
  const validatePronouns = compileSubschema(schema.definitions.customPronouns);
  const validateCharacter = compileSubschema(schema.definitions.character);
  const pronouns = {
    subject: 'xe', object: 'xem', reflexive: 'xemself',
    'possessive-pronoun': { determiner: 'xyr', independent: 'xyrs' },
    agreement: 'singular',
  };
  const { agreement, ...missing } = pronouns;
  assertInvalid(validatePronouns, missing, 'missing agreement', 'required');
  for (const value of [true, false, 'yes', 'Singular', null, [], {}, '']) {
    assertInvalid(validatePronouns, { ...pronouns, agreement: value }, 'invalid agreement');
    assertInvalid(validateCharacter, { 'first-name': 'Ada', 'name-agreement': value }, 'invalid name agreement');
  }
  for (const agreement of ['singular', 'plural']) {
    for (const nameAgreement of ['singular', 'plural']) {
      assertValid(validateCharacter, {
        'first-name': 'the pack', 'name-agreement': nameAgreement, pronouns: { ...pronouns, agreement },
      }, 'independent grammatical fields');
    }
  }
  for (const replacement of [false, true]) {
    assertInvalid(validatePronouns, { ...(replacement ? pronouns : missing), plural: false },
      'obsolete pronoun plural', 'additionalProperties');
    assertInvalid(validateCharacter, {
      'first-name': 'the goblins', plural: true, ...(replacement ? { 'name-agreement': 'plural' } : {}),
    }, 'obsolete character plural', 'additionalProperties');
  }
});

test('schema rejects unknown keys at every closed reader mapping', async () => {
  const source = await read('engine/src/Talewright.Talemark/Parsing/StoryYamlReader.cs');
  const readerSets = [...source.matchAll(/CheckKeys\(\w+,\s*((?:"[^"]+"\s*,?\s*)+)\);/g)]
    .map(match => [...match[1].matchAll(/"([^"]+)"/g)].map(key => key[1]).sort());
  assert.ok(readerSets.length > 0, 'reader CheckKeys lists should be discoverable');

  const closed = [
    ['root', schema, { game: { id: 'test', title: 'Test', start: 'beginning' }, characters: { player: { 'first-name': 'Ada' } } }],
    ['game', schema.properties.game, { id: 'test', title: 'Test', start: 'beginning' }],
    ['ruleset', schema.definitions.ruleset, {}],
    ['statDefinition', schema.definitions.statDefinition, { id: 'courage' }],
    ['character', schema.definitions.character, { 'first-name': 'Ada' }],
    ['customPronouns', schema.definitions.customPronouns, {
      subject: 'xe',
      object: 'xem',
      'possessive-pronoun': { determiner: 'xyr', independent: 'xyrs' },
      reflexive: 'xemself',
      agreement: 'singular',
    }],
    ['possessivePronouns', schema.definitions.possessivePronouns, { determiner: 'xyr', independent: 'xyrs' }],
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
  story.game.ruleset = { base: 'project-rules' };
  assertValid(validateManifest, story, 'custom ruleset ID');
});

test('governing-attribute is an optional identifier restricted to explicit skill definitions', () => {
  const validate = compileSubschema(schema.definitions.statDefinition);
  assertValid(validate, { id: 'tracking', category: 'skill', 'governing-attribute': 'grit' }, 'custom attribute link');
  assertValid(validate, { id: 'athletics', category: 'skill', 'governing-attribute': 'str' }, 'built-in skill override');
  assertValid(validate, { id: 'tracking', category: 'skill' }, 'unlinked skill');
  for (const link of [{ governingAttribute: 'str' }, { governingAttribute: 'str', 'governing-attribute': 'grit' }]) {
    assertInvalid(validate, { id: 'tracking', category: 'skill', ...link }, 'camelCase key', 'additionalProperties');
  }
  for (const category of ['attribute', 'resource', 'progression', 'custom', undefined]) {
    assertInvalid(validate, {
      id: 'tracking', ...(category === undefined ? {} : { category }), 'governing-attribute': 'grit',
    }, `invalid link category ${category}`);
  }
  for (const target of ['', 'bad/id', null, [], {}, 3]) {
    assertInvalid(validate, { id: 'tracking', category: 'skill', 'governing-attribute': target }, 'invalid attribute ID');
  }
});

test('preset IDs use the shared identifier rule and retain the existing 80-character bound', () => {
  const validate = compileSubschema(schema.definitions.creation.properties.presets);
  for (const id of ['', ' space ', 'bad/id', 'x'.repeat(81)]) {
    assertInvalid(validate, { [id]: {} }, `invalid preset ID ${JSON.stringify(id)}`, 'propertyNames');
  }
  for (const id of ['x', 'x'.repeat(80), 'custom-preset']) {
    assertValid(validate, { [id]: {} }, `valid preset ID ${JSON.stringify(id)}`);
  }
});

test('schema rejects missing required fields, wrong types, and invalid closed enums', () => {
  assertValid(validateManifest, {
    characters: { guide: { 'first-name': 'Wick' } },
  }, 'individual fragment without game or player');
  assertInvalid(validateManifest, { game: { id: 'missing-fields' } }, 'missing game fields', 'required');
  assertInvalid(validateManifest, {
    id: 'old-format',
    title: 'Old format',
    start: 'beginning',
  }, 'old top-level game settings', 'additionalProperties');
  assertInvalid(validateManifest, {
    game: {
      id: 'wrong-types',
      title: 'Wrong types',
      start: 'beginning',
      randomness: 'always',
    },
    characters: { player: { 'first-name': 'Ada', inventory: 12 } },
  }, 'wrong types and randomness enum');
  assertInvalid(validateManifest, {
    game: {
      id: 'wrong-trigger',
      title: 'Wrong trigger',
      start: 'beginning',
    },
    characters: { player: { 'first-name': 'Ada' } },
    triggers: [{ id: 'bad', when: 'true', do: 'set flag:ready', once: 'sometimes' }],
  }, 'wrong trigger scalar');
  assertInvalid(validateManifest, {
    game: {
      id: 'wrong-creation',
      title: 'Wrong creation',
      start: 'beginning',
      creation: { pronouns: ['they', 'they'], stats: { str: [] } },
    },
    characters: { player: { 'first-name': 'Ada' } },
  }, 'creation value constraints');
});
