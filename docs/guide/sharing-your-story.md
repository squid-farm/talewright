# Sharing your story

The simplest way to share a story is to zip its folder and send the ZIP to your players. They open
it in the [web player](https://play.trytalewright.com/) with **Open a story file**. See the
[ZIP instructions](first-story.md#no-desktop-tools-use-the-web-player) in the first-story tutorial.

You can also make a story file with the CLI, or turn your story into its own website or desktop game.
In VS Code, run **Talewright: Publish Story ZIP** to make a story file (run **Talewright: Set Up CLI**
first if asked). Without VS Code, use the [story tools](cli-installation.md).

The commands below are written as `talewright`. If you use the downloaded tools instead, type
`.\Talewright.Cli.exe` on Windows or `./Talewright.Cli` on Mac/Linux from the tools folder, and put
quotation marks around paths that contain spaces.

Every `publish` checks your story first and stops if there are errors.

## Choose a format

| You want to… | Publish | Players need | You need |
|---|---|---|---|
| Send the story to someone with a Talewright player | A story file | The [web player](https://play.trytalewright.com/) or any Talewright player | Nothing extra |
| Put a playable game on the web or itch.io | A web game | A web browser | The Talewright CLI and an internet connection the first time you publish |
| Give players a downloadable app | A desktop game | Nothing on macOS. WebView2 on Windows (built into Windows 11 and up-to-date Windows 10). WebKitGTK 4.1 and libnotify on Linux. | The Talewright CLI and an internet connection the first time you publish |

Publishing downloads the matching runner for your CLI version and caches it for later. The CLI
download can publish standalone games without the .NET SDK.

Talewright doesn't vouch for stories or games made with it. See
[distribution and runtime safety](../reference/safety.md) for what players and authors should know.

## A story file

```bash
talewright publish my-story --output my-story.zip
```

The ZIP contains `story.talewright.yaml`, your `.tale` files and the supported images from
`images/`, keeping their folders. Other files are left out. Anyone can open it with
**Open a story file** in the web player. See [adding images](writing-stories.md#images).

## A web game

```bash
talewright publish my-story -a --target web --output my-game-web.zip
```

`-a` (short for `--all-in-one`) bundles the player with your story. The game opens straight into
your story, with no library screen or samples. Keep everything from the ZIP together, including
its `story/` folder. To try it yourself, use a web server rather than double-clicking the HTML file.

- **itch.io:** create a project, set *Kind of project* to **HTML**, upload the ZIP and tick
  *This file will be played in the browser*.
- **Your own site:** extract the ZIP anywhere that serves static files, such as GitHub Pages,
  Netlify, or a folder on your web server. A subfolder works too.

### Artwork and hosting size

- Use PNG for graphics that need sharp detail or transparency, and JPEG or WebP for photos and
  painted artwork.
- Resize images to the size they'll be shown at, rather than using camera originals.
- Give each image a useful description (alt text). Mark images that add no information
  `#decorative` instead.

Web games download each picture as the reader scrolls to it, not all at once. Large pictures
still mean longer downloads, so test image-heavy stories on the phones and computers your readers
will use.

itch.io currently allows **200 MB per file, 500 MB total, and 1,000 files** after unzipping; check
[itch.io's requirements](https://itch.io/docs/creators/html5) before uploading.
`talewright validate my-story` estimates your story's size, and publishing a web game checks the
size of the whole game. Both warn if it's over those limits, but still let you publish. If you get
a warning, make your images smaller and publish again, or offer a desktop game instead.

To create both web and desktop packages in one pass, use the `all` target:

```bash
talewright publish my-story -a --target all --output publish/
```

This validates the story once and writes `publish/web/my-story-web.zip` and
`publish/desktop/my-story-desktop.zip`. Add `--split` to put one desktop ZIP per supported computer
type in `publish/desktop/` instead of the universal desktop ZIP. The web ZIP is still created.
When `game.version` is declared in `story.talewright.yaml`, published archive names include it, for
example `publish/web/my-story-v1.2.3-web.zip` and `publish/desktop/my-story-v1.2.3-win-x64.zip`.
If the version is omitted or empty, Talewright keeps the unversioned names.

## A desktop game

```bash
talewright publish my-story -a --target desktop --output my-game.zip
```

This makes one ZIP that runs on Windows, macOS and Linux. Players extract **the whole ZIP**, open
the story-named folder it creates, and run:

| System | Run |
|---|---|
| Windows | The story-named `.cmd` file, such as `My Story.cmd` |
| macOS | The story-named `.command` file, such as `My Story.command` |
| Linux | The story-named `.sh` file, such as `My Story.sh` (or `sh "My Story.sh"`) |

Players don't need to install .NET; the game includes it. Linux players do need WebKitGTK 4.1 and
libnotify, so share the [Linux desktop setup commands](getting-started.md#linux-desktop-setup) with
them. A web game avoids this.

Runtime libraries and other implementation files are kept in an `internal/` folder inside the game
folder. Keep the extracted files together.

Desktop publications pack the manifest and story text into `story.storypack`, so extracted
packages do not expose `.tale` or `.talewright.yaml` story text as loose files. Images remain
external in the package's `story/images/` folder. Universal packages share one pack across runtimes.

That ZIP is large because it contains versions for six kinds of computer. To make smaller,
separate downloads instead, use `--split` with a folder for `--output`:

```bash
talewright publish my-story -a --target desktop --split --output desktop-builds/
```

This makes one ZIP per kind of computer, such as `my-story-linux-x64.zip` and
`my-story-win-arm64.zip`. Upload them as separate downloads and label each one. In these ZIPs,
players run the story-named `.sh`, `.command` or `.cmd` launcher inside the game folder. Supporting
runtime and story files stay in `internal/`. To build just one, use `--runtime` with
an ID such as `win-x64`; see the [CLI reference](../reference/cli.md#publish).

**Good to know:** desktop games aren't installers and aren't signed, so Windows and macOS may warn
players the first time they open one (see [Troubleshooting](troubleshooting.md#a-published-desktop-game-wont-start)).
They aren't macOS `.app` bundles. Only the Linux x64 version has been tested so far.

## Republishing

Run the same command again. If the output file already exists, `publish` asks
`Replace it? [y/N]`. Type `y` to replace it, or press Enter to keep the old file. If the build
fails, the old file is kept.

If you publish from a script that can't answer questions, add `--force` to replace the file
without asking. See the [CLI reference](../reference/cli.md#publish) for all options.

### Your players' saves

Changing your story can stop your players' existing saves from loading. Small changes like
rewording prose are usually fine; changing starting values or the outcome of choices players have
already made is not. **Keep a copy of each version you release**, so players can reopen their
saves. See [updating a story with existing saves](playing-and-saving.md#updating-a-story-with-existing-saves).

Updating Talewright itself can also matter during the preview: saves from one preview may not load
in the next. Starting with 1.0.0-preview.1, saves will keep loading in later 1.x players.
