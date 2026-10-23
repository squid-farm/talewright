import { resolveCliArgsFromVSCodeExecutablePath, runTests } from '@vscode/test-electron';
import { execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile, readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { vscodePath } from './vscode-path.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
await mkdir(path.join(root, 'artifacts'), { recursive: true });
const profile = await mkdtemp(path.join(root, 'artifacts/editor-'));
try {
  await mkdir(path.join(profile, 'user', 'User'), { recursive: true });
  await writeFile(path.join(profile, 'user', 'User', 'settings.json'), JSON.stringify({
    'talewright.languageServer.command': 'dotnet',
    'talewright.languageServer.args': [
      path.resolve(root, '../language-server/src/Talewright.LanguageServer/bin/Release/net10.0/Talewright.LanguageServer.dll'),
      '--stdio',
    ],
  }));
  const executable = await vscodePath();
  const profileArgs = [
    '--user-data-dir', path.join(profile, 'user'),
    '--extensions-dir', path.join(profile, 'extensions'),
  ];
  const [cli, ...cliArgs] = resolveCliArgsFromVSCodeExecutablePath(executable);
  execFileSync(cli, [
    ...cliArgs.filter(arg => !arg.startsWith('--extensions-dir=') && !arg.startsWith('--user-data-dir=')),
    ...profileArgs, '--install-extension', path.join(root, 'artifacts/talewright.vsix'),
    '--force', '--no-sandbox',
  ], { stdio: 'inherit', shell: process.platform === 'win32' });
  execFileSync(cli, [
    ...cliArgs.filter(arg => !arg.startsWith('--extensions-dir=') && !arg.startsWith('--user-data-dir=')),
    ...profileArgs, '--install-extension', 'redhat.vscode-yaml', '--force', '--no-sandbox',
  ], { stdio: 'inherit', shell: process.platform === 'win32' });
  await runTests({
    vscodeExecutablePath: executable,
    extensionDevelopmentPath: path.join(root, 'tests/host'),
    extensionTestsPath: path.join(root, 'tests/editor.cjs'),
    launchArgs: [
      ...profileArgs,
      '--disable-workspace-trust', '--skip-welcome',
      '--skip-release-notes', '--disable-gpu', '--no-sandbox',
    ],
  });
} catch (error) {
  for (const entry of await readdir(profile, { recursive: true })) {
    if (entry.endsWith('.log') && (entry.includes('output') || entry.includes('exthost'))) {
      console.error(`--- ${entry} ---\n${(await readFile(path.join(profile, entry), 'utf8')).split('\n').slice(-60).join('\n')}`);
    }
  }
  throw error;
} finally {
  await rm(profile, { recursive: true, force: true });
}
