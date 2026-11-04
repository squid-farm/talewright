import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import textmate from 'vscode-textmate';
import oniguruma from 'vscode-oniguruma';

const wasm = await readFile(new URL('../node_modules/vscode-oniguruma/release/onig.wasm', import.meta.url));
await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
const registry = new textmate.Registry({
  onigLib: Promise.resolve({
    createOnigScanner: patterns => new oniguruma.OnigScanner(patterns),
    createOnigString: value => new oniguruma.OnigString(value),
  }),
  loadGrammar: async scope => {
    const file = scope === 'text.tale'
      ? new URL('../syntaxes/tale.tmLanguage.json', import.meta.url)
      : null;
    return file ? textmate.parseRawGrammar(await readFile(file, 'utf8'), 'grammar.json') : null;
  },
});
const grammar = await registry.loadGrammar('text.tale');

function tokens(line, state = textmate.INITIAL) {
  return grammar.tokenizeLine(line, state);
}

function hasScope(line, text, scope) {
  const offset = line.indexOf(text);
  assert.notEqual(offset, -1, `Missing fixture text ${text}`);
  const token = tokens(line).tokens.find(token => token.startIndex <= offset && token.endIndex > offset);
  assert.ok(token.scopes.includes(scope), `${line}: ${text} expected ${scope}, got ${token.scopes.join(', ')}`);
}

test('TextMate scopes agree with the owned vocabulary contract', async () => {
  const cases = JSON.parse(await readFile(new URL('./fixtures/vocabulary.json', import.meta.url), 'utf8'));
  for (const entry of cases) {
    const start = entry.line.indexOf(entry.spelling);
    assert.notEqual(start, -1, `${entry.owner}.${entry.member}: missing spelling in ${entry.line}`);
    const checks = entry.scopes ?? [{ offset: 0, length: entry.spelling.length, scope: entry.scope }];
    const covered = new Set();
    const lineTokens = tokens(entry.line).tokens;
    for (const check of checks) {
      assert.ok(check.offset >= 0 && check.length > 0 && check.offset + check.length <= entry.spelling.length,
        `${entry.owner}.${entry.member}: invalid scope range`);
      for (let relative = check.offset; relative < check.offset + check.length; relative++) {
        covered.add(relative);
        const offset = start + relative;
        const token = lineTokens.find(candidate => candidate.startIndex <= offset && candidate.endIndex > offset);
        assert.ok(token?.scopes.includes(check.scope),
          `${entry.owner}.${entry.member}: ${entry.line} expected ${check.scope} at ${offset}, got ${token?.scopes.join(', ')}`);
        if (entry.exactScope) assert.deepEqual(token.scopes, [check.scope], entry.line);
      }
    }
    for (let relative = 0; relative < entry.spelling.length; relative++) {
      assert.ok(covered.has(relative) || /\s/.test(entry.spelling[relative]),
        `${entry.owner}.${entry.member}: unchecked character at ${relative}`);
    }
  }
});

test('emphasis scopes match the shared compiler corpus and record its known difference', async () => {
  const cases = JSON.parse(await readFile(
    new URL('../../../engine/tests/Talewright.Talemark.Tests/Fixtures/reader-text-emphasis-parity.json', import.meta.url),
    'utf8',
  ));
  assert.equal(cases.filter(testCase => testCase.knownDifference).length, 1);
  for (const { input, segments } of cases) {
    const lineTokens = tokens(input).tokens;
    for (const { start, length, text, styles } of segments) {
      assert.equal(input.slice(start, start + length), text, `${input}: invalid fixture range`);
      for (let offset = start; offset < start + length; offset++) {
        const token = lineTokens.find(candidate => candidate.startIndex <= offset && candidate.endIndex > offset);
        const actualStyles = token.scopes
          .filter(scope => /^markup\.(bold|italic)\.markdown$/.test(scope))
          .map(scope => scope.includes('.bold.') ? 'bold' : 'italic')
          .sort();
        assert.deepEqual(actualStyles, [...styles].sort(), `${input}: unexpected formatting at ${offset}`);
      }
    }
  }
});

