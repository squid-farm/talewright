import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import vm from 'node:vm';

const source = await readFile(new URL('../refactor-saves.cjs', import.meta.url), 'utf8');
const token = { isCancellationRequested: false };
const range = (start, end) => ({
  start, end,
  isEqual(other) { return start === other.start && end === other.end; },
});
const replacement = (start = 0, end = 3, newText = 'new') => ({ range: range(start, end), newText });

function host() {
  const calls = { errors: [], information: [], logs: [], saves: [], commands: [], opened: [] };
  const documents = [];
  const commands = new Map();
  const uri = key => ({ toString: () => key });
  const vscode = {
    Uri: { parse: uri },
    workspace: {
      textDocuments: documents,
      openTextDocument: async resource => {
        const key = resource.toString();
        calls.opened.push(key);
        const result = documents.find(document => document.uri.toString() === key);
        if (!result) throw new Error('File unavailable');
        return result;
      },
      onDidChangeTextDocument: listener => { calls.change = listener; return { dispose() {} }; },
      onDidCloseTextDocument: listener => { calls.close = listener; return { dispose() {} }; },
    },
    commands: {
      registerCommand: (id, handler) => { commands.set(id, handler); return { dispose() {} }; },
      executeCommand: async (id, ...args) => {
        calls.commands.push(id);
        calls.commandArguments = args;
        return commands.get(id)?.(...args);
      },
    },
    window: {
      showErrorMessage: message => { calls.errors.push(message); return new Promise(() => {}); },
      showInformationMessage: message => { calls.information.push(message); },
    },
  };
  const module = { exports: {} };
  vm.runInNewContext(source, { require: () => vscode, module });
  const context = { subscriptions: [] };
  const middleware = module.exports.registerRefactorSaves(context, { appendLine: message => calls.logs.push(message) });
  const document = (key, options = {}) => {
    const result = {
      uri: uri(key), isDirty: true, version: 1,
      async save() {
        calls.saves.push(key);
        if (options.throws) throw new Error('Permission denied');
        if (!options.stillDirty) this.isDirty = false;
        return !options.failed;
      },
    };
    documents.push(result);
    return result;
  };
  const edit = entries => ({ entries: () => entries.map(([key, edits]) => [uri(key), edits]) });
  const actions = async entries => middleware.provideCodeActions(null, null, null, token,
    async () => [{ title: 'Fix', edit: edit(entries) }]);
  const rename = async entries => middleware.provideRenameEdits(null, null, 'new', token, async () => edit(entries));
  const change = (doc, edits = [replacement()], reason) => {
    doc.version++;
    calls.change({
      document: doc, reason,
      contentChanges: edits.map(edit => ({ range: edit.range, text: edit.newText })),
    });
  };
  const flush = () => new Promise(resolve => setImmediate(resolve));
  return { calls, document, edit, actions, rename, change, flush, middleware, commands, context };
}

test('quick fixes save only affected documents after application, then preserve the original command', async () => {
  const instance = host();
  const target = instance.document('file:///manifest.yaml');
  instance.document('file:///unrelated.tale');
  const original = { title: 'Create', command: 'vscode.open', arguments: ['file:///manifest.yaml'] };
  const actions = await instance.middleware.provideCodeActions(null, null, null, token, async () => [
    { title: 'Create', edit: instance.edit([['file:///manifest.yaml', [replacement()]]]), command: original },
    { title: 'Disabled', disabled: { reason: 'Unsafe' } },
    { title: 'Other command', command: original },
  ]);
  assert.deepEqual(instance.calls.saves, [], 'Listing or previewing fixes never saves');
  assert.equal(actions[1].command, undefined);
  assert.equal(actions[2].command, original);
  await instance.commands.get(actions[0].command.command)(...actions[0].command.arguments);
  assert.deepEqual(instance.calls.saves, ['file:///manifest.yaml']);
  assert.equal(target.isDirty, false);
  assert.deepEqual(instance.calls.commands, ['vscode.open']);
  assert.equal(instance.calls.commandArguments[0].toString(), 'file:///manifest.yaml');
});

test('all text quick fixes, including new scene contents, receive the same save command', async () => {
  const instance = host();
  for (const key of ['item', 'character', 'faction', 'flag', 'number', 'text', 'stat', 'rpg', 'typo', 'scene', 'new-scene']) {
    instance.document(`file:///${key}`);
    const [action] = await instance.actions([[`file:///${key}`, [replacement()]]]);
    await instance.commands.get(action.command.command)(...action.command.arguments);
  }
  assert.equal(instance.calls.saves.length, 11);
  assert.deepEqual(instance.calls.errors, []);
});

test('save errors identify applied-but-unsaved files and do not block saving other documents or subsequent actions', async () => {
  for (const options of [{ failed: true }, { stillDirty: true }, { throws: true }]) {
    const instance = host();
    instance.document('file:///failed.yaml', options);
    instance.document('file:///good.tale');
    const [action] = await instance.actions([
      ['file:///failed.yaml', [replacement()]], ['file:///good.tale', [replacement()]],
    ]);
    await instance.commands.get(action.command.command)(...action.command.arguments);
    assert.deepEqual(instance.calls.saves, ['file:///failed.yaml', 'file:///good.tale']);
    assert.match(instance.calls.errors[0], /edits were applied but could not be saved/);
    assert.match(instance.calls.errors[0], /failed.yaml/);
    assert.equal(instance.calls.logs.length, 1);
    instance.document('file:///later.tale');
    const [later] = await instance.actions([['file:///later.tale', [replacement()]]]);
    await instance.commands.get(later.command.command)(...later.command.arguments);
    assert.equal(instance.calls.saves.at(-1), 'file:///later.tale');
  }
});

