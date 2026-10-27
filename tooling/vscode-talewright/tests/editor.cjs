const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const vscode = require('vscode');

exports.run = async () => {
  const extension = vscode.extensions.getExtension('squid-farm.talewright');
  assert.ok(extension, 'Extension must be registered in the clean editor');
  assert.ok(extension.extensionPath.includes(`${path.sep}extensions${path.sep}`),
    'Exercise the installed VSIX, not the development source');
  assert.equal(extension.packageJSON.main, './dist/extension.cjs');
  if (process.env.TALEWRIGHT_TEST_ACTIONS === 'true') {
    await testAuthorActions(extension);
    return;
  }
  assert.equal(vscode.workspace.getConfiguration('talewright').get('languageServer.command'), '',
    'Installed tests must exercise the bundled default, not a development override');
  if (process.env.TALEWRIGHT_TEST_UNTRUSTED === 'true') {
    assert.equal(vscode.workspace.isTrusted, false, 'Exercise real restricted mode');
    const file = path.join(vscode.workspace.workspaceFolders[0].uri.fsPath, 'start.tale');
    const document = await vscode.workspace.openTextDocument(file);
    await vscode.window.showTextDocument(document);
    await extension.activate();
    await new Promise(resolve => setTimeout(resolve, 1500));
    assert.equal((await bundledServerProcesses(extension)).length, 0, 'Untrusted workspaces must not launch the server');
    assert.equal(vscode.languages.getDiagnostics(document.uri).filter(item => item.source === 'talewright').length, 0);
    assert.equal(document.languageId, 'tale', 'Syntax support remains available in restricted mode');
    console.log('Installed VSIX restricted-mode smoke passed: no server process or compiler execution.');
    return;
  }
  assert.equal(vscode.workspace.isTrusted, true);
  const snippets = JSON.parse(await fs.readFile(path.join(extension.extensionPath, 'snippets/tale.json'), 'utf8'));
  const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'talewright-editor-'));
  try {
    await fs.writeFile(path.join(folder, 'story.talewright.yaml'), 'game:\n  id: smoke\n  title: Smoke\n  start: smoke\n');
    const file = path.join(folder, 'smoke.tale');
    await fs.writeFile(file, '=== smoke\nHello.\n-> END\n');
    const document = await vscode.workspace.openTextDocument(file);
    assert.equal(document.languageId, 'tale', 'File association must work without setTextDocumentLanguage');
    const editor = await vscode.window.showTextDocument(document);
    for (const snippet of Object.values(snippets)) {
      await editor.edit(edit => edit.delete(new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length))));
      editor.selection = new vscode.Selection(0, 0, 0, 0);
      assert.equal(await editor.insertSnippet(new vscode.SnippetString(snippet.body.join('\n'))), true);
      const expected = snippet.body.join('\n').replace(/\$\{\d+:([^{}]*)\}/g, '$1').replace(/\$0/g, '');
      assert.equal(document.getText().replace(/\r\n/g, '\n'), expected);
      const placeholders = [...snippet.body.join('\n').matchAll(/\$\{(\d+):([^{}]*)\}/g)];
      for (const [index, placeholder] of placeholders.entries()) {
        if (index > 0) await vscode.commands.executeCommand('jumpToNextSnippetPlaceholder');
        assert.equal(document.getText(editor.selection), placeholder[2]);
      }
      await vscode.commands.executeCommand('leaveSnippet');
    }
    await editor.edit(edit => edit.replace(new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)), 'Hello.'));
    editor.selection = new vscode.Selection(0, 0, 0, 0);
    await vscode.commands.executeCommand('editor.action.commentLine');
    assert.equal(document.getText(), '// Hello.');
    await vscode.commands.executeCommand('editor.action.commentLine');
    assert.equal(document.getText(), 'Hello.');
    editor.selection = new vscode.Selection(0, document.getText().length, 0, document.getText().length);
    // One command per character exercises the auto-closing path that a single multi-character type skips.
    for (const character of " It's {literal") await vscode.commands.executeCommand('type', { text: character });
    assert.equal(document.getText(), "Hello. It's {literal");
    console.log('Installed VSIX smoke passed: .tale detection, five real snippet expansions and placeholder navigation, prose typing, comment toggle.');
  } finally {
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    await fs.rm(folder, { recursive: true, force: true });
  }
  await testYamlSchema();
  await testLiveDiagnostics();
  assert.equal((await bundledServerProcesses(extension)).length, 1,
    'The installed extension must run exactly its own bundled server executable');
};

