import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { releaseIdentity } from './release-identity.mjs';

test('VSIX contains exactly the distributable assets', async () => {
  const archive = fileURLToPath(new URL('../artifacts/talewright.vsix', import.meta.url));
  const entries = execFileSync('unzip', ['-Z1', archive], { encoding: 'utf8' }).trim().split('\n').sort();
  const release = releaseIdentity();
  assert.deepEqual(entries.filter(entry => !entry.startsWith('extension/server/')), [
    '[Content_Types].xml',
    'extension.vsixmanifest',
    'extension/LICENSE.txt',
    'extension/THIRD-PARTY-NOTICES.txt',
    'extension/readme.md',
    'extension/language-configuration.json',
    'extension/package.json',
    'extension/dist/extension.cjs',
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
    const actual = execFileSync('unzip', ['-p', archive, `extension/${file}`], { encoding: 'utf8' });
    const expected = await readFile(new URL(`../${file}`, import.meta.url), 'utf8');
    if (file === 'package.json' && release) {
      assert.deepEqual(JSON.parse(actual), { ...JSON.parse(expected), version: release.vscode });
    } else {
      assert.equal(actual, expected);
    }
  }
  const vsixManifest = execFileSync('unzip', ['-p', archive, 'extension.vsixmanifest'], { encoding: 'utf8' });
  assert.ok(vsixManifest.includes('TargetPlatform="linux-x64"'));
  assert.equal(vsixManifest.includes('Id="Microsoft.VisualStudio.Code.PreRelease" Value="true"'),
    release?.prerelease ?? false);
  const verifier = fileURLToPath(new URL('../../../.github/scripts/verify_release_vsix.py', import.meta.url));
  execFileSync('python3', [verifier, archive, ...(release ? [process.env.TALEWRIGHT_RELEASE_TAG] : ['--bundle-only'])], { stdio: 'inherit' });
  assert.ok(!entries.some(entry => /\.(pdb|csproj|cs)$/.test(entry)), 'No build sources or debug symbols in VSIX');
  assert.equal(execFileSync('unzip', ['-p', archive, 'extension/LICENSE.txt'], { encoding: 'utf8' }),
    await readFile(new URL('../../../LICENSE', import.meta.url), 'utf8'));
  const notices = execFileSync('unzip', ['-p', archive, 'extension/THIRD-PARTY-NOTICES.txt'], { encoding: 'utf8' });
  const lock = JSON.parse(await readFile(new URL('../package-lock.json', import.meta.url), 'utf8'));
  for (const [directory, entry] of Object.entries(lock.packages)) {
    if (directory && !entry.dev) assert.ok(notices.includes(`${directory} @ ${entry.version}`), `Missing notice for ${directory}`);
  }
  const readme = execFileSync('unzip', ['-p', archive, 'extension/readme.md'], { encoding: 'utf8' });
  assert.ok(readme.includes('https://github.com/squid-farm/talewright/blob/main/docs/reference/talemark-syntax.md'));
  const links = readme.match(/https?:\/\/[^\s)]+/g) ?? [];
  assert.ok(links.every(link => !link.includes('/../')), 'Packaged links must not escape the repository URL');
});
