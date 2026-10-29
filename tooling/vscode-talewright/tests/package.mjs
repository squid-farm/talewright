import { createVSIX } from '@vscode/vsce';
import { copyFile, mkdir, rm, readFile, readdir, writeFile } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';
import { releaseIdentity } from './release-identity.mjs';

const root = new URL('../', import.meta.url);
const license = new URL('LICENSE.txt', root);
const notices = new URL('THIRD-PARTY-NOTICES.txt', root);
const manifestPath = new URL('package.json', root);
const manifestSource = await readFile(manifestPath, 'utf8');
const release = releaseIdentity();
const target = process.env.TALEWRIGHT_VSIX_TARGET || 'linux-x64';
const rid = {
  'linux-x64': 'linux-x64', 'linux-arm64': 'linux-arm64',
  'win32-x64': 'win-x64', 'win32-arm64': 'win-arm64',
  'darwin-x64': 'osx-x64', 'darwin-arm64': 'osx-arm64',
}[target];
if (!rid) throw new Error(`Unsupported VSIX target: ${target}`);
const packageName = target === 'linux-x64' ? 'talewright.vsix' : `talewright-${target}.vsix`;
await mkdir(new URL('artifacts/', root), { recursive: true });
await copyFile(new URL('../../LICENSE', root), license);
try {
  if (release) {
    const manifest = JSON.parse(manifestSource);
    manifest.version = release.vscode;
    await writeFile(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
  }
  const server = new URL('server/', root);
  await rm(server, { recursive: true, force: true });
  execFileSync('dotnet', [
    'publish', fileURLToPath(new URL('../language-server/src/Talewright.LanguageServer', root)),
    '-c', 'Release', '-r', rid, '--self-contained', 'true',
    '-p:PackAsTool=false', `-p:Version=${release?.nuget ?? JSON.parse(manifestSource).version}`,
    '-o', fileURLToPath(server),
  ], { stdio: 'inherit' });
  const runtime = JSON.parse(await readFile(new URL('Talewright.LanguageServer.runtimeconfig.json', server), 'utf8'));
  const runtimeVersion = runtime.runtimeOptions.includedFrameworks.find(framework => framework.name === 'Microsoft.NETCore.App').version;
  const cache = execFileSync('dotnet', ['nuget', 'locals', 'global-packages', '--list'], { encoding: 'utf8' })
    .trim().replace(/^global-packages:\s*/, '');
  const runtimePackage = new URL(`microsoft.netcore.app.runtime.${rid}/${runtimeVersion}/`, pathToFileURL(cache.replace(/\/?$/, '/')));
  for (const name of ['LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT']) {
    await copyFile(new URL(name, runtimePackage), new URL(name, server));
  }
  await writeFile(new URL('talewright-server.json', server), JSON.stringify({
    version: release?.nuget ?? JSON.parse(manifestSource).version,
    target,
  }) + '\n');
  const lock = JSON.parse(await readFile(new URL('package-lock.json', root), 'utf8'));
  const serverNotices = await readFile(new URL('tests/server-notices.txt', root), 'utf8');
  const dependencies = JSON.parse(await readFile(new URL('Talewright.LanguageServer.deps.json', server), 'utf8'));
  for (const [identity, library] of Object.entries(dependencies.libraries)) {
    if (library.type === 'package' && !serverNotices.includes(`\n${identity}\n`)) {
      throw new Error(`Update server-notices.txt with the pinned license for ${identity}`);
    }
  }
  const sections = [serverNotices];
  for (const [directory, entry] of Object.entries(lock.packages)) {
    if (!directory || entry.dev) continue;
    const packageRoot = new URL(`${directory}/`, root);
    const licenses = (await readdir(packageRoot)).filter(file => /^licen[cs]e(\..*)?$/i.test(file));
    if (licenses.length !== 1) throw new Error(`Expected one license file for ${directory}`);
    sections.push(`${directory} @ ${entry.version}\n\n${await readFile(new URL(licenses[0], packageRoot), 'utf8')}`);
  }
  await writeFile(notices, sections.join('\n\n----------------------------------------\n\n'));
  await createVSIX({
    cwd: fileURLToPath(root),
    packagePath: fileURLToPath(new URL(`artifacts/${packageName}`, root)),
    dependencies: false,
    githubBranch: 'main',
    preRelease: release?.prerelease ?? false,
    target,
  });
} finally {
  if (release) await writeFile(manifestPath, manifestSource);
  await rm(license);
  await rm(notices, { force: true });
}
