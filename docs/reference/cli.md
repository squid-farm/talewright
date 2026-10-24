# CLI reference

The `talewright` command validates, lints, debugs and publishes stories. See
[Getting started](../guide/getting-started.md#install-the-desktop-tools) to install it. Until
ready-made downloads arrive, use `dotnet run --project tooling/cli/src/Talewright.Cli --` in place of `talewright`.

| Command | Does |
|---|---|
| `talewright validate <story>` | Checks a story folder for errors and warnings. |
| `talewright lint <story>` | Checks compiler diagnostics and configurable Talemark-aware rules. |
| `talewright debug <story>` | Opens the Author Debugger. |
| `talewright publish <story>` | Builds a story zip, web game or desktop game. |

`talewright --help` and `talewright <command> --help` list every option. Unknown options are rejected.

## Options shared by all commands

Put `--non-interactive` after the command name:

```bash
talewright validate path/to/story --non-interactive --format json
talewright publish path/to/story --output story.zip --non-interactive --force
```

`--non-interactive` guarantees that the CLI never asks a question, even if a script runner provides a
terminal. It does **not** imply `--force`, skip validation, or change diagnostics, logging or JSON.
There is no `--ci` alias. `validate` and `debug` do not ask CLI questions today; they accept the option
for the same contract. `debug` still opens its graphical workbench; this option does not change
confirmation dialogs inside that window.

## validate

```bash
talewright validate path/to/story
talewright validate path/to/story --format json --warnings-as-errors
```

Runs the same compiler as the players on a folder containing `story.talewright.yaml` and `.tale` files. Validation
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
  `error: {"code": "TWCLI001" | "TWCLI002" | "TWCLI003", "message": "..."}`: `TWCLI001` for invocation errors,
  `TWCLI002` for input errors, and `TWCLI003` for invalid lint configuration.
- A readable folder with no `story.talewright.yaml` is a compiler diagnostic (`TW4002`), not an operational error.

## lint

```bash
talewright lint path/to/story
talewright lint path/to/story --format json --warnings-as-errors
talewright lint path/to/story --rule TWL001=error
```

`lint` includes compiler diagnostics once, then applies shared Talemark-aware rules. Compiler diagnostics
cannot be suppressed by lint configuration. Lint findings use stable `TWL` codes and source ranges;
their JSON diagnostics include exclusive `endLine` and `endColumn` positions.

The optional `talewright.lint.json` file lives at the story root, beside the canonical manifest:

```json
{
  "rules": {
    "TWL001": "warning",
    "TWL002": "warning"
  },
  "suppressions": {
    "chapters/draft.tale": ["TWL001"]
  }
}
```

Rule severities are `off`, `warning` or `error`. Both initial rules default to `warning`; config values
override those defaults. A suppression hides only the listed lint rule in that relative file. Paths use
forward slashes and cannot contain `.` or `..` path segments. Repeat `--rule ID=SEVERITY` to override
configuration for individual rules; command-line values take precedence. `--warnings-as-errors` uses the
same policy as `validate`, and warnings otherwise pass by default.

| Option | Effect |
|---|---|
| `--rule <ID=SEVERITY>` | Override one rule's severity (`off`, `warning`, or `error`); may be repeated. |
| `--warnings-as-errors` | Fail if any compiler or lint warning is reported. |
| `--format json` | Machine-readable diagnostics, including ranges for lint findings. |

Invalid configuration, including unknown rule IDs or malformed suppression paths, is an explicit
operational error (`TWCLI003`, exit 2).

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

**Workbench behavior.** See the testing guide for [scene setup and inspection](../guide/testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger),
[live edits, jumps and navigation](../guide/testing-your-story.md#change-state-without-starting-over),
and [source reload/restart](../guide/testing-your-story.md#editing-while-you-test).
For API and reconstruction invariants, see the [runtime documentation](../../engine/src/Talewright.Runtime/README.md#live-editing-and-author-navigation).

**Distribution.** Debugger assets ship in the CLI tool, not in Player packages or published games.
See the testing guide for [portable scenarios and read-only player-save forks](../guide/testing-your-story.md#share-and-restore-a-debugging-scenario)
and a [hands-on test plan](../guide/debugger-test-plan.md). Author scenarios never become player saves.

## publish

```bash
talewright publish path/to/story --output story.zip
talewright publish path/to/story -a --target web --output game-web.zip
talewright publish path/to/story -a --target desktop --output game-desktop.zip
talewright publish path/to/story -a --target desktop --runtime win-x64 --output game-windows.zip
talewright publish path/to/story -a --target desktop --split --output desktop-builds/
```

Validates first, then builds. For a walkthrough, see [Sharing your story](../guide/sharing-your-story.md).

| Option | Effect |
|---|---|
| `-o`, `--output <path>` | Output ZIP, or output directory with `--split`. |
| `-a`, `--all-in-one` | Include the player and engine, opening only this story (no samples). Without it, the zip holds just `story.talewright.yaml` and `.tale` files in their folder layout. `--a` is not accepted. |
| `--target web` | Default. A static site with a root `index.html` and relative URLs. |
| `--target desktop` | A self-contained desktop app. |
| `--runtime <rid>` | Desktop only: a single platform instead of all six. `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-x64` or `osx-arm64`. |
| `--split` | With `-a --target desktop`, write one self-contained ZIP per supported runtime into the `--output` directory. Cannot be combined with `--runtime`; it is not supported for story-only or web output. Filenames are `<story-id>-<rid>.zip`. |
| `--force` | Authorize replacement without asking, independently of `--non-interactive`. |

### Split desktop output

```bash
talewright publish path/to/story -a --target desktop --split --output desktop-builds/
```

This builds each of the six supported RIDs once and writes six ZIPs, such as
`desktop-builds/my-story-linux-x64.zip` and `desktop-builds/my-story-osx-arm64.zip`.
Each contains only its runtime-specific self-contained apphost and files, the published story,
the engine license and deployment instructions. The universal `start.cmd`, `start.sh` and
`start.command` launchers are not included. PDB inclusion is unchanged. These are unsigned
standalone ZIPs, not installers or macOS app bundles.

If any output already exists, an interactive terminal lists the existing paths and asks once
whether to replace the set. Declining preserves every existing file and exits 2. In non-interactive
or redirected use, an existing output requires `--force`; `--force` authorizes replacing the full
set. The output directory is created as needed. Build and archive all six outputs to temporary files
before replacing any destination, so a build/archive failure leaves the previous set intact.

The six final file replacements are not one filesystem transaction. A machine crash or filesystem
failure during that final replacement phase can expose a partial set; ordinary replacement errors
trigger best-effort rollback, retaining recovery backups if rollback itself fails. Concurrent
publishers targeting the same directory are not locked out. Do not rely on cross-file atomicity or
crash durability. Each ZIP independently retains the published-output limits.

### Replacing an existing archive

For a new output path, publication never asks a question. For an existing file, an interactive
terminal shows the full destination path and asks `Replace it? [y/N]` **before** compiling or building.
Type `y` or `yes` to replace it. No, Enter, end-of-input or any other answer cancels publication,
leaves the existing file alone, and exits 2. The question is written to stderr, not stdout.

With `--non-interactive`, redirected stdin, stdout **or** stderr, there is no question and no
confirmation input is read. An existing destination fails with guidance to choose another `--output`
path or add `--force`. Piping `yes` into publish is not approval.

For an intentional scripted update:

```bash
talewright publish path/to/story --output story.zip --non-interactive --force
```

`--force` also works on its own in an interactive terminal without asking. It never bypasses story
validation or build errors.

**Overlapping publications.** Without force, a file that appears while publishing is not overwritten.
For an approved existing file, publish compares its content fingerprint, size, timestamps and link
target again just before replacement. If it has observably changed or disappeared, publish fails
instead of using the earlier approval. Retry to approve the current file, choose a new path, or
explicitly use `--force`. Force permits replacement even if the destination appears or changes.

The completed ZIP is staged beside the destination and moved into place; the original is not deleted
first. The check and replacement are separate operations, **not** a filesystem transaction or a
lock against other programs. A change in that final gap, or a replacement indistinguishable by the
checked content and metadata, may not be detected. Do not run concurrent publishers against the same
path when that distinction matters. No universal atomicity or crash-durability guarantee is made
across operating systems and filesystems.

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
`story.talewright.yaml` and `.tale` files are included. Media support will extend this deliberately; other files
are never bundled automatically.
