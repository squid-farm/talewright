# Hands-on debugger test plan

This is a review/release checklist for someone new to the debugger. Allow about 20 minutes.
Use disposable story copies and export paths, not your only copy of a real project.
The [testing guide](testing-your-story.md) explains the controls; this page supplies one fixture and
specific expected results.

## 1. Prepare and launch

You need .NET 10 and a graphical desktop. Linux needs WebKitGTK 4.1; Windows uses WebView2 and macOS
uses WKWebView. Linux x64 has automated native coverage; Windows, macOS and ARM64 remain unverified
until these steps are executed there.

Create a folder named `debugger-story` with these two files:

**story.talewright.yaml**

```yaml
id: debugger-review
title: Debugger Review
version: 1.0.0
start: scene1
flags: [ready]
vars: {count: 0}
characters:
  player: {first-name: Tester}
items:
  rope: {name: Rope}
creation:
  name: true
triggers:
  - id: reward
    when: flag:ready
    do: var:count += 10
    once: true
```

**main.tale**

```text
=== scene1
~ var:count += 1
Opening. Count {var:count}.
* [Prepared] (requires: flag:ready) -> scene1000
* [Wait] -> scene1
=== scene1000
@title Late chapter
~ var:count += 2
Late chapter. {player:first-name}. Count {var:count}.
* [Attempt] (requires: flag:ready) -> check
* [Return] -> scene1
=== check
Check passage.
::: skill-check athletics dc 10
success: -> finish
failure: -> finish
:::
=== finish
Finished.
* [Return] -> scene1
* [End] -> END
```

Installed tool:

```bash
talewright validate /absolute/path/to/debugger-story
talewright debug /absolute/path/to/debugger-story
```

For an unmerged PR checkout, run from the repository/worktree instead:

```bash
dotnet run --project tooling/cli/src/Talewright.Cli -c Release -- validate /absolute/path/to/debugger-story
dotnet run --project tooling/cli/src/Talewright.Cli -c Release -- debug /absolute/path/to/debugger-story
```

Expect validation success and a native **Talewright Author Debugger** window. There is no browser
fallback. Keep a copy of the initial files for the later player-save checks.

## 2. Skip to the late scene with prerequisites

1. Search `Late chapter`, select `scene1000`, and set **Seed** to `42`.
2. Set **First name** to `Robin`, then click **Start story** in character creation. This confirms
   the character; it does not start the selected scene.
3. Open **Prerequisite overrides**. Enable **Override flag:ready** and its **Value** checkbox.
   Enable `var:count`, set its value to `7`; enable `player inventory:rope`, set it to `3`.
4. Click **Start selected scene**.

Expect **Synthetic history**, seed 42, rolls 0, and `Late chapter. Robin. Count 9.`
In **Flags, variables, factions, visits and triggers**, expect `scene1 visits: 0`,
`scene1000 visits: 1`, count 9, ready true, and `reward` not fired. The setup is primed before entry,
so it does not fake an earlier rising edge. Inventory shows three ropes.

Filter **Branch explanations** with `c0`, expand the offered choice and inspect its events/condition.
Expect **Attempt** to be offered with `ConditionTrue`. Read-only accepted source should select
`=== scene1000` and show its file/line range. Browse away and select **Go to current scene source**;
the declaration should be visible again.

## 3. Edit live state, observe cascades and navigate

1. Open **Live edit values**, enable **Edit var:count**, enter `19`, and apply the batch.
   Expect count 19, one applied command, still one visit to scene1000 and zero rolls.
2. Enable **Edit flag:ready**, untick its value, apply. **Attempt** disappears.
3. Enable the ready edit again, tick its value and apply. Expect count **29**, **Attempt** restored,
   and `reward` fired once. Command consequences show the flag change, `TriggerFired` and variable
   change. Returning ready false/true again must not reward twice (optional; adds two commands).
4. Put `scene1` in **Target scene ID**, click **Jump retaining state**. Expect count 30 and one visit
   to scene1. **Back command** returns to scene1000/count29; **Forward command** returns to
   scene1/count30 without extra visits.
5. Go **Back command** again and choose **Attempt**. Expect check passage and **Rolls: 1**.
   This new choice branches: the redo jump is gone. Expand command history to check it.

For a rollback check, enable a live inventory edit and enter `-1`. Apply: expect an explicit error,
with the passage, command count, rope quantity and rolls unchanged. Disable/correct the draft before
continuing. A huge base stat uses normal clamping, not an arbitrary out-of-range value.

## 4. Test accepted source and guarded reload

While the check passage is current, replace only `Check passage.` in the Talemark file with
`Revised check passage.` and save.

Expect a reload boundary at `check`, revised passage/source text, and still one roll. The entry check
is reconstructed once from its boundary, not rolled a second time on top of existing history.

