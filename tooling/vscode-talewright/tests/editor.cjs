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
  const snippets = JSON.parse(await fs.readFile(path.join(extension.extensionPath, 'snippets/tale.json'), 'utf8'));
  const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'talewright-editor-'));
  try {
    await fs.writeFile(path.join(folder, 'story.yaml'), 'id: smoke\ntitle: Smoke\nstart: smoke\n');
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
};

async function testYamlSchema() {
  const extension = vscode.extensions.getExtension('redhat.vscode-yaml');
  assert.ok(extension, 'Red Hat YAML must be installed in the clean editor profile');
  await extension.activate();

  const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'talewright-yaml-editor-'));
  try {
    const file = path.join(folder, 'story.talewright.yaml');
    const invalid = [
      'id: schema-smoke',
      'title: Schema smoke',
      'start: beginning',
      'randomness: always',
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
      'id: schema-smoke',
      'title: Schema smoke',
      'start: beginning',
      'randomness: checks-only',
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
      'id: schema-fallback',
      'title: Schema fallback',
      'start: beginning',
      'randomness: always',
      'characters:',
      '  player:',
      '    first-name: Ada',
      '',
    ].join('\n'));
    const fallbackDocument = await vscode.workspace.openTextDocument(fallbackFile);
    await vscode.window.showTextDocument(fallbackDocument);
    diagnostics = await waitForDiagnostics(fallbackDocument, items =>
      items.some(item => item.range.start.line === 4 && item.message.includes('Valid values: "off"')));
    assert.ok(diagnostics.length > 0, 'Modeline schema fallback should validate another YAML filename');
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
    await fs.writeFile(path.join(folder, 'story.yaml'), 'id: live\ntitle: Live\nstart: start\n');
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
    console.log('Installed VSIX LSP smoke passed: compiler diagnostics and clearing after unsaved edits.');
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
