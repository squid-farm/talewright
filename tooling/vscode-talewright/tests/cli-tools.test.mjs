import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import vm from 'node:vm';
import { test } from 'node:test';

const source = await readFile(new URL('../cli-tools.cjs', import.meta.url), 'utf8');

function loadTools(platform = 'linux', arch = 'x64') {
  const vscode = {
    ProgressLocation: { Notification: 15 },
    workspace: { isTrusted: true },
    window: {},
  };
  const module = { exports: {} };
  vm.runInNewContext(source, {
    require: name => {
      if (name === 'vscode') return vscode;
      if (name === 'node:path') return (awaitPath => awaitPath)(undefined);
      if (name === 'node:os') return { homedir: () => '/home/author' };
      if (name === 'node:fs/promises') return {};
      if (name === 'node:fs') return {};
      if (name === 'node:child_process') return { execFile: () => {} };
      if (name === 'node:util') return { promisify: () => async () => ({}) };
      if (name === 'node:crypto') return { createHash: () => {} };
      throw new Error(`Unexpected dependency: ${name}`);
    },
    module,
    process: { platform, arch, env: {} },
    fetch: async () => {},
    AbortSignal,
  });
  return module.exports;
}

test('portable CLI platform mapping covers every published runtime target', () => {
  const tools = loadTools();
  assert.equal(tools.cliPlatform('win32', 'x64'), 'win-x64');
  assert.equal(tools.cliPlatform('win32', 'arm64'), 'win-arm64');
  assert.equal(tools.cliPlatform('darwin', 'arm64'), 'osx-arm64');
  assert.equal(tools.cliPlatform('darwin', 'x64'), 'osx-x64');
  assert.equal(tools.cliPlatform('linux', 'x64'), 'linux-x64');
  assert.equal(tools.cliPlatform('linux', 'arm64'), 'linux-arm64');
  assert.equal(tools.cliPlatform('linux', 'riscv64'), undefined);
});

test('release asset resolution requires the exact archive and checksum', () => {
  const tools = loadTools();
  const release = {
    draft: false,
    assets: [
      { name: 'talewright-cli-0.1.0-linux-x64.tar.gz', browser_download_url: 'https://example/archive' },
      { name: 'talewright-cli-0.1.0-linux-x64.tar.gz.sha256', browser_download_url: 'https://example/checksum' },
    ],
  };
  assert.equal(tools.releaseAsset(release, 'talewright-cli-0.1.0-linux-x64.tar.gz').browser_download_url,
    'https://example/archive');
  assert.equal(tools.releaseAsset(release, 'missing.tar.gz'), undefined);
});

test('download writes every byte when FileHandle.write returns a short count', async () => {
  const tools = loadTools();
  const calls = [];
  const handle = {
    write: async (_chunk, offset, length) => {
      calls.push([offset, length]);
      return { bytesWritten: offset === 0 ? 2 : length };
    },
  };

  assert.equal(await tools.writeFully(handle, new Uint8Array([1, 2, 3, 4, 5])), 5);
  assert.deepEqual(calls, [[0, 5], [2, 3]]);
});

