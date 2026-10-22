# CLI reference

The `talewright` command checks, debugs and publishes stories. See
[Getting started](../guide/getting-started.md#install-the-desktop-tools) to install it. Until
ready-made downloads arrive, use `dotnet run --project src/Talewright.Cli --` in place of `talewright`.

| Command | Does |
|---|---|
| `talewright validate <story>` | Checks a story folder for errors and warnings. |
| `talewright debug <story>` | Opens the Author Debugger. |
| `talewright publish <story>` | Builds a story zip, web game or desktop game. |

`talewright --help` and `talewright <command> --help` list every option. Unknown options are rejected.

## validate

```bash
talewright validate path/to/story
talewright validate path/to/story --format json --warnings-as-errors
```

Runs the same compiler as the players on a folder containing `story.yaml` and `.tale` files. Validation
proves the story is well formed; it doesn't play every possible path.

| Option | Effect |
|---|---|
| `--warnings-as-errors` | Fail if there are any warnings. Warnings pass by default. |
| `--format json` | Machine-readable output (below). |

### Output contract

| Exit code | Meaning |
|---|---|
| 0 | Story accepted (or help shown). |
| 1 | Compiler errors, or warnings when `--warnings-as-errors` is set. |
| 2 | Invalid command, or the input couldn't be read. |

Human output prints `file:line:column: severity CODE: message` per diagnostic, then a final status.
Operational errors go to stderr.

`--format json` writes exactly one JSON object to stdout, with no progress or help text:

```json
{"schemaVersion":1,"success":true,"diagnostics":[]}
```

- Each diagnostic has `code`, `severity` (`error` or `warning`), `message`, `file` (relative to the story),
  `line` and `column`. Positions are one-based; `0` means unspecified. `--warnings-as-errors` doesn't
  change reported severities.
- Operational failures (exit 2) return `success: false`, empty `diagnostics` and
  `error: {"code": "TWCLI001" | "TWCLI002", "message": "..."}`: `TWCLI001` for invocation errors,
  `TWCLI002` for input errors.
- A readable folder with no `story.yaml` is a compiler diagnostic (`TW4002`), not an operational error.

## debug

```bash
talewright debug path/to/story
```

Opens the native Author Debugger and waits for it to close. For a walkthrough, see
[Testing your story](../guide/testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger).

**Requirements.** A graphical desktop and the OS webview: WebKitGTK 4.1 on Linux
(`libwebkit2gtk-4.1-0`), WebView2 on Windows, WKWebView on macOS. There's no browser fallback. Linux x64
is runtime-tested; other platforms need validation. Missing folders, assets or prerequisites, and native
launch failures, exit 2 with `TWCLI002`. Compiler errors appear in the window so you can fix and save
without closing it.

**Starting a scene.** Search by id or title (or type an exact id), optionally choose a seed and create
the character, enable only the overrides you need (flags, variables, base stats, absolute item counts,
faction standing), then start. Normal rules still apply: stats are clamped and entry effects and
triggers run. Each start is a fresh synthetic history, so skipped scenes are unvisited and reachability
isn't proven. Setup and pre-entry initialization events are listed separately. Player saves are never
read or written.

**Inspection.** Shows actual state, events and historical branch decisions. Explanations record how
conditions evaluated at the time, not predictions against the current state.

**Reloading `.tale` changes.** Saves are debounced and compiled as one complete source set. Automatic
reload rebuilds the state just before the current scene was entered (declarations, rules, earlier
scenes and choice meanings, events, RNG position, pending notes, trigger memory), then enters the current
scene once from there. Its entry effects and check outcomes may legitimately change. The passage is
replaced, explanations are rebuilt, and a reload marker is shown. Only the current passage is displayed,
not a reconstructed transcript.

The guard is deliberately conservative. Restart is required after:

- any structural change to a scene already passed through, even its prose;
- changed Tale auto-declarations, missing targets, or a run that ended without reaching a new scene;
- **any `story.yaml` change**, even comments (later Tale saves can't bypass this);
- a file-watcher error, since missed events can't prove `story.yaml` didn't change.

Errors keep the last good run and mark the source as stale.

**Restart from source** compiles the latest source and revalidates the setup, seed, target and character,
discarding the run only on success. Changed or removed ids are reported, never silently dropped: fix or
disable the overrides (or clear the character), then restart. **Rescan / retry watcher** retries
compilation and rearms a failed watcher without bypassing these guards. Closing the window releases
the watcher and pending work.

**Not yet supported:** live state editing, jumping while preserving state, save editing and portable
scenarios ([#63](https://github.com/squid-farm/talewright/issues/63),
[#64](https://github.com/squid-farm/talewright/issues/64)). C# extensions aren't hot-reloaded; rebuild and
restart. Debugger assets ship in the CLI tool, not in Player packages or published games.

## publish

```bash
talewright publish path/to/story --output story.zip
talewright publish path/to/story -a --target web --output game-web.zip
talewright publish path/to/story -a --target desktop --output game-desktop.zip
talewright publish path/to/story -a --target desktop --runtime win-x64 --output game-windows.zip
```

Validates first, then builds. For a walkthrough, see [Sharing your story](../guide/sharing-your-story.md).

| Option | Effect |
|---|---|
| `-o`, `--output <file>` | Output zip. |
| `-a`, `--all-in-one` | Include the player and engine, opening only this story (no samples). Without it, the zip holds just `story.yaml` and `.tale` files in their folder layout. `--a` is not accepted. |
| `--target web` | Default. A static site with a root `index.html` and relative URLs. |
| `--target desktop` | A self-contained desktop app. |
| `--runtime <rid>` | Desktop only: a single platform instead of all six. `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-x64` or `osx-arm64`. |
| `--force` | Overwrite an existing output file. |

**Web output** runs on any static HTTP(S) host, including beneath a subpath, and can be uploaded to
itch.io as an HTML5 game. Players need no backend or .NET.

**Desktop output** is one universal zip with self-contained .NET for Windows, Linux and macOS on x64 and
ARM64. Extract everything, then run `start.cmd` (Windows), `start.sh` (Linux) or `start.command` (macOS);
the launcher picks the right platform. If extraction dropped execute permissions, use `sh start.sh`. A
single-runtime zip is smaller; run `Talewright.Host.Desktop` (`.exe` on Windows). Photino supplies the
native binaries, so any platform can be built from any machine. Linux x64 is runtime-verified; Windows,
macOS and ARM64 builds are unverified. Players need the OS webview (WebKitGTK 4.1, WebView2 or
WKWebView). Output isn't an installer, signed, notarized or a macOS `.app` bundle.

**Author requirements.** A .NET 10 SDK and package restore access. Installed tools carry their
templates, binaries, UI assets and the engine license, so no source checkout is needed. The generated
host uses precompiled Player binaries and copies their JS modules as static assets.

**Failures.** Compiler or build errors exit 1. Invalid invocation, input, output or missing tools exit 2.
A failed build leaves any existing output unchanged and never writes a partial zip. `publish` prints
human-readable diagnostics only (`--format json` is for `validate`).

**Limits.**

| Input | Limit |
|---|---|
| Story files | 1,000 files, 5 MiB each, 50 MiB total, folder depth 64 |
| Symbolic links inside the story | Rejected |
| Output (before compression) | 20,000 files and 512 MiB (1 GiB for the universal desktop zip) |

Files are rechecked after UTF-8 staging, because UTF-16 input can grow when rewritten. Only
`story.yaml` and `.tale` files are included. Media support will extend this deliberately; other files
are never bundled automatically.
