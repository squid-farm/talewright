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