test('download verifies, extracts, permissions, configures and reports progress', async () => {
  const calls = { progress: [], exec: [], updates: [], chmod: [] };
  const vscode = {
    ProgressLocation: { Notification: 15 },
    workspace: {
      isTrusted: true,
      getConfiguration: () => ({ update: async (key, value) => calls.updates.push([key, value]) }),
    },
    window: {
      showInformationMessage: () => {},
      withProgress: async (options, task) => {
        calls.progress.push(options);
        const token = { onCancellationRequested: () => {} };
        return task({
          report: report => calls.progress.push(report),
        }, token);
      },
    },
  };
  const path = {
    join: (...parts) => parts.join('/').replaceAll('//', '/'),
    isAbsolute: value => value.startsWith('/'),
    basename: value => value.split('/').pop(),
  };
  const checksum = 'a'.repeat(64);
  const module = { exports: {} };
  vm.runInNewContext(source, {
    require: name => {
      if (name === 'vscode') return vscode;
      if (name === 'node:path') return path;
      if (name === 'node:os') return { homedir: () => '/home/author' };
      if (name === 'node:fs/promises') return {
        access: async () => {},
        stat: async () => ({ isFile: () => true }),
        readFile: async () => Buffer.from(checksum),
        mkdir: async () => {},
        rm: async (file, options) => calls.exec.push(['rm', file, options]),
        writeFile: async () => {},
        chmod: async (file, mode) => calls.chmod.push([file, mode]),
        open: async () => ({
          write: async (chunk, offset, length) => ({ bytesWritten: Math.min(length, chunk.byteLength - offset) }),
          close: async () => {},
        }),
      };
      if (name === 'node:fs') return { constants: { F_OK: 0, X_OK: 1 } };
      if (name === 'node:child_process') return { execFile: () => {} };
      if (name === 'node:util') return {
        promisify: () => async (file, args) => {
          calls.exec.push([file, args]);
          return { stdout: file.endsWith('Talewright.Cli') ? '0.1.0-preview.1\n' : '' };
        },
      };
      if (name === 'node:crypto') return {
        createHash: () => ({ update: () => ({ digest: () => checksum }) }),
      };
      throw new Error(`Unexpected dependency: ${name}`);
    },
    module,
    process: { platform: 'linux', arch: 'x64', env: {} },
    AbortSignal,
    AbortController,
    fetch: async url => {
      if (url.includes('/releases/tags/')) {
        return {
          status: 200, ok: true,
          json: async () => ({
            draft: false,
            assets: [
              { name: 'talewright-cli-0.1.0-preview.1-linux-x64.tar.gz',
                browser_download_url: 'https://example/archive' },
              { name: 'talewright-cli-0.1.0-preview.1-linux-x64.tar.gz.sha256',
                browser_download_url: 'https://example/checksum' },
            ],
          }),
        };
      }
      if (url === 'https://example/archive') {
        return {
          ok: true, status: 200, body: [Buffer.from('archive')],
          headers: { get: () => '7' },
        };
      }
      return { ok: true, status: 200, text: async () => checksum };
    },
  });

  await module.exports.downloadTools({
    globalStorageUri: { fsPath: '/storage' },
  }, '/workspace', '0.1.0-preview.1');

  assert.equal(calls.progress[0].cancellable, true);
  assert.ok(calls.progress.some(entry => entry.message === '100%'));
  assert.ok(calls.exec.some(entry => entry[0] === 'tar'));
  assert.ok(calls.chmod.some(entry => entry[0].endsWith('/Talewright.Cli') && entry[1] === 0o755));
  assert.deepEqual(calls.updates, [['cli.executablePath', '/storage/cli/0.1.0-preview.1/linux-x64/Talewright.Cli']]);
});

