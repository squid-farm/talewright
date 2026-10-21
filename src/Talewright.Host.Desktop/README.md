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

Not done in M1. `dotnet publish -c Release -r <rid> --self-contained` produces a runnable folder; M2's
`talewright publish` will produce itch-ready zips with a single story baked in.

## Native regression tests

`python3 tests/run_e2e.py linux --probe` verifies accessibility and the native chooser;
`python3 tests/run_e2e.py linux` runs the shared story scenarios and desktop-specific file/startup tests.
The same C#/xUnit project used for browser tests drives this actual Photino host through a thin AT-SPI
command adapter in an isolated Xvfb/D-Bus session. All scenarios and assertions are authored in C#;
Python only bridges native actions/observations. It needs additional distro accessibility packages and
runs as an ordinary user. Linux development-machine coverage is the current target; Windows remains manual.

The Linux app **can be automated**: the suite above does so using the distro's AT-SPI dependencies.
Playwright itself has no supported direct attachment to Photino's embedded WebKitGTK view.
Another option is WebKitWebDriver, but that requires native-host automation hooks and a WebDriver client
(such as Selenium); installing a dependency alone does not make this view controllable by Playwright.
Windows WebView2 does support Playwright attachment through CDP; macOS WKWebView needs a separate native
approach. See [testing guidance](../../tests/README.md) for prerequisites, evidence, coverage limits,
and expansion options.
