# Talewright.Host.Web — browser app (Blazor WebAssembly)

The same Player UI running entirely in the browser on WebAssembly. The published output is **static files only**
(HTML, JS, `.wasm`, DLLs as webcil): no server-side code, no API, no database. Any static host works.

## Run locally

```bash
dotnet run --project src/Talewright.Host.Web          # dev server, prints the URL
```

## Publish and host

For a standalone game with exactly one story and relative URLs:
`talewright publish story-folder -a --target web --output game.zip`.
See the [CLI reference](../Talewright.Cli/README.md#publishing). Development hosts retain samples by
default; `TalewrightIncludeSamples=false` disables them in custom builds.

```bash
dotnet publish src/Talewright.Host.Web -c Release -o out/web
# then serve out/web/wwwroot with anything static, e.g.
python3 -m http.server 8080 --directory out/web/wwwroot
```

- **Subfolder hosting** (GitHub Pages `/<repo>/`, itch.io): change `<base href="/" />` in `wwwroot/index.html`
  to the folder, with a trailing slash. There is no router, so no rewrite/404 tricks are needed.
- Hosts that don't serve pre-compressed `.br`/`.gz` files still work (the uncompressed files are there too).
  itch.io specifics are an M2 item.

### GitHub Pages

After Build and all test jobs pass on a `main` push, Publish Samples publishes this
host with every bundled sample. Select **GitHub Actions** as the repository's Pages
source and permit `main` in the `github-pages` environment.

The workflow obtains the base path from `actions/configure-pages`, applies it
before publishing (including generated/compressed output), and restores the source
shell afterward. Local root hosting is unchanged. It smoke-tests the actual publish
at that path before uploading only `wwwroot` to Pages. No branch full of generated
site files or 404 rewrite is needed.

To reproduce a repository-subpath publish locally:

```bash
python3 .github/scripts/publish_samples.py --base-path /talewright --output artifacts/pages
```

Serve that output under `/talewright/`, not `/`. See the [deployment-path test](../../tests/README.md#ci-and-deployment-checks)
for the suite-owned subpath server.

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

`python3 tests/run_e2e.py web` publishes this host in Release, serves the resulting static files on a
suite-owned loopback port, and exercises it in Chromium, Firefox, and WebKit. It does not replace the host
with a test app. See [testing guidance](../../tests/README.md) for browser installation and targeted runs.
