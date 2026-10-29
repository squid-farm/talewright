const vscode = require('vscode');
const path = require('node:path');
const { accessSync, constants } = require('node:fs');
const { LanguageClient } = require('vscode-languageclient/node');
const { registerAuthorActions } = require('./author-actions.cjs');

let client;

async function activate(context) {
  registerAuthorActions(context);
  if (!vscode.workspace.isTrusted) {
    // VS Code does not reactivate extensions with limited trust support, so start once trust is granted.
    context.subscriptions.push(vscode.workspace.onDidGrantWorkspaceTrust(() => startLanguageServer(context)));
    return;
  }
  await startLanguageServer(context);
}

async function startLanguageServer(context) {
  if (client) return;
  const configuration = vscode.workspace.getConfiguration('talewright');
  if (!configuration.get('languageServer.enabled', true)) return;
  let command = configuration.get('languageServer.command', '');
  const args = configuration.get('languageServer.args', ['--stdio']);
  const metadataPath = configuration.get('languageServer.metadataPath', '');
  const fileEvents = [
    '**/*.tale',
    '**/images/**',
    '**/*.talewright.yaml',
    '**/talewright.lint.json',
    '**/*.csproj',
    '**/*.authoring.json',
  ]
    .map(pattern => vscode.workspace.createFileSystemWatcher(pattern));
  if (metadataPath) {
    const pattern = path.isAbsolute(metadataPath)
      ? new vscode.RelativePattern(vscode.Uri.file(path.dirname(metadataPath)), path.basename(metadataPath))
      : `**/${metadataPath.replaceAll('\\', '/')}`;
    fileEvents.push(vscode.workspace.createFileSystemWatcher(pattern));
  }
  context.subscriptions.push(...fileEvents);
  const outputChannel = vscode.window.createOutputChannel('Talewright Language Server');
  context.subscriptions.push(outputChannel);
  try {
    if (!command) {
      if (process.platform !== 'linux' || process.arch !== 'x64') {
        throw new Error('The bundled server supports Linux x64 only. Use a Linux x64 VS Code host or configure an explicit development server.');
      }
      command = path.join(context.extensionPath, 'server', 'Talewright.LanguageServer');
      accessSync(command, constants.X_OK);
    }
    client = new LanguageClient('talewright', 'Talewright', {
      command, args,
    }, {
      documentSelector: [
        { scheme: 'file', language: 'tale' },
        { scheme: 'file', pattern: '**/*.talewright.yaml' },
      ],
      initializationOptions: {
        metadataPath,
        requiresProjectMetadata: configuration.get('languageServer.requiresProjectMetadata', false),
      },
      synchronize: {
        fileEvents,
      },
      outputChannel,
    });
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(event => {
      if (event.affectsConfiguration('talewright.languageServer')) {
        vscode.window.showInformationMessage('Reload the window to apply Talewright language-server settings.');
      }
    }));
    await client.start();
  } catch (error) {
    outputChannel.appendLine(String(error));
    vscode.window.showErrorMessage(
      `Talewright language server could not start: ${error.message}. Check the Linux x64 system prerequisites and reinstall the VSIX if bundled files are missing. For development, configure talewright.languageServer.command. See the Talewright Language Server output.`);
  }
}

async function deactivate() {
  if (client?.isRunning()) await client.stop();
  client = undefined;
}

module.exports = { activate, deactivate };