async function testAuthorActions(extension) {
  const root = vscode.workspace.workspaceFolders[0].uri.fsPath;
  const document = await vscode.workspace.openTextDocument(path.join(root, 'start.tale'));
  await vscode.window.showTextDocument(document);
  await extension.activate();
  const configuredCli = vscode.workspace.getConfiguration('talewright', document.uri).get('cli.executablePath');
  await vscode.commands.executeCommand('talewright.setUpCli');
  assert.equal(vscode.workspace.getConfiguration('talewright', document.uri).get('cli.executablePath'), configuredCli,
    'Setup must preserve an existing configured CLI');
  const run = async (command, stopDebugger = false) => {
    let execution;
    let pid;
    let cancelled = false;
    let children = [];
    const finished = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error(`${command} task timed out`)), 30000);
      const start = vscode.tasks.onDidStartTaskProcess(async event => {
        if (event.execution.task.source !== 'Talewright') return;
        execution = event.execution;
        pid = event.processId;
        if (stopDebugger) {
          await new Promise(resolve => setTimeout(resolve, 3000));
          try {
            children = (await fs.readFile(`/proc/${pid}/task/${pid}/children`, 'utf8')).trim().split(/\s+/).filter(Boolean);
            assert.ok(children.length > 0, 'CLI must have launched the native debugger before cancellation');
          } catch (error) {
            reject(error);
            return;
          }
          cancelled = true;
          execution.terminate();
        }
      });
      const end = vscode.tasks.onDidEndTaskProcess(event => {
        if (event.execution !== execution) return;
        clearTimeout(timeout);
        start.dispose();
        end.dispose();
        resolve(event.exitCode);
      });
    });
    await vscode.commands.executeCommand(`talewright.${command}`);
    const code = await finished;
    if (stopDebugger) {
      assert.equal(cancelled, true, 'Debugger must remain running until task cancellation');
      await new Promise(resolve => setTimeout(resolve, 500));
      for (const child of children) {
        await assert.rejects(fs.access(`/proc/${child}/exe`), { code: 'ENOENT' },
          'Task cancellation must not leave the native debugger running');
      }
    }
    return { code, pid };
  };
  assert.notEqual((await run('validateStory')).code, 0, 'Invalid story must retain CLI failure status');
  await fs.writeFile(path.join(root, 'start.tale'), '=== start\nHello.\n-> END\n');
  assert.equal((await run('validateStory')).code, 0, 'Valid story must return CLI success');
  const debug = await run('openAuthorDebugger', true);
  assert.notEqual(debug.code, 0, 'Task cancellation must not report debugger success');
  console.log('Installed CLI action smoke passed: real validation success/failure, visible debugger task and termination.');
}

async function bundledServerProcesses(extension) {
  const children = await fs.readFile(`/proc/${process.pid}/task/${process.pid}/children`, 'utf8');
  const matches = [];
  for (const pid of children.trim().split(/\s+/).filter(Boolean)) {
    let executable;
    try {
      executable = await fs.readlink(`/proc/${pid}/exe`);
    } catch (error) {
      if (error.code === 'ENOENT') continue;
      throw error;
    }
    if (executable === path.join(extension.extensionPath, 'server/Talewright.LanguageServer')) matches.push(pid);
  }
  return matches;
}

