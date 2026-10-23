import { createVSIX } from '@vscode/vsce';
import { copyFile, mkdir, rm, readFile, readdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const root = new URL('../', import.meta.url);
const license = new URL('LICENSE.txt', root);
const notices = new URL('THIRD-PARTY-NOTICES.txt', root);
await mkdir(new URL('artifacts/', root), { recursive: true });
await copyFile(new URL('../../LICENSE', root), license);
try {
  const lock = JSON.parse(await readFile(new URL('package-lock.json', root), 'utf8'));
  const sections = [];
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
    packagePath: fileURLToPath(new URL('artifacts/talewright.vsix', root)),
    dependencies: false,
    githubBranch: 'main',
  });
} finally {
  await rm(license);
  await rm(notices, { force: true });
}