test('Talemark syntax categories tokenize with real Oniguruma/TextMate', () => {
  for (const [line, text, scope] of [
    ['=== temple.hall', 'temple.hall', 'entity.name.section.tale'],
    ['  // {if true} * [not a choice]', '//', 'comment.line.double-slash.tale'],
    ['@requires player:dex >= 12 and @CanClimb', '@requires', 'keyword.control.metadata.tale'],
    ['@requires player:dex >= 12 and @CanClimb', '@CanClimb', 'support.function.predicate.tale'],
    ['@requires @Can.Open-door_2', '@Can.Open-door_2', 'support.function.predicate.tale'],
    ['@hub any(camp, temple)', '@hub', 'keyword.control.metadata.tale'],
    ['@hub-limit 3', '@hub-limit', 'keyword.control.metadata.tale'],
    ['@once', '@once', 'keyword.control.metadata.tale'],
    ['@title The **sealed** door', 'sealed', 'markup.bold.markdown'],
    ['> guide: Hello {player:first-name}.', 'guide', 'entity.name.tag.character.tale'],
    ['> guide: Hello {player:first-name}.', 'first-name', 'variable.other.member.tale'],
    ['{faction:guild.name:name}', 'guild.name', 'variable.other.member.tale'],
    ['{faction:guild:name}', 'name', 'variable.other.member.tale'],
    ['{player:pronoun}', 'pronoun', 'variable.other.member.tale'],
    ['{player:pronoun:reflexive#capitalize}', 'reflexive', 'variable.other.member.tale'],
    ['{player:pronoun:subject}', 'subject', 'variable.other.member.tale'],
    ['{player:pronoun:object#capitalize}', 'object', 'variable.other.member.tale'],
    ['{player:pronoun:possessive-pronoun:independent}', 'independent', 'variable.other.member.tale'],
    ['{player:pronoun:possessive-pronoun:determiner}', 'determiner', 'variable.other.member.tale'],
    ['{if (player:pronoun:object == "her")}yes{/if}', 'object', 'variable.other.member.tale'],
    ['~ var:word = player:pronoun:possessive-pronoun:independent', 'independent', 'variable.other.member.tale'],
    ['{("a # literal")#capitalize}', '# literal', 'string.quoted.double.tale'],
    [String.raw`{("a \" } # literal")#capitalize}`, '\\"', 'constant.character.escape.tale'],
    [String.raw`{("a \" } # literal")#capitalize}`, '# literal', 'string.quoted.double.tale'],
    [String.raw`{("a \" } # literal")#capitalize}`, '#capitalize', 'support.function.modifier.tale'],
    [String.raw`~ var:path = "C:\\stories"`, '\\\\', 'constant.character.escape.tale'],
    [String.raw`{"bad\q"}`, '\\q', 'invalid.illegal.escape.tale'],
    [String.raw`{"bad\n"}`, '\\n', 'invalid.illegal.escape.tale'],
    [String.raw`* [{"a \" ] }"}] (requires: "a \" )" != "") -> END`, 'END', 'entity.name.section.target.tale'],
    [String.raw`{player:pronoun#one-many("a \" , #", "b \\")}`, ', #', 'string.quoted.double.tale'],
    ['{player:pronoun#one-many("has", "have")}', '#one-many', 'support.function.modifier.tale'],
    ['{player:pronoun#one-many("has", "have")}', 'has', 'string.quoted.double.tale'],
    ['{player:pronoun#one-many("has", "have")}', 'have', 'string.quoted.double.tale'],
    ['{item:weapon.knife}', 'weapon.knife', 'variable.other.member.tale'],
    ['{item:pants#that#capitalize}', '#that', 'support.function.modifier.tale'],
    ['{item:pants#this}', '#this', 'support.function.modifier.tale'],
    ['{item:pants#the}', '#the', 'support.function.modifier.tale'],
    ['{item:pants#one-many("is", "are")}', '#one-many', 'support.function.modifier.tale'],
    ['{item:pants#one-many("is", "are")}', 'are', 'string.quoted.double.tale'],
    ['~ var:torches += 1', '~', 'keyword.operator.effect.tale'],
    ['~ var:torches += 1', '+=', 'keyword.operator.tale'],
    ['* [Read **glyphs**] (requires: player:int >= 12) { player:hp -= 2 } -> temple.hall', '*', 'punctuation.definition.choice.tale'],
    ['* [Read **glyphs**] (requires: player:int >= 12) { player:hp -= 2 } -> temple.hall', 'glyphs', 'markup.bold.markdown'],
    ['* [Read] (requires: player:int >= 12) -> temple.hall', 'requires', 'keyword.control.tale'],
    ['* [Read] (requires: player:int >= 12) -> temple.hall', 'temple.hall', 'entity.name.section.target.tale'],
    ['* -> END', '*', 'punctuation.definition.choice.tale'],
    ['* -> END', 'END', 'entity.name.section.target.tale'],
    ['* (requires: flag:ready) { set flag:left } -> temple.hall', 'requires', 'keyword.control.tale'],
    ['* (requires: flag:ready) { set flag:left } -> temple.hall', 'temple.hall', 'entity.name.section.target.tale'],
    ['* { set flag:left } -> END', '*', 'punctuation.definition.choice.tale'],
    ['* [Climb] skill-check guide athletics dc 14', 'skill-check', 'keyword.control.check.tale'],
    ['::: skill-check perception dc 12', 'skill-check', 'keyword.control.check.tale'],
    ['  success: { player:hp += 1 } -> END', 'success', 'keyword.control.outcome.tale'],
    ['failure: -> temple.hall', 'failure', 'keyword.control.outcome.tale'],
    ['-> END', 'END', 'entity.name.section.target.tale'],
    ['{if visited(temple.hall) and true}Yes{else}No{/if}', 'if', 'keyword.control.conditional.tale'],
    ['{if visited(temple.hall)}Yes{/if}', 'visited', 'support.function.tale'],
    ['{player:first-name#possessive}', '#possessive', 'support.function.modifier.tale'],
    ['{if var:count == 12}', '12', 'constant.numeric.tale'],
    ['*emphasis* and **strong**', 'emphasis', 'markup.italic.markdown'],
    ['*emphasis* and **strong**', 'strong', 'markup.bold.markdown'],
    ['**{player:first-name}**', 'first-name', 'variable.other.member.tale'],
    ['Use `code` here.', 'code', 'markup.inline.raw.string.markdown'],
    ['Use ``a`b`` here.', 'a`b', 'markup.inline.raw.string.markdown'],
    ['![torch](torch.png)', 'torch.png', 'invalid.deprecated.image.tale'],
    ['{image("maps/camp", "A camp")}', 'image', 'support.function.image.tale'],
    ['{image("maps/camp", "A } camp")}', 'camp"', 'string.quoted.double.tale'],
    ['{image("camp", "He said \\"Go\\".")}', '\\"', 'constant.character.escape.tale'],
    ['* [{image("camp", "A ] camp")} Go] -> END', 'A ] camp', 'string.quoted.double.tale'],
    ['> guide: {image("camp", "Camp")}', 'image', 'support.function.image.tale'],
    ['{image("camp", "{player:first-name} at the gate")}', 'first-name', 'variable.other.member.tale'],
    ['{image("camp", "**The guide**")}', 'The guide', 'markup.bold.markdown'],
    [String.raw`{image("camp", "a \* b")}`, String.raw`\*`, 'constant.character.escape.tale'],
    [String.raw`{image("camp", "a \[door\]")}`, String.raw`\[`, 'constant.character.escape.tale'],
    ['{image("camp", "\\{literal\\}")}', '\\{', 'constant.character.escape.tale'],
    ['{image("{player:name}", "Camp")}', 'player', 'string.quoted.double.tale'],
    ['{image:name}', 'name', 'variable.other.member.tale'],
    ['Literal \\{player:first-name\\}', '\\{', 'constant.character.escape.tale'],
    ['* [Continue] (requires: visited(start) and player:hp > 0) -> END', 'and', 'keyword.operator.word.tale'],
    ['* [Continue] (requires: visited(start) and player:hp > 0) -> END', 'hp', 'variable.other.member.tale'],
    ['* [Continue] (requires: visited(start) and player:hp > 0) -> END', 'END', 'entity.name.section.target.tale'],
    ['* [Go] (requires: (visited(a) or visited(b)) and player:hp > 0) -> END', 'hp', 'variable.other.member.tale'],
    ['{if visited(start) and player:hp > 0}Yes{/if}', 'hp', 'variable.other.member.tale'],
    ['__snake__case__ after', 'case', 'markup.bold.markdown'],
    ['_snake_case_words_ after', 'case_words', 'markup.italic.markdown'],
    ['**bold** text', 'bold', 'markup.bold.markdown'],
    ['*it* text', 'it', 'markup.italic.markdown'],
    ['**a*b** after', 'b', 'markup.bold.markdown'],
    ['**real \\** still bold** after', 'still bold', 'markup.bold.markdown'],
    ['*real \\* still italic* after', 'still italic', 'markup.italic.markdown'],
    ['__real \\__ still bold__ after', 'still bold', 'markup.bold.markdown'],
    ['_real \\_ still italic_ after', 'still italic', 'markup.italic.markdown'],
    ['**bold *italic* bold**', 'italic', 'markup.italic.markdown'],
    ['**bold *italic* bold**', 'bold', 'markup.bold.markdown'],
    ['[Hello {player:first-name}](https://example.org)', 'first-name', 'variable.other.member.tale'],
    ['[**Bold** {player:name} [x]](<https://example.org>)', 'Bold', 'markup.bold.markdown'],
    ['[**Bold** {player:name} [x]](<https://example.org>)', 'name', 'variable.other.member.tale'],
  ]) hasScope(line, text, scope);
  const path = '{image("{player:name}", "Camp")}';
  const pathName = path.indexOf('name');
  const pathToken = tokens(path).tokens.find(token => token.startIndex <= pathName && token.endIndex > pathName);
  assert.ok(!pathToken.scopes.includes('variable.other.member.tale'), pathToken.scopes.join(', '));
});

