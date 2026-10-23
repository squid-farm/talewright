import { downloadAndUnzipVSCode } from '@vscode/test-electron';

export function vscodePath() {
  return process.env.VSCODE_EXECUTABLE_PATH
    ? Promise.resolve(process.env.VSCODE_EXECUTABLE_PATH)
    : downloadAndUnzipVSCode('1.100.3');
}
