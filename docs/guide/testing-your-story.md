# Testing your story

Talewright checks your whole story every time it loads, so most mistakes show up before anyone plays.
There are three ways to test, from quickest to most thorough.

> These commands need the [desktop tools](getting-started.md#install-the-desktop-tools).
> Until ready-made downloads arrive, write `dotnet run --project src/Talewright.Cli --` instead of `talewright`.

## 1. Open it in a player

Open your story in the web or desktop player. If there are errors, you'll see a list instead of the
story. Each line gives the file, line and column, an error code and a description:

```text
intro.tale:4:41  TW2001  Unknown item 'lantern'.
```

Look up any code in the [error code reference](../reference/diagnostics.md).

## 2. Check it from the command line

```bash
talewright validate my-story
```

This runs exactly the same checks as the players, without opening a window, and prints
`Validation succeeded.` or the list of problems. It also reports **warnings**: things that aren't
errors but are probably mistakes, like a scene that can never be reached. Warnings don't stop the story
from running. Add `--warnings-as-errors` if you want to be strict about them.

Validation finds broken links, unknown names, type mistakes (like comparing a stat to a word) and
scenes with no way out. It can't play every possible path for you, so you still need to playtest.

## 3. Jump to any scene with the Author Debugger

Playing from the start to reach scene forty gets old fast. The Author Debugger opens any scene directly,
with whatever game state you need:

```bash
talewright debug my-story
```

1. **Search scenes** by id or title and pick one.
2. Optionally set a **seed** (for repeatable dice) and create the player character.
3. Under **Prerequisite overrides**, set only what the scene needs: flags, variables, stats, items or
   faction standing.
4. Click **Start selected scene** and play from there.

The **State inspection** panel shows the current stats, inventory, flags and variables, the events that
led there, and **Branch explanations**: why each condition passed or failed.

### Editing while you test

Leave the debugger open while you write. When you save a `.tale` file, it recompiles and reloads the
scene you're on, keeping the state you'd built up to reach it. You stay where you are.

Some changes can't be applied safely in place, for example editing a scene the player already passed
through. Any change to `story.yaml` also needs a restart. In those cases the debugger asks you to click
**Restart from source**. You don't have to set anything up again: it keeps your selected scene, seed,
overrides and character, and tells you about any that no longer exist in the story so you can fix them.

### Good to know

- The debugger never reads or changes player save files.
- Starting at a scene skips everything before it, so earlier scenes count as **unvisited**, and there's
  no override for that yet. If a scene checks `visited(...)`, start the debugger at the earlier scene
  and play forward from there. Jumping ahead while keeping visit history is planned
  ([#63](https://github.com/squid-farm/talewright/issues/63)).
- It needs a desktop environment (on Linux, WebKitGTK 4.1).

Full details: [CLI reference: debug](../reference/cli.md#debug).
