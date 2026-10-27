import { resolveCliArgsFromVSCodeExecutablePath, runTests } from '@vscode/test-electron';
import { execFileSync, spawn } from 'node:child_process';
import { chmod, mkdir, mkdtemp, rm, writeFile, readdir, readFile, access } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { vscodePath } from './vscode-path.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
await mkdir(path.join(root, 'artifacts'), { recursive: true });
const profile = await mkdtemp(path.join(root, 'artifacts/editor-'));
try {
  await mkdir(path.join(profile, 'user', 'User'), { recursive: true });
  await writeFile(path.join(profile, 'user', 'User', 'settings.json'), JSON.stringify({
    'security.workspace.trust.startupPrompt': 'never',
  }));
  const bin = path.join(profile, 'no-dotnet');
  const dotnetMarker = path.join(profile, 'dotnet-was-used');
  await mkdir(bin);
  await writeFile(path.join(bin, 'dotnet'), '#!/bin/sh\n: > "$TALEWRIGHT_DOTNET_SENTINEL"\nexit 99\n');
  await chmod(path.join(bin, 'dotnet'), 0o755);
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
  const untrusted = path.join(profile, 'untrusted-story');
  await mkdir(untrusted);
  await writeFile(path.join(untrusted, 'story.talewright.yaml'),
    'game:\n  id: untrusted\n  title: Untrusted\n  start: start\ncharacters:\n  player: {first-name: Tester}\n');
  await writeFile(path.join(untrusted, 'start.tale'), '=== start\n* [Go] -> missing\n');
  for (const restricted of [true, false]) {
    const options = {
      vscodeExecutablePath: executable,
      extensionDevelopmentPath: path.join(root, 'tests/host'),
      extensionTestsPath: path.join(root, 'tests/editor.cjs'),
      extensionTestsEnv: {
        PATH: `${bin}${path.delimiter}${process.env.PATH}`,
        DOTNET_ROOT: path.join(profile, 'no-runtime'),
        DOTNET_ROOT_X64: path.join(profile, 'no-runtime'),
        DOTNET_MULTILEVEL_LOOKUP: '0',
        TALEWRIGHT_DOTNET_SENTINEL: dotnetMarker,
        TALEWRIGHT_TEST_UNTRUSTED: String(restricted),
      },
      launchArgs: [
        ...profileArgs,
        ...(restricted ? [untrusted] : ['--disable-workspace-trust']),
        '--skip-welcome', '--skip-release-notes', '--disable-gpu', '--no-sandbox',
      ],
    };
    if (restricted) {
      // test-electron unconditionally disables workspace trust; launch this pass without that flag.
      await new Promise((resolve, reject) => {
        const editor = spawn(executable, [
          ...options.launchArgs,
          `--extensionDevelopmentPath=${options.extensionDevelopmentPath}`,
          `--extensionTestsPath=${options.extensionTestsPath}`,
          '--disable-updates', '--no-cached-data',
        ], { env: { ...process.env, ...options.extensionTestsEnv }, stdio: 'inherit' });
        const timeout = setTimeout(() => {
          editor.kill();
          reject(new Error('Restricted-mode installed extension tests timed out.'));
        }, 120000);
        editor.once('error', error => { clearTimeout(timeout); reject(error); });
        editor.once('exit', code => {
          clearTimeout(timeout);
          if (code === 0) resolve();
          else reject(new Error(`Restricted-mode editor exited with code ${code}.`));
        });
      });
    } else {
      await runTests(options);
    }
  }
  await assertAbsent(dotnetMarker);
  if (process.env.TALEWRIGHT_TEST_CLI) {
    await writeFile(path.join(profile, 'user', 'User', 'settings.json'), JSON.stringify({
      'talewright.cli.executablePath': process.env.TALEWRIGHT_TEST_CLI,
      'talewright.languageServer.enabled': false,
    }));
    await runTests({
      vscodeExecutablePath: executable,
      extensionDevelopmentPath: path.join(root, 'tests/host'),
      extensionTestsPath: path.join(root, 'tests/editor.cjs'),
      extensionTestsEnv: { TALEWRIGHT_TEST_ACTIONS: 'true' },
      launchArgs: [...profileArgs, untrusted, '--disable-workspace-trust', '--disable-gpu', '--no-sandbox'],
    });
  }
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

async function assertAbsent(file) {
  try {
    await access(file);
  } catch (error) {
    if (error.code === 'ENOENT') return;
    throw error;
  }
  throw new Error('Installed extension attempted to use an external dotnet SDK/runtime.');
}
