# Author Debugger

Native Photino/Blazor workbench for local Talemark story folders. Prefer
`talewright debug path/to/story`; from source:

```sh
dotnet run --project tooling/debugger/src/Talewright.Debugger -- samples/LostRelic
```

For native prerequisites and platform coverage, see the [CLI reference](../../../../docs/reference/cli.md#debug).
For the author workflow, see [Testing your story](../../../../docs/guide/testing-your-story.md#3-jump-to-any-scene-with-the-author-debugger).

How it's built:

- **Normal play.** The same native host has a separate play-only mode for `talewright run`. It uses
  the shared player and file-backed saves, with its own `player-assets` shell and the Player files
  already shipped in `publish-assets/player`. Its distinct `player.html` host page avoids the
  debugger's development static-web-assets overlay. There is no debugger model, debugger panel,
  or Library navigation.
- **Session.** `WorkbenchModel` adapts the [author-session API](../../../../engine/src/Talewright.Runtime/README.md#live-editing-and-author-navigation)
  to UI fields and inspection snapshots. The workbench reuses Player rendering and creation components,
  has its own `debug-assets` root, and never writes player save storage. Explicit file imports
  restore a normal save before forking into isolated author history.
- **Reload.** `StoryReloadWatcher` watches saved changes, creates, deletes and renames, debounces editor
  bursts, and compiles one complete source snapshot at a time off the UI thread. Generation checks
  discard superseded results, including at commit after runtime reconstruction. Play and swaps share the
  Blazor dispatcher. YAML changes latch an explicit restart; failed compiles or reconstruction keep the
  last good run. Rescan retries and rearms the watcher. Disposal cancels pending work.
- **Restart from source** revalidates the retained setup, character and seed before discarding the run.
  Invalidated ids stay visible for correction. Creation forms keep their draft across Talemark reloads and
  initialize from the retained selection on restart, via Player's optional `InitialSelection`.
- **Tests.** The UI-independent model is covered by CLI tests; native interactions run through the Linux
  AT-SPI E2E harness, including export/import across two debugger processes.
- **Scenario files.** `AuthorScenarioFiles` bounds reads, resolves links and link-target ancestors
  with a 64-link cycle/depth guard before excluding story
  and player-save locations, and uses confirmed, same-directory atomic replacements for `.twscenario`
  exports. Raw link targets containing `..` are rejected before normalization, including links in
  protected roots; use direct paths instead. Imports are candidates against accepted source; errors
  leave the active run and input files unchanged. See
  [replay format/API invariants](../../../../engine/src/Talewright.Runtime/README.md#portable-author-scenarios).
- **Source view.** `SourceView` consumes only `WorkbenchModel.AcceptedSourceMap` from successful adoption,
  not disk reads or candidate diagnostics. The map pairs [parser-owned ranges](../../../../engine/src/Talewright.Talemark/README.md#accepted-source-provenance)
  with immutable compiled text. At most 100 rows render per file window; scene/snapshot changes reset
  the window to the scene declaration. Watcher generations publish pending status on the same dispatcher
  as source adoption. For controls and stale-state meaning, see the [author guide](../../../../docs/guide/testing-your-story.md#see-the-source-being-tested).
- **Map tab.** `MapView` projects the accepted story's [story map](../../../../engine/src/Talewright.Talemark/README.md#story-map-preview)
  into scenes, END, hub offer groups by tag, and lines that merge diverts and hub offers between the same
  scenes. It omits diverts on lines that cannot run, and keeps scenes the start scene cannot reach in a
  separate area with stubs for reachable targets. `MapTab` rebuilds it whenever the accepted story changes.
  `wwwroot/map/map.js` lays each area out with ELK in a Web Worker, then paints, hit-tests and pans or zooms
  one canvas with D3 zoom; it needs no GPU. The canvas is hidden from assistive technology: hidden scene
  buttons with arrow-key navigation, per-scene divert and hub offer lists, and a status line carry the
  same names and full conditions that hover shows. Selecting a scene shows a Preview of its prose and
  eligible choices against either the current playtest state or scene setup values; the preview never runs
  entry effects or skill checks. During a synthetic playtest, visited scenes and taken diverts or hub offers
  are highlighted in route order and update with Back and Forward. Export SVG or PNG from the map controls;
  both formats reuse the canvas drawing primitives and preserve the current route presentation. PNG
  allocation is capped at 8192 pixels per side and 16,777,216 total pixels (64 MiB of RGBA pixels); use
  SVG for larger maps. The native save dialog writes the image to the selected path using a streamed,
  same-directory replacement. `wwwroot/map/vendor` pins elkjs 0.12.1 and a d3-zoom bundle (d3-zoom 3.0.0
  and its dependencies), with notices in `THIRD-PARTY-NOTICES.md` and license files beside them, so the map
  works offline. ELK stays an authoring-tool dependency and must not ship in games.
