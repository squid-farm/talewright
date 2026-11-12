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

test('fallback keeps lightweight structural scopes', () => {
  hasScope('  // comment', '//', 'comment.line.double-slash.tale');
  hasScope('@continues', '@continues', 'keyword.control.metadata.tale');
  hasScope('@choice-label', '@choice-label', 'keyword.control.metadata.tale');
  hasScope('@choice-label Speak to the guide', '@choice-label', 'keyword.control.metadata.tale');
  hasScope('=== temple.hall', 'temple.hall', 'entity.name.section.tale');
  hasScope('-> temple.hall', '->', 'keyword.control.divert.tale');
  hasScope('* [Go] -> temple.hall', '->', 'keyword.control.divert.tale');
  assert.ok(!tokens('A -> sign in prose').tokens.some(token =>
    token.scopes.includes('keyword.control.divert.tale')));
});

test('fallback highlights templates, quoted strings, and escaped braces', () => {
  hasScope('{player:first-name}', 'first-name', 'meta.template.tale');
  hasScope('{image("camp", "A camp")}', 'camp', 'string.quoted.double.tale');
  hasScope('"quoted text"', 'quoted text', 'string.quoted.double.tale');
  hasScope(String.raw`{image("a \" quote", "alt")}`, String.raw`\"`, 'constant.character.escape.tale');
  hasScope(String.raw`\{literal\}`, String.raw`\{`, 'constant.character.escape.tale');
  assert.ok(!tokens(String.raw`\{player:first-name\}`).tokens.some(token =>
    token.scopes.includes('meta.template.tale')));
});

test('fallback keeps common bold and italic forms', () => {
  hasScope('**bold** text', 'bold', 'markup.bold.markdown');
  hasScope('__strong__ text', 'strong', 'markup.bold.markdown');
  hasScope('*italic* text', 'italic', 'markup.italic.markdown');
  hasScope('_emphasis_ text', 'emphasis', 'markup.italic.markdown');
  const escaped = String.raw`\*literal\*`;
  hasScope(escaped, String.raw`\*`, 'constant.character.escape.tale');
  assert.ok(!tokens(escaped).tokens.some(token => token.scopes.includes('markup.italic.markdown')));
  assert.ok(!tokens('word*inner*word').tokens.some(token => token.scopes.includes('markup.italic.markdown')));
});

test('unterminated templates and emphasis recover before the next scene', () => {
  for (const line of ['{unfinished template', '**unfinished emphasis']) {
    const next = tokens('=== recovered.scene', tokens(line).ruleStack);
    assert.ok(next.tokens.some(token => token.scopes.includes('entity.name.section.tale')), line);
    assert.ok(!next.tokens.some(token =>
      token.scopes.includes('meta.template.tale') || token.scopes.includes('string.quoted.double.tale')), line);
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
