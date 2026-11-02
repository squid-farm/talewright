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
  live checker did not start. Only Linux x64 is runtime-tested; please report failures on other systems.
- **Talewright: Validate Story** in the Command Palette uses the separate CLI, not the live checker.
  Run **Talewright: Set Up CLI** first if VS Code says the CLI is missing.

## A choice I wrote doesn't appear

Choices with `(requires: ...)` are hidden while the condition is false. So are choices inside an
`{if}` block whose condition is false. Use the [Author Debugger](testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger)
to open the scene with the state you expect, then read its **Branch explanations**.

## A hub shows nothing, or not the scenes I expected

Check that the scenes have the hub's tag in `@tags`, that their `@requires` is true, and that `@once`
scenes haven't already been visited. `validate` warns (`TW3008`) when no scene has the tag at all.

## My old save won't load

Saves only load into the exact story version they were made with, so any edit to the story stops older
saves from loading. See [saves](playing-and-saving.md#updating-a-story-with-existing-saves). Carrying
saves over to a new version is planned ([#39](https://github.com/squid-farm/talewright/issues/39)).

## My saves disappeared (web)

Web saves live in your browser for that site. Clearing browsing data, private windows and switching
browsers or devices all lose them. Export a save file before clearing data or moving devices, then
import it into the same story on the other player. See
[backing up and moving saves](playing-and-saving.md#backing-up-and-moving-saves). If no backup exists,
cleared browser saves cannot be recovered by the player.

## The desktop player or debugger won't start on Linux

Install WebKitGTK 4.1 and libnotify using the
[Linux desktop setup commands](getting-started.md#linux-desktop-setup). You also need a graphical
desktop session (X11 or Wayland); the debugger cannot open from a remote terminal or SSH session
without one. Published Linux games need the same libraries but do not check for them before launch.
The browser player and published web games do not need them.

## The debugger says it can't watch your story folder

The debugger watches your story folder so it can reload your saved edits automatically. Every editor
window and file-watching tool uses some of a limited system allowance, so with several open, the
debugger may not get one. Automatic reload pauses, but your current playtest keeps running.

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

## Still stuck?

[Report a problem on GitHub](https://github.com/squid-farm/talewright/issues/new?template=report-a-problem.yml).
Include your operating system, the Talewright version, what you tried, and the exact error message.
A GitHub account is required. Don't include private story files or personal information.
If useful, you can attach a small example that shows the problem, but story files are optional.
