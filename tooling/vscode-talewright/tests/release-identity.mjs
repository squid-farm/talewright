import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export function releaseIdentity() {
  const tag = process.env.TALEWRIGHT_RELEASE_TAG;
  if (!tag) return undefined;
  const script = fileURLToPath(new URL('../../../.github/scripts/package_version.py', import.meta.url));
  return JSON.parse(execFileSync('python3', [script, '--tag', tag, '--release-json'], { encoding: 'utf8' }));
}
