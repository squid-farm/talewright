# Author Debugger

Native Photino/Blazor workbench for local Tale folders. Prefer
`talewright debug path/to/story`; from source:

```sh
dotnet run --project src/Talewright.Debugger -- samples/LostRelic
```

Requires .NET 10 and a graphical desktop with WebKitGTK 4.1 (Linux),
WebView2 (Windows), or WKWebView (macOS). Linux x64 is verified; other targets
require platform testing.

The workbench uses `AuthorSession` for fresh-state scene jumps and recorded
inspection. It reuses Player rendering/creation components but has its own
`debug-assets` root and does not access player save storage. The UI-independent
model is tested with CLI tests; actual native interactions run through the
existing Linux AT-SPI E2E harness.

`StoryReloadWatcher` watches saved changes/creates/deletes/renames, debounces editor
bursts and compiles one complete source snapshot at a time off the UI thread.
Generation checks discard superseded results, including at commit after runtime
reconstruction. Play and swaps share the Blazor dispatcher. YAML changes latch
explicit restart; failed compiles/reconstruction retain the last good run.
Rescan retries/rearms the watcher. Component/host disposal cancels pending work.

`Restart from source` revalidates retained setup/character/seed/target before
discarding the run. Invalidated IDs stay visible for correction instead of being
dropped. Creation forms keep their draft on Tale reload and initialize from the
retained selection on explicit restart, using Player's optional `InitialSelection`.
See [CLI usage](../Talewright.Cli/README.md#author-debugging) for behavior and limits.
