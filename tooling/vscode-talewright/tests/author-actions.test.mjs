import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import vm from 'node:vm';

const source = await readFile(new URL('../author-actions.cjs', import.meta.url), 'utf8');
const toolsSource = await readFile(new URL('../cli-tools.cjs', import.meta.url), 'utf8');

function host(options = {}) {
  const uri = file => ({ scheme: 'file', fsPath: file });
  const document = (file, text, settings = {}) => {
    const lines = text.split(/\r\n|\r|\n/);
    return {
      uri: uri(file), languageId: settings.languageId ?? 'tale', isDirty: settings.isDirty ?? false,
      lineCount: lines.length, lineAt: line => ({ text: lines[line] }), getText: () => text,
      save: settings.save ?? (async () => true),
    };
  };
  const calls = {
    tasks: [], errors: [], picks: [], dialogs: [], openDialogs: [], messages: [], warnings: [], updates: [],
    urls: [], fetches: [], sdk: [], cli: [], runtimeChecks: [], openFolders: [], openedDocuments: [],
    shownDocuments: [], commands: new Map(), contextKeys: [], codeLensProviders: [],
  };
  const globalState = options.state ?? new Map();
  const roots = options.roots ?? ['/workspace/My Story'];
  const documents = options.documents ?? [];
  const activeDocument = options.activeDocument
    ?? (options.active ? documents.find(item => item.uri.fsPath === options.active)
      ?? document(options.active, options.activeText ?? '') : undefined);
  const textDocuments = activeDocument && !documents.includes(activeDocument)
    ? [...documents, activeDocument] : documents;
  class EventEmitter {
    listeners = [];
    event = listener => { this.listeners.push(listener); return { dispose() {} }; };
    fire(value) { for (const listener of this.listeners) listener(value); }
    dispose() {}
  }
  const vscode = {
    Uri: { file: uri, parse: url => url },
    Position: class { constructor(line, character) { Object.assign(this, { line, character }); } },
    Range: class { constructor(startLine, startCharacter, endLine, endCharacter) {
      this.start = new vscode.Position(startLine, startCharacter);
      this.end = new vscode.Position(endLine, endCharacter);
    } },
    CodeLens: class { constructor(range, command) { Object.assign(this, { range, command }); } },
    EventEmitter,
    RelativePattern: class { constructor(folder, pattern) { this.folder = folder; this.pattern = pattern; } },
    ProcessExecution: class { constructor(executable, args, settings) { Object.assign(this, { executable, args, settings }); } },
    Task: class { constructor(definition, scope, name, source, execution, problemMatchers) {
      Object.assign(this, { definition, scope, name, source, execution, problemMatchers });
    } },
    TaskRevealKind: { Always: 1 }, TaskPanelKind: { Dedicated: 2 }, TaskScope: { Global: 1, Workspace: 2 },
    env: { openExternal: async url => { calls.urls.push(url); return true; } },
    workspace: {
      isTrusted: options.trusted ?? true,
      workspaceFolders: (options.folders ?? ['/workspace']).map(folder => ({ uri: uri(folder) })),
      textDocuments,
      findFiles: async pattern => roots.filter(root => {
        const relative = path.relative(pattern.folder.uri.fsPath, root);
        return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
      }).map(root => uri(`${root}/story.talewright.yaml`)),
      getConfiguration: (section, resource) => {
        calls.configurationResource = resource;
        return {
          get: (key, defaultValue) => key === 'codeLens.playFromScene'
            ? options.codeLens ?? true
            : key === 'cli.executablePath' ? options.executable ?? '' : defaultValue,
          update: async (key, value) => { calls.updates.push([key, value]); options.executable = value; },
        };
      },
      getWorkspaceFolder: resource => vscode.workspace.workspaceFolders.find(folder => {
        const relative = path.relative(folder.uri.fsPath, resource.fsPath);
        return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
      }),
      openTextDocument: async resource => {
        calls.openedDocuments.push(resource);
        return textDocuments.find(item => item.uri.fsPath === resource.fsPath) ?? { uri: resource };
      },
      onDidChangeTextDocument: listener => { calls.documentChanged = listener; return { dispose() {} }; },
      onDidChangeConfiguration: listener => { calls.configurationChanged = listener; return { dispose() {} }; },
    },
    window: {
      activeTextEditor: activeDocument ? {
        document: activeDocument, selection: { active: { line: options.activeLine ?? 0 } },
      } : undefined,
      onDidChangeActiveTextEditor: listener => { calls.activeEditorChanged = listener; return { dispose() {} }; },
      onDidChangeTextEditorSelection: listener => { calls.selectionChanged = listener; return { dispose() {} }; },
      showErrorMessage: async (message, ...actions) => { calls.errors.push(message); calls.errorActions = actions; return options.errorChoice; },
      showInformationMessage: async message => { calls.messages.push(message); return options.infoChoice; },
      showQuickPick: async (choices, settings) => {
        calls.picks.push(choices);
        calls.pickSettings = settings;
        const pick = options.pickSequence ? options.pickSequence.shift() : options.pick;
        return choices[pick];
      },
      showWarningMessage: async message => { calls.warnings.push(message); options.onConsent?.(vscode); return options.saveChoice; },
      showOpenDialog: async settings => {
        calls.openDialogs.push(settings);
        return options.selected ? [uri(options.selected)] : undefined;
      },
      showTextDocument: async document => { calls.shownDocuments.push(document); return {}; },
      showSaveDialog: async settings => { calls.dialogs.push(settings); return options.output ? uri(options.output) : undefined; },
    },
    commands: {
      registerCommand: (id, handler) => { calls.commands.set(id, handler); return {}; },
      executeCommand: async (id, ...args) => {
        if (id === 'setContext') {
          calls.contextKeys.push(args);
          return;
        }
        if (id === 'vscode.openFolder') {
          calls.openFolders.push(args);
          return options.openFolderResult ?? true;
        }
        return calls.commands.get(id)(...args);
      },
    },
    tasks: {
      registerTaskProvider: (type, provider) => { calls.provider = provider; return {}; },
      executeTask: async task => {
        calls.tasks.push(task);
        if (options.launchError) throw new Error(options.launchError);
        calls.execution = { task };
        if (options.installSuccess) files.push('/home/author/.dotnet/tools/talewright');
        if (!options.noProcess) {
          calls.endHandler?.({ execution: calls.execution, exitCode: options.cancelledInstall ? undefined : options.exitCode ?? 0 });
        }
        calls.taskEndHandler?.({ execution: calls.execution });
        return calls.execution;
      },
      onDidEndTaskProcess: handler => {
        calls.endHandler = handler;
        return { dispose() {} };
      },
      onDidEndTask: handler => {
        calls.taskEndHandler = handler;
        return { dispose() {} };
      },
    },
    languages: {
      registerCodeLensProvider: (selector, provider) => {
        calls.codeLensProviders.push({ selector, provider });
        return {};
      },
    },
  };
  const files = options.files ?? ['/bin/talewright'];
  const module = { exports: {} };
  const dependencies = {
    vscode, 'node:path': path, 'node:fs': { constants: { X_OK: 1, F_OK: 0 } },
    'node:os': { homedir: () => '/home/author' },
    'node:child_process': { execFile: {} },
    'node:util': { promisify: () => async (...args) => {
      if (args[0] === 'dotnet' && args[1][0] === '--list-runtimes') {
        calls.runtimeChecks.push(args);
        if (options.dotnetError) throw Object.assign(new Error(options.dotnetError), { code: options.dotnetError });
        return { stdout: options.runtimes ?? 'Microsoft.NETCore.App 10.0.0 [/usr/share/dotnet/shared/Microsoft.NETCore.App]' };
      }
      if (args[0] === 'dotnet') {
        calls.sdk.push(args);
        if (options.sdkError) throw Object.assign(new Error(options.sdkError), { code: options.sdkError });
        return { stdout: options.sdks ?? '' };
      }
      calls.cli.push(args);
      if (options.cliError) {
        throw Object.assign(new Error(options.cliError), {
          code: options.cliErrorCode ?? 1,
          stderr: options.cliStderr ?? options.cliError,
          stdout: options.cliStdout ?? '',
        });
      }
      return { stdout: options.cliStdout ?? '', stderr: options.cliStderr ?? '' };
    } },
    'node:fs/promises': {
      access: async file => { if (!files.includes(file)) throw Object.assign(new Error('missing'), { code: 'ENOENT' }); },
      stat: async () => ({ isFile: () => true }),
      readFile: async () => JSON.stringify({ version: options.version ?? '0.1.0-preview.1' }),
      readdir: async () => options.directoryEntries ?? [],
    },
  };
  const globals = {
    process: { platform: 'linux', arch: 'x64', env: { PATH: '/bin' } },
    AbortSignal,
    fetch: async url => {
      calls.fetches.push(url);
      if (options.networkError) throw new Error(options.networkError);
      return {
        status: options.httpStatus ?? 200, ok: !options.httpStatus || options.httpStatus === 200,
        json: async () => url.includes('nuget.org') ? { versions: options.versions ?? ['0.1.0-preview.1'] }
          : { assets: (options.assets ?? ['talewright-cli-0.1.0-preview.1-linux-x64.tar.gz',
            'talewright-cli-0.1.0-preview.1-linux-x64.tar.gz.sha256']).map(name => ({ name })) },
      };
    },
  };
  const tools = { exports: {} };
  vm.runInNewContext(toolsSource, { ...globals, require: name => dependencies[name], module: tools });
  dependencies['./cli-tools.cjs'] = tools.exports;
  vm.runInNewContext(source, { ...globals, require: name => dependencies[name], module });
  const activation = module.exports.registerAuthorActions({
    subscriptions: [],
    extensionPath: '/extension',
    globalState: {
      get: key => globalState.get(key),
      update: async (key, value) => {
        if (value === undefined) globalState.delete(key);
        else globalState.set(key, value);
      },
    },
  });
  const run = name => calls.commands.get(`talewright.${name}`)();
  return { calls, run, activation, state: globalState, document, api: module.exports };
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

test('Play From This Scene detects the active scene, offers code lenses, and launches the CLI scene option', async () => {
  const tale = host().document('/workspace/My Story/story.tale',
    'Preamble\n\n=== beginning\nText\n=== next.scene-2\nMore text\n');
  const { calls, api } = host({ activeDocument: tale, activeLine: 0 });
  assert.equal(api.sceneAtLine(tale, 0), undefined);
  assert.equal(api.sceneAtLine(tale, 2).id, 'beginning');
  assert.equal(api.sceneAtLine(tale, 4).id, 'next.scene-2');
  const invalid = host().document('/workspace/My Story/invalid.tale',
    '=== valid\nText\n=== invalid scene\nMore text\n');
  assert.equal(api.sceneAtLine(invalid, 3), undefined);
  assert.equal(calls.contextKeys.at(-1)[1], false);

  const provider = calls.codeLensProviders[0].provider;
  const lenses = provider.provideCodeLenses(tale);
  assert.deepEqual(Array.from(lenses, lens => [lens.range.start.line, lens.command.title]),
    [[2, 'Play from here'], [4, 'Play from here']]);
  calls.selectionChanged({ textEditor: { document: tale, selection: { active: { line: 3 } } } });
  assert.equal(calls.contextKeys.at(-1)[1], true);
  await calls.commands.get('talewright.playFromScene')(tale.uri, 3);
  assert.deepEqual(Array.from(calls.tasks[0].execution.args),
    ['debug', '/workspace/My Story', '--scene', 'beginning']);
  assert.equal(calls.tasks[0].name, 'Play From This Scene');
  assert.equal(calls.errors.length, 0);

  const disabled = host({ codeLens: false });
  assert.deepEqual(Array.from(disabled.calls.codeLensProviders[0].provider.provideCodeLenses(tale)), []);
});

test('Play From This Scene rejects positions before the first scene', async () => {
  const document = host().document('/workspace/My Story/story.tale', 'Preamble\n=== beginning\nText\n');
  const { calls, run } = host({ activeDocument: document, activeLine: 0 });
  await run('playFromScene');
  assert.equal(calls.tasks.length, 0);
  assert.match(calls.errors[0], /inside a valid scene/);
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

test('New Story uses one empty workspace folder and opens the generated start scene', async () => {
  const { calls, run } = host({ folders: ['/workspace'], directoryEntries: [] });
  await run('newStory');
  assert.deepEqual(Array.from(calls.cli[0][1]), ['new', '/workspace']);
  assert.equal(calls.cli[0][2].cwd, '/workspace');
  assert.equal(calls.openDialogs.length, 0);
  assert.equal(calls.openFolders.length, 0);
  assert.equal(calls.openedDocuments[0].fsPath, '/workspace/story.tale');
  assert.equal(calls.shownDocuments.length, 1);
  assert.equal(calls.errors.length, 0);
});

test('New Story asks for a folder and offers current or new window when needed', async () => {
  for (const [pick, newWindow] of [[0, false], [1, true]]) {
    const { calls, run, state } = host({
      folders: ['/workspace'], directoryEntries: ['README.md'],
      selected: '/exports/New Story', pickSequence: [pick],
    });
    await run('newStory');
    assert.equal(calls.openDialogs.length, 1);
    assert.equal(calls.openDialogs[0].canSelectFolders, true);
    assert.equal(calls.openDialogs[0].canSelectFiles, false);
    assert.deepEqual(Array.from(calls.cli[0][1]), ['new', '/exports/New Story']);
    assert.equal(calls.openFolders[0][0].fsPath, '/exports/New Story');
    assert.equal(calls.openFolders[0][1], newWindow);
    assert.equal(state.size, 1);
    assert.equal(calls.errors.length, 0);
  }
});

test('New Story opens the chosen folder in an empty window and restores story.tale on activation', async () => {
  const created = host({ folders: [], selected: '/exports/New Story' });
  await created.run('newStory');
  assert.equal(created.calls.openFolders[0][0].fsPath, '/exports/New Story');
  assert.equal(created.calls.openFolders[0][1], false);
  const reopened = host({ folders: ['/exports/New Story'], state: created.state });
  await reopened.activation;
  assert.equal(reopened.calls.openedDocuments[0].fsPath, '/exports/New Story/story.tale');
  assert.equal(reopened.calls.shownDocuments.length, 1);
  assert.equal(reopened.state.size, 0);
});

test('New Story surfaces CLI output unchanged and offers CLI setup when unavailable', async () => {
  const failed = host({
    folders: ['/workspace'], directoryEntries: ['README.md'], selected: '/exports/Story',
    cliError: 'The target folder already contains a story.', cliStderr: 'The target folder already contains a story.',
  });
  await failed.run('newStory');
  assert.match(failed.calls.errors[0], /The target folder already contains a story\./);
  assert.equal(failed.calls.openFolders.length, 0);
  assert.equal(failed.calls.openedDocuments.length, 0);

  const missing = host({ folders: [], selected: '/exports/Story', files: [] });
  await missing.run('newStory');
  assert.match(missing.calls.errors[0], /CLI not found/);
  assert.deepEqual(missing.calls.errorActions, ['Set Up CLI']);
  assert.equal(missing.calls.cli.length, 0);
});

test('New Story does not launch when the picker is cancelled or workspace trust is absent', async () => {
  const cancelled = host({ folders: [] });
  await cancelled.run('newStory');
  assert.equal(cancelled.calls.cli.length, 0);
  assert.equal(cancelled.calls.errors.length, 0);

  const untrusted = host({ trusted: false });
  await untrusted.run('newStory');
  assert.match(untrusted.calls.errors[0], /Trust this workspace/);
  assert.equal(untrusted.calls.openDialogs.length, 0);
  assert.equal(untrusted.calls.cli.length, 0);
});

test('Run Story executes talewright run as a visible, space-safe player task', async () => {
  const { calls, run } = host();
  await run('runStory');
  const task = calls.tasks[0];
  assert.equal(task.name, 'Run Story');
  assert.equal(task.execution.executable, '/bin/talewright');
  assert.deepEqual(Array.from(task.execution.args), ['run', '/workspace/My Story']);
  assert.equal(task.execution.settings.cwd, '/workspace/My Story');
  assert.equal(task.presentationOptions.focus, true);
  assert.equal(calls.runtimeChecks[0][0], 'dotnet');
  assert.deepEqual(Array.from(calls.runtimeChecks[0][1]), ['--list-runtimes']);
  assert.equal(calls.errors.length, 0);
});

test('Run Story reports task launch failures without claiming success', async () => {
  const { calls, run } = host({ launchError: 'spawn failed' });
  await run('runStory');
  assert.match(calls.errors[0], /spawn failed/);
  assert.equal(calls.messages.length, 0);
});

test('Run Story uses normal story selection and does not launch on selection cancellation', async () => {
  const roots = ['/workspace/A', '/workspace/B'];
  const active = host({ roots, active: '/workspace/B/scene.tale' });
  await active.run('runStory');
  assert.equal(active.calls.tasks[0].execution.args[1], '/workspace/B');
  const chosen = host({ roots, active: '/outside/file.tale', pick: 1 });
  await chosen.run('runStory');
  assert.equal(chosen.calls.tasks[0].execution.args[1], '/workspace/B');
  const cancelled = host({ roots });
  await cancelled.run('runStory');
  assert.equal(cancelled.calls.tasks.length, 0);
});

test('Run Story requires .NET 10 and reports missing runtimes without installing anything', async () => {
  for (const options of [{ runtimes: '' }, { dotnetError: 'ENOENT' }]) {
    const { calls, run } = host(options);
    await run('runStory');
    assert.match(calls.errors[0], /\.NET 10 Runtime.*download\.dotnet\.com|\.NET 10 Runtime.*dotnet\.microsoft\.com/);
    assert.equal(calls.tasks.length, 0);
    assert.equal(calls.sdk.length, 0);
    assert.equal(calls.updates.length, 0);
  }
  const missingCli = host({ files: [] });
  await missingCli.run('runStory');
  assert.match(missingCli.calls.errors[0], /CLI not found/);
  assert.deepEqual(missingCli.calls.errorActions, ['Set Up CLI']);
  assert.equal(missingCli.calls.tasks.length, 0);
  assert.equal(missingCli.calls.sdk.length, 0);
  assert.equal(missingCli.calls.updates.length, 0);
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
  const runWithDirtyDocument = host({ documents: [document()], saveChoice: 'Save and Continue' });
  await runWithDirtyDocument.run('runStory');
  assert.equal(runWithDirtyDocument.calls.tasks.length, 1);
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

test('debug and run actions and reusable tasks share selection, executable, trust and save handling', async () => {
  const { calls, run } = host();
  await run('openAuthorDebugger');
  assert.deepEqual(Array.from(calls.tasks[0].execution.args), ['debug', '/workspace/My Story']);
  await run('runStory');
  assert.deepEqual(Array.from(calls.tasks[1].execution.args), ['run', '/workspace/My Story']);
  const definition = { type: 'talewright', command: 'debug', story: 'My Story', scene: 'beginning' };
  const task = await calls.provider.resolveTask({ definition, scope: { uri: { scheme: 'file', fsPath: '/workspace' } } });
  assert.equal(task.definition, definition);
  assert.deepEqual(Array.from(task.execution.args), ['debug', '/workspace/My Story', '--scene', 'beginning']);
  const runDefinition = { type: 'talewright', command: 'run', story: 'My Story' };
  const runTask = await calls.provider.resolveTask({
    definition: runDefinition, scope: { uri: { scheme: 'file', fsPath: '/workspace' } },
  });
  assert.equal(runTask.definition, runDefinition);
  assert.deepEqual(Array.from(runTask.execution.args), ['run', '/workspace/My Story']);
  assert.equal(await calls.provider.resolveTask({
    definition: { type: 'talewright', command: 'run', story: 'My Story', scene: 'beginning' },
    scope: { uri: { scheme: 'file', fsPath: '/workspace' } },
  }), undefined);
  assert.match(calls.errors.at(-1), /only be used with command "debug"/);
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

test('setup is invocation-only and preserves existing custom, PATH and global tool installations', async () => {
  for (const options of [{}, { executable: '/tools/My CLI', files: ['/tools/My CLI'] },
    { files: ['/home/author/.dotnet/tools/talewright'] }]) {
    const { calls, run } = host(options);
    assert.equal(calls.sdk.length, 0);
    assert.equal(calls.messages.length, 0);
    await run('setUpCli');
    assert.match(calls.messages[0], /existing.*No installation or upgrade/);
    assert.equal(calls.sdk.length, 0);
    assert.equal(calls.tasks.length, 0);
    await run('validateStory');
    assert.equal(calls.tasks.length, 1);
  }
});

test('setup respects trust and broken custom paths; missing CLI action offers explicit setup', async () => {
  for (const options of [{ trusted: false }, { executable: '/missing/custom', files: [] }]) {
    const { calls, run } = host(options);
    await run('setUpCli');
    assert.equal(calls.errors.length, 1);
    assert.equal(calls.sdk.length, 0);
    assert.equal(calls.tasks.length, 0);
    assert.equal(calls.updates.length, 0);
  }
  const missing = host({ files: [], errorChoice: 'Set Up CLI' });
  await missing.run('validateStory');
  assert.equal(missing.calls.sdk.length, 1);
  assert.equal(missing.calls.tasks.length, 0);
  const outside = host({ active: '/outside/file.txt' });
  await outside.run('setUpCli');
  assert.equal(outside.calls.configurationResource.fsPath, '/workspace');
});

test('only a stable .NET 10 SDK offers tool installation, and cancellation installs nothing', async () => {
  for (const options of [{ sdks: '9.0.100 [/sdk]\n11.0.100 [/sdk]' }, { sdkError: 'ENOENT' },
    { sdks: '10.0.100-rc.1 [/sdk]' }]) {
    const { calls, run } = host({ files: [], ...options });
    await run('setUpCli');
    assert.equal(calls.picks[0][0], 'Use standalone download');
    assert.match(calls.pickSettings.placeHolder, /No compatible/);
    assert.equal(calls.fetches.length, 0);
  }
  const cancelled = host({ files: [], sdks: '10.0.113 [/sdk]', pick: 0 });
  await cancelled.run('setUpCli');
  assert.equal(cancelled.calls.warnings.length, 1);
  assert.equal(cancelled.calls.tasks.length, 0);
});

test('approved installation pins the published version and discovers the new shim without PATH refresh', async () => {
  const { calls, run } = host({
    files: [], sdks: '10.0.113 [/sdk]', pick: 0, saveChoice: 'Install CLI', installSuccess: true,
  });
  await run('setUpCli');
  const task = calls.tasks[0];
  assert.equal(task.execution.executable, 'dotnet');
  assert.deepEqual(Array.from(task.execution.args), [
    'tool', 'install', '--global', 'Talewright.Cli', '--version', '0.1.0-preview.1',
    '--source', 'https://api.nuget.org/v3/index.json',
  ]);
  assert.equal(task.presentationOptions.focus, true);
  assert.equal(calls.updates.length, 0);
  assert.equal(calls.errors.length, 0);
  await run('validateStory');
  assert.equal(calls.tasks[1].execution.executable, '/home/author/.dotnet/tools/talewright');
  for (const version of ['0.1.0-preview.2', '0.1.0']) {
    const other = host({ files: [], version, versions: [version], sdks: '10.0.113 [/sdk]',
      pick: 0, saveChoice: 'Install CLI', installSuccess: true });
    await other.run('setUpCli');
    assert.equal(other.calls.tasks[0].execution.args[5], version);
  }
});

test('unavailable version, feed/network/SDK failures and revoked trust never install', async () => {
  for (const [options, expected] of [
    [{ versions: [] }, /not published/], [{ httpStatus: 404 }, /not published/],
    [{ httpStatus: 401 }, /HTTP 401/], [{ networkError: 'offline' }, /offline/],
    [{ sdkError: 'EACCES' }, /Could not inspect/],
    [{ version: '0.1.0-preview.2', versions: ['0.1.0-preview.1'] }, /0.1.0-preview.2 is not published/],
    [{ version: '../bad' }, /no supported CLI version/],
    [{ onConsent: vscode => { vscode.workspace.isTrusted = false; }, saveChoice: 'Install CLI' }, /Trust/],
  ]) {
    const { calls, run } = host({ files: [], sdks: '10.0.113 [/sdk]', pick: 0, ...options });
    await run('setUpCli');
    assert.match(calls.errors[0], expected);
    assert.equal(calls.tasks.length, 0);
    assert.equal(calls.updates.length, 0);
  }
});

test('failed installation reports failure; standalone requires a separate explicit choice', async () => {
  const options = { files: [], sdks: '10.0.113 [/sdk]', pick: 0, saveChoice: 'Install CLI', exitCode: 1 };
  const failed = host(options);
  await failed.run('setUpCli');
  assert.match(failed.calls.errors[0], /failed or was cancelled.*exit 1/);
  assert.equal(failed.calls.messages.length, 0);
  assert.equal(failed.calls.urls.length, 0);
  assert.ok(failed.calls.errorActions.includes('Use standalone instead'));
  const explicit = host({ ...options, errorChoice: 'Use standalone instead' });
  await explicit.run('setUpCli');
  assert.deepEqual(explicit.calls.urls, ['https://github.com/squid-farm/talewright/releases/tag/v0.1.0-preview.1']);
  const launch = host({ ...options, launchError: 'permission denied' });
  await launch.run('setUpCli');
  assert.match(launch.calls.errors[0], /permission denied/);
  const cancelled = host({ ...options, cancelledInstall: true });
  await cancelled.run('setUpCli');
  assert.match(cancelled.calls.errors[0], /failed or was cancelled.*unknown/);
  assert.equal(cancelled.calls.messages.length, 0);
  const noProcess = host({ ...options, noProcess: true });
  await noProcess.run('setUpCli');
  assert.match(noProcess.calls.errors[0], /failed or was cancelled.*unknown/);
  assert.equal(noProcess.calls.messages.length, 0);
  await noProcess.run('setUpCli');
  assert.equal(noProcess.calls.tasks.length, 2, 'A terminated task must not leave the installation lock held');
  const noShim = host({ ...options, exitCode: 0 });
  await noShim.run('setUpCli');
  assert.match(noShim.calls.errors[0], /CLI not found/);
  assert.equal(noShim.calls.messages.length, 0, 'Exit zero without a discoverable CLI is not setup success');
});

test('standalone opens only an existing release with archive/checksum; selection is space-safe', async () => {
  const unavailable = host({ files: [], pick: 0, assets: [] });
  await unavailable.run('setUpCli');
  assert.match(unavailable.calls.errors[0], /does not supply/);
  assert.equal(unavailable.calls.urls.length, 0);
  const missing = host({ files: [], pick: 0, httpStatus: 404 });
  await missing.run('setUpCli');
  assert.equal(missing.calls.urls.length, 0);
  const executable = '/downloads/My CLI/Talewright.Cli';
  const selected = host({ files: [executable], pick: 0, selected: executable, infoChoice: 'Select Executable' });
  await selected.run('setUpCli');
  assert.deepEqual(selected.calls.updates, [['cli.executablePath', executable]]);
  await selected.run('validateStory');
  assert.equal(selected.calls.tasks[0].execution.executable, executable);
  const cancelled = host({ files: [], pick: 1 });
  await cancelled.run('setUpCli');
  assert.equal(cancelled.calls.updates.length, 0);
  const invalid = host({ files: ['/downloads/CLI.dll'], pick: 1, selected: '/downloads/CLI.dll' });
  await invalid.run('setUpCli');
  assert.match(invalid.calls.errors[0], /not its DLL/);
  assert.equal(invalid.calls.updates.length, 0);
});
