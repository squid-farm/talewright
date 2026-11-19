# Troubleshooting

## My story won't open

The player lists each problem with its file and line. Fix the **first** error and try again: one mistake
can cause several messages. Look up any code in [error codes](../reference/diagnostics.md).

If the list says the story has no `story.talewright.yaml`, make sure the zip contains `story.talewright.yaml`, either at the
top level or inside a single folder.

## VS Code doesn't underline mistakes as I type

- Check that the bottom-right corner says **Talemark** when a `.tale` file is open. If it says
  **Plain Text**, the file name must end in `.tale`.
- Live checks only run in a trusted folder. If VS Code shows **Restricted Mode** in the bottom-left
  corner, select it and choose **Trust**. Checking starts straight away.
- Open **View > Output** and choose **Talewright Language Server** from the list to see why the
  live checker didn't start.
- **Talewright: Validate Story** in the Command Palette uses the separate CLI, not the live checker.
  Run **Talewright: Set Up CLI** first if VS Code says the CLI is missing.

## VS Code keeps suggesting text while I write

If GitHub Copilot's suggestions are distracting, you can
[turn off its automatic suggestions for Talemark](../../tooling/vscode-talewright/README.md#control-copilot-suggestions)
without turning off Talewright's story suggestions. Other AI extensions have their own settings.

The grey text after a `{...}` substitution is a Talewright preview, not a Copilot suggestion. To
hide or show those previews, run **Talewright: Toggle Substitution Preview Hints** from the Command
Palette.

## A choice I wrote doesn't appear

Choices with `(requires ...)` are hidden while the condition is false. So are choices inside an
`{if}` block whose condition is false. Use the [Author Debugger](testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger)
to open the scene with the state you expect, then read its **Branch explanations**.

## A hub shows nothing, or not the scenes I expected

Check that the scenes have the hub's tag in `@tags`, that their `@requires` is true, and that `@once`
scenes haven't already been visited. `validate` warns (`TW3008`) when no scene has the tag at all.

## My old save won't load

The story or Talewright has probably changed since the save was made. Rewording prose usually
doesn't matter, but changing starting values or the outcome of choices already made does. Open the
save with the version of the story that made it. A failed load never changes the save or your
current game. See [updating a story with existing saves](playing-and-saving.md#updating-a-story-with-existing-saves).

## My saves disappeared (web)

Web saves live in your browser, for that website only. Clearing browsing data, using a private
window, or switching browsers or devices all lose them, and they can't be recovered. Export a save
file before clearing data or moving devices, then import it into the same story on the other player.
See [backing up and moving saves](playing-and-saving.md#backing-up-and-moving-saves).

## The desktop player or debugger won't start

Install WebKitGTK 4.1 and libnotify using the
[Linux desktop setup commands](getting-started.md#linux-desktop-setup). You also need a graphical
desktop (X11 or Wayland); these windows can't open from a remote terminal or SSH session. Published
Linux games need the same libraries. The browser player doesn't.

On Windows, the Author Debugger and desktop games need **Microsoft Edge WebView2**, which is built
into Windows 11 and up-to-date Windows 10.

## The debugger says it can't watch your story folder

The debugger watches your story folder so it can reload your edits automatically. Your computer
only allows a limited number of these watchers, and every editor window and similar tool uses some.
With several open, the debugger may not get one. Automatic reload pauses, but your current playtest
keeps running.

Close other editor windows or file-watching tools, then select **Rescan / retry watcher**. **Restart
from source** also retries, and starts a fresh run with your latest edits. The original system message
is shown after **Details** if you need it.

On Linux these limits are the `fs.inotify.max_user_instances` and `fs.inotify.max_user_watches` system
settings. Closing other programs is usually enough; raising the limits is up to you or your system
administrator.

## A published desktop game won't start

- Extract the **whole** zip first; don't run files from inside it.
- On Linux or macOS, if double-clicking does nothing, run `sh start.sh` from a terminal in that folder.
- Windows SmartScreen and macOS Gatekeeper may warn about an unsigned app. Players can choose
  **Run anyway** (Windows) or right-click → **Open** (macOS).

## `dotnet` isn't found

If you are building from source, publishing a standalone game or installing the CLI as a .NET tool,
install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK, not just the
runtime), then open a new terminal. See [which .NET version you need](getting-started.md#which-net-version-do-i-need)
for the portable CLI and player requirements.

## Can I add audio, background music or video?

Not in the initial release. Talewright currently supports still images (PNG, JPEG, WebP)
via `{image("path", "alt text")}` (animated WebP and APNG also render in modern browsers).
GIF format support, sound effects, background audio, and video files (like MP4) are planned
for post-1.0 releases as media systems expand.

## Why didn't my Markdown heading or bullet list format?

Talemark is designed specifically for interactive prose and game state, not general-purpose Markdown:
- Headings use `@title Scene Title` directly after the scene declaration `=== scene-id`.
- The `#` character in Talemark introduces text manipulators such as `#capitalize` and `#the`, not Markdown headings.
- Choices use `* [Choice text] -> target`. Bulleted lists (`- item`), Markdown links (`[text](url)`),
  and HTML tags are not interpreted and render as plain text.

## Can I build turn-based tactical combat?

Talewright is built for narrative, stat-driven RPGs (similar to *Disco Elysium* or *Choice of Games*),
where combat encounters are resolved through narrative choices, skill checks against DCs, health
adjustments (`~ player:hp -= 3`), and item requirements. Turn-based grid or tactical battle modules
are planned as optional extensions post-1.0.

## Collecting diagnostics

In the browser or desktop player, choose **Diagnostics** beside the build identity on the library
screen. During play, choose **More actions > Diagnostics**. If a story fails to open, choose
**Diagnostics** on the startup screen. Review the report, then choose **Download diagnostic
report** if you want to attach it to a bug report. Nothing is uploaded automatically.

The report lists the player version, your system, the story and scene IDs, and recent warnings and
errors. It doesn't include your story text, saves or file paths, but IDs such as scene names can
still say something about your story, so read it before sharing. It only covers the current session:
restarting the player clears it.

For a CLI problem, copy the command and everything it printed. For an editor problem, open
**View > Output**, choose **Talewright Language Server**, and copy the relevant lines. Read these
before sharing: they can contain file paths and story text.

## Still stuck?

[Report a problem on GitHub](https://github.com/squid-farm/talewright/issues/new?template=report-a-problem.yml).
Include your operating system, the Talewright version, what you tried, and the exact error message.
To find the version in the web or desktop player, copy the line that starts with "Talewright" from
the bottom of the library page or the **Settings** dialog, for example
`Talewright 0.5.0-preview.1 · commit 1a2b3c4 · built 2026-01-31`.
A published game shows the version it was built with.
You'll need a GitHub account. Don't include private story files or personal information; a small
example that shows the problem helps, but is optional.