test('CLI updates only replace the resource still configured to the managed download', async () => {
  const calls = { messages: [], updates: [], writes: [] };
  const managedExecutable = '/storage/cli/0.1.0-preview.1/linux-x64/Talewright.Cli';
  const newerExecutable = '/storage/cli/0.2.0-preview.1/linux-x64/Talewright.Cli';
  const configured = new Map([
    ['/workspace/A', '/custom/cli-a'],
    ['/workspace/B', '/custom/cli-b'],
  ]);
  const checksum = 'a'.repeat(64);
  const vscode = {
    ProgressLocation: { Notification: 15 },
    Uri: { file: fsPath => ({ scheme: 'file', fsPath }) },
    workspace: {
      isTrusted: true,
      workspaceFolders: ['/workspace/A', '/workspace/B'].map(fsPath => ({ uri: { fsPath } })),
      getWorkspaceFolder: uri => uri.fsPath.startsWith('/workspace/') ? { uri } : undefined,
      getConfiguration: (_namespace, resource) => ({
        get: (_key, fallback) => configured.get(resource?.fsPath) ?? fallback,
        update: async (_key, executable) => {
          calls.updates.push([resource?.fsPath, executable]);
          configured.set(resource?.fsPath, executable);
        },
      }),
    },
    window: {
      activeTextEditor: { document: { uri: { fsPath: '/workspace/B/story.tale' } } },
      showInformationMessage: async (...args) => {
        calls.messages.push(args);
        return args[1] === 'Update CLI' ? 'Update CLI' : undefined;
      },
      withProgress: async (_options, task) => task({
        report: () => {},
      }, { onCancellationRequested: () => {} }),
    },
  };
  const module = { exports: {} };
  vm.runInNewContext(source, {
    require: name => {
      if (name === 'vscode') return vscode;
      if (name === 'node:path') return path;
      if (name === 'node:os') return { homedir: () => '/home/author' };
      if (name === 'node:fs/promises') return {
        access: async () => {},
        stat: async () => ({ isFile: () => true }),
        readFile: async file => {
          if (file.endsWith('/downloaded.json')) {
            return JSON.stringify({
              version: '0.1.0-preview.1',
              rid: 'linux-x64',
              executable: managedExecutable,
              resource: '/workspace/A',
            });
          }
          if (file.endsWith('/talewright-server.json')) {
            return JSON.stringify({ version: '0.2.0-preview.1' });
          }
          return 'archive';
        },
        mkdir: async () => {},
        rm: async () => {},
        writeFile: async (file, contents) => calls.writes.push([file, String(contents)]),
        chmod: async () => {},
        open: async () => ({
          write: async (chunk, offset, length) => ({ bytesWritten: Math.min(length, chunk.byteLength - offset) }),
          close: async () => {},
        }),
      };
      if (name === 'node:fs') return { constants: { F_OK: 0, X_OK: 1 } };
      if (name === 'node:child_process') return { execFile: () => {} };
      if (name === 'node:util') return {
        promisify: () => async file => ({ stdout: file === newerExecutable ? '0.2.0-preview.1\n' : '' }),
      };
      if (name === 'node:crypto') return {
        createHash: () => ({ update: () => ({ digest: () => checksum }) }),
      };
      throw new Error(`Unexpected dependency: ${name}`);
    },
    module,
    process: { platform: 'linux', arch: 'x64', env: {} },
    AbortSignal,
    AbortController,
    fetch: async url => {
      if (url.includes('/releases/tags/')) {
        return {
          status: 200,
          ok: true,
          json: async () => ({
            draft: false,
            assets: [
              { name: 'talewright-cli-0.2.0-preview.1-linux-x64.tar.gz', browser_download_url: 'https://example/archive' },
              { name: 'talewright-cli-0.2.0-preview.1-linux-x64.tar.gz.sha256', browser_download_url: 'https://example/checksum' },
            ],
          }),
        };
      }
      if (url === 'https://example/archive') {
        return {
          ok: true,
          status: 200,
          body: [Buffer.from('archive')],
          headers: { get: () => '7' },
        };
      }
      return { ok: true, status: 200, text: async () => checksum };
    },
  });

  const context = {
    globalStorageUri: { fsPath: '/storage' },
    extensionPath: '/extension',
  };
  await module.exports.checkCliUpdates(context);
  assert.equal(calls.messages.length, 0);
  assert.deepEqual(calls.updates, []);

  configured.set('/workspace/A', managedExecutable);
  await module.exports.checkCliUpdates(context);

  assert.equal(calls.messages[0][1], 'Update CLI');
  assert.deepEqual(calls.updates, [[
    '/workspace/A', newerExecutable,
  ]]);
  assert.equal(configured.get('/workspace/B'), '/custom/cli-b');
  const downloadedMetadata = calls.writes.find(([file]) => file.endsWith('/downloaded.json'));
  assert.deepEqual(JSON.parse(downloadedMetadata[1]), {
    version: '0.2.0-preview.1',
    rid: 'linux-x64',
    executable: newerExecutable,
    resource: '/workspace/A',
  });
});
