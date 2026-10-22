# Troubleshooting

## My story won't open

The player lists each problem with its file and line. Fix the **first** error and try again: one mistake
can cause several messages. Look up any code in [error codes](../reference/diagnostics.md).

If the list says the story has no `story.yaml`, make sure the zip contains `story.yaml`, either at the
top level or inside a single folder.

## A choice I wrote doesn't appear

Choices with `(requires: ...)` are hidden while the condition is false. So are choices inside an
`{if}` block whose condition is false. Use the [Author Debugger](testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger)
to open the scene with the state you expect, then read its **Branch explanations**.

## A hub shows nothing, or not the scenes I expected

Check that the scenes have the hub's tag in `@tags`, that their `@requires` is true, and that `@once`
scenes haven't already been visited. `validate` warns (`TW3008`) when no scene has the tag at all.

## My old save won't load

Saves only load into the exact story version they were made with, so any edit to the story stops older
saves from loading. See [saves](playing-and-saving.md#updating-a-story-with-existing-saves).

## My saves disappeared (web)

Web saves live in your browser for that site. Clearing browsing data, private windows and switching
browsers or devices all lose them. Use the desktop player if you need saves to stick around.

## The desktop player or debugger won't start on Linux

Install WebKitGTK 4.1: `sudo apt install libwebkit2gtk-4.1-0` (Debian, Ubuntu, Mint) or your
distribution's equivalent. You also need a graphical desktop session.

## A published desktop game won't start

- Extract the **whole** zip first; don't run files from inside it.
- On Linux or macOS, if double-clicking does nothing, run `sh start.sh` from a terminal in that folder.
- Windows SmartScreen and macOS Gatekeeper may warn about an unsigned app. Players can choose
  **Run anyway** (Windows) or right-click → **Open** (macOS).

## `dotnet` isn't found

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK, not just the
runtime), then open a new terminal.

## Still stuck?

[Open an issue](https://github.com/squid-farm/talewright/issues/new) with what you tried, what you
expected, and the error text. Attach the smallest story that shows the problem.
