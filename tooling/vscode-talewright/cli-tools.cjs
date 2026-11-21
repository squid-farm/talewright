const vscode = require('vscode');
const path = require('node:path');
const os = require('node:os');
const { access, stat, readFile, mkdir, rm, writeFile, chmod, open } = require('node:fs/promises');
const { constants } = require('node:fs');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { createHash } = require('node:crypto');

const RELEASES = 'https://github.com/squid-farm/talewright/releases';
const execute = promisify(execFile);
let installing = false;

function requireTrust() {
  if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before setting up or running the Talewright CLI.');
}

async function writeFully(handle, chunk) {
  let offset = 0;
  while (offset < chunk.byteLength) {
    const { bytesWritten } = await handle.write(chunk, offset, chunk.byteLength - offset, null);
    if (!Number.isInteger(bytesWritten) || bytesWritten <= 0) {
      throw new Error('Could not write the Talewright CLI download completely.');
    }
    offset += bytesWritten;
  }
  return offset;
}

async function requireDotnetRuntime() {
  let stdout;
  try {
    ({ stdout } = await execute('dotnet', ['--list-runtimes'], { timeout: 10000, maxBuffer: 1024 * 1024 }));
  } catch (error) {
    if (error.code === 'ENOENT') {
      throw new Error('The .NET 10 Runtime is required to run a story. Install it from https://dotnet.microsoft.com/download/dotnet/10.0; no installation was attempted.');
    }
    throw new Error(`Could not check the .NET 10 Runtime: ${error.message}. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and try again.`);
  }
  if (!stdout.split(/\r?\n/).some(line => /^Microsoft\.NETCore\.App 10\./.test(line))) {
    throw new Error('The .NET 10 Runtime is required to run a story. Install it from https://dotnet.microsoft.com/download/dotnet/10.0; no installation was attempted.');
  }
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

async function cliIsSelfContained(executable) {
  const runtimeConfig = path.join(path.dirname(executable), `${path.parse(executable).name}.runtimeconfig.json`);
  let config;
  try {
    config = JSON.parse(await readFile(runtimeConfig, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT') return false;
    throw new Error(`Could not inspect the CLI runtime configuration at ${runtimeConfig}: ${error.message}`);
  }
  return Array.isArray(config?.runtimeOptions?.includedFrameworks)
    && config.runtimeOptions.includedFrameworks.some(framework => framework?.name === 'Microsoft.NETCore.App');
}

async function executeCli(root, args) {
  const executable = await cliExecutable(root);
  return execute(executable, args, { cwd: root, timeout: 60000, maxBuffer: 1024 * 1024 });
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
  return file.fsPath;
}

async function fetchJson(url, version) {
  const response = await fetch(url, { signal: AbortSignal.timeout(15000), headers: { Accept: 'application/json' } });
  if (response.status === 404) throw new Error(`Talewright CLI ${version} is not published at ${url} yet.`);
  if (!response.ok) throw new Error(`Could not check CLI availability: HTTP ${response.status} from ${url}.`);
  return response.json();
}

function cliPlatform(platform = process.platform, arch = process.arch) {
  const rid = `${{ win32: 'win', darwin: 'osx', linux: 'linux' }[platform]}-${arch}`;
  if (!/^(win|osx|linux)-(x64|arm64)$/.test(rid)) return undefined;
  return rid;
}

function releaseAsset(release, name) {
  return Array.isArray(release.assets) ? release.assets.find(asset => asset.name === name) : undefined;
}

async function downloadTools(context, resource, version) {
  const rid = cliPlatform();
  if (!/^(win|osx|linux)-(x64|arm64)$/.test(rid)) {
    throw new Error(`No portable archive is supported for ${process.platform}/${process.arch}. See ${RELEASES}.`);
  }
  const release = await fetchJson(`https://api.github.com/repos/squid-farm/talewright/releases/tags/v${version}`, version);
  const name = `talewright-cli-${version}-${rid}.${process.platform === 'win32' ? 'zip' : 'tar.gz'}`;
  const archiveAsset = releaseAsset(release, name);
  const checksumAsset = releaseAsset(release, `${name}.sha256`);
  if (!archiveAsset || !checksumAsset || release.draft) {
    throw new Error(`Release v${version} does not supply ${name} and its checksum yet. See ${RELEASES}.`);
  }
  requireTrust();
  const storage = context.globalStorageUri?.fsPath;
  if (!storage) throw new Error('VS Code did not provide a persistent global storage directory.');
  const destination = path.join(storage, 'cli', version, rid);
  const downloadDirectory = path.join(storage, 'downloads');
  const archive = path.join(downloadDirectory, name);
  await mkdir(downloadDirectory, { recursive: true });

  await vscode.window.withProgress({
    location: vscode.ProgressLocation.Notification,
    title: `Downloading Talewright CLI ${version} for ${rid}`,
    cancellable: true,
  }, async (progress, token) => {
    const abort = new AbortController();
    token.onCancellationRequested(() => abort.abort());
    const response = await fetch(archiveAsset.browser_download_url, {
      signal: abort.signal,
      headers: { Accept: 'application/octet-stream' },
    });
    if (!response.ok) throw new Error(`Could not download ${name}: HTTP ${response.status}.`);
    if (!response.body) throw new Error(`The download response for ${name} contained no data.`);
    const handle = await open(archive, 'w');
    let downloaded = 0;
    const total = Number(response.headers.get('content-length') || 0);
    try {
      for await (const chunk of response.body) {
        if (abort.signal.aborted) throw new Error('Talewright CLI download was cancelled.');
        const written = await writeFully(handle, chunk);
        downloaded += written;
        progress.report({
          message: total ? `${Math.round(downloaded * 100 / total)}%` : `${Math.round(downloaded / 1024)} KB`,
          increment: total ? written * 100 / total : undefined,
        });
      }
    } finally {
      await handle.close();
    }
  });

  const checksumResponse = await fetch(checksumAsset.browser_download_url, { signal: AbortSignal.timeout(15000) });
  if (!checksumResponse.ok) throw new Error(`Could not download the checksum for ${name}: HTTP ${checksumResponse.status}.`);
  const checksumText = (await checksumResponse.text()).trim();
  const expected = checksumText.match(/^[a-fA-F0-9]{64}/)?.[0]?.toLowerCase();
  if (!expected) throw new Error(`The checksum for ${name} was missing or invalid.`);
  const actual = createHash('sha256').update(await readFile(archive)).digest('hex');
  if (actual !== expected) throw new Error(`Checksum verification failed for ${name}. The download was not installed.`);

  await rm(destination, { recursive: true, force: true });
  await mkdir(destination, { recursive: true });
  if (process.platform === 'win32') {
    await execute('powershell.exe', [
      '-NoProfile', '-NonInteractive', '-Command',
      'Expand-Archive', '-LiteralPath', archive, '-DestinationPath', destination, '-Force',
    ], { timeout: 60000, maxBuffer: 1024 * 1024 });
  } else {
    await execute('tar', ['-xzf', archive, '-C', destination], { timeout: 60000, maxBuffer: 1024 * 1024 });
  }
  await rm(archive, { force: true });

  const executable = path.join(destination, process.platform === 'win32' ? 'Talewright.Cli.exe' : 'Talewright.Cli');
  if (process.platform !== 'win32') await chmod(executable, 0o755);
  if (!await executableExists(executable)) {
    throw new Error(`The downloaded archive did not contain ${path.basename(executable)}.`);
  }
  const { stdout } = await execute(executable, ['--version'], { timeout: 30000, maxBuffer: 1024 * 1024 });
  if (!stdout.trim()) throw new Error('The downloaded Talewright CLI returned no version information.');
  await configuration(resource).update('cli.executablePath', executable);
  await writeFile(path.join(destination, 'talewright-cli.json'), JSON.stringify({
    version, rid, executable, verifiedVersion: stdout.trim(),
  }, null, 2));
  await mkdir(path.join(storage, 'cli'), { recursive: true });
  const resourcePath = typeof resource === 'string' ? resource : resource?.fsPath;
  await writeFile(path.join(storage, 'cli', 'downloaded.json'), JSON.stringify({
    version, rid, executable, resource: resourcePath,
  }));
  vscode.window.showInformationMessage(`Talewright CLI ${stdout.trim()} was downloaded and configured.`);
  return executable;
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
    return executable;
  } finally {
    for (const listener of listeners) listener.dispose();
    installing = false;
  }
}

async function setUpCli(context, options = {}) {
  const resumeAction = options?.resumeAction;
  const active = vscode.window.activeTextEditor?.document.uri;
  const resource = options?.resource?.scheme
    ? options.resource
    : typeof options?.resource === 'string'
      ? vscode.Uri.file(options.resource)
      : active && vscode.workspace.getWorkspaceFolder(active) ? active : vscode.workspace.workspaceFolders?.[0]?.uri;
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
      'Download Tools (Recommended)',
      ...(compatible ? ['Install via .NET Tool'] : []),
      'Locate Existing...',
    ];
    const choice = await vscode.window.showQuickPick(choices, {
      placeHolder: compatible ? `Set up CLI ${version}; availability is checked before installation`
        : 'No compatible .NET 10 SDK found. Use a portable CLI or select an existing executable.',
    });
    let configured;
    if (choice === 'Download Tools (Recommended)') configured = await downloadTools(context, resource, version);
    else if (choice === 'Install via .NET Tool' && compatible) configured = await installTool(resource, version);
    else if (choice === 'Locate Existing...') configured = await selectExecutable(resource);
    if (configured && typeof resumeAction === 'function') await resumeAction();
  } catch (error) {
    const choice = await vscode.window.showErrorMessage(`Talewright CLI setup: ${error.message}`,
      ...(vscode.workspace.isTrusted ? [...(version ? ['Download Tools (Recommended)'] : []), 'Locate Existing...'] : []));
    try {
      let configured;
      if (choice === 'Download Tools (Recommended)' && version) configured = await downloadTools(context, resource, version);
      if (choice === 'Locate Existing...') configured = await selectExecutable(resource);
      if (configured && typeof resumeAction === 'function') await resumeAction();
    } catch (followUpError) {
      await vscode.window.showErrorMessage(`Talewright CLI setup: ${followUpError.message}`);
    }
  }
}

