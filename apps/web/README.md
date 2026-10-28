# Talewright.Host.Web — browser app (Blazor WebAssembly)

The same Player UI running entirely in the browser on WebAssembly. The published output is **static files only**
(HTML, JS, `.wasm`, DLLs as webcil): no server-side code, no API, no database. Any static host works.

## Run locally

```bash
dotnet run --project apps/web          # dev server, prints the URL
```

## Publish and host

For a standalone game with exactly one story and relative URLs:
`talewright publish story-folder -a --target web --output game.zip`.
See the [CLI reference](../../docs/reference/cli.md#publish). Development hosts retain samples by
default; `TalewrightIncludeSamples=false` disables them in custom builds.

```bash
dotnet publish apps/web -c Release -o out/web
# then serve out/web/wwwroot with anything static, e.g.
python3 -m http.server 8080 --directory out/web/wwwroot
```

- **Subfolder hosting** (GitHub Pages `/<repo>/`, itch.io): change `<base href="/" />` in `wwwroot/index.html`
  to the folder, with a trailing slash. There is no router, so no rewrite/404 tricks are needed.
- Hosts that don't serve pre-compressed `.br`/`.gz` files still work (the uncompressed files are there too).

### GitHub Pages

Every `main` push that passes Build publishes this host, with all bundled samples, to
GitHub Pages at the root of the custom domain, <https://play.trytalewright.com/>. The workflow
publishes with base path `/`, smoke-tests the result at the root and uploads only `wwwroot`. See
[releasing and CI](../../docs/developer/releasing.md) for repository settings.

The publish script also supports subpath hosting. Reproduce one locally, then serve it under
`/talewright/`:

```bash
python3 .github/scripts/publish_samples.py --base-path /talewright --output artifacts/pages
```

The [deployment-path test](../../tests/README.md#ci-and-deployment-checks) covers this in CI.

## Files

| File | Contents |
|---|---|
| `Program.cs` | Registers `EmbeddedStoryLibrary` and mounts `App` on `#app`. |
| `App.razor` | `<TalewrightApp />`. Deliberately no `Router`. |
| `wwwroot/index.html` | Shell page with loading indicator and Blazor's error banner; links the Player stylesheet. |
| `wwwroot/css/app.css` | Only the loading indicator and error banner; everything else is in the Player RCL. |
| `Talewright.Host.Web.csproj` | Blazor WASM SDK; embeds `samples/**` like the desktop host. |

Players can open their own stories with the "Open a story file" button (a `.zip` of a story folder).
Manual saves use IndexedDB scoped to the current origin. Storage/quota/read/write failures are displayed
in the player; no localStorage fallback or automatic overwrite is used. Clearing site data removes saves.

## Regression tests

`python3 player/tests/run_e2e.py web` publishes this host in Release, serves the resulting static files on a
suite-owned loopback port, and exercises it in Chromium, Firefox, and WebKit. It does not replace the host
with a test app. See [testing guidance](../../tests/README.md) for browser installation and targeted runs.
