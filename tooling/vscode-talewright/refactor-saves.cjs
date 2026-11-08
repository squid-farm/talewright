const vscode = require('vscode');

const SAVE_COMMAND = 'talewright.saveRefactorDocuments';

function registerRefactorSaves(context, output) {
  let pendingRename = new Map();
  const appliedRename = new Set();
  let renameRequest = 0;
  let renameWarningShown = false;
  let saves = Promise.resolve();
  const report = error => {
    output.appendLine(String(error));
    void vscode.window.showErrorMessage(`Talewright: ${error.message}`);
  };
  const saveDocuments = async uris => {
    const failures = [];
    for (const uri of new Set(uris)) {
      try {
        const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(uri));
        if (document.isDirty && (!await document.save() || document.isDirty)) {
          throw new Error('The document remains unsaved.');
        }
      } catch (error) {
        failures.push(`${uri}: ${error.message}`);
      }
    }
    if (failures.length) {
      throw new Error(`Refactor edits were applied but could not be saved. Save the affected files manually.\n${failures.join('\n')}`);
    }
  };
  const enqueueSave = uris => {
    const result = saves.then(() => saveDocuments(uris));
    saves = result.catch(report);
    return saves;
  };
  const saveAppliedRename = document => {
    const key = document.uri.toString();
    if (document.isDirty && appliedRename.delete(key)) void enqueueSave([key]);
  };
  context.subscriptions.push(vscode.commands.registerCommand(SAVE_COMMAND, async (uris, nextCommand) => {
    await enqueueSave(uris);
    if (nextCommand) {
      const args = [...(nextCommand.arguments ?? [])];
      if (nextCommand.command === 'vscode.open' && typeof args[0] === 'string') {
        args[0] = vscode.Uri.parse(args[0]);
      }
      await vscode.commands.executeCommand(nextCommand.command, ...args);
    }
  }));
  context.subscriptions.push(vscode.workspace.onDidChangeTextDocument(event => {
    const key = event.document.uri.toString();
    saveAppliedRename(event.document);
    if (!event.contentChanges.length) return;
    const pending = pendingRename.get(key);
    if (!pending) return;
    pendingRename.delete(key);
    if (event.reason || (pending.version !== undefined && event.document.version !== pending.version + 1)) return;
    // Providers have no post-apply hook. Only an actual event matching the returned edits may save.
    const remaining = [...pending.edits];
    for (const change of event.contentChanges) {
      const index = remaining.findIndex(edit => edit.newText === change.text && edit.range.isEqual(change.range));
      if (index < 0) return;
      remaining.splice(index, 1);
    }
    if (!renameWarningShown) {
      renameWarningShown = true;
      vscode.window.showInformationMessage(
        'Renamed story identities may no longer match existing player saves or debugger scenarios. Saves are not migrated.');
    }
    // VS Code can deliver the text event before the dirty-state event.
    appliedRename.add(key);
    saveAppliedRename(event.document);
  }));
  context.subscriptions.push(vscode.workspace.onDidCloseTextDocument(document => {
    pendingRename.delete(document.uri.toString());
    appliedRename.delete(document.uri.toString());
  }));
  return {
    provideCodeActions: async (document, range, actionContext, token, next) => {
      const actions = await next(document, range, actionContext, token);
      if (token.isCancellationRequested || !actions) return actions;
      for (const action of actions) {
        if (!action.edit) continue;
        if (action.command?.command === SAVE_COMMAND) continue;
        const uris = action.edit.entries().map(([uri]) => uri.toString());
        action.command = {
          title: action.title,
          command: SAVE_COMMAND,
          arguments: [uris, action.command],
        };
      }
      return actions;
    },
    provideRenameEdits: async (document, position, newName, token, next) => {
      const request = ++renameRequest;
      pendingRename = new Map();
      const edit = await next(document, position, newName, token);
      if (request !== renameRequest || token.isCancellationRequested || !edit) return edit;
      renameWarningShown = false;
      pendingRename = new Map(edit.entries().map(([uri, edits]) => [
        uri.toString(),
        {
          edits,
          version: vscode.workspace.textDocuments.find(open => open.uri.toString() === uri.toString())?.version,
        },
      ]));
      return edit;
    },
  };
}

module.exports = { registerRefactorSaves };