async function testYamlSchema() {
  const extension = vscode.extensions.getExtension('redhat.vscode-yaml');
  assert.ok(extension, 'Red Hat YAML must be installed in the clean editor profile');
  await extension.activate();

  const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'talewright-yaml-editor-'));
  try {
    const file = path.join(folder, 'story.talewright.yaml');
    const invalid = [
      'game:',
      '  id: schema-smoke',
      '  title: Schema smoke',
      '  start: beginning',
      '  randomness: always',
      '  unknown-game: true',
      'unknown-root: true',
      'characters:',
      '  player:',
      '    first-name: Ada',
      '    unknown-character: true',
      '',
    ].join('\n');
    await fs.writeFile(file, invalid);
    let document = await vscode.workspace.openTextDocument(file);
    await vscode.window.showTextDocument(document);
    let diagnostics = await waitForDiagnostics(document, items =>
      items.some(item => item.message.includes('unknown-root')) &&
      items.some(item => item.message.includes('Valid values: "off"')));
    assert.ok(diagnostics.some(item => item.message.includes('unknown-character')),
      'Schema should report unknown keys in nested character objects');

    const valid = [
      'game:',
      '  id: schema-smoke',
      '  title: Schema smoke',
      '  start: beginning',
      '  randomness: checks-only',
      'characters:',
      '  player:',
      '    first-name: Ada',
      '    ',
      '',
    ].join('\n');
    await replaceDocument(document, valid);
    diagnostics = await waitForDiagnostics(document, items => items.length === 0);

    const completionPosition = document.positionAt(document.getText().indexOf('    \n') + 4);
    const completions = await vscode.commands.executeCommand(
      'vscode.executeCompletionItemProvider',
      document.uri,
      completionPosition,
    );
    assert.ok(completions?.items?.some(item => item.label === 'pronouns'),
      'Schema key completion should offer character fields');

    const startPosition = document.positionAt(document.getText().indexOf('start:') + 2);
    const hovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri, startPosition);
    assert.ok(hovers?.some(hover => hover.contents.some(content =>
      (typeof content === 'string' ? content : content.value).includes('scene where play begins'))),
    'Schema hover should show the start field description');

    const fallbackSchema = path.join(folder, 'story.schema.json');
    const taleExtension = vscode.extensions.getExtension('squid-farm.talewright');
    assert.ok(taleExtension, 'Talewright VSIX must be installed in the clean editor profile');
    const installedSchema = path.join(taleExtension.extensionPath, 'schemas/story.schema.json');
    await fs.copyFile(installedSchema, fallbackSchema);
    const fallbackFile = path.join(folder, 'fallback.yaml');
    await fs.writeFile(fallbackFile, [
      '# yaml-language-server: $schema=./story.schema.json',
      'game:',
      '  id: schema-fallback',
      '  title: Schema fallback',
      '  start: beginning',
      '  randomness: always',
      'characters:',
      '  player:',
      '    first-name: Ada',
      '',
    ].join('\n'));
    const fallbackDocument = await vscode.workspace.openTextDocument(fallbackFile);
    await vscode.window.showTextDocument(fallbackDocument);
    diagnostics = await waitForDiagnostics(fallbackDocument, items =>
      items.some(item => item.range.start.line === 5 && item.message.includes('Valid values: "off"')));
    assert.ok(diagnostics.length > 0, 'Modeline schema fallback should validate another YAML filename');

    await fs.mkdir(path.join(folder, 'characters'));
    const fragmentFile = path.join(folder, 'characters', 'guide.talewright.yaml');
    await fs.writeFile(fragmentFile, 'characters:\n  guide:\n    first-name: Bea\n    unknown-character: true\n');
    const fragment = await vscode.workspace.openTextDocument(fragmentFile);
    await vscode.window.showTextDocument(fragment);
    diagnostics = await waitForDiagnostics(fragment, items => items.some(item => item.message.includes('unknown-character')));
    assert.ok(!diagnostics.some(item => /game|required/i.test(item.message)),
      'Supporting files must not receive whole-story schema requirements');
    await replaceDocument(fragment, 'characters:\n  guide:\n    first-name: Bea\n');
    await waitForDiagnostics(fragment, items => items.length === 0);
    console.log('Red Hat YAML smoke passed: schema diagnostics/clearing, key completion, hover descriptions, and modeline fallback.');
  } finally {
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    await fs.rm(folder, { recursive: true, force: true });
  }
}

async function replaceDocument(document, text) {
  const edit = new vscode.WorkspaceEdit();
  edit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)), text);
  assert.equal(await vscode.workspace.applyEdit(edit), true, 'YAML document edit should apply');
  await document.save();
}

async function waitForDiagnostics(document, predicate, timeoutMs = 15000) {
  const deadline = Date.now() + timeoutMs;
  do {
    const diagnostics = vscode.languages.getDiagnostics(document.uri).filter(item => item.source !== 'talewright');
    if (predicate(diagnostics)) return diagnostics;
    await new Promise(resolve => setTimeout(resolve, 100));
  } while (Date.now() < deadline);
  assert.fail(`Timed out waiting for YAML diagnostics: ${
    vscode.languages.getDiagnostics(document.uri).map(item => item.message).join('; ')
  }`);
}

