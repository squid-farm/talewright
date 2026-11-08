const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const vscode = require('vscode');
const WebSocket = require('ws');

exports.run = async () => {
  const extension = vscode.extensions.getExtension('squid-farm.talewright');
  assert.ok(extension, 'Extension must be registered in the clean editor');
  assert.ok(extension.extensionPath.includes(`${path.sep}extensions${path.sep}`),
    'Exercise the installed VSIX, not the development source');
  assert.equal(extension.packageJSON.main, './dist/extension.cjs');
  if (process.env.TALEWRIGHT_TEST_ACTIONS === 'true') {
    try {
      await testAuthorActions(extension);
    } catch (error) {
      console.error(error);
      throw error;
    }
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
    assert.ok(extension.packageJSON.contributes.grammars.some(grammar =>
      grammar.language === 'tale' && grammar.scopeName === 'text.tale'),
    'Untrusted workspaces retain the TextMate fallback grammar');
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
  await testRename();
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
  const run = async (command, stopNativePlayer = false, ...args) => {
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
        if (stopNativePlayer) {
          await new Promise(resolve => setTimeout(resolve, 3000));
          try {
            children = (await fs.readFile(`/proc/${pid}/task/${pid}/children`, 'utf8')).trim().split(/\s+/).filter(Boolean);
            assert.ok(children.length > 0, 'CLI must have launched the native player before cancellation');
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
    await vscode.commands.executeCommand(`talewright.${command}`, ...args);
    const code = await finished;
    if (stopNativePlayer) {
      assert.equal(cancelled, true, 'Native player must remain running until task cancellation');
      await new Promise(resolve => setTimeout(resolve, 500));
      for (const child of children) {
        await assert.rejects(fs.access(`/proc/${child}/exe`), { code: 'ENOENT' },
          'Task cancellation must not leave the native player running');
      }
    }
    return { code, pid, args: execution?.task.execution.args };
  };
  assert.notEqual((await run('validateStory')).code, 0, 'Invalid story must retain CLI failure status');
  assert.notEqual((await run('runStory')).code, 0, 'Invalid story must not report a successful run');
  await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  await fs.writeFile(path.join(root, 'start.tale'), '=== start\nHello.\n-> END\n');
  const sceneDocument = await vscode.workspace.openTextDocument(path.join(root, 'start.tale'));
  await vscode.window.showTextDocument(sceneDocument);
  assert.equal((await run('validateStory')).code, 0, 'Valid story must return CLI success');
  const lenses = await vscode.commands.executeCommand('vscode.executeCodeLensProvider', sceneDocument.uri);
  const playLens = lenses.find(lens => lens.command.command === 'talewright.playFromScene');
  assert.ok(playLens, 'Every valid scene must offer a Play from here code lens');
  const player = await run('runStory', true);
  assert.notEqual(player.code, 0, 'Task cancellation must not report player success');
  const debug = await run('openAuthorDebugger', true);
  assert.notEqual(debug.code, 0, 'Task cancellation must not report debugger success');
  const sceneDebug = await run('playFromScene', true, ...playLens.command.arguments);
  assert.notEqual(sceneDebug.code, 0, 'Scene debugger cancellation must not report success');
  assert.deepEqual(Array.from(sceneDebug.args), ['debug', root, '--scene', 'start'],
    'Play from here must pass the selected scene to the CLI');
  await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  await fs.rm(path.join(root, 'story.talewright.yaml'));
  await fs.rm(path.join(root, 'start.tale'));
  await vscode.commands.executeCommand('talewright.newStory');
  const scene = path.join(root, 'story.tale');
  assert.equal(vscode.window.activeTextEditor.document.uri.fsPath, scene,
    'Creating a story in the sole empty workspace folder must open its start scene');
  assert.match(await fs.readFile(path.join(root, 'story.talewright.yaml'), 'utf8'), /start:\s*start/);
  assert.equal((await run('validateStory')).code, 0, 'The generated story must validate through the extension CLI action');
  console.log('Installed CLI action smoke passed: validation success/failure, player/debugger/scene tasks, new story creation and start-scene opening.');
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

    const withGameLine = valid.replace('  randomness: checks-only\n', '  randomness: checks-only\n  \n');
    await replaceDocument(document, withGameLine);
    const gameCompletions = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider',
      document.uri, document.positionAt(withGameLine.indexOf('  \n') + 2));
    assert.ok(gameCompletions?.items?.some(item => item.label === 'character-sheet'),
      'Schema key completion should offer the character-sheet game setting');

    const withSheetLine = valid.replace('  randomness: checks-only\n',
      '  randomness: checks-only\n  character-sheet:\n    stats: false\n    \n');
    await replaceDocument(document, withSheetLine);
    const sheetCompletions = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider',
      document.uri, document.positionAt(withSheetLine.indexOf('    \n') + 4));
    assert.ok(sheetCompletions?.items?.some(item => item.label === 'inventory'),
      'Schema key completion should offer character-sheet sections');
    const sheetHovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', document.uri,
      document.positionAt(withSheetLine.indexOf('character-sheet:') + 2));
    assert.ok(sheetHovers?.some(hover => hover.contents.some(content =>
      (typeof content === 'string' ? content : content.value).includes('Which character sheet sections'))),
    'Schema hover should describe the character-sheet setting');

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
    const missingScene = vscode.languages.getDiagnostics(document.uri)
      .find(item => item.source === 'talewright' && item.code === 'TW2002');
    assert.ok(missingScene, 'Missing-scene reference must have a compiler diagnostic');
    const actions = await vscode.commands.executeCommand(
      'vscode.executeCodeActionProvider', document.uri, missingScene.range, vscode.CodeActionKind.QuickFix.value);
    assert.ok(actions.some(action => action.title === "Create scene 'missing' in this file"));
    const appendAction = actions.find(action => action.title === "Create scene 'missing' in this file");
    assert.equal(await vscode.workspace.applyEdit(appendAction.edit), true);
    assert.equal(document.getText(), '=== start\n* [Go] -> missing\n\n=== missing\n');
    await waitForCompiler(document, items => !items.some(item => item.code === 'TW2002'));

    const restoreMissing = new vscode.WorkspaceEdit();
    restoreMissing.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)),
      '=== start\n* [Go] -> missing\n');
    assert.equal(await vscode.workspace.applyEdit(restoreMissing), true);
    await waitForCompiler(document, items => items.some(item => item.code === 'TW2002'));
    const currentDiagnostic = vscode.languages.getDiagnostics(document.uri)
      .find(item => item.source === 'talewright' && item.code === 'TW2002');
    const currentActions = await vscode.commands.executeCommand(
      'vscode.executeCodeActionProvider', document.uri, currentDiagnostic.range, vscode.CodeActionKind.QuickFix.value);
    const createAction = currentActions.find(action => action.title === "Create scene 'missing' in a new file");
    assert.ok(createAction, 'Missing-scene action must offer a new adjacent file');
    assert.equal(await vscode.workspace.applyEdit(createAction.edit), true);
    await vscode.commands.executeCommand(createAction.command.command, ...createAction.command.arguments);
    assert.equal(vscode.window.activeTextEditor.document.uri.fsPath, path.join(folder, 'missing.tale'));
    assert.equal(vscode.window.activeTextEditor.document.getText(), '=== missing\n');
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    await vscode.window.showTextDocument(document);

    const edit = new vscode.WorkspaceEdit();
    edit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)),
      '=== start\n{if true}\n\u{1F600} {player:first-name#upper} Ready.\nword*inner*word\n**bold**text\n__snake__case__\n*a **b* c**\n{else}\nStill here.\n{/if}\n-> END\n');
    assert.equal(await vscode.workspace.applyEdit(edit), true);
    assert.equal(document.isDirty, true, 'Exercise an unsaved overlay');
    await waitForCompiler(document, items => items.length === 0);
    await assertSemanticTokens(document);
    await assertRenderedColors(document);
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

    const textlessEdit = new vscode.WorkspaceEdit();
    textlessEdit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)),
      '=== start\n* -> sta');
    assert.equal(await vscode.workspace.applyEdit(textlessEdit), true);
    completions = await vscode.commands.executeCommand(
      'vscode.executeCompletionItemProvider', document.uri, new vscode.Position(1, 8));
    const textlessTarget = completions.items.find(item => item.label === 'start');
    assert.ok(textlessTarget, 'Textless choices must offer scene completion');
    assert.equal(textlessTarget.range.start.character, 5);
    assert.equal(textlessTarget.range.end.character, 8);
    const finishTextless = new vscode.WorkspaceEdit();
    finishTextless.replace(document.uri, new vscode.Range(1, 5, 1, 8), 'start');
    assert.equal(await vscode.workspace.applyEdit(finishTextless), true);
    const choiceHovers = await vscode.commands.executeCommand(
      'vscode.executeHoverProvider', document.uri, new vscode.Position(1, 0));
    assert.ok(choiceHovers.some(hover => hover.contents.some(content => content.value?.includes('Continue'))),
      'Textless choice hover must explain its default label');

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
    const previewEdit = new vscode.WorkspaceEdit();
    previewEdit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)),
      '=== start\n{player:name#upper} {player:pronoun:object}\n-> END\n');
    assert.equal(await vscode.workspace.applyEdit(previewEdit), true);
    await waitForCompiler(document, items => items.length === 0);
    const hints = await vscode.commands.executeCommand(
      'vscode.executeInlayHintProvider', document.uri, new vscode.Range(0, 0, 3, 0));
    assert.deepEqual(hints.map(hint => hint.label), ['"ADA"', '"them"']);
    assert.equal(hints[0].position.character, 19);
    assert.ok(hints[0].tooltip.value.includes('default'));
    assert.equal(document.isDirty, true, 'Preview hints must not modify or save source');
    const previewHover = await vscode.commands.executeCommand(
      'vscode.executeHoverProvider', document.uri, new vscode.Position(1, 14));
    assert.ok(previewHover.some(hover => hover.contents.some(content =>
      content.value?.includes('**Result:**') && content.value.includes('ADA'))));
    await vscode.commands.executeCommand('talewright.togglePreviewHints');
    const editorConfig = vscode.workspace.getConfiguration('editor', { uri: document.uri, languageId: 'tale' });
    assert.equal(editorConfig.get('inlayHints.enabled'), 'off');
    await vscode.commands.executeCommand('talewright.togglePreviewHints');
    assert.equal(vscode.workspace.getConfiguration('editor', { uri: document.uri, languageId: 'tale' })
      .get('inlayHints.enabled'), 'on');

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

    const manifestBuffer = await vscode.workspace.openTextDocument(path.join(folder, 'story.talewright.yaml'));
    await vscode.window.showTextDocument(manifestBuffer);
    const flagManifest = 'game:\n  id: live\n  title: Live\n  start: start\nflags: [ready]\ncharacters:\n  player: {first-name: Ada}\n';
    const flagManifestEdit = new vscode.WorkspaceEdit();
    flagManifestEdit.replace(manifestBuffer.uri,
      new vscode.Range(manifestBuffer.positionAt(0), manifestBuffer.positionAt(manifestBuffer.getText().length)),
      flagManifest);
    assert.equal(await vscode.workspace.applyEdit(flagManifestEdit), true);
    assert.equal(manifestBuffer.isDirty, true, 'Exercise an unsaved root-manifest overlay');

    const undeclaredFlag = new vscode.WorkspaceEdit();
    undeclaredFlag.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)),
      '=== start\n{if flag:missingFlag}Never.{/if}\n-> END\n');
    assert.equal(await vscode.workspace.applyEdit(undeclaredFlag), true);
    await waitForCompiler(document, items => items.some(item => item.code === 'TW2004'));
    const flagDiagnostic = vscode.languages.getDiagnostics(document.uri)
      .find(item => item.source === 'talewright' && item.code === 'TW2004');
    assert.ok(flagDiagnostic, 'Undeclared flag must have a compiler diagnostic');
    const flagActions = await vscode.commands.executeCommand(
      'vscode.executeCodeActionProvider', document.uri, flagDiagnostic.range, vscode.CodeActionKind.QuickFix.value);
    const declareFlag = flagActions.find(action => action.title === "Declare flag 'missingFlag'");
    assert.ok(declareFlag, 'Missing flag must offer a declaration quick fix');
    assert.equal(await vscode.workspace.applyEdit(declareFlag.edit), true);
    assert.equal(manifestBuffer.getText(), flagManifest.replace('[ready]', '[ready, missingFlag]'));
    assert.equal(manifestBuffer.isDirty, true, 'The flag quick fix must preserve and edit the unsaved manifest buffer');
    await waitForCompiler(document, items => !items.some(item => item.code === 'TW2004'));
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    await vscode.window.showTextDocument(document);
    console.log('Installed VSIX LSP smoke passed: compiler diagnostics, unsaved clearing, semantic completion, source-aware Markdown hover and supporting-file navigation.');
  } finally {
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    await fs.rm(folder, { recursive: true, force: true });
  }
}

