import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import vm from 'node:vm';

const source = await readFile(new URL('../author-actions.cjs', import.meta.url), 'utf8');

function host(options = {}) {
  const uri = file => ({ scheme: 'file', fsPath: file });
  const calls = { tasks: [], errors: [], picks: [], dialogs: [], commands: new Map() };
  const roots = options.roots ?? ['/workspace/My Story'];
  const documents = options.documents ?? [];
  const vscode = {
    Uri: { file: uri },
    RelativePattern: class { constructor(folder, pattern) { this.folder = folder; this.pattern = pattern; } },
    ProcessExecution: class { constructor(executable, args, settings) { Object.assign(this, { executable, args, settings }); } },
    Task: class { constructor(definition, scope, name, source, execution, problemMatchers) {
      Object.assign(this, { definition, scope, name, source, execution, problemMatchers });
    } },
    TaskRevealKind: { Always: 1 }, TaskPanelKind: { Dedicated: 2 }, TaskScope: { Global: 1, Workspace: 2 },
    workspace: {
      isTrusted: options.trusted ?? true,
      workspaceFolders: (options.folders ?? ['/workspace']).map(folder => ({ uri: uri(folder) })),
      textDocuments: documents,
      findFiles: async pattern => roots.filter(root => {
        const relative = path.relative(pattern.folder.uri.fsPath, root);
        return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
      }).map(root => uri(`${root}/story.talewright.yaml`)),
      getConfiguration: () => ({ get: () => options.executable ?? '' }),
      getWorkspaceFolder: () => ({ uri: uri('/workspace') }),
    },
    window: {
      activeTextEditor: options.active ? { document: { uri: uri(options.active) } } : undefined,
      showErrorMessage: async message => calls.errors.push(message),
      showQuickPick: async choices => { calls.picks.push(choices); return choices[options.pick]; },
      showWarningMessage: async () => options.saveChoice,
      showSaveDialog: async settings => { calls.dialogs.push(settings); return options.output ? uri(options.output) : undefined; },
    },
    commands: { registerCommand: (id, handler) => { calls.commands.set(id, handler); return {}; } },
    tasks: {
      registerTaskProvider: (type, provider) => { calls.provider = provider; return {}; },
      executeTask: async task => { calls.tasks.push(task); if (options.launchError) throw new Error(options.launchError); },
    },
  };
  const files = options.files ?? ['/bin/talewright'];
  const module = { exports: {} };
  vm.runInNewContext(source, {
    require: name => ({
      vscode, 'node:path': path, 'node:fs': { constants: { X_OK: 1, F_OK: 0 } },
      'node:fs/promises': {
        access: async file => { if (!files.includes(file)) throw Object.assign(new Error('missing'), { code: 'ENOENT' }); },
        stat: async () => ({ isFile: () => true }),
      },
    })[name],
    process: { platform: 'linux', env: { PATH: '/bin' } }, module,
  });
  module.exports.registerAuthorActions({ subscriptions: [] });
  const run = name => calls.commands.get(`talewright.${name}`)();
  return { calls, run };
}

test('workspace-scoped debugger tasks resolve relative or absolute stories across open folders', async () => {
  for (const [options, story] of [
    [{ folders: ['/workspace', '/other'], roots: ['/other/My Story'] }, 'My Story'],
    [{ folders: ['/workspace', '/other'], roots: ['/workspace/My Story', '/other/My Story'] }, '/other/My Story'],
    [{ folders: ['/other', '/other/My Story'], roots: ['/other/My Story'] }, '.'],
  ]) {
    const { calls } = host(options);
    const definition = { type: 'talewright', command: 'debug', story };
    const task = await calls.provider.resolveTask({ definition, scope: 2 });
    assert.ok(task);
    assert.equal(task.definition, definition);
    assert.equal(task.scope, 2);
    assert.deepEqual(Array.from(task.execution.args), ['debug', '/other/My Story']);
    assert.equal(calls.errors.length, 0);
  }
});

test('workspace-scoped debugger tasks reject ambiguous, missing, nested and outside stories', async () => {
  for (const [options, story, message] of [
    [{ folders: ['/workspace', '/other'], roots: ['/workspace/Story', '/other/Story'] }, 'Story', /ambiguous.*absolute/i],
    [{}, 'missing', /canonical story root/],
    [{}, 'My Story/nested', /canonical story root/],
    [{ roots: ['/outside/Story'] }, '/outside/Story', /canonical story root/],
    [{ folders: [] }, '/workspace/My Story', /canonical story root/],
    [{ trusted: false }, 'My Story', /Trust this workspace/],
  ]) {
    const { calls } = host(options);
    const task = await calls.provider.resolveTask({
      definition: { type: 'talewright', command: 'debug', story }, scope: 2,
    });
    assert.equal(task, undefined);
    assert.match(calls.errors[0], message);
    assert.equal(calls.tasks.length, 0);
  }
});

test('debugger tasks reject global, missing and non-file folder scopes explicitly', async () => {
  for (const scope of [1, undefined, { uri: { scheme: 'vscode-remote', fsPath: '/workspace' } }]) {
    const { calls } = host();
    assert.equal(await calls.provider.resolveTask({
      definition: { type: 'talewright', command: 'debug', story: 'My Story' }, scope,
    }), undefined);
    assert.match(calls.errors[0], /workspace.*scope/);
  }
});

test('validation runs the CLI in a visible process task with separate space-safe arguments', async () => {
  const { calls, run } = host();
  await run('validateStory');
  const task = calls.tasks[0];
  assert.equal(task.execution.executable, '/bin/talewright');
  assert.deepEqual(Array.from(task.execution.args), ['validate', '/workspace/My Story']);
  assert.equal(task.execution.settings.cwd, '/workspace/My Story');
  assert.equal(task.presentationOptions.focus, true);
  assert.equal(task.presentationOptions.reveal, 1);
  assert.equal(calls.errors.length, 0);
});