test('clean documents are not saved and unavailable files are reported', async () => {
  const instance = host();
  instance.document('file:///clean.yaml').isDirty = false;
  const [action] = await instance.actions([['file:///clean.yaml', []], ['file:///missing.tale', []]]);
  await instance.commands.get(action.command.command)(...action.command.arguments);
  assert.deepEqual(instance.calls.saves, []);
  assert.match(instance.calls.errors[0], /missing.tale: File unavailable/);
});

test('rename saves applied changes across files, including previously closed files, with one nonblocking warning', async () => {
  const instance = host();
  const first = instance.document('file:///first.tale');
  const third = instance.document('file:///unrelated.tale');
  await instance.rename([
    ['file:///first.tale', [replacement()]], ['file:///closed.yaml', [replacement()]],
  ]);
  assert.deepEqual(instance.calls.saves, []);
  const closed = instance.document('file:///closed.yaml');
  instance.change(first);
  instance.change(closed);
  await instance.flush();
  assert.deepEqual(instance.calls.saves, ['file:///first.tale', 'file:///closed.yaml']);
  assert.equal(third.isDirty, true);
  assert.equal(instance.calls.information.length, 1);
  assert.match(instance.calls.information[0], /Saves are not migrated/);
});

test('rename preview subsets save only actually applied edits and documents', async () => {
  const instance = host();
  const first = instance.document('file:///first.tale');
  const second = instance.document('file:///second.tale');
  await instance.rename([
    ['file:///first.tale', [replacement(), replacement(10, 13)]],
    ['file:///second.tale', [replacement()]],
  ]);
  instance.change(first, [replacement(10, 13)]);
  await instance.flush();
  assert.deepEqual(instance.calls.saves, ['file:///first.tale']);
  assert.equal(second.isDirty, true);
});

test('rename waits for dirty-state changes delivered after the text event', async () => {
  const instance = host();
  const doc = instance.document('file:///story.tale');
  doc.isDirty = false;
  await instance.rename([['file:///story.tale', [replacement()]]]);
  instance.change(doc);
  await instance.flush();
  assert.deepEqual(instance.calls.saves, [], 'The text event can still expose a clean document');
  doc.isDirty = true;
  instance.calls.change({ document: doc, contentChanges: [] });
  await instance.flush();
  assert.deepEqual(instance.calls.saves, ['file:///story.tale']);
  assert.equal(doc.isDirty, false);
  instance.calls.change({ document: doc, contentChanges: [] });
  await instance.flush();
  assert.equal(instance.calls.saves.length, 1);
});

test('cancelled, superseded, unrelated, stale, undo, redo and close events never trigger rename saving', async () => {
  for (const scenario of ['cancel', 'supersede', 'unrelated', 'stale', 'undo', 'redo', 'close', 'mixed']) {
    const instance = host();
    const doc = instance.document('file:///story.tale');
    await instance.rename([['file:///story.tale', [replacement()]]]);
    if (scenario === 'cancel') {
      await instance.middleware.provideRenameEdits(null, null, 'new', { isCancellationRequested: true },
        async () => instance.edit([['file:///story.tale', [replacement()]]]));
    } else if (scenario === 'supersede') {
      await instance.rename([]);
    } else if (scenario === 'close') {
      instance.calls.close(doc);
    } else if (scenario === 'stale') {
      doc.version++;
    }
    if (scenario === 'unrelated') instance.change(doc, [replacement(1, 4, 'typing')]);
    else if (scenario === 'mixed') instance.change(doc, [replacement(), replacement(10, 13, 'typing')]);
    else instance.change(doc, [replacement()], scenario === 'undo' ? 1 : scenario === 'redo' ? 2 : undefined);
    await instance.flush();
    assert.deepEqual(instance.calls.saves, [], scenario);
    assert.deepEqual(instance.calls.information, [], scenario);
  }
});

test('out-of-order rename responses cannot replace the latest pending edit', async () => {
  const instance = host();
  const doc = instance.document('file:///story.tale');
  let finish;
  const earlier = instance.middleware.provideRenameEdits(null, null, 'earlier', token,
    () => new Promise(resolve => { finish = resolve; }));
  await instance.rename([['file:///story.tale', [replacement()]]]);
  finish(instance.edit([['file:///story.tale', [replacement(0, 3, 'earlier')]]]));
  await earlier;
  instance.change(doc);
  await instance.flush();
  assert.deepEqual(instance.calls.saves, ['file:///story.tale']);
});

test('rename save failures use the same explicit error handling as quick fixes', async () => {
  const instance = host();
  const doc = instance.document('file:///story.tale', { throws: true });
  await instance.rename([['file:///story.tale', [replacement()]]]);
  instance.change(doc);
  await instance.flush();
  assert.match(instance.calls.errors[0], /story.tale: Permission denied/);
});
