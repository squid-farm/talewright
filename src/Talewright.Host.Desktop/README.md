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
