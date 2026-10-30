import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import textmate from 'vscode-textmate';
import oniguruma from 'vscode-oniguruma';
import { vscodePath } from './vscode-path.mjs';

const wasm = await readFile(new URL('../node_modules/vscode-oniguruma/release/onig.wasm', import.meta.url));
await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
const executable = await vscodePath();
// macOS keeps app resources beside Contents/MacOS rather than beside the executable.
const appRoot = process.platform === 'darwin'
  ? path.join(path.dirname(executable), '..', 'Resources', 'app')
  : path.join(path.dirname(executable), 'resources', 'app');
const markdownPath = path.join(appRoot, 'extensions/markdown-basics/syntaxes/markdown.tmLanguage.json');
const registry = new textmate.Registry({
  onigLib: Promise.resolve({
    createOnigScanner: patterns => new oniguruma.OnigScanner(patterns),
    createOnigString: value => new oniguruma.OnigString(value),
  }),
  loadGrammar: async scope => {
    const file = scope === 'text.tale'
      ? new URL('../syntaxes/tale.tmLanguage.json', import.meta.url)
      : scope === 'text.html.markdown' ? markdownPath : null;
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
    ['![torch](torch.png)', 'torch.png', 'invalid.deprecated.image.tale'],
    ['{image("maps/camp", "A camp")}', 'image', 'support.function.image.tale'],
    ['{image("maps/camp", "A } camp")}', 'camp"', 'string.quoted.double.tale'],
    ['{image("camp", "He said \\"Go\\".")}', '\\"', 'constant.character.escape.tale'],
    ['* [{image("camp", "A ] camp")} Go] -> END', 'A ] camp', 'string.quoted.double.tale'],
    ['> guide: {image("camp", "Camp")}', 'image', 'support.function.image.tale'],
    ['{image:name}', 'name', 'variable.other.member.tale'],
    ['[label](https://example.org)', 'label', 'meta.link.inline.markdown'],
    ['Literal \\{player:first-name\\}', '\\{', 'constant.character.escape.tale'],
    ['* [Continue] (requires: visited(start) and player:hp > 0) -> END', 'and', 'keyword.operator.word.tale'],
    ['* [Continue] (requires: visited(start) and player:hp > 0) -> END', 'hp', 'variable.other.member.tale'],
    ['* [Continue] (requires: visited(start) and player:hp > 0) -> END', 'END', 'entity.name.section.target.tale'],
    ['* [Go] (requires: (visited(a) or visited(b)) and player:hp > 0) -> END', 'hp', 'variable.other.member.tale'],
    ['{if visited(start) and player:hp > 0}Yes{/if}', 'hp', 'variable.other.member.tale'],
    ['__snake__case__ after', 'case', 'markup.bold.markdown'],
    ['_snake_case_words_ after', 'case_words', 'markup.italic.markdown'],
    ['**bold**text', 'bold', 'markup.bold.markdown'],
    ['*it*text', 'it', 'markup.italic.markdown'],
    ['*a**b**c*', 'b', 'markup.bold.markdown'],
    ['*a**b**c*', 'c', 'markup.italic.markdown'],
    ['**a*b** after', 'b', 'markup.bold.markdown'],
    ['***both*** after', 'both', 'markup.bold.markdown'],
    ['***both*** after', 'both', 'markup.italic.markdown'],
    ['**real \\** still bold** after', 'still bold', 'markup.bold.markdown'],
    ['*real \\* still italic* after', 'still italic', 'markup.italic.markdown'],
    ['__real \\__ still bold__ after', 'still bold', 'markup.bold.markdown'],
    ['_real \\_ still italic_ after', 'still italic', 'markup.italic.markdown'],
    ['*real `code*` still italic* after', 'still italic', 'markup.italic.markdown'],
    ['**literal \\}** after', 'literal', 'markup.bold.markdown'],
    ['__literal \\}__ after', 'literal', 'markup.bold.markdown'],
    ['*literal \\}* after', 'literal', 'markup.italic.markdown'],
    ['_literal \\}_ after', 'literal', 'markup.italic.markdown'],
    ['[Hello {player:first-name}](https://example.org)', 'first-name', 'variable.other.member.tale'],
    ['[Hello {player:first-name}](https://example.org)', 'first-name', 'string.other.link.title.markdown'],
    ['[Hello {player:first-name}](https://example.org)', 'example.org', 'markup.underline.link.markdown'],
    ['[{if flag:met}Old friend{else}Stranger{/if}](https://example.org "title")', 'if', 'keyword.control.conditional.tale'],
    ['[{if flag:met}Old friend{else}Stranger{/if}](https://example.org "title")', 'met', 'variable.other.member.tale'],
    ['[**Bold** {player:name} [x]](<https://example.org>)', 'Bold', 'markup.bold.markdown'],
    ['[**Bold** {player:name} [x]](<https://example.org>)', 'name', 'variable.other.member.tale'],
  ]) hasScope(line, text, scope);
});

test('ordinary prose, Markdown, comments and escaped braces are not Talemark commands', () => {
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
  ]) {
    const offset = line.indexOf(text);
    const token = tokens(line).tokens.find(token => token.startIndex <= offset && token.endIndex > offset);
    assert.ok(!token.scopes.some(scope => scope.startsWith('markup.') || scope.startsWith('meta.link')), `${line}: ${text} should be outside formatting, got ${token.scopes.join(', ')}`);
  }
  hasScope('**a*b** after', '*b', 'markup.bold.markdown');
  assert.ok(!tokens('**a*b** after').tokens.some(token => token.scopes.includes('markup.italic.markdown')), 'unmatched * inside strong stays literal');
  const requires = '* [Continue] (requires: visited(start) and player:hp > 0) -> END';
  const close = requires.indexOf(') ->');
  const closeToken = tokens(requires).tokens.find(token => token.startIndex <= close && token.endIndex > close);
  assert.ok(closeToken.scopes.includes('punctuation.section.condition.end.tale'), closeToken.scopes.join(', '));
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
