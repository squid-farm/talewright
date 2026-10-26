# Sharing your story

> These commands need the [desktop tools](getting-started.md#install-the-desktop-tools).
> Until ready-made downloads arrive, write `dotnet run --project tooling/cli/src/Talewright.Cli --` instead of `talewright`.

Every `publish` checks your story first and stops if there are errors.

## Choose a format

| You want to… | Publish | Players need |
|---|---|---|
| Send the story to someone who already has a Talewright player | A story file | The [web player](https://squid-farm.github.io/talewright/) or any Talewright player |
| Put a playable game on the web or itch.io | A web game | A web browser |
| Give players a downloadable app | A desktop game | Nothing on macOS. WebView2 on Windows (built into Windows 11 and up-to-date Windows 10). WebKitGTK 4.1 on Linux. |

## A story file

```bash
talewright publish my-story --output my-story.zip
```

The zip contains `story.talewright.yaml`, your `.tale` files and supported image files
from `images/`, preserving their folders. Private notes and unsupported image formats
are left out. Anyone can open it with **Open a story file** in the web player.
Images display lazily when opened in the desktop or web player, including ZIP uploads.

## A web game

```bash
talewright publish my-story -a --target web --output my-game-web.zip
```

`-a` (short for `--all-in-one`) bundles the player with your story. The game opens straight into your
story, with no library screen, Library navigation, or samples. This also applies
during character creation; save/load, history, and Restart remain available during play.

- **itch.io:** create a project, set *Kind of project* to **HTML**, upload the zip and tick
  *This file will be played in the browser*.
- **Your own site:** extract the zip anywhere that serves static files: GitHub Pages, Netlify, a
  folder on your web server. It also works from a subfolder.

### Artwork and hosting size

Use PNG for graphics that need lossless detail or transparency, and JPEG or WebP for photographs
and artwork where smaller downloads matter. Resize images for their intended display rather
than shipping camera originals. Images fit narrow windows without stretching their aspect ratio;
provide descriptive alt text for readers and for visible failure states.

Lazy display delays image stream/Blob creation until an occurrence nears the viewport. It does
not make image bytes free: downloads, retained history, Blob data and browser-decoded bitmaps
consume resources. Repeated occurrences share transferred data while mounted, but distinct
images can still accumulate in history.

`validate` uses a source-only size estimate: UTF-8 source text and supported images, excluding the
generated runtime and precompressed copies. `publish -a --target web` measures the **final
extracted payload**, including those runtime and compression files. Both use one advisory check
against recorded [itch.io HTML5 guidance](https://itch.io/docs/creators/html5):
200 MB per extracted file, 500 MB total, and 1,000 files. Talewright compares MB conservatively
as decimal bytes. Hosts can change their requirements; other restrictions such as filename
length also apply. A warning never changes validation success or prevents publication, including
with `validate --warnings-as-errors` (which applies to compiler warnings).

**Current embedded-artwork caveat:** generated games still embed story assets in the program.
Large artwork collections can therefore push one generated file over the host's per-file limit,
even when each original image is smaller. Lazy image display does not mean a lazy download of
that embedded program. [#271](https://github.com/squid-farm/talewright/issues/271) moves story assets
beside the host; it is a follow-up, not shipped here.
Choose downloadable desktop distribution when browser hosting or resource use is unsuitable.

## A desktop game

```bash
talewright publish my-story -a --target desktop --output my-game.zip
```

This makes one zip that runs on Windows, macOS and Linux (Intel and ARM). Players extract **the whole
zip** and run:

| System | Run |
|---|---|
| Windows | `start.cmd` |
| macOS | `start.command` |
| Linux | `start.sh` (or `sh start.sh`) |

Like a web game, the published app stays in your story and does not expose Library
navigation. The general-purpose desktop player still has a library by default;
its optional `--no-library story-folder-or-zip` launch mode is described in the
[desktop host guide](../../apps/desktop/README.md#run).

The universal zip is large because it includes everything for six platforms. To build for one
platform only, add `--runtime` with one of `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64` or
`linux-arm64`. Players then run `Talewright.Host.Desktop` (`.exe` on Windows).

To publish all six platforms as separate files, use `--split` and give `--output` a directory:

```bash
talewright publish my-story -a --target desktop --split --output desktop-builds/
```

This produces one self-contained ZIP per runtime, for example `my-story-linux-x64.zip` and
`my-story-win-arm64.zip`. Each has only that runtime's app and does not contain the universal
`start.*` launchers. Upload the ZIPs to your release or itch.io download page as separate
platform downloads, and label each with its runtime; do not describe the macOS ZIP as signed or
as an app bundle. The command builds each runtime once and stages the complete set before replacing
existing files. If any target exists, interactive use asks once before the build; scripts must pass
`--force` to replace existing ZIPs. Build and archive failures leave previous ZIPs untouched.
The final six replacements are separate filesystem operations, not a crash-atomic transaction.

**Current limits:** desktop games aren't installers, aren't code-signed (Windows and macOS may warn
players the first time) and aren't macOS `.app` bundles. So far only Linux x64 builds have been
tested; Windows, macOS and ARM builds are unverified.

## Republishing

Run the same command again:

```bash
talewright publish my-story --output my-story.zip
```

In an interactive terminal, publish shows the full existing file path and asks `Replace it? [y/N]`
before doing the build. Type `y` or `yes` to replace it. Press Enter or answer `n` to keep the old file
and cancel (exit 2). End-of-input also cancels. A failed build or ZIP write keeps the old file.

For a script that must never ask questions:

```bash
talewright publish my-story --output my-story.zip --non-interactive
```

This succeeds for a new path but stops if the ZIP already exists. If you intentionally want a script
to replace it, add `--force`:

```bash
talewright publish my-story --output my-story.zip --non-interactive --force
```

`--force` means "replace without asking"; `--non-interactive` only means "never ask." Neither skips
validation. Redirecting input or output also prevents questions, so those commands need `--force`
to replace an existing file.

Avoid publishing two builds to the same path at once. Without force, publish stops if another file
appears or an approved file observably changes during the build. This is a safety check, not a lock
against every possible filesystem race; see the [replacement policy](../reference/cli.md#replacing-an-existing-archive).

If you publish an update, existing saves won't load in the new version
(see [saves](playing-and-saving.md#updating-a-story-with-existing-saves)).

All options and limits: [CLI reference: publish](../reference/cli.md#publish).
