# Talewright.Host.Desktop — desktop app (Photino)

A native window around the shared Player UI, using [Photino.Blazor](https://github.com/tryphotino/photino.Blazor):
Blazor runs in-process on .NET and renders into the operating system's webview (WebView2 on Windows, WKWebView
on macOS, WebKitGTK on Linux). There is no browser, no local web server and no open port.

## Run

```bash
dotnet run --project src/Talewright.Host.Desktop                          # library with the bundled samples
dotnet run --project src/Talewright.Host.Desktop -- samples/LostRelic     # also opens this story immediately
dotnet run --project src/Talewright.Host.Desktop -- ~/stories/a ~/b.zip   # several folders/zips in the library
```

Linux needs WebKitGTK: `sudo apt install libwebkit2gtk-4.1-0` (or your distro's equivalent).
Manual saves use per-user local application data under `Talewright/saves` (normally
`$XDG_DATA_HOME/Talewright/saves`, or `~/.local/share/Talewright/saves` on Linux).
Slot replacement is explicit and atomic; incompatible/corrupt loads leave the current playthrough intact.

## Files

| File | Contents |
|---|---|
| `Program.cs` | Builds the Photino app: registers `CompositeStoryLibrary(EmbeddedStoryLibrary, FileSystemStoryLibrary(args))`, passes the first path as `DesktopOptions.AutoStartStoryId`, sets the window title/size, shows unhandled exceptions in a message box. Arguments starting with `-` are ignored as story paths. |
| `App.razor` | Root component: `<TalewrightApp AutoStartStoryId=… />`. |
| `wwwroot/index.html` | Shell page: `<app>` mount point, Player stylesheet, `_framework/blazor.webview.js`. Copied to the output folder. |
| `Talewright.Host.Desktop.csproj` | Razor SDK; embeds `samples/**/*.tale` and `*.yaml` as `stories/<Story>/<path>` resources. |

## Packaging

`talewright publish story-folder -a --target desktop --output game.zip` produces a self-contained
ZIP with a single story baked in and no samples. An optional `--runtime win-x64` cross-publishes;
see the [CLI reference](../../docs/reference/cli.md#publish) for targets and verification limits.
`TalewrightIncludeSamples=false` disables development sample embedding in custom builds.

## Native regression tests

```bash
python3 tests/run_e2e.py linux --probe   # accessibility and native chooser check
python3 tests/run_e2e.py linux           # shared story scenarios plus desktop file/startup tests
```

The browser tests' C#/xUnit project drives this real Photino host through a thin AT-SPI adapter in an
isolated Xvfb/D-Bus session. Scenarios and assertions are in C#; Python only bridges native actions.
Linux is automated; Windows is manual for now.

Playwright can't attach to Photino's WebKitGTK view. Windows WebView2 supports Playwright over CDP, and
macOS WKWebView needs a separate native approach. See [testing guidance](../../tests/README.md) for
prerequisites, coverage and expansion options.