async function checkCliUpdates(context) {
  try {
    if (!context.globalStorageUri?.fsPath || !vscode.workspace.isTrusted) return;
    const storage = context.globalStorageUri.fsPath;
    const downloaded = JSON.parse(await readFile(path.join(storage, 'cli', 'downloaded.json'), 'utf8'));
    const metadata = JSON.parse(await readFile(path.join(context.extensionPath, 'server', 'talewright-server.json'), 'utf8'));
    if (typeof downloaded.version !== 'string' || typeof metadata.version !== 'string'
        || downloaded.version === metadata.version) return;
    const resources = [];
    if (typeof downloaded.resource === 'string' && path.isAbsolute(downloaded.resource)) {
      resources.push(vscode.Uri.file(downloaded.resource));
    }
    const active = vscode.window.activeTextEditor?.document.uri;
    if (active && vscode.workspace.getWorkspaceFolder(active)) resources.push(active);
    resources.push(...(vscode.workspace.workspaceFolders ?? []).map(folder => folder.uri));
    if (!resources.length) resources.push(undefined);
    const configuredResource = resources
      .map(resource => ({ resource, executable: configuration(resource).get('cli.executablePath', '') }))
      .find(entry => entry.executable === downloaded.executable);
    if (!configuredResource) return;
    if (await vscode.window.showInformationMessage(
      `A newer Talewright CLI ${metadata.version} is available for this extension.`,
      'Update CLI',
    ) === 'Update CLI') {
      await downloadTools(context, configuredResource.resource, metadata.version);
    }
  } catch (error) {
    if (error.code !== 'ENOENT') vscode.window.showErrorMessage(`Talewright CLI update check: ${error.message}`);
  }
}

function registerCliSetup(context) {
  context.subscriptions.push(vscode.commands.registerCommand('talewright.setUpCli', options =>
    setUpCli(context, options)));
  void checkCliUpdates(context);
}

module.exports = {
  cliExecutable, cliIsSelfContained, executeCli, registerCliSetup, requireDotnetRuntime,
  cliPlatform, releaseAsset, writeFully, downloadTools, checkCliUpdates,
};
