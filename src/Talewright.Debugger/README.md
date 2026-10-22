# Author Debugger

Native Photino/Blazor workbench for local Tale folders. Prefer
`talewright debug path/to/story`; from source:

```sh
dotnet run --project src/Talewright.Debugger -- samples/LostRelic
```

For native prerequisites and platform coverage, see the [CLI reference](../../docs/reference/cli.md#debug).
For the author workflow, see [Testing your story](../../docs/guide/testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger).

How it's built:

- **Session.** `WorkbenchModel` adapts the [author-session API](../Talewright.Runtime/README.md#live-editing-and-author-navigation)
  to UI fields and inspection snapshots. The workbench reuses Player rendering and creation components,
  has its own `debug-assets` root, and never touches player save storage.
- **Reload.** `StoryReloadWatcher` watches saved changes, creates, deletes and renames, debounces editor
  bursts, and compiles one complete source snapshot at a time off the UI thread. Generation checks
  discard superseded results, including at commit after runtime reconstruction. Play and swaps share the
  Blazor dispatcher. YAML changes latch an explicit restart; failed compiles or reconstruction keep the
  last good run. Rescan retries and rearms the watcher. Disposal cancels pending work.
- **Restart from source** revalidates the retained setup, character and seed before discarding the run.
  Invalidated ids stay visible for correction. Creation forms keep their draft across Tale reloads and
  initialize from the retained selection on restart, via Player's optional `InitialSelection`.
- **Tests.** The UI-independent model is covered by CLI tests; native interactions run through the Linux
  AT-SPI E2E harness.