test('active containing story wins, otherwise multiple stories require an explicit choice', async () => {
  const roots = ['/workspace/A', '/workspace/B'];
  const active = host({ roots, active: '/workspace/B/scene.tale' });
  await active.run('validateStory');
  assert.equal(active.calls.tasks[0].execution.args[1], '/workspace/B');
  assert.equal(active.calls.picks.length, 0);
  const chosen = host({ roots, active: '/outside/A/file.tale', pick: 1 });
  await chosen.run('validateStory');
  assert.equal(chosen.calls.tasks[0].execution.args[1], '/workspace/B');
  const cancelled = host({ roots });
  await cancelled.run('validateStory');
  assert.equal(cancelled.calls.tasks.length, 0);
});

test('nested manifests are not independent story roots and do not match sibling prefixes', async () => {
  const { calls, run } = host({
    roots: ['/workspace/A', '/workspace/A/nested', '/workspace/AB'],
    active: '/workspace/A/nested/scene.tale',
  });
  await run('validateStory');
  assert.equal(calls.tasks[0].execution.args[1], '/workspace/A');
  const sibling = host({ roots: ['/workspace/A', '/workspace/AB'], active: '/workspace/AB/file.tale' });
  await sibling.run('validateStory');
  assert.equal(sibling.calls.tasks[0].execution.args[1], '/workspace/AB');
});

test('missing roots, CLI, broken explicit paths, untrusted workspaces and launch errors are visible', async () => {
  for (const [options, message] of [
    [{ roots: [] }, /story.talewright.yaml/],
    [{ files: [] }, /CLI not found.*bundled language server is not the CLI/],
    [{ executable: '/missing/custom', files: ['/bin/talewright'] }, /no fallback/],
    [{ executable: 'relative/tool' }, /absolute path/],
    [{ trusted: false }, /Trust this workspace/],
    [{ launchError: 'spawn failed' }, /spawn failed/],
  ]) {
    const { calls, run } = host(options);
    await run('validateStory');
    assert.match(calls.errors[0], message);
    if (!options.launchError) assert.equal(calls.tasks.length, 0);
  }
});

test('explicit executable with spaces is used without shell quoting or PATH fallback', async () => {
  const executable = '/tools/Talewright CLI/talewright';
  const { calls, run } = host({ executable, files: [executable] });
  await run('validateStory');
  assert.equal(calls.tasks[0].execution.executable, executable);
});

test('dirty documents require consent and successful saves; unrelated documents are untouched', async () => {
  let saves = 0;
  const document = () => ({
    uri: { scheme: 'file', fsPath: '/workspace/My Story/scene.tale' }, isDirty: true,
    async save() { saves++; this.isDirty = false; return true; },
  });
  const cancelled = host({ documents: [document()] });
  await cancelled.run('validateStory');
  assert.equal(saves, 0);
  assert.equal(cancelled.calls.tasks.length, 0);
  const saved = host({ documents: [document()], saveChoice: 'Save and Continue' });
  await saved.run('validateStory');
  assert.equal(saves, 1);
  assert.equal(saved.calls.tasks.length, 1);
  for (const save of [async () => false, async () => { throw new Error('read-only filesystem'); }]) {
    const failed = host({ documents: [{ ...document(), save }], saveChoice: 'Save and Continue' });
    await failed.run('validateStory');
    assert.equal(failed.calls.tasks.length, 0);
    assert.equal(failed.calls.errors.length, 1);
  }
  const unrelated = document();
  unrelated.uri.fsPath = '/elsewhere/scene.tale';
  await host({ documents: [unrelated] }).run('validateStory');
  assert.equal(saves, 1);
});

test('publish chooses a ZIP then uses interactive CLI overwrite behavior, never --force', async () => {
  const { calls, run } = host({ output: '/exports/My Game.zip' });
  await run('publishStoryZip');
  assert.deepEqual(Array.from(calls.tasks[0].execution.args),
    ['publish', '/workspace/My Story', '--output', '/exports/My Game.zip']);
  assert.match(calls.dialogs[0].title, /not a standalone game/);
  const cancelled = host();
  await cancelled.run('publishStoryZip');
  assert.equal(cancelled.calls.tasks.length, 0);
  const invalid = host({ output: '/exports/no-extension' });
  await invalid.run('publishStoryZip');
  assert.equal(invalid.calls.tasks.length, 0);
  assert.match(invalid.calls.errors[0], /\.zip/);
});

test('debug action and reusable task share selection, executable, trust and save handling', async () => {
  const { calls, run } = host();
  await run('openAuthorDebugger');
  assert.deepEqual(Array.from(calls.tasks[0].execution.args), ['debug', '/workspace/My Story']);
  const definition = { type: 'talewright', command: 'debug', story: 'My Story' };
  const task = await calls.provider.resolveTask({ definition, scope: { uri: { scheme: 'file', fsPath: '/workspace' } } });
  assert.equal(task.definition, definition);
  assert.deepEqual(Array.from(task.execution.args), ['debug', '/workspace/My Story']);
  for (const story of ['../outside', 'My Story/nested']) {
    assert.equal(await calls.provider.resolveTask({
      definition: { ...definition, story }, scope: { uri: { scheme: 'file', fsPath: '/workspace' } },
    }), undefined);
  }
  const untrusted = host({ trusted: false });
  assert.equal(await untrusted.calls.provider.resolveTask({
    definition, scope: { uri: { scheme: 'file', fsPath: '/workspace' } },
  }), undefined);
  assert.match(untrusted.calls.errors[0], /Trust this workspace/);
});
