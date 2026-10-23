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
const markdownPath = path.join(path.dirname(executable), 'resources/app/extensions/markdown-basics/syntaxes/markdown.tmLanguage.json');
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

test('Tale syntax categories tokenize with real Oniguruma/TextMate', () => {
  for (const [line, text, scope] of [
    ['=== temple.hall', 'temple.hall', 'entity.name.section.tale'],
    ['  // {if true} * [not a choice]', '//', 'comment.line.double-slash.tale'],
    ['@requires player:dex >= 12 and @CanClimb', '@requires', 'keyword.control.metadata.tale'],
    ['@requires player:dex >= 12 and @CanClimb', '@CanClimb', 'support.function.predicate.tale'],
    ['@hub any(camp, temple)', '@hub', 'keyword.control.metadata.tale'],
    ['@hub-limit 3', '@hub-limit', 'keyword.control.metadata.tale'],
    ['@once', '@once', 'keyword.control.metadata.tale'],
    ['@title The **sealed** door', 'sealed', 'markup.bold.markdown'],
    ['> guide: Hello {player:first-name}.', 'guide', 'entity.name.tag.character.tale'],
    ['> guide: Hello {player:first-name}.', 'first-name', 'variable.other.member.tale'],
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
    ['![torch](torch.png)', 'torch.png', 'meta.image.inline.markdown'],
    ['[label](https://example.org)', 'label', 'meta.link.inline.markdown'],
    ['Literal \\{player:first-name\\}', '\\{', 'constant.character.escape.tale'],
  ]) hasScope(line, text, scope);
});

test('ordinary prose, Markdown, comments and escaped braces are not Tale commands', () => {
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
});

test('unterminated edits recover before the next scene', () => {
  for (const line of [
    '{player:first-name', '{if var:count == "unfinished', '* [unfinished',
    '* [choice] (requires: player:dex >', '**unfinished', '_unfinished',
    '> guide: {player:first-name', '::: skill-check', '~ var:count = "unfinished',
    '`unfinished code', '![unfinished](image', '[unfinished](link',
  ]) {
    const state = tokens(line).ruleStack;
    const next = tokens('=== recovered.scene', state);
    assert.ok(next.tokens.some(token => token.scopes.includes('entity.name.section.tale')), line);
    assert.ok(!next.tokens.some(token => token.scopes.includes('meta.template.tale') || token.scopes.includes('string.quoted.double.tale')), line);
  }
});

test('every shipped Tale sample can be tokenized without timeouts', async () => {
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
