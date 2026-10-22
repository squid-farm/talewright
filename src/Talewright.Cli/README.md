# Talewright CLI

Requires the .NET 10 SDK. Run from source with
`dotnet run --project src/Talewright.Cli -- validate samples/LostRelic`.
The project packs as a .NET tool with command name `talewright`.
Spectre.Console.Cli supplies typed command/option parsing, strict unknown-option
rejection and generated help (`--help`, `validate --help`, `publish --help`).
The validation JSON schema, diagnostics and exit codes below remain independent
of the CLI framework; JSON output never contains its help or console formatting.

```bash
talewright validate path/to/story
talewright debug path/to/story
talewright validate path/to/story --format json --warnings-as-errors
talewright publish path/to/story --output story.zip
talewright publish path/to/story -a --target web --output game-web.zip
talewright publish path/to/story --all-in-one --target desktop --output game-desktop.zip
talewright publish path/to/story -a --target desktop --runtime win-x64 --output game-windows.zip
```

Validation uses the same compiler as the player. It checks a directory containing
`story.yaml` and `.tale` files. Static validation does **not** prove every possible
playthrough succeeds. Warnings succeed unless `--warnings-as-errors` is supplied.

## Output contract

- Exit **0**: accepted validation (or help).
- Exit **1**: compiler errors, or warnings rejected by the selected policy.
- Exit **2**: invalid invocation or input/I/O failure.

Human output uses the compiler's `file:line:column: severity CODE: message`
diagnostics and a final status. Operational errors go to stderr.

`--format json` writes one JSON object to stdout, with no progress messages:

```json
{"schemaVersion":1,"success":true,"diagnostics":[]}
```

Every diagnostic has `code`, `severity` (`error` or `warning`), `message`, `file`
(story-relative path), `line`, and `column`. Source positions are one-based;
zero denotes an unspecified position. Compiler severities are not rewritten by
warnings-as-errors.

Operational failures return exit 2 and include `success:false`, empty
`diagnostics`, and `error:{"code":"TWCLI001","message":"..."}` for invocation
errors or `TWCLI002` for input errors. A missing manifest in an otherwise readable
directory is a compiler diagnostic, not an operational error.

## Author debugging

`debug <story-root>` launches a separate native Author Debugger window and waits
for it to close. It requires a graphical desktop and the OS webview: WebKitGTK 4.1
on Linux (`libwebkit2gtk-4.1-0`), WebView2 on Windows, or macOS WKWebView. There is
no browser/server fallback. Linux x64 is runtime-tested; other native targets
still need platform validation. Missing folders/assets/prerequisites and native
launch failures return exit 2 with `TWCLI002`. Compiler errors appear in the
window so the author can repair the source and explicitly reload.

Search scenes by ID/title or enter an exact ID, optionally choose a seed and
create the player character, then start the selected scene. Enable only the
prerequisite overrides you need: flags, variables, character base stats and
absolute item counts, or faction standings. Normal rules clamp stats and run
entry effects/triggers. Inspect actual state, events and historical branch
decisions; these are recorded evaluations, not predictions against current state.

Every start is a fresh synthetic history: skipped scenes remain unvisited and
reachability is not proven. Setup and pre-entry initialization events are shown
separately. Player saves are neither read nor written. Reload is manual and
discards setup/run only after successful compilation; rejected source leaves
the previous compiled story and run visible. There is no watcher, live state
editing, preserve-state jump, save editor, or portable scenario support yet.
Debugger assets ship in the CLI tool, not Player packages or published games.

## Publishing

`publish` validates before creating an output artifact. By default it ZIPs only
`story.yaml` and `.tale` files, retaining their relative layout; private notes,
engine binaries and unrelated files are not included.

`-a` / `--all-in-one` includes the player and engine, automatically opening exactly
the chosen story with no bundled samples. `--target web` (default) creates a full
static site with root `index.html` and relative URLs: extract onto a static HTTP(S)
host, or upload the ZIP as an itch.io HTML5 game. No backend or .NET installation
is needed on the player's machine.

`--target desktop` creates **one universal self-contained ZIP**, including .NET
for Windows, Linux and macOS on x64 and ARM64. Extract the entire archive, then
run `start.cmd` on Windows, `start.sh` on Linux or `start.command` on macOS.
The launchers select the matching platform directory. If extraction dropped
executable permissions, use `sh start.sh` on Linux/macOS.

For a smaller, single-platform ZIP, `--runtime` explicitly selects `linux-x64`,
`linux-arm64`, `win-x64`, `win-arm64`, `osx-x64` or `osx-arm64`. Photino supplies the
native binaries, so ordinary publishing can cross-target from the current SDK.
Linux x64 is runtime-verified here. Windows builds can be tested separately;
macOS/ARM64 builds are unverified, not implicitly supported by Linux test results.
Desktop ZIPs are not installers, signed applications or macOS `.app` bundles.
The OS webview is still required: Linux WebKitGTK 4.1, Windows WebView2, or built-in
macOS WKWebView. In a single-runtime archive, run `Talewright.Host.Desktop`
(`.exe` on Windows) after extracting.

Publishing requires a .NET 10 SDK and package restore access on the **author's**
machine. Installed tools include their publishing templates, binaries, UI assets
and engine license; no source checkout is needed. The generated host uses
precompiled Player binaries and copies their JS modules as static assets, rather
than trying to associate them with Razor components in the generated host.

Existing output requires explicit `--force`. Compiler/build errors return 1;
invalid invocation, input, output or missing-tool failures return 2. Failed builds
leave an existing final archive unchanged and do not create a partial final ZIP.
Publishing uses human-readable diagnostics; `--format json` belongs to `validate`.
`--a` is invalid, not an alias.

Inputs are bounded to 1,000 story files, 5 MiB each and 50 MiB total text.
Staged UTF-8 files are rechecked against the same limits before either archive
or standalone publication, since BOM-detected UTF-16 input can expand on rewrite.
Folder depth is limited to 64 and symbolic links inside a story are rejected.
Outputs are bounded to 20,000 files and 512 MiB before compression (1 GiB for the
six-runtime universal desktop ZIP). Later media
support will extend the story payload deliberately; arbitrary adjacent files are
not automatically bundled.