async function testLiveDiagnostics() {
  const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'talewright-lsp-editor-'));
  try {
    await fs.writeFile(path.join(folder, 'story.talewright.yaml'),
      'game:\n  id: live\n  title: Live\n  start: start\ncharacters:\n  player: {first-name: Ada}\n');
    const file = path.join(folder, 'start.tale');
    await fs.writeFile(file, '=== start\n* [Go] -> missing\n');
    const document = await vscode.workspace.openTextDocument(file);
    await vscode.window.showTextDocument(document);
    await waitForCompiler(document, items => items.length > 0);
    const edit = new vscode.WorkspaceEdit();
    edit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)),
      '=== start\nHello.\n-> END\n');
    assert.equal(await vscode.workspace.applyEdit(edit), true);
    assert.equal(document.isDirty, true, 'Exercise an unsaved overlay');
    await waitForCompiler(document, items => items.length === 0);
    assert.ok((await fs.readFile(file, 'utf8')).includes('missing'), 'Disk must remain invalid');
    const completionEdit = new vscode.WorkspaceEdit();
    completionEdit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)),
      '=== start\n-> sta');
    assert.equal(await vscode.workspace.applyEdit(completionEdit), true);
    let completions = await vscode.commands.executeCommand(
      'vscode.executeCompletionItemProvider', document.uri, new vscode.Position(1, 6));
    const scene = completions.items.find(item => item.label === 'start');
    assert.ok(scene, 'Shared language server must complete an unsaved Talemark divert');
    assert.equal(scene.insertText, 'start');
    assert.equal(scene.range.start.character, 3);
    assert.equal(scene.range.end.character, 6);
    const hoverEdit = new vscode.WorkspaceEdit();
    hoverEdit.replace(document.uri, new vscode.Range(1, 3, 1, 6), 'start');
    assert.equal(await vscode.workspace.applyEdit(hoverEdit), true);
    const hovers = await vscode.commands.executeCommand(
      'vscode.executeHoverProvider', document.uri, new vscode.Position(1, 5));
    const semanticHover = hovers.find(hover => hover.contents.some(content =>
      content.value?.includes('**Scene**') && content.value.includes('Declared at')));
    assert.ok(semanticHover, 'Installed VSIX must display shared Markdown hover for an unsaved scene reference');
    assert.equal(semanticHover.range.start.character, 3);
    assert.equal(semanticHover.range.end.character, 8);
    assert.equal(document.isDirty, true, 'Hover must not save the buffer');
    const manifest = await vscode.workspace.openTextDocument(path.join(folder, 'story.talewright.yaml'));
    await vscode.window.showTextDocument(manifest);
    await waitForCompiler(manifest, items => items.some(item => item.code === 'TW4003') || items.length === 0);
    const manifestEdit = new vscode.WorkspaceEdit();
    manifestEdit.replace(manifest.uri, new vscode.Range(3, 9, 3, 14), '"sta"');
    assert.equal(await vscode.workspace.applyEdit(manifestEdit), true);
    completions = await vscode.commands.executeCommand(
      'vscode.executeCompletionItemProvider', manifest.uri, new vscode.Position(3, 13));
    assert.ok(completions.items.some(item => item.label === 'start' && item.detail?.startsWith('Scene:')),
      'Canonical manifest values must use shared story completion alongside Red Hat YAML');
    assert.equal(manifest.isDirty, true, 'Manifest completion must use the unsaved overlay');
    const manifestHovers = await vscode.commands.executeCommand(
      'vscode.executeHoverProvider', manifest.uri, new vscode.Position(3, 4));
    assert.ok(manifestHovers.some(hover => hover.contents.some(content =>
      content.value?.includes('scene where play begins'))), 'Canonical manifest schema help remains available');
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');

    await vscode.window.showTextDocument(document);
    const speakerEdit = new vscode.WorkspaceEdit();
    speakerEdit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)),
      '=== start\n> guide: Hi.\n-> END\n');
    assert.equal(await vscode.workspace.applyEdit(speakerEdit), true);
    await waitForCompiler(document, items => items.length > 0);
    await fs.mkdir(path.join(folder, 'characters'));
    const fragmentFile = path.join(folder, 'characters', 'guide.talewright.yaml');
    await fs.writeFile(fragmentFile, '{}\n');
    const fragment = await vscode.workspace.openTextDocument(fragmentFile);
    await vscode.window.showTextDocument(fragment);
    const declaration = new vscode.WorkspaceEdit();
    declaration.replace(fragment.uri, new vscode.Range(fragment.positionAt(0), fragment.positionAt(fragment.getText().length)),
      'characters:\n  guide: {first-name: Bea}\n');
    assert.equal(await vscode.workspace.applyEdit(declaration), true);
    assert.equal(fragment.isDirty, true, 'Exercise an unsaved supporting configuration overlay');
    await waitForCompiler(document, items => items.length === 0);
    const definitions = await vscode.commands.executeCommand(
      'vscode.executeDefinitionProvider', document.uri, new vscode.Position(1, 3));
    const target = definitions?.[0];
    assert.ok(target, 'Go to definition must resolve a supporting-file declaration');
    assert.equal((target.uri ?? target.targetUri).fsPath, fragmentFile);
    assert.equal((target.range ?? target.targetRange).start.line, 1);
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    await waitForCompiler(document, items => items.length > 0);
    console.log('Installed VSIX LSP smoke passed: compiler diagnostics, unsaved clearing, semantic completion, source-aware Markdown hover and supporting-file navigation.');
  } finally {
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    await fs.rm(folder, { recursive: true, force: true });
  }
}

async function waitForCompiler(document, predicate) {
  const deadline = Date.now() + 20000;
  do {
    const diagnostics = vscode.languages.getDiagnostics(document.uri).filter(item => item.source === 'talewright');
    if (predicate(diagnostics)) return diagnostics;
    await new Promise(resolve => setTimeout(resolve, 100));
  } while (Date.now() < deadline);
  assert.fail(`Timed out waiting for compiler diagnostics: ${JSON.stringify(vscode.languages.getDiagnostics(document.uri))}`);
}
