# Author Debugger

Native Photino/Blazor workbench for local Tale folders. Prefer
`talewright debug path/to/story`; from source:

```sh
dotnet run --project src/Talewright.Debugger -- samples/LostRelic
```

Requires .NET 10 and a graphical desktop with WebKitGTK 4.1 (Linux),
WebView2 (Windows), or WKWebView (macOS). Linux x64 is verified; other targets
require platform testing.

How it's built:

- **Session.** `AuthorSession` provides fresh-state starts, validated live edit batches, preserve-state
  jumps and a typed author command timeline with atomic Back/Forward/branching. Live refresh never
  repeats entry effects or rerolls entry checks; an already resolved continuation is retained.
  Ended runs require Back or a fresh-world start. Before/actual-after values and cascades appear in
  command history. Navigation is available regardless of ordinary player undo policy. The workbench
  reuses Player rendering and creation components, has its own `debug-assets` root, and never touches
  player save storage.
- **Reload.** `StoryReloadWatcher` watches saved changes, creates, deletes and renames, debounces editor
  bursts, and compiles one complete source snapshot at a time off the UI thread. Generation checks
  discard superseded results, including at commit after runtime reconstruction. Play and swaps share the
  Blazor dispatcher. YAML changes latch an explicit restart; failed compiles or reconstruction keep the
  last good run. Rescan retries and rearms the watcher. Disposal cancels pending work.
  Applied edits/jumps remain in the replay recipe. Pending redo postpones reload without dropping
  commands: move Forward to the end and Rescan, or explicitly start a fresh run.
- **Restart from source** revalidates the retained setup, character and seed before discarding the run.
  Invalidated ids stay visible for correction. Creation forms keep their draft across Tale reloads and
  initialize from the retained selection on restart, via Player's optional `InitialSelection`.
- **Tests.** The UI-independent model is covered by CLI tests; native interactions run through the Linux
  AT-SPI E2E harness.

For authors: [Testing your story](../../docs/guide/testing-your-story.md) ·
[CLI reference: debug](../../docs/reference/cli.md#debug).
