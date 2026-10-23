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
  assert.equal(extension.packageJSON.main, undefined);
  const snippets = JSON.parse(await fs.readFile(path.join(extension.extensionPath, 'snippets/tale.json'), 'utf8'));
  const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'talewright-editor-'));
  try {
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
    await vscode.commands.executeCommand('type', { text: " It's {literal" });
    assert.equal(document.getText(), "Hello. It's {literal");
    console.log('Installed VSIX smoke passed: .tale detection, five real snippet expansions and placeholder navigation, prose typing, comment toggle.');
  } finally {
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    await fs.rm(folder, { recursive: true, force: true });
  }
};