test('ordinary prose, unsupported Markdown, comments and escaped braces follow Talemark syntax', () => {
  for (const line of [
    '*emphasis*', '**strong**', 'A -> sign in prose', 'Write @title here.',
    'Literal \\{player:first-name\\}', '// * [choice] -> END',
    '`* [not a choice]`',
  ]) {
    const scopes = tokens(line).tokens.flatMap(token => token.scopes);
    assert.ok(!scopes.includes('punctuation.definition.choice.tale'), line);
    assert.ok(!scopes.includes('keyword.control.metadata.tale'), line);
  }
  assert.ok(!tokens('Literal \\{player:first-name\\}').tokens.some(token => token.scopes.includes('meta.template.tale')));
  hasScope(String.raw`a \. b`, String.raw`\.`, 'constant.character.escape.tale');
  hasScope(String.raw`a \— b`, String.raw`—`, 'text.tale');
  assert.ok(!tokens('A -> sign in prose').tokens.some(token => token.scopes.includes('keyword.control.divert.tale')));
  assert.ok(!tokens('snake_case_words').tokens.some(token => token.scopes.includes('markup.italic.markdown')));
  for (const [line, text] of [
    ['__snake__case__ after', 'after'], ['_snake_case_words_ after', 'after'],
    ['**a*b** after', 'after'], ['*a**b**c* after', 'after'], ['***both*** after', 'after'],
    ['**literal\\** plain', 'plain'], ['*literal\\* plain', 'plain'],
    ['__literal\\__ plain', 'plain'], ['_literal\\_ plain', 'plain'],
    ['**literal \\}** after', 'after'], ['__literal \\}__ after', 'after'],
    ['*literal \\}* after', 'after'], ['_literal \\}_ after', 'after'],
    ['An *unfinished `code*` after', 'after'],
    ['2 * 3 and 4*', '3'], ['a ** b', 'b'], ['[{player:name}](https://example.org) after', 'after'],
    ['word*inner*word', 'inner'], ['2*3*4', '3'], ['snake_case', 'case'],
    ['**bold**text', 'bold'], ['*it*text', 'it'], ['***both***', 'both'],
    ['`unfinished code', 'unfinished'],
    ['[label](https://example.org)', 'label'], ['&amp; &#65;', '&amp;'],
  ]) {
    const offset = line.indexOf(text);
    const token = tokens(line).tokens.find(token => token.startIndex <= offset && token.endIndex > offset);
    assert.ok(!token.scopes.some(scope => scope.startsWith('markup.') || scope.startsWith('meta.link')), `${line}: ${text} should be outside formatting, got ${token.scopes.join(', ')}`);
  }
  hasScope('**a*b** after', '*b', 'markup.bold.markdown');
  assert.ok(!tokens('**a*b** after').tokens.some(token => token.scopes.includes('markup.italic.markdown')), 'unmatched * inside strong stays literal');
  hasScope('**bold *italic* bold**', 'italic', 'markup.italic.markdown');
  hasScope('**bold *italic* bold**', 'bold', 'markup.bold.markdown');
  hasScope('*(door)*', '(door)', 'markup.italic.markdown');
  hasScope('*door!*', 'door!', 'markup.italic.markdown');
  hasScope('_(door)_', '(door)', 'markup.italic.markdown');
  hasScope('_door!_', 'door!', 'markup.italic.markdown');
  hasScope('**(door)**', '(door)', 'markup.bold.markdown');
  hasScope('__door!__', 'door!', 'markup.bold.markdown');
  for (const line of ['**_combined_**', '__*combined*__', '_**combined**_', '*__combined__*']) {
    hasScope(line, 'combined', 'markup.bold.markdown');
    hasScope(line, 'combined', 'markup.italic.markdown');
  }
  hasScope('Use ``a`b`` here.', 'a`b', 'markup.inline.raw.string.markdown');
  hasScope(String.raw`\*literal\*`, String.raw`\*`, 'constant.character.escape.tale');
  hasScope('![torch](torch.png)', 'torch.png', 'invalid.deprecated.image.tale');
  for (const line of ['![a [b]](p)', String.raw`![a\]](p)`, '![a](p(q))']) {
    assert.ok(!tokens(line).tokens.some(token => token.scopes.includes('invalid.deprecated.image.tale')), line);
  }
  const requires = '* [Continue] (requires: visited(start) and player:hp > 0) -> END';
  const close = requires.indexOf(') ->');
  const closeToken = tokens(requires).tokens.find(token => token.startIndex <= close && token.endIndex > close);
  assert.ok(closeToken.scopes.includes('punctuation.section.condition.end.tale'), closeToken.scopes.join(', '));
});

