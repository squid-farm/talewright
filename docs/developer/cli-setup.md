# CLI setup: technical details

For authors, start with [Get the story-checking tools](../guide/cli-installation.md).
This page covers verification, system dependencies, alternate installation and source builds.

## Portable downloads and verification

Use the [releases list](https://github.com/squid-farm/talewright/releases), including prereleases.
An existing release does not imply CLI assets exist. Look for
`talewright-cli-VERSION-RUNTIME.zip` (Windows) or `.tar.gz` (macOS/Linux), plus its `.sha256` file.
Supported build targets are `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64` and
`linux-arm64`. Linux requires glibc; Alpine/musl is not supported.

Linux x64 execution is verified on Ubuntu 24.04. Windows, macOS and ARM archives are cross-built
and asset-checked only, not runtime-verified. In particular, macOS security approval behavior has
not been verified by cross-compilation.

Download the archive and matching checksum into the same directory:

- Linux: `sha256sum -c FILE.sha256`
- macOS: `shasum -a 256 -c FILE.sha256`
- Windows PowerShell: `Get-FileHash FILE.zip -Algorithm SHA256`, then compare with the checksum text.

Replace `FILE` with the downloaded filename. Checksums detect corruption, not authenticity.
Archives are not signed or notarized. Obtain them from the project's release page; do not disable
SmartScreen, Gatekeeper or other platform security globally to run them.

Extract the entire archive into a fresh versioned directory. The portable executable is
`Talewright.Cli` (`Talewright.Cli.exe` on Windows), not the .NET-tool shim `talewright`.
Use an absolute executable path from another working directory and quote paths containing spaces.
Adding its directory to PATH or creating an alias is optional and manual; restart terminals and
VS Code after changing PATH. No shell profile or system setting is changed automatically.
If you remove the tools, also remove any PATH entry or alias you added.

## Requirements by command

| Command | Portable download requirements |
|---|---|
| `--help`, `validate`, `lint` | No SDK or installed .NET runtime; OS runtime libraries still required. |
| `publish STORY --output FILE.zip` | No SDK/runtime; outputs story files for a Talewright player, not a standalone game. |
| `debug STORY` | No installed .NET with the bundled debugger host; graphical desktop and native webview libraries required. |
| `publish -a --target web` or `desktop` | .NET 10 SDK, restore/network access and applicable runtime/workload packs are required by the build-based publisher. |

Ubuntu 24.04 x64 requires `libc6`, `libgcc-s1`, `libstdc++6`, `libicu74`, `libssl3t64` and `zlib1g`.
Native debugging additionally requires WebKitGTK 4.1 (`libwebkit2gtk-4.1-0`), `libnotify4`, and a
graphical X11 or Wayland desktop (`DISPLAY` or `WAYLAND_DISPLAY`). Windows needs WebView2; macOS uses WKWebView.
See the [Linux desktop setup commands](../guide/getting-started.md#linux-desktop-setup) for common distributions.
Other OS versions/architectures are not claimed runtime-tested. Launch failures are reported,
not silently redirected to another tool.

## Alternative: .NET tool

With the .NET 10 SDK installed, and only after the selected version is published to nuget.org:

```bash
dotnet tool install --global Talewright.Cli --version VERSION
talewright --help
```

Replace `VERSION` with an exact compatible published version; do not assume any preview exists.
The global tool requires the matching installed .NET runtime and the native debugging prerequisites.
If PATH has not refreshed, the global shim is normally at `~/.dotnet/tools/talewright`
(`%USERPROFILE%\.dotnet\tools\talewright.exe` on Windows). Use/select that absolute path.

Update with `dotnet tool update --global Talewright.Cli --version VERSION`.
Remove with `dotnet tool uninstall --global Talewright.Cli`.

## Run from source

### Install the current checkout for local testing

On Linux or macOS (x64/ARM64), run from any directory:

```bash
python3 /path/to/talewright/tooling/install-dev.py
# From the repository root:
python3 tooling/install-dev.py
```

Requires Python 3.8+, Node.js 22+, npm, `unzip`, the .NET 10 SDK and the VS Code `code`
command on PATH. The script installs missing extension dependencies with `npm ci`;
restores and packaging may need network access. It builds the current working tree,
including uncommitted fixes, without publishing to NuGet, GitHub or the Marketplace.
Builds run sequentially, with MSBuild node reuse disabled.

The script publishes a self-contained CLI and native debugger, packages and verifies
the matching VSIX (including the current language server), and force-installs it.
Rerun the same command after each fix; no version bump or release is needed.

The CLI symlink is `~/.local/bin/talewright`. Its files live under
`${XDG_DATA_HOME:-~/.local/share}/talewright/dev`; successful reruns replace the
previous development build. An existing non-symlink CLI is never overwritten.
Use `--bin-dir /path/to/bin` to choose another symlink directory.
Build, package-check and VSIX-install failures leave the previous CLI unchanged.
Close running CLI/debugger processes before reinstalling, and do not run multiple
installers or VSIX packagers concurrently in the same checkout.

Set **`talewright.cli.executablePath`** in VS Code to the printed CLI path, especially
if a released/global tool is already on PATH. For terminal use, prepend the printed
bin directory to PATH or invoke the absolute path. The script does not change
shell profiles, VS Code settings or an existing .NET global-tool installation.
Clear any **`talewright.languageServer.command`** development override to use the
new bundled server, then run **Developer: Reload Window**. Existing native-library
requirements still apply.

For VS Code Insiders or a named editor profile:

```bash
python3 tooling/install-dev.py --code code-insiders --profile "Development"
```

`--code` also accepts an absolute path to the editor CLI. The default installs into
the editor's default profile. Windows is not supported by this convenience script;
use the source-build and manual VSIX instructions instead.

### Build and run directly

Source builds are a developer option, not a prerequisite for writing stories.
Install the .NET 10 SDK and obtain the repository or a release's source archive.
From the Talewright source directory:

```bash
dotnet build tooling/cli/src/Talewright.Cli -c Release
dotnet run --project tooling/cli/src/Talewright.Cli -- validate "/path/to/story"
```

The built CLI apphost is under `tooling/cli/src/Talewright.Cli/bin/Release/net10.0/`.
The source/global-tool debugger launch still needs installed .NET.
To run the desktop player from source, use
`dotnet run --project apps/desktop -- "/path/to/story"`; it opens story folders directly.
The standalone CLI download includes the debugger, not a separate desktop-player download.
