import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import vm from 'node:vm';

const source = await readFile(new URL('../extension.cjs', import.meta.url), 'utf8');

function host(settings = {}, trusted = true, failStart = false, platform = 'linux', arch = 'x64', missing = false) {
  const calls = { clients: [], errors: [], watchers: [], stops: 0 };
  class LanguageClient {
    constructor(id, name, server, options) {
      calls.clients.push({ id, name, server, options });
      this.running = false;
    }
    async start() {
      if (failStart) throw new Error('executable not found');
      this.running = true;
    }
    isRunning() { return this.running; }
    async stop() { calls.stops++; this.running = false; }
  }
  const vscode = {
    Uri: { file: value => value },
    RelativePattern: class { constructor(base, pattern) { this.base = base; this.pattern = pattern; } },
    workspace: {
      isTrusted: trusted,
      getConfiguration: () => ({ get: (key, fallback) => settings[key] ?? fallback }),
      createFileSystemWatcher: pattern => {
        const watcher = { pattern };
        calls.watchers.push(watcher);
        return watcher;
      },
      onDidChangeConfiguration: listener => ({ listener }),
    },
    window: {
      createOutputChannel: () => ({ appendLine: () => {} }),
      showInformationMessage: () => {},
      showErrorMessage: message => calls.errors.push(message),
    },
  };
  const module = { exports: {} };
  vm.runInNewContext(source, {
    require: name => {
      if (name === 'vscode') return vscode;
      if (name === 'node:path') return path;
      if (name === 'node:fs') return {
        constants: { X_OK: 1 },
        accessSync: () => { if (missing) throw new Error('Bundled executable is missing'); },
      };
      assert.equal(name, 'vscode-languageclient/node');
      return { LanguageClient };
    },
    module,
    process: { platform, arch },
  });
  return { extension: module.exports, calls, context: { subscriptions: [], extensionPath: '/installed/talewright' } };
}

test('explicit development override launches one executable and forwards source/watch configuration', async () => {
  const { extension, calls, context } = host({
    'languageServer.command': 'dotnet',
    'languageServer.args': ['/server.dll', '--stdio'],
    'languageServer.metadataPath': 'build/registrations.json',
    'languageServer.requiresProjectMetadata': true,
  });
  await extension.activate(context);
  assert.equal(calls.clients.length, 1);
  const client = calls.clients[0];
  assert.equal(client.server.command, 'dotnet');
  assert.deepEqual(Array.from(client.server.args), ['/server.dll', '--stdio']);
  assert.equal(client.server.transport, undefined, 'Do not append a second --stdio argument');
  assert.equal(client.options.initializationOptions.metadataPath, 'build/registrations.json');
  assert.equal(client.options.initializationOptions.requiresProjectMetadata, true);
  assert.equal(client.options.documentSelector.length, 2);
  assert.equal(client.options.documentSelector[1].pattern, '**/*.talewright.yaml');
  assert.ok(calls.watchers.some(watcher => watcher.pattern === '**/*.talewright.yaml'));
  assert.ok(calls.watchers.some(watcher => watcher.pattern === '**/images/**'));
  assert.ok(!calls.watchers.some(watcher => watcher.pattern === '**/story.yaml'));
  assert.ok(calls.watchers.some(watcher => watcher.pattern === '**/build/registrations.json'));
  assert.ok(calls.watchers.every(watcher => context.subscriptions.includes(watcher)));
  await extension.deactivate();
  assert.equal(calls.stops, 1);
});

test('untrusted or explicitly disabled workspaces never launch a process', async () => {
  for (const instance of [host({}, false), host({ 'languageServer.enabled': false })]) {
    await instance.extension.activate(instance.context);
    assert.equal(instance.calls.clients.length, 0);
    assert.equal(instance.calls.watchers.length, 0);
  }
});

test('launch failures are actionable and do not cause stop failures', async () => {
  const { extension, calls, context } = host({}, true, true);
  await extension.activate(context);
  assert.ok(calls.errors.some(message => message.includes('executable not found') && message.includes('reinstall the VSIX')));
  await extension.deactivate();
  assert.equal(calls.stops, 0);
});

test('default server is the bundled executable, not a PATH lookup', async () => {
  const { extension, calls, context } = host();
  await extension.activate(context);
  assert.equal(calls.clients[0].server.command, '/installed/talewright/server/Talewright.LanguageServer');
  assert.deepEqual(Array.from(calls.clients[0].server.args), ['--stdio']);
  await extension.deactivate();
});

test('unsupported platforms and missing bundled assets fail clearly without fallback', async () => {
  for (const instance of [host({}, true, false, 'win32'), host({}, true, false, 'linux', 'arm64'),
    host({}, true, false, 'linux', 'x64', true)]) {
    await instance.extension.activate(instance.context);
    assert.equal(instance.calls.clients.length, 0);
    assert.equal(instance.calls.errors.length, 1);
    assert.match(instance.calls.errors[0], /Linux x64|missing/);
  }
  const override = host({ 'languageServer.command': 'custom-lsp' }, true, false, 'darwin', 'arm64');
  await override.extension.activate(override.context);
  assert.equal(override.calls.clients[0].server.command, 'custom-lsp');
  await override.extension.deactivate();
});

test('explicit absolute metadata files receive their own watcher', async () => {
  const metadata = path.resolve('registrations.json');
  const { extension, calls, context } = host({ 'languageServer.metadataPath': metadata });
  await extension.activate(context);
  assert.ok(calls.watchers.some(watcher =>
    watcher.pattern.base === path.dirname(metadata) && watcher.pattern.pattern === 'registrations.json'));
  await extension.deactivate();
});
