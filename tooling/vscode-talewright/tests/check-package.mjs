import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

test('VSIX contains exactly the distributable assets', async () => {
  const archive = fileURLToPath(new URL('../artifacts/talewright.vsix', import.meta.url));
  const entries = execFileSync('unzip', ['-Z1', archive], { encoding: 'utf8' }).trim().split('\n').sort();
  assert.deepEqual(entries, [
    '[Content_Types].xml',
    'extension.vsixmanifest',
    'extension/LICENSE.txt',
    'extension/readme.md',
    'extension/language-configuration.json',
    'extension/package.json',
    'extension/snippets/tale.json',
    'extension/schemas/story.schema.json',
    'extension/syntaxes/tale.tmLanguage.json',
  ].sort());
  for (const file of [
    'language-configuration.json',
    'package.json',
    'schemas/story.schema.json',
    'snippets/tale.json',
    'syntaxes/tale.tmLanguage.json',
  ]) {
    assert.equal(execFileSync('unzip', ['-p', archive, `extension/${file}`], { encoding: 'utf8' }),
      await readFile(new URL(`../${file}`, import.meta.url), 'utf8'));
  }
  assert.equal(execFileSync('unzip', ['-p', archive, 'extension/LICENSE.txt'], { encoding: 'utf8' }),
    await readFile(new URL('../../../LICENSE', import.meta.url), 'utf8'));
  const readme = execFileSync('unzip', ['-p', archive, 'extension/readme.md'], { encoding: 'utf8' });
  assert.ok(readme.includes('https://github.com/squid-farm/talewright/blob/main/docs/reference/tale-syntax.md'));
  const links = readme.match(/https?:\/\/[^\s)]+/g) ?? [];
  assert.ok(links.every(link => !link.includes('/../')), 'Packaged links must not escape the repository URL');
});
