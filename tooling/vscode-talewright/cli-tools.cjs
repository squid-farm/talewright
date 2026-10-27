const vscode = require('vscode');
const path = require('node:path');
const os = require('node:os');
const { access, stat, readFile } = require('node:fs/promises');
const { constants } = require('node:fs');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');

const RELEASES = 'https://github.com/squid-farm/talewright/releases';
const execute = promisify(execFile);
let installing = false;

function requireTrust() {
  if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before setting up or running the Talewright CLI.');
}

async function executableExists(file) {
  try {
    await access(file, process.platform === 'win32' ? constants.F_OK : constants.X_OK);
    return (await stat(file)).isFile();
  } catch (error) {
    if (['ENOENT', 'ENOTDIR', 'EACCES'].includes(error.code)) return false;
    throw error;
  }
}

function configuration(resource) {
  return vscode.workspace.getConfiguration('talewright', resource);
}

async function cliExecutable(root) {
  const configured = configuration(vscode.Uri.file(root)).get('cli.executablePath', '');
  if (configured) {
    if (!path.isAbsolute(configured) || !await executableExists(configured)) {
      throw new Error('talewright.cli.executablePath must name an existing executable using an absolute path. Fix it in Settings; no fallback was used.');
    }
    return configured;
  }
  const name = process.platform === 'win32' ? 'talewright.exe' : 'talewright';
  const directories = [...(process.env.PATH ?? '').split(path.delimiter).filter(Boolean),
    path.join(os.homedir(), '.dotnet', 'tools')];
  for (const directory of directories) {
    const candidate = path.join(directory, name);
    if (path.isAbsolute(candidate) && await executableExists(candidate)) return candidate;
  }
  throw Object.assign(new Error('Talewright CLI not found on PATH or in ~/.dotnet/tools. The bundled language server is not the CLI.'),
    { code: 'CLI_NOT_FOUND' });
}

async function selectExecutable(resource) {
  requireTrust();
  const [file] = await vscode.window.showOpenDialog({
    title: 'Select the extracted Talewright CLI executable (not a DLL)',
    canSelectMany: false, canSelectFolders: false,
  }) ?? [];
  if (!file) return;
  if (file.scheme !== 'file' || !path.isAbsolute(file.fsPath) || !await executableExists(file.fsPath)
      || /\.(dll|zip|gz)$/i.test(file.fsPath)) {
    throw new Error('Select an existing, executable CLI file, not its DLL or archive.');
  }
  requireTrust();
  await configuration(resource).update('cli.executablePath', file.fsPath);
  vscode.window.showInformationMessage(`Talewright CLI selected: ${file.fsPath}`);
}

async function fetchJson(url, version) {
  const response = await fetch(url, { signal: AbortSignal.timeout(15000), headers: { Accept: 'application/json' } });
  if (response.status === 404) throw new Error(`Talewright CLI ${version} is not published at ${url} yet.`);
  if (!response.ok) throw new Error(`Could not check CLI availability: HTTP ${response.status} from ${url}.`);
  return response.json();
}

async function standalone(resource, version) {
  const rid = `${{ win32: 'win', darwin: 'osx', linux: 'linux' }[process.platform]}-${process.arch}`;
  if (!/^(win|osx|linux)-(x64|arm64)$/.test(rid)) {
    throw new Error(`No portable archive is supported for ${process.platform}/${process.arch}. See ${RELEASES}.`);
  }
  const release = await fetchJson(`https://api.github.com/repos/squid-farm/talewright/releases/tags/v${version}`, version);
  const name = `talewright-cli-${version}-${rid}.${process.platform === 'win32' ? 'zip' : 'tar.gz'}`;
  if (!Array.isArray(release.assets) || ![name, `${name}.sha256`].every(expected =>
    release.assets.some(asset => asset.name === expected)) || release.draft) {
    throw new Error(`Release v${version} does not supply ${name} and its checksum yet. See ${RELEASES}.`);
  }
  requireTrust();
  if (!await vscode.env.openExternal(vscode.Uri.parse(`${RELEASES}/tag/v${version}`))) {
    throw new Error('Could not open the release page. Open it manually: ' + RELEASES);
  }
  if (await vscode.window.showInformationMessage(
    `Download ${name} and unpack all its files into a folder. Then select Talewright.Cli${process.platform === 'win32' ? '.exe' : ''}. The included INSTALL.md explains setup and troubleshooting.`,
    'Select Executable',
  ) === 'Select Executable') await selectExecutable(resource);
}