test('image alt text follows reader-text escapes while the path stays strict', () => {
  for (const line of [String.raw`{image("camp", "a \q b")}`, String.raw`{image("camp", "a \. b")}`, String.raw`{image("camp", "a \ b")}`]) {
    assert.ok(!tokens(line).tokens.some(token => token.scopes.includes('invalid.illegal.escape.tale')), line);
  }
  hasScope(String.raw`{image("camp", "a \q b")}`, String.raw`\q`, 'string.quoted.double.tale');
  hasScope(String.raw`{image("camp", "a \. b")}`, String.raw`\.`, 'constant.character.escape.tale');
  hasScope(String.raw`{image("camp\q", "alt")}`, String.raw`\q`, 'invalid.illegal.escape.tale');
});

test('reader-text highlighting escapes exactly the ASCII punctuation and symbols', () => {
  const punctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
  for (const character of punctuation) {
    const escape = `\\${character}`;
    const line = `text ${escape} end`;
    const offset = line.indexOf(escape);
    const token = tokens(line).tokens.find(item => item.startIndex <= offset && item.endIndex > offset + 1);
    assert.ok(token?.scopes.includes('constant.character.escape.tale'), line);
  }
  for (const character of ['a', '1', ' ', '—']) {
    const line = `text \\${character} end`;
    const offset = line.indexOf('\\');
    const token = tokens(line).tokens.find(item => item.startIndex <= offset && item.endIndex > offset);
    assert.ok(!token.scopes.includes('constant.character.escape.tale'), line);
  }
});