Temporarily change the check's success target to `missing` and save. Expect compiler errors,
**Source not adopted**, and the last good revised passage/source still displayed. Restore the legal
target and save; expect recovery.

Do not edit a consumed scene for this check: that deliberately requires a restart. Source highlighting
marks whole-scene ownership, not the executing expression.

## 5. Export and import in a new process, including redo

1. With the revised check current, target `scene1`, **Jump retaining state**, then **Back command**.
   Expect the check passage, one roll and one pending redo command.
2. Enter an absolute **Scenario file path** ending in `.twscenario`, in a folder **outside**
   `debugger-story` and outside player saves. Click **Export synthetic scenario**.
3. Export again without confirmation: expect an explicit error and unchanged file. Enable
   **Confirm replacement of existing scenario file on next export** and retry: expect success
   and the checkbox cleared.
4. Close the debugger. Launch the same tool against the same revised story. Before creating a
   character or starting a scene, enter the scenario path and **Import exact scenario**.

Expect **Exact synthetic scenario imported**, Robin, seed42, revised check passage, one roll, the
same command cursor/count and pending redo. **Forward command** reaches scene1/count30; **Back
command** returns to the same check. Export to a **different** path: bytes should match the first
export when the cursor is back at the same position.

## 6. Failed imports and explicit source adoption

Keep a copy of the successful export. Test on copies only:

- Enter a nonexistent scenario path and import. Expect an error; the active passage/history stays.
- Copy the scenario, change `formatVersion` to `999`, and import it. Expect rejection, not a fresh
  scene or partially adopted state. The input file is never rewritten.
- Try exporting to `main.tale`, a `.json` player-save path, or a `.twscenario` **inside** the story
  folder. All must fail without modifying/creating source or player slots.
- With no pending redo (go Forward first), edit the currently displayed scene's prose and save.
  Exact import of the older scenario must now reject its source identity.
- Click **Adopt scenario to accepted source** using the original scenario path. Expect an explicit
  **New synthetic scenario** notice, changed-source warning, and replay divergence notices when
  current/full-timeline evidence differs. All IDs/choices must still validate. Export under a new
  path; the old file remains unchanged. This is not a guarantee that changed positional choices
  have the same meaning.

## 7. YAML restart and ordinary player-save isolation

1. Add a comment to `story.talewright.yaml`. Expect **Restart required**; passage and accepted source stay.
   More Talemark saves must not bypass that requirement.
2. Click **Restart from source**. Expect the run cleared only after validation, with compatible
   origin scene/seed/setup/character retained. **Start selected scene** starts a fresh run.
3. Open a normal desktop player on this exact story version and save an ordinary run. Copy its
   JSON file from the per-user `Talewright/saves` directory (LocalApplicationData; on Linux usually
   `~/.local/share/Talewright/saves`). Record a checksum/copy before using it in the debugger.
4. Enter its path in **Player save file path (read only)** and click **Fork compatible player save**.
   Expect normal replay state at the normal cursor, now labeled synthetic. Edit/jump and export to
   `.twscenario`. The original JSON and player slot must remain byte-for-byte unchanged.
5. A save from different source must fail normal compatibility and preserve the active author run.
   Loading `.twscenario` through the ordinary player must fail rather than inject synthetic state.

End by validating and publishing the disposable story normally; the output contains story/player
files, not the workbench or scenario files. Record platform/architecture, tool version, pass/fail and
any error messages. Do not infer native-platform support or latency budgets from a successful Linux
run; see [measured performance and limitations](../developer/performance.md).

## 8. Text variables through both declaration paths

Open the repository's `examples/TextVariables` story. It includes explicit `weather`
and `remembered-weather` declarations and an implicit `forecast` assignment.

1. Confirm all three appear as text in inspection and setup controls; integer
   variables in other stories still use numeric controls and integer labels.
2. Before starting `shelter`, enable the `weather` override with an empty field.
   Expect empty text, displayed as `""`, not zero or the manifest's initial value.
3. Live-edit it to `12`, then to non-ASCII text and text containing a line break.
   Expect literal text, with quoted/escaped inspection values and text-change events.
   Back/Forward must reproduce each value without changing its type.
4. Export after going Back with pending redo. Import into a fresh debugger on the
   same source: setup fields, values, passage, events and redo must match. A separate
   player run/save must remain unchanged.
5. In a copy of the scenario, replace a `textVariables` string with a JSON number
   or null. Import must fail explicitly and preserve the active run and input file.
6. Enable a text prerequisite override, then change that declaration to an integer
   in source. Restart must report the incompatible retained override without
   discarding the run. Disable/correct it, restart, and confirm integer controls.

These scenario JSON checks do not change authoring syntax: story declarations
remain YAML or Talemark, not JSON.
