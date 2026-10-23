const vscode = require('vscode');
const path = require('node:path');
const { LanguageClient } = require('vscode-languageclient/node');

let client;

async function activate(context) {
  if (!vscode.workspace.isTrusted) return;
  const configuration = vscode.workspace.getConfiguration('talewright');
  if (!configuration.get('languageServer.enabled', true)) return;
  const command = configuration.get('languageServer.command', 'talewright-lsp');
  const args = configuration.get('languageServer.args', ['--stdio']);
  const metadataPath = configuration.get('languageServer.metadataPath', '');
  const fileEvents = ['**/*.tale', '**/story.yaml', '**/*.csproj', '**/*.authoring.json']
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
  client = new LanguageClient('talewright', 'Talewright', {
    command, args,
  }, {
    documentSelector: [
      { scheme: 'file', language: 'tale' },
      { scheme: 'file', pattern: '**/story.yaml' },
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
  try {
    await client.start();
  } catch (error) {
    outputChannel.appendLine(String(error));
    vscode.window.showErrorMessage(
      'Talewright language server could not start. Install Talewright.LanguageServer or configure talewright.languageServer.command. See the Talewright Language Server output.');
  }
}

async function deactivate() {
  if (client?.isRunning()) await client.stop();
}

module.exports = { activate, deactivate };