async function testRename() {
  const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'talewright-lsp-rename-'));
  try {
    const manifestFile = path.join(folder, 'story.talewright.yaml');
    await fs.writeFile(manifestFile, 'game:\n  id: rename\n  title: Rename\n  start: start\ncharacters:\n  player: {first-name: Ada}\n');
    const hallFile = path.join(folder, 'hall.tale');
    await fs.writeFile(hallFile, '=== hall\n-> start\n');
    await fs.writeFile(path.join(folder, 'start.tale'), '=== start\n-> hall\n');
    const start = await vscode.workspace.openTextDocument(path.join(folder, 'start.tale'));
    await vscode.window.showTextDocument(start);
    await waitForCompiler(start, items => items.length === 0);

    const edit = await vscode.commands.executeCommand(
      'vscode.executeDocumentRenameProvider', start.uri, new vscode.Position(0, 5), 'entry');
    assert.ok(edit, 'Rename must return a workspace edit for a scene');
    const entries = edit.entries();
    assert.deepEqual(entries.map(([uri]) => path.basename(uri.fsPath)).sort(),
      ['hall.tale', 'start.tale', 'story.talewright.yaml']);
    assert.ok(entries.every(([, edits]) => edits.every(item => item.newText === 'entry')));

    const plain = new vscode.WorkspaceEdit();
    for (const [uri, edits] of entries) for (const item of edits) plain.replace(uri, item.range, item.newText);
    assert.equal(await vscode.workspace.applyEdit(plain), true);
    assert.equal(start.getText(), '=== entry\n-> hall\n');
    assert.equal((await vscode.workspace.openTextDocument(hallFile)).getText(), '=== hall\n-> entry\n');
    assert.match((await vscode.workspace.openTextDocument(manifestFile)).getText(), /\n  start: entry\n/);
    await waitForCompiler(start, items => items.length === 0);
    console.log('Installed VSIX rename smoke passed: cross-file scene rename including the manifest start reference.');
  } finally {
    await vscode.commands.executeCommand('workbench.action.files.saveAll');
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
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

async function assertSemanticTokens(document) {
  const legend = await vscode.commands.executeCommand('vscode.provideDocumentSemanticTokensLegend', document.uri);
  assert.ok(legend?.tokenTypes?.includes('operator'), 'The language server must register a semantic-token legend');
  const result = await vscode.commands.executeCommand('vscode.provideDocumentSemanticTokens', document.uri);
  assert.ok(result?.data, 'The installed extension must provide compiler-backed semantic tokens');
  const data = Array.from(result.data);
  assert.equal(data.length % 5, 0, 'Semantic token data must use five-integer LSP groups');

  const lines = document.getText().replace(/\r\n/g, '\n').split('\n');
  const tokens = [];
  let line = 0;
  let previousStart = 0;
  for (let index = 0; index < data.length; index += 5) {
    const lineDelta = data[index];
    line += lineDelta;
    const start = lineDelta === 0 ? previousStart + data[index + 1] : data[index + 1];
    const length = data[index + 2];
    const type = legend.tokenTypes[data[index + 3]];
    tokens.push({ line, type, text: lines[line].slice(start, start + length) });
    previousStart = start;
  }

  assert.ok(tokens.some(token => token.type === 'keyword' && token.text === 'if'));
  assert.ok(tokens.some(token => token.type === 'property' && token.text === 'first-name'));
  assert.ok(tokens.some(token => token.type === 'modifier' && token.text === 'upper'));
  assert.deepEqual(tokens.filter(token => token.type === 'talemarkFormatting' &&
    (token.text === '__' || token.text === '**')).map(token => token.text), ['__', '__', '**', '**']);
  assert.equal(tokens.some(token => token.type === 'talemarkFormatting' && token.text === '*' && token.line === 3), false,
    'Compiler classifications must preserve the emphasis boundary rules');
}

async function assertRenderedColors(document) {
  const source = [
    '=== start',
    '@title The End',
    'Plain narrative.',
    '* [Go] -> ending',
    '{player:first-name#upper}',
    String.raw`\{literal\} **bold**`,
    '=== ending',
    '-> END',
  ].join('\n');
  const edit = new vscode.WorkspaceEdit();
  edit.replace(document.uri, new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)), source);
  assert.equal(await vscode.workspace.applyEdit(edit), true);
  await waitForCompiler(document, items => items.length === 0);
  const port = process.env.TALEWRIGHT_RENDERER_PORT;
  assert.ok(port, 'Rendered-color checks require the isolated editor renderer port');
  const pagesResponse = await fetch(`http://127.0.0.1:${port}/json/list`);
  assert.equal(pagesResponse.ok, true);
  const pages = await pagesResponse.json();
  const page = pages.find(item => item.type === 'page' && item.url.includes('workbench'));
  assert.ok(page?.webSocketDebuggerUrl, 'The test editor renderer must be available');
  const socket = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  const configuration = vscode.workspace.getConfiguration('workbench');
  const oldTheme = configuration.inspect('colorTheme').globalValue;
  const tokenConfiguration = vscode.workspace.getConfiguration('editor');
  const oldColors = tokenConfiguration.inspect('tokenColorCustomizations').globalValue;
  const oldHints = tokenConfiguration.inspect('inlayHints.enabled').globalValue;
  const oldBracketColors = tokenConfiguration.inspect('bracketPairColorization.enabled').globalValue;
  const oldSemanticColors = tokenConfiguration.inspect('semanticTokenColorCustomizations').globalValue;
  const availableThemes = [
    ...vscode.extensions.getExtension('vscode.theme-defaults').packageJSON.contributes.themes,
    ...vscode.extensions.getExtension('squid-farm.talewright-editor-test-host').packageJSON.contributes.themes,
  ];
  const themes = [
    ['./themes/dark_plus.json', '#D4D4D4', 'rgb(212, 212, 212)'],
    ['./themes/light_plus.json', '#000000', 'rgb(0, 0, 0)'],
  ];
  if (availableThemes.some(theme => theme.path === './themes/2026-dark.json')) {
    themes.push(['./themes/2026-dark.json', '#BBBEBF', 'rgb(187, 190, 191)']);
  }
  const sparseColors = {
    scene: 'rgb(255, 208, 128)',
    target: 'rgb(255, 208, 128)',
    divert: 'rgb(128, 192, 255)',
    choice: 'rgb(128, 192, 255)',
    punctuation: 'rgb(255, 128, 192)',
    title: 'rgb(255, 192, 128)',
    modifier: 'rgb(192, 128, 255)',
    escape: 'rgb(128, 255, 176)',
    formatting: 'rgb(128, 255, 176)',
  };
  themes.push(['./sparse-theme.json', '#D4D4D4', 'rgb(212, 212, 212)', sparseColors]);
  themes.push([
    './sparse-theme.json', '#D4D4D4', 'rgb(212, 212, 212)',
    { ...sparseColors, divert: 'rgb(212, 212, 212)', scene: 'rgb(18, 171, 205)', target: 'rgb(18, 171, 205)' },
    [{ scope: 'keyword.control.divert.tale', settings: { foreground: '#D4D4D4' } }],
    { 'talemarkScene:tale': '#12ABCD' },
  ]);
  let requestId = 0;

  async function renderedLines() {
    const id = ++requestId;
    const result = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        socket.removeEventListener('message', receive);
        reject(new Error('Timed out inspecting rendered editor colors'));
      }, 5000);
      function receive(event) {
        const message = JSON.parse(event.data);
        if (message.id !== id) return;
        clearTimeout(timeout);
        socket.removeEventListener('message', receive);
        if (message.error || message.result.exceptionDetails) reject(new Error(JSON.stringify(message)));
        else resolve(message.result.result.value);
      }
      socket.addEventListener('message', receive);
    });
    socket.send(JSON.stringify({
      id, method: 'Runtime.evaluate', params: {
        returnByValue: true,
        expression: `Array.from(document.querySelectorAll('.monaco-editor .view-line')).map(line =>
          Array.from(line.querySelectorAll('span')).filter(span => span.children.length === 0).map(span => ({
            text: span.textContent.replaceAll('\\u00a0', ' '),
            color: getComputedStyle(span).color
          })))`,
      },
    }));
    return result;
  }

  try {
    await tokenConfiguration.update('inlayHints.enabled', 'off', vscode.ConfigurationTarget.Global);
    await tokenConfiguration.update('bracketPairColorization.enabled', false, vscode.ConfigurationTarget.Global);
    for (const [themePath, neutral, expectedProse, expectedColors, extraRules = [], semanticRules = {}] of themes) {
      const theme = availableThemes.find(item => item.path === themePath);
      assert.ok(theme, `The test editor must include ${themePath}`);
      await configuration.update('colorTheme', theme.id, vscode.ConfigurationTarget.Global);
      // Reproduce themes that intentionally render ordinary variables and operators like prose.
      await tokenConfiguration.update('tokenColorCustomizations', {
        textMateRules: [
          { scope: ['variable.other', 'keyword.operator'], settings: { foreground: neutral } },
          ...extraRules,
        ],
      }, vscode.ConfigurationTarget.Global);
      await tokenConfiguration.update('semanticTokenColorCustomizations', {
        rules: semanticRules,
      }, vscode.ConfigurationTarget.Global);
      const deadline = Date.now() + 20000;
      let observed;
      do {
        const lines = await renderedLines();
        function color(lineText, text) {
          const line = lines.find(parts => parts.map(part => part.text).join('') === lineText);
          if (!line) return undefined;
          const offset = lineText.indexOf(text);
          let start = 0;
          for (const part of line) {
            if (offset >= start && offset < start + part.text.length) return part.color;
            start += part.text.length;
          }
        }
        const prose = color('Plain narrative.', 'Plain');
        observed = {
          scene: color('=== start', 'start'),
          target: color('* [Go] -> ending', 'ending'),
          divert: color('* [Go] -> ending', '->'),
          choice: color('* [Go] -> ending', '*'),
          punctuation: color('* [Go] -> ending', '['),
          title: color('@title The End', 'The End'),
          modifier: color('{player:first-name#upper}', 'upper'),
          escape: color(String.raw`\{literal\} **bold**`, String.raw`\{`),
          formatting: color(String.raw`\{literal\} **bold**`, '**'),
        };
        const colorsMatch = expectedColors
          ? Object.entries(expectedColors).every(([role, value]) => observed[role] === value)
          : Object.values(observed).every(value => value && value !== prose);
        if (prose === expectedProse && colorsMatch &&
            observed.scene === observed.target) break;
        await new Promise(resolve => setTimeout(resolve, 100));
      } while (Date.now() < deadline);
      assert.ok(Date.now() < deadline, `${theme.id}: rendered syntax must match fallback/override policy: ${JSON.stringify(observed)}`);
      console.log(`${theme.id} rendered semantic colors passed: ${JSON.stringify(observed)}`);
    }
  } finally {
    socket.close();
    await tokenConfiguration.update('tokenColorCustomizations', oldColors, vscode.ConfigurationTarget.Global);
    await tokenConfiguration.update('inlayHints.enabled', oldHints, vscode.ConfigurationTarget.Global);
    await tokenConfiguration.update('bracketPairColorization.enabled', oldBracketColors, vscode.ConfigurationTarget.Global);
    await tokenConfiguration.update('semanticTokenColorCustomizations', oldSemanticColors, vscode.ConfigurationTarget.Global);
    await configuration.update('colorTheme', oldTheme, vscode.ConfigurationTarget.Global);
  }
}