test('literal backslashes inside reader text do not suppress emphasis', () => {
  for (const [line, text, scope] of [
    [String.raw`*look \q here*`, 'here', 'markup.italic.markdown'],
    [String.raw`*look \ here*`, 'here', 'markup.italic.markdown'],
    [String.raw`**look \— here**`, 'here', 'markup.bold.markdown'],
    [String.raw`**look \. here**`, 'here', 'markup.bold.markdown'],
  ]) hasScope(line, text, scope);
});

test('escaped leading markers remain reader text', () => {
  for (const [line, directiveScope] of [
    [String.raw`\* [Go] -> hall`, 'punctuation.definition.choice.tale'],
    [String.raw`\> guide: Hello`, 'punctuation.definition.speech.tale'],
    [String.raw`\=== hall`, 'entity.name.section.tale'],
    [String.raw`\{player}`, 'meta.template.tale'],
  ]) {
    hasScope(line, line.slice(0, 2), 'constant.character.escape.tale');
    assert.ok(!tokens(line).tokens.some(token => token.scopes.includes(directiveScope)), line);
  }
});

test('choice conditions highlight only the exact requires marker', () => {
  const valid = '* [Continue] (requires: flag:ready) -> END';
  hasScope(valid, 'requires', 'keyword.control.tale');
  hasScope(valid, '(requires:', 'punctuation.section.condition.begin.tale');
  for (const spelling of ['( requires:', '(requires :', '( requires :']) {
    const line = `* [Continue] ${spelling} flag:ready) -> END`;
    const token = tokens(line).tokens.find(item => item.startIndex <= line.indexOf('(') && item.endIndex > line.indexOf('('));
    assert.ok(!token.scopes.includes('punctuation.section.condition.begin.tale'), line);
  }
});

