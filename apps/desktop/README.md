# Talewright.Host.Desktop — desktop app (Photino)

A native window around the shared Player UI, using [Photino.Blazor](https://github.com/tryphotino/photino.Blazor):
Blazor runs in-process on .NET and renders into the operating system's webview (WebView2 on Windows, WKWebView
on macOS, WebKitGTK on Linux). There is no browser, no local web server and no open port.

## Run

```bash
dotnet run --project apps/desktop                          # library with the bundled samples
dotnet run --project apps/desktop -- samples/LostRelic     # also opens this story immediately
dotnet run --project apps/desktop -- ~/stories/a ~/b.zip   # several folders/zips in the library
dotnet run --project apps/desktop -- --no-library ~/b.zip # one story, no Library navigation
```

Library remains available by default, even when a story opens automatically.
`--no-library` requires exactly one folder or ZIP; it hides Library during creation
and play. Startup errors show diagnostics and a retry action for that same story.
Use `--` before a path beginning with `-`; unknown options and invalid combinations
exit with code 2 before opening a window. `--help` prints usage.

Linux needs WebKitGTK 4.1 and libnotify; see the
[Linux desktop setup commands](../../docs/guide/getting-started.md#linux-desktop-setup).
Manual saves use per-user local application data under `Talewright/saves` (normally
`$XDG_DATA_HOME/Talewright/saves`, or `~/.local/share/Talewright/saves` on Linux).
Slot replacement is explicit and atomic; incompatible/corrupt loads leave the current playthrough intact.
Export uses a native save dialog with the story-and-save filename prefilled.

## Files

| File | Contents |
|---|---|
| `Program.cs` | Builds the Photino app, registers embedded/file libraries, passes startup selection and library policy, and sets the window title/size. |
| `DesktopLaunchOptions.cs` | Parses story paths, `--no-library`, `--help`, and `--`. |
| `App.razor` | Root component: passes `AutoStartStoryId` and `LibraryEnabled` to the shared player. |
| `wwwroot/index.html` | Shell page: `<app>` mount point, Player stylesheet, `_framework/blazor.webview.js`. Copied to the output folder. |
| `Talewright.Host.Desktop.csproj` | Razor SDK; embeds `samples/**/*.tale` and `*.yaml` as `stories/<Story>/<path>` resources. |

## Packaging

`talewright publish story-folder -a --target desktop --output game.zip` produces a self-contained
ZIP with a single story baked in, no samples, and no Library navigation during creation or play.
This policy is built into published hosts, not inferred from auto-start or a launcher flag.
An optional `--runtime win-x64` cross-publishes;
see the [CLI reference](../../docs/reference/cli.md#publish) for targets and verification limits.
`TalewrightIncludeSamples=false` disables development sample embedding in custom builds.

## Native regression tests

```bash
python3 player/tests/run_e2e.py linux --probe   # accessibility and native chooser check
python3 player/tests/run_e2e.py linux           # shared story scenarios plus desktop file/startup tests
```

The browser tests' C#/xUnit project drives this real Photino host through a thin AT-SPI adapter in an
isolated Xvfb/D-Bus session. Scenarios and assertions are in C#; Python only bridges native actions.
Linux is automated; Windows is manual for now.

Playwright can't attach to Photino's WebKitGTK view. Windows WebView2 supports Playwright over CDP, and
macOS WKWebView needs a separate native approach. See [testing guidance](../../tests/README.md) for
prerequisites, coverage and expansion options.
