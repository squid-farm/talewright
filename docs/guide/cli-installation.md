# Install the Talewright CLI

The CLI is independent of VS Code. The extension's bundled language server does not install
`talewright` or the native Author Debugger.

## Portable downloads

Release automation builds versioned archives; **no public release/download is available yet**.
After a release is published, open [GitHub Releases](https://github.com/squid-farm/talewright/releases)
(including previews) and select `talewright-cli-VERSION-RUNTIME.zip` on Windows or
`talewright-cli-VERSION-RUNTIME.tar.gz` on macOS/Linux. Choose your OS and CPU:
`win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, or `linux-arm64`.
Linux uses glibc; Alpine/musl is not supported. Cross-building is not runtime verification:
Linux x64 is verified locally; Windows, macOS and ARM archives are build-checked only.

Download the matching `.sha256` file. On Linux/macOS, verify with `sha256sum -c FILE.sha256`
(macOS: `shasum -a 256 -c FILE.sha256`). On Windows, compare
`Get-FileHash FILE.zip -Algorithm SHA256` with the checksum text.
Checksums detect corruption; archives are not signed or notarized.

Extract **the entire archive** into a new versioned directory. Do not copy just the executable.

```bash
# Linux/macOS, from the extracted directory:
./Talewright.Cli --help
./Talewright.Cli validate /path/to/story
./Talewright.Cli publish /path/to/story --output /path/to/story.zip
./Talewright.Cli debug /path/to/story
```

```powershell
# Windows PowerShell, from the extracted directory:
.\Talewright.Cli.exe --help
.\Talewright.Cli.exe validate C:\Stories\MyStory
.\Talewright.Cli.exe publish C:\Stories\MyStory --output C:\Exports\MyStory.zip
```

Use the full executable path from any other directory; quote paths containing spaces.
To use the editor actions, select this executable in `talewright.cli.executablePath`.
Optionally add the extracted directory to PATH through your OS settings, and restart terminals/
VS Code. The portable executable is named `Talewright.Cli` (`.exe` on Windows), not `talewright`;
use that name on PATH, or deliberately create your own alias named `talewright`. No shell
profile or system setting is changed automatically.

Update by extracting a new version into a different directory and changing the path/setting.
Keep the old directory if you need rollback. Remove by deleting the exact extracted directory
and removing any PATH entry/alias you added. There is no background updater.

Windows SmartScreen or macOS Gatekeeper may warn about unsigned downloads. Verify the source
and checksum before using your OS's explicit approval procedure; do not disable platform security
globally. macOS approval/notarization behavior is not verified by Linux cross-compilation.

## Requirements by command

| Command | Portable archive requirements |
|---|---|
| `--help`, `validate`, `lint` | No SDK or installed .NET runtime; OS runtime libraries still required. |
| `publish STORY --output FILE.zip` | No SDK/runtime; outputs story files for a Talewright player, not a standalone game. |
| `debug STORY` | No installed .NET with the portable debugger host; graphical desktop and native webview libraries required. |
| `publish -a --target web` or `desktop` | **.NET 10 SDK**, restore/network access and applicable runtime/workload packs are still required by the existing build-based publisher. Portable does not mean SDK-free standalone-game publishing. |

Ubuntu 24.04 x64 requires `libc6`, `libgcc-s1`, `libstdc++6`, `libicu74`, `libssl3t64` and `zlib1g`.
Native debugging additionally requires WebKitGTK 4.1 (`libwebkit2gtk-4.1-0`), `libnotify4`, and a graphical
desktop (`DISPLAY`). Windows needs WebView2; macOS uses WKWebView. Other OS versions/architectures
are not claimed runtime-tested. Launch failures are reported, not silently redirected to another tool.

## Alternative: .NET tool

With the .NET 10 SDK installed, and **after the version is published to nuget.org**:

```bash
dotnet tool install --global Talewright.Cli --version 0.1.0-preview.1
talewright --help
```

The global tool requires the matching installed .NET runtime and the native debugging prerequisites.
Use an explicit compatible published version; do not assume the preview exists before publication.
If PATH has not refreshed, the global shim is normally at `~/.dotnet/tools/talewright`
(`%USERPROFILE%\.dotnet\tools\talewright.exe` on Windows). Use/select that absolute path.
Update explicitly with `dotnet tool update --global Talewright.Cli --version VERSION`;
remove with `dotnet tool uninstall --global Talewright.Cli`.

Until release artifacts/packages exist, source users need the .NET 10 SDK:
`dotnet build tooling/cli/src/Talewright.Cli -c Release`, then run the apphost under
`tooling/cli/src/Talewright.Cli/bin/Release/net10.0/`, or use
`dotnet run --project tooling/cli/src/Talewright.Cli -- validate STORY`.
