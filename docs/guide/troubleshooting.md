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
browsers or devices all lose them. Use the desktop player if you need saves to stick around. Exporting
saves to a file for backup is planned ([#84](https://github.com/squid-farm/talewright/issues/84)).

## The desktop player or debugger won't start on Linux

Install WebKitGTK 4.1: `sudo apt install libwebkit2gtk-4.1-0` (Debian, Ubuntu, Mint) or your
distribution's equivalent. You also need a graphical desktop session (X11 or Wayland); the
debugger cannot open from a remote terminal or SSH session without one. Players of published Linux games
need it too; removing that step is being explored ([#85](https://github.com/squid-farm/talewright/issues/85)).

## A published desktop game won't start

- Extract the **whole** zip first; don't run files from inside it.
- On Linux or macOS, if double-clicking does nothing, run `sh start.sh` from a terminal in that folder.
- Windows SmartScreen and macOS Gatekeeper may warn about an unsigned app. Players can choose
  **Run anyway** (Windows) or right-click → **Open** (macOS).

## `dotnet` isn't found

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK, not just the
runtime), then open a new terminal.

## Still stuck?

[Report a problem on GitHub](https://github.com/squid-farm/talewright/issues/new?template=report-a-problem.yml).
Include your operating system, the Talewright version, what you tried, and the exact error message.
A GitHub account is required. Don't include private story files or personal information.
If useful, you can attach a small example that shows the problem, but story files are optional.