async function installTool(resource, version) {
  const feed = await fetchJson('https://api.nuget.org/v3-flatcontainer/talewright.cli/index.json', version);
  if (!Array.isArray(feed.versions) || !feed.versions.includes(version)) {
    throw new Error(`Talewright.Cli ${version} is not published on nuget.org yet. No installation was attempted.`);
  }
  if (await vscode.window.showWarningMessage(
    `Install Talewright.Cli ${version} globally from nuget.org using the .NET 10 SDK? This runs dotnet tool install, changes your global tools, and does not install an SDK or edit PATH.`,
    { modal: true }, 'Install CLI',
  ) !== 'Install CLI') return;
  requireTrust();
  if (installing) throw new Error('A Talewright CLI installation is already running. See its task terminal.');
  installing = true;
  const listeners = [];
  try {
    const task = new vscode.Task({ type: 'talewright-cli-install' }, vscode.TaskScope.Global,
      'Install CLI', 'Talewright', new vscode.ProcessExecution('dotnet', [
        'tool', 'install', '--global', 'Talewright.Cli', '--version', version,
        '--source', 'https://api.nuget.org/v3/index.json',
      ]), []);
    task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, panel: vscode.TaskPanelKind.Dedicated, focus: true };
    let execution;
    const exitCodes = new Map();
    const ended = new Set();
    const completion = new Promise(resolve => {
      listeners.push(vscode.tasks.onDidEndTaskProcess(event => {
        exitCodes.set(event.execution, event.exitCode);
      }));
      listeners.push(vscode.tasks.onDidEndTask(event => {
        ended.add(event.execution);
        if (event.execution === execution) resolve(exitCodes.get(event.execution));
      }));
    });
    execution = await vscode.tasks.executeTask(task);
    const exitCode = ended.has(execution) ? exitCodes.get(execution) : await completion;
    if (exitCode !== 0) throw new Error(`CLI installation failed or was cancelled (exit ${exitCode ?? 'unknown'}). See the Install CLI task terminal.`);
    const executable = await cliExecutable(resource?.fsPath ?? os.homedir());
    vscode.window.showInformationMessage(`Talewright CLI is available at ${executable}. Run your story action again.`);
  } finally {
    for (const listener of listeners) listener.dispose();
    installing = false;
  }
}

async function setUpCli(context) {
  const active = vscode.window.activeTextEditor?.document.uri;
  const resource = active && vscode.workspace.getWorkspaceFolder(active) ? active : vscode.workspace.workspaceFolders?.[0]?.uri;
  let version;
  try {
    requireTrust();
    try {
      const executable = await cliExecutable(resource?.fsPath ?? os.homedir());
      vscode.window.showInformationMessage(`Using the existing Talewright CLI at ${executable}. No installation or upgrade was performed.`);
      return;
    } catch (error) {
      if (error.code !== 'CLI_NOT_FOUND') throw error;
    }
    const metadata = JSON.parse(await readFile(path.join(context.extensionPath, 'server', 'talewright-server.json'), 'utf8'));
    if (typeof metadata.version !== 'string' || !/^\d+\.\d+\.\d+(?:-preview\.[1-9]\d*)?$/.test(metadata.version)) {
      throw new Error('The bundled build metadata has no supported CLI version. Reinstall the VSIX or select an existing executable.');
    }
    version = metadata.version;
    let compatible = false;
    try {
      const { stdout } = await execute('dotnet', ['--list-sdks'], { timeout: 10000, maxBuffer: 1024 * 1024 });
      compatible = stdout.split(/\r?\n/).some(line => /^10\.\d+\.\d+\s/.test(line));
    } catch (error) {
      if (error.code !== 'ENOENT') throw new Error(`Could not inspect .NET SDKs: ${error.message}`);
    }
    const choices = [
      ...(compatible ? ['Install .NET tool (recommended)'] : []),
      'Use standalone download', 'Select Existing Executable',
    ];
    const choice = await vscode.window.showQuickPick(choices, {
      placeHolder: compatible ? `Set up CLI ${version}; availability is checked before installation`
        : 'No compatible .NET 10 SDK found. Use a portable CLI or select an existing executable.',
    });
    if (choice === choices[0] && compatible) await installTool(resource, version);
    else if (choice === 'Use standalone download') await standalone(resource, version);
    else if (choice === 'Select Existing Executable') await selectExecutable(resource);
  } catch (error) {
    const choice = await vscode.window.showErrorMessage(`Talewright CLI setup: ${error.message}`,
      ...(vscode.workspace.isTrusted ? [...(version ? ['Use standalone instead'] : []), 'Select Existing Executable'] : []));
    try {
      if (choice === 'Use standalone instead' && version) await standalone(resource, version);
      if (choice === 'Select Existing Executable') await selectExecutable(resource);
    } catch (followUpError) {
      await vscode.window.showErrorMessage(`Talewright CLI setup: ${followUpError.message}`);
    }
  }
}

function registerCliSetup(context) {
  context.subscriptions.push(vscode.commands.registerCommand('talewright.setUpCli', () => setUpCli(context)));
}

module.exports = { cliExecutable, registerCliSetup };
