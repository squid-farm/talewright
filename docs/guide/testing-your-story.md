# Testing your story

Talewright checks your whole story every time it loads, so most mistakes show up before anyone plays.
There are three ways to test, from quickest to most thorough.

In VS Code, use **Talewright: Validate Story** and **Talewright: Open Author Debugger** from the
Command Palette. Run **Talewright: Set Up CLI** if needed; the extension's live diagnostics and the
CLI are separate. See [tool setup](cli-installation.md) for portable downloads and prerequisites.
The examples below use `talewright`, the .NET-tool command. From a portable tools folder, use
`.\Talewright.Cli.exe` on Windows or `./Talewright.Cli` on Mac/Linux instead, and quote story paths
that contain spaces.

## 1. Open it in a player

Make a story ZIP using **Talewright: Publish Story ZIP** or your file manager, then open it in the
[web player](https://play.trytalewright.com/). If there are errors, you'll see a list instead of the
story. Each line gives the file, line and column, an error code and a description:

```text
intro.tale:4:41  TW2001  Unknown item 'lantern'.
```

Look up any code in the [error code reference](../reference/diagnostics.md).
After changing source files, save, make a fresh ZIP and open it again; browser play does not refresh
automatically. If you already use a desktop player, it can also open a story folder.

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

The editor action opens this same native testing window, not a VS Code debug adapter or F5 session.
The portable CLI includes the debugger, but its window still needs the system components listed
in the [setup guide](cli-installation.md#if-something-goes-wrong).

1. **Search scenes** by id or title and pick one.
2. Optionally set a **seed** (for repeatable dice) and create the player character.
3. Under **Prerequisite overrides**, set only what the scene needs: flags, variables, stats, items or
   faction standing.
4. Click **Start selected scene** and play from there.

The **State inspection** panel shows the current stats, inventory, flags and variables, the events that
led there, and **Branch explanations**: why each condition passed or failed.

### See the source being tested

**Accepted Talemark source** shows the complete source file in read-only, 100-line windows with line
numbers. It follows the current scene when you choose, jump, or use Back/Forward. Use
**Previous source lines** / **Next source lines** to browse the file and **Go to current scene source**
to return to the scene declaration. These controls reset the source viewport to the top of the
requested window, even when returning to the window already selected.

The marked range runs from the `===` declaration through the line before the next scene (or the end
of that file), including intervening comments and blank lines. It describes the scene's source, **not**
the statement currently executing. Line endings count consistently; a final newline leaves an empty
last line.

The text belongs to the accepted compilation running in the workbench, not a fresh disk read.
Pending, rejected or restart-required disk changes are labeled; they cannot replace this view until
adopted successfully. When no compiler mapping is available (for example, a C#-defined scene), the
viewer explains that instead of guessing a file. Source viewing is debugger-only and cannot edit files.

### Change state without starting over

Open **Live edit values**, enable the fields you want to change, and click **Apply live edit batch**.
You can change declared flags, integer or text variables, faction standing, and every character's base stats
and inventory. Quantities are absolute; zero removes an item. Stats use normal bounds.
The whole batch is applied together, then normal triggers and leveling run. Text and choices refresh
without repeating entry effects, visits or entry-check dice. An already resolved entry check keeps
its original continuation; edits do not change that historical outcome.

**Command history and consequences** shows before/actual-after values (including clamping), trigger
firings and all other resulting events. Fields reset to actual values after success. Invalid edits
leave the run and your draft unchanged. If an edit would leave no live continuation, use fresh-world
prerequisite overrides instead.

To move elsewhere without losing accumulated state, set **Target scene ID** and click **Jump retaining
state**. This bypasses scene eligibility and enters the target once, keeping visits, trigger history
and dice position. **Start selected scene** instead discards progress and creates a fresh world using
prerequisite overrides.

Use **Back command** and **Forward command** across choices, edit batches and jumps. A successful new
command after Back discards redo. This author-only navigation works even when the story disables
player undo. Ended runs cannot be edited or jumped retaining state: go Back or start a fresh scene.
None of these operations prove that the resulting state is reachable through ordinary play.

### Inspect and override text variables

Both manifest-declared text and text introduced by Talemark assignments appear under
**Text variables** in prerequisite overrides and live edits. Enter literal text without
surrounding quotes. To set empty text, enable the override and leave its field empty;
disabling the override instead keeps the normal initial/current value. Numeric-looking
text such as `12` stays text, and whitespace and non-ASCII characters are preserved.

State inspection labels variables as **integer** or **text**. Text is shown as a quoted,
escaped literal so `""`, line breaks, quotes and backslashes are unambiguous. This is a
display representation, not new Talemark escape syntax. Setup values of the wrong type
fail explicitly and leave the active run unchanged.

Scenario exports include typed text setup and live edits. Import replays text changes,
including pending redo, before adopting the candidate. Existing integer-only recipes
without text fields remain supported. A text variable's type cannot change during play;
source edits changing a declaration's type require restart and revalidation of overrides.

### Editing while you test

Leave the debugger open while you write. When you save a `.tale` file, it recompiles and reloads the
scene you're on, keeping the state you'd built up to reach it. You stay where you are.

Some changes can't be applied safely in place, for example editing a scene the player already passed
through, changing Talemark-inferred flags/variables, removing a target, or reloading a run that ended
without entering a new scene. Any change to `story.talewright.yaml`, even a comment, also needs a restart.
A file-watcher failure requires a restart because source changes may have been missed.
In those cases the debugger asks you to click
**Restart from source**. You don't have to set anything up again: it keeps your selected scene, seed,
overrides and character, and tells you about any that no longer exist in the story so you can fix them.
Correct or disable invalid overrides (or clear the character), then restart. The current run is only
discarded after the new source and retained setup validate successfully.

Failed compilations and runtime errors keep the last good run visible and mark the source as stale.
**Rescan / retry watcher** retries compilation and rearms a failed watcher without bypassing restart
requirements. C# extensions are not hot-reloaded; rebuild and restart them.

Reload reconstructs the state before the current scene entry and enters once, so changes to its entry
effects or checks may deliberately change their results. It replaces the current passage and rebuilds
branch explanations, not a transcript of earlier passages.

Applied edit and jump commands are kept during compatible reloads. If you have pending redo after
going Back, automatic reload pauses rather than silently dropping commands. Go Forward to the end
and click **Rescan / retry watcher**, or deliberately start a fresh scene.

### Good to know

- The debugger never writes player saves. It reads one only when you explicitly fork it (below).
- Starting at a scene skips everything before it, so earlier scenes count as **unvisited**, and there's
  no arbitrary visit-count override. If a scene checks `visited(...)`, start at the earlier scene and
  play forward or use **Jump retaining state** to keep the visits you have accumulated.
- It needs a desktop environment (on Linux, WebKitGTK 4.1).

### Share and restore a debugging scenario

**Portable author scenarios** exports a synthetic `.twscenario` replay recipe: accepted story
ID/version/fingerprint, extension and engine/tool compatibility, character, seed, starting scene,
prerequisite overrides, choices, live edits, jumps and the timeline cursor (including redo).
It does **not** export arbitrary state, events, source code or extensions. Share the matching story
source separately and use the same tool version.

1. Set **Scenario file path** to an absolute path outside your story and player-save folders.
   The initial suggestion is in your local application's `Talewright/author-scenarios` directory,
   separate from `Talewright/saves`.
2. Click **Export synthetic scenario**. Existing files are not replaced unless you enable
   **Confirm replacement of existing scenario file on next export**. Confirmation is consumed by
   the next export attempt. Successful writes use a temporary file and atomic replacement.
   If a symbolic link in the export path or protected story/save folders contains `..` in its target,
   export is rejected; use direct paths without those links.
3. In another debugger process, open the matching story, enter that path, and click
   **Import exact scenario**. You do not need to recreate the character or start a scene first.
   The entire timeline, including redo, must replay successfully before the active run is replaced.
   Back/Forward and accepted-source viewing then work normally.

Exact import rejects mismatched source or compatibility, malformed files and changed replay evidence.
It uses the **accepted compilation**, even when newer disk edits are pending/rejected.
If source intentionally changed, **Adopt scenario to accepted source** is a separate, explicit
operation. It revalidates every declared ID and command, reports identity and current/full-timeline
state/event/dice/presentation differences, and creates a new synthetic run. This is **not a migration
guarantee**: positional choice keys may now mean something else. Review all differences and export
under a new path to preserve the original. Story ID, engine/tool and extension compatibility must
still match; adoption cannot load arbitrary extensions or bypass those requirements.

For an existing game, put its ordinary save's path in **Player save file path (read only)** and click
**Fork compatible player save**. This first uses the normal save compatibility and restore policy,
then copies its character, seed, choices and cursor into an independent author timeline. Your original
save is never modified; subsequent author exports must use `.twscenario`, not a player slot.
This accepts files, not browser IndexedDB slots; it does not add a browser-save download UI.

Imports are bounded to 2 MiB, 10,000 commands, 10,000 fields per setup/edit batch and 512 characters per
ID/key/compatibility identity. Numbers must be signed 32-bit integers (seed: unsigned 64-bit); character
options and all IDs must be legal for the accepted story. Unknown versions, fields, commands, duplicate
JSON members and invalid redo are rejected explicitly. Failed imports preserve the active run and
input file. These recipes are editable diagnostic data, **not authenticated proof of normal play**.

New to the debugger? Follow the [hands-on debugger test plan](debugger-test-plan.md), including a
small copyable story, expected values, rollback checks and save isolation.
Performance evidence and its limitations are recorded in the [performance notes](../developer/performance.md);
scenario replay has no new responsiveness guarantee.

Full details: [CLI reference: debug](../reference/cli.md#debug).