test('speaker names use the shared identifier fixture', async () => {
  const fixture = JSON.parse(await readFile(new URL('./fixtures/identifiers.json', import.meta.url), 'utf8'));
  const speaker = id => tokens(`> ${id}: Hello.`).tokens
    .filter(token => token.scopes.includes('entity.name.tag.character.tale'))
    .map(token => `> ${id}: Hello.`.slice(token.startIndex, token.endIndex)).join('');
  for (const id of fixture.valid) assert.equal(speaker(id), id, `valid identifier ${JSON.stringify(id)}`);
  // A tokenized line cannot contain a line break.
  for (const id of fixture.invalid.filter(id => !/[\r\n]/.test(id))) {
    assert.equal(speaker(id), '', `invalid identifier ${JSON.stringify(id)}`);
  }
});

test('unterminated edits recover before the next scene', () => {
  for (const line of [
    '{player:first-name', '{if var:count == "unfinished', '* [unfinished',
    '* [choice] (requires: player:dex >', '* [choice] (requires: visited(start', '**unfinished', '_unfinished', '__unfinished',
    '> guide: {player:first-name', '::: skill-check', '~ var:count = "unfinished',
    '`unfinished code', '![unfinished](image', '[unfinished](link',
  ]) {
    const state = tokens(line).ruleStack;
    const next = tokens('=== recovered.scene', state);
    assert.ok(next.tokens.some(token => token.scopes.includes('entity.name.section.tale')), line);
    assert.ok(!next.tokens.some(token => token.scopes.includes('meta.template.tale') || token.scopes.includes('string.quoted.double.tale')), line);
  }
});

test('every shipped Talemark sample can be tokenized without timeouts', async () => {
  const { glob } = await import('node:fs/promises');
  let count = 0;
  for await (const file of glob('../../samples/**/*.tale')) {
    let state = textmate.INITIAL;
    for (const line of (await readFile(file, 'utf8')).split(/\r\n|\r|\n/)) {
      const result = grammar.tokenizeLine(line, state, 200);
      assert.equal(result.stoppedEarly, false, `${file}: ${line}`);
      state = result.ruleStack;
    }
    count++;
  }
  assert.ok(count > 0);
});
