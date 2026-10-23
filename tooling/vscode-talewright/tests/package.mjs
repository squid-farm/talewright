import { createVSIX } from '@vscode/vsce';
import { copyFile, mkdir, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const root = new URL('../', import.meta.url);
const license = new URL('LICENSE.txt', root);
await mkdir(new URL('artifacts/', root), { recursive: true });
await copyFile(new URL('../../LICENSE', root), license);
try {
  await createVSIX({
    cwd: fileURLToPath(root),
    packagePath: fileURLToPath(new URL('artifacts/talewright.vsix', root)),
    dependencies: false,
    githubBranch: 'main',
  });
} finally {
  await rm(license);
}
