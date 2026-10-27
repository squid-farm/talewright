const vscode = require('vscode');
const path = require('node:path');
const { cliExecutable, registerCliSetup } = require('./cli-tools.cjs');

function contains(root, file) {
  const relative = path.relative(root, file);
  return relative === '' || (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
}

async function storyRoots() {
  const roots = [];
  for (const folder of vscode.workspace.workspaceFolders ?? []) {
    if (folder.uri.scheme !== 'file') continue;
    const manifests = await vscode.workspace.findFiles(
      new vscode.RelativePattern(folder, '**/story.talewright.yaml'),
      '**/{.git,node_modules,bin,obj,artifacts}/**',
    );
    roots.push(...manifests.map(uri => path.dirname(uri.fsPath)));
  }
  return [...new Set(roots)].filter(root => !roots.some(parent => parent !== root && contains(parent, root))).sort();
}

async function selectStory(requested) {
  const roots = await storyRoots();
  if (requested) {
    if (!roots.includes(requested)) throw new Error('The task story must be a canonical story root inside an open workspace folder.');
    return requested;
  }
  const active = vscode.window.activeTextEditor?.document.uri;
  const containing = active?.scheme === 'file' && roots.find(root => contains(root, active.fsPath));
  if (containing) return containing;
  if (roots.length === 0) throw new Error('No story.talewright.yaml found. Open your story folder in VS Code.');
  if (roots.length === 1) return roots[0];
  return (await vscode.window.showQuickPick(
    roots.map(root => ({ label: path.basename(root), description: root, root })),
    { placeHolder: 'Choose a Talewright story' },
  ))?.root;
}

async function saveStory(root) {
  const dirty = vscode.workspace.textDocuments.filter(document =>
    document.isDirty && document.uri.scheme === 'file' && contains(root, document.uri.fsPath));
  if (!dirty.length) return true;
  const choice = await vscode.window.showWarningMessage(
    `Save ${dirty.length} changed story document(s) before running the CLI? Unsaved contents will not be used.`,
    { modal: true }, 'Save and Continue',
  );
  if (choice !== 'Save and Continue') return false;
  for (const document of dirty) {
    if (!await document.save() || document.isDirty) {
      throw new Error(`Could not save ${document.uri.fsPath}. No CLI task was started.`);
    }
  }
  if (vscode.workspace.textDocuments.some(document =>
    document.isDirty && document.uri.scheme === 'file' && contains(root, document.uri.fsPath))) {
    throw new Error('Story documents changed while saving. Save them and try again; no CLI task was started.');
  }
  return true;
}

async function createTask(command, requested, definition, scope) {
  if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before running Talewright CLI commands.');
  const root = await selectStory(requested);
  if (!root) return undefined;
  const executable = await cliExecutable(root);
  const args = [command, root];
  if (command === 'publish') {
    const output = await vscode.window.showSaveDialog({
      title: 'Publish story files for a Talewright player (not a standalone game)',
      defaultUri: vscode.Uri.file(path.join(root, `${path.basename(root)}.zip`)),
      filters: { 'Story ZIP': ['zip'] },
    });
    if (!output) return undefined;
    if (output.scheme !== 'file' || path.extname(output.fsPath).toLowerCase() !== '.zip') {
      throw new Error('Choose a local .zip destination for the story files.');
    }
    args.push('--output', output.fsPath);
  }
  if (!await saveStory(root)) return undefined;
  if (!vscode.workspace.isTrusted) throw new Error('Workspace trust was revoked. No CLI task was started.');
  const folder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(root));
  const names = { validate: 'Validate Story', publish: 'Publish Story ZIP', debug: 'Open Author Debugger' };
  const task = new vscode.Task(
    definition ?? { type: 'talewright', command, story: root },
    scope ?? folder, names[command], 'Talewright',
    new vscode.ProcessExecution(executable, args, { cwd: root }), [],
  );
  task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, panel: vscode.TaskPanelKind.Dedicated, focus: true };
  return task;
}

function registerAuthorActions(context) {
  registerCliSetup(context);
  const report = async error => {
    const choice = await vscode.window.showErrorMessage(`Talewright: ${error.message}`,
      ...(error.code === 'CLI_NOT_FOUND' ? ['Set Up CLI'] : []));
    if (choice === 'Set Up CLI') await vscode.commands.executeCommand('talewright.setUpCli');
  };
  for (const [id, command] of [
    ['validateStory', 'validate'], ['publishStoryZip', 'publish'], ['openAuthorDebugger', 'debug'],
  ]) {
    context.subscriptions.push(vscode.commands.registerCommand(`talewright.${id}`, async () => {
      try {
        const task = await createTask(command);
        if (task) await vscode.tasks.executeTask(task);
      } catch (error) {
        await report(error);
      }
    }));
  }
  context.subscriptions.push(vscode.tasks.registerTaskProvider('talewright', {
    provideTasks: () => [],
    resolveTask: async task => {
      try {
        if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before running Talewright CLI commands.');
        const workspaceScope = task.scope === vscode.TaskScope.Workspace;
        if (task.definition.command !== 'debug' || typeof task.definition.story !== 'string'
            || (!workspaceScope && task.scope?.uri?.scheme !== 'file')) {
          throw new Error('A Talewright task requires command "debug", a story path, and a workspace or workspace-folder scope.');
        }
        let requested;
        if (workspaceScope) {
          const roots = await storyRoots();
          const candidates = new Set((vscode.workspace.workspaceFolders ?? [])
            .filter(folder => folder.uri.scheme === 'file')
            .map(folder => path.resolve(folder.uri.fsPath, task.definition.story))
            .filter(root => roots.includes(root)));
          if (candidates.size > 1) {
            throw new Error('The task story is ambiguous across workspace folders. Use an absolute canonical story-root path.');
          }
          if (candidates.size === 0) {
            throw new Error('The task story must be a canonical story root inside an open workspace folder.');
          }
          [requested] = candidates;
        } else {
          requested = path.resolve(task.scope.uri.fsPath, task.definition.story);
        }
        return await createTask('debug', requested, task.definition, task.scope);
      } catch (error) {
        await report(error);
        return undefined;
      }
    },
  }));
}

module.exports = { registerAuthorActions };
