# Talewright.Player — the shared Blazor UI

A Razor Class Library containing the whole user interface. Both hosts render a single component,
`<TalewrightApp />`, so desktop and web are identical. Its source dependency remains
`Talewright.Runtime`; its NuGet dependency is the `Talewright` packaging shell.
It uses the Runtime namespace and the read-only `IGameStateView`,
not engine implementation internals.

For C# customization, installing only `Talewright.Player` restores the engine automatically.
This is a Razor UI library, not an executable host. Ordinary story authors need neither package.
See [registry setup](../../../docs/developer/csharp-extensions.md).

## Components (`Components/`)

| Component | Job |
|---|---|
| `TalewrightApp` | Root. Library screen listing `IStoryLibrary` stories plus a `.zip` upload (`InputFile`, buffered to memory because browser streams can't seek). Compiles the chosen story; shows `DiagnosticList` on errors, otherwise `StoryPlayer`. Parameters: `AutoStartStoryId`, `LibraryEnabled` (true by default), `MaxUploadBytes` (20 MB). |
| `StoryPlayer` | Composes the play screen and owns `StoryHistory`, save operations, busy/error state and transcript focus. Passes state and callbacks to focused children rather than mixing their markup with session orchestration. Parameters: `Story`, `Seed`, `Character`, `OnExit`, `Visible`, optional `SaveStore`. |
| `PlayerToolbar` | Sticky themed title/navigation/save header, More actions disclosure for Restart/Library, and modal Restart confirmation. Enabled states and actions come from the parent; Library is omitted when no exit callback is supplied. |
| `SavedGamesPanel` | Accessible modal Saves dialog, summary list, named-save input, explicit overwrite and conditional load confirmation; persistence and replay state live in the parent. |
| `StoryPassage` | One history frame's title, document and chosen-label echo. |
| `StoryChoices` | Numbered choices, end/replay controls and visible history-limit notice. |
| `DocumentView` | `RenderedDocument` → `<p>` paragraphs, speech (`.tw-speaker` + line) and notes (`.tw-note-check/-change/-info`). |
| `InlineView` | Styled runs → `<strong>`/`<em>`/`<code>` (nested for combined styles), `<br>`, and `MediaView` for resolved images on every narrative surface. |
| `MediaView` | Viewport-lazy image lease and independent decode/presentation. Responsive image, effective alt and accessible visible failures; releases its lease when unmounted. |
| `CharacterSheet` | Built from the ruleset: stats grouped by `StatCategory` (skills at 0 hidden), effective value with the modifier shown when items change it, inventory with counts, faction standings with tier names. A dating-sim ruleset gets a dating-sim sheet automatically. |
| `DiagnosticList` | Errors first, then warnings, each with `file:line:col`, code and message. |

## Story libraries (`Library/StoryLibraries.cs`)

`IStoryLibrary` has `ListAsync()` → `StoryEntry(Id, Title, Origin)` and `LoadAsync(id)` → `StorySource`.
`OpenAsync(id)` additionally returns a disposable `LoadedStory` with optional `StoryImages`.
Its default implementation wraps `LoadAsync` without image support, preserving custom/text-only
libraries. The compiler's source and pure renderer never own image I/O or resources.

| Implementation | Used by | Notes |
|---|---|---|
| `EmbeddedStoryLibrary(assembly, prefix = "stories/", enableImages = false)` | both hosts | Reads manifest resources named `stories/<Story>/<path>`; a story needs a `story.talewright.yaml`. Built-in sample hosts opt into lazy image resource streams with `enableImages: true`. No HTTP, works in WASM. |
| `FileSystemStoryLibrary(paths)` | desktop | Each path is a story folder or `.zip`. Unreadable entries are listed with the error rather than hidden. |
| `CompositeStoryLibrary(params libraries)` | desktop | Concatenates lists; ids become `"<index>|<inner id>"`. |

Register one as a singleton `IStoryLibrary` in the host.

### Image host coverage and ownership

| Source/host | Image display |
|---|---|
| Native desktop, command-line story folder | Supported: live safe local files |
| Native Author Debugger, story folder | Supported: accepted-source image generations |
| Built-in desktop/web samples | Supported: opt-in lazy embedded resource streams |
| Desktop ZIP / browser upload | Retained compressed archive with lazy, bounded image reads |
| Generated published desktop/web host | Supported: same opt-in embedded resource streams, including static web subpaths |
| Custom library using only `LoadAsync` | Text works; images show unavailable unless it supplies a capability |

Generated hosts reuse the sample hosts' embedded-image capability and the publisher's validated
staging; they do not introduce another image adapter or transport. Web asset URLs resolve
relative to the page base. Embedded images may still arrive as part of the initial program
download even though display/stream acquisition is lazy. The embedded artwork can push one
generated program file above a host's per-file limit even when each original image is smaller.
[#271](https://github.com/squid-farm/talewright/issues/271) tracks moving story assets beside the
host; that layout is not shipped here. See the author guide's
[artwork and hosting size](../../../docs/guide/sharing-your-story.md#artwork-and-hosting-size)
for the advisory source estimate, final web-payload measurement and current embedded-artwork caveat.
Published Windows, macOS and ARM image playback remain unverified; Linux x64 and three browser
engines have extracted-artifact coverage.

`TalewrightApp` cascades the accepted `StoryImages`. Direct `StoryPlayer`/`DocumentView`
consumers can supply a `CascadingValue<StoryImages>` with `StoryImages.FromDirectory` or
`FromResources`, and must await `DisposeAsync` when replacing the accepted story or closing the host.
Successful replacement cancels the old capability and unmounts its media; failed adoption
preserves the previous capability/playthrough. Opening the library merely hides the mounted
story, retaining its history URLs. A capability's identity is a source generation: refresh it
even when presentation text is unchanged so retained components cannot show stale assets.

The catalogue/resolver policy is reused without reading image bytes. Folder open rechecks
catalogue membership and rejects symbolic links in the path. Files remain live: later reads
may see changed bytes or fail. Each mounted occurrence acquires a lease only near the viewport.
Within one accepted owner generation, identical resolved paths share one read/transfer and
Blob URL. Alt, style and decode remain per occurrence. Removing one holder cannot cancel a
transfer needed by another. Final release retires the entry and revokes its URL exactly once;
a later acquisition reads the live file again. Late retired completions cannot affect a new entry.
There is no all-story read or global durable cache. Streams are disposed after transfer;
observers, cancellation and receivers belong to occurrences. Owner teardown invalidates
entries and awaits asynchronous browser URL cleanup. Sharing removes duplicate transfers/data;
it does not bound distinct images retained by history or guarantee one decoded bitmap.
No eviction, unique-image budget, or history virtualization is introduced. No filesystem browser URL, remote
fetch/fallback, or new author file-size limit is introduced.

ZIP opens use `ArchiveStoryImages.Open`, which takes ownership of the compressed input even
when loading fails. A loaded story retains one parsed archive, not expanded image copies.
Source discovery uses the shared configuration/narrative predicates and root mapping.
Compressed input (including the separate 20 MiB browser upload convenience cap), expanded
content, each expanded read, and retained stream/Blob ownership are distinct concerns.
ZIP loading currently has a **temporary known limitation**: 50 MiB combined expanded source
text and image content. This is not an approved game-size budget; #196 removes it alongside
the inherited ceilings. ZIP images remain enabled in pre-alpha: use at your own risk.
Metadata preflight is supplemented by bounded actual reads. Each image read also stops if
actual expansion exceeds that entry's declared size; only that image fails. This declared-size
check remains when #196 removes the temporary combined cap. Per-image high-water accounting
means repeat and partial reads do not charge the same content twice. This is not a folder
image limit or a history memory budget. Late read/decode failures preserve the adopted narrative
and show the effective alt with an accessible error. Rejected candidates, accepted replacement
and teardown release archive/input resources after image transfers drain.

`LibraryEnabled=false` requires an `AutoStartStoryId` and removes Library navigation
and upload UI during both creation and play. A failed startup shows diagnostics
and lets the player retry the selected story. Auto-start alone does not disable
the library. Published web and desktop hosts explicitly disable it.
Pass `TalewrightApp.Options` for custom `CompilerOptions`; its default preserves
built-in behavior. Use one explicit options factory for the player and build-time
validation, as in the [C# example](../../../examples/CSharpStory/README.md).
`TalewrightApp.OnTitleChanged` reports the loaded/resumed story title or null on library return.
The web host uses `PageTitle`; the desktop host updates its native window title.

## Saves and history

Register `ISaveStore` for the host: `FileSaveStore` on desktop or the scoped
`IndexedDbSaveStore` from `Talewright.Player.Saves` on web. `TalewrightApp` supplies it to `StoryPlayer`;
custom consumers without a store still play normally but have no save controls.
**Quick Save** writes/replaces one dedicated checkpoint for the story, without named-save
overwrite confirmation. It uses the same atomic storage, summary and persisted-progress
load guard as named saves. The dialog marks it **Quick Save checkpoint**, independently of
user-entered names; Quick Save can never overwrite a named slot. Failure keeps the previous
checkpoint and reports advice; retry using Quick Save. This is explicit saving, not autosave.
Use **Saves** to open the modal and automatically refresh its list. New entries show scene title,
actual UTC save time and character name. Legacy/malformed details are explicitly unavailable;
listing never replays a story. Saving to an existing name requires **Confirm overwrite**.
Loading asks **Load save** / **Keep playing** only when the complete current replay state
(including seed, character, cursor and redo) is not persisted in any slot. The store is checked
again before loading; a failed write never counts as a saved state.
Escape or **Close saves** returns focus to the toolbar. Storage errors offer **Retry save list**.
Load failures preserve the current playthrough and show an alert. Operations
disable conflicting controls while in flight. Browser saves live only in that origin's IndexedDB:
clearing browser/site data removes them; no server account or cloud sync exists.

Back/Forward restores state, choices and transcript together. Choosing after Back discards redo.
At 10,000 choices, an explanatory alert replaces silent failure and disables further
choices. Save, Back/Forward (when allowed by the story), Restart and Library remain
usable. Going Back below the cap re-enables choices; returning Forward restores the notice.
Authors can disable these controls with `undo: false`. Restart starts a fresh playthrough but does not
delete manual saves. Autosave is a [separate follow-up](https://github.com/squid-farm/talewright/issues/54).
See the [runtime compatibility policy](../../../engine/src/Talewright.Runtime/README.md#history-and-saves).

## Styling (`wwwroot/talewright.css`)

Hosts link `_content/Talewright.Player/talewright.css`. Colours and fonts are CSS variables on `:root`
(`--tw-bg`, `--tw-fg`, `--tw-muted`, `--tw-accent`, `--tw-panel`, `--tw-border`, `--tw-error`, `--tw-warning`,
`--tw-font`, `--tw-ui-font`), with a dark palette under `prefers-color-scheme: dark`. Override them in a host
stylesheet to re-theme. All classes are prefixed `tw-`. Layout is two columns (transcript + sheet) collapsing to
one below 52rem.

## Not here yet

- Generated published-host image adapters and deployment behavior (#179).
- Reader settings such as text size and a theme picker (unscheduled).
- Story and host theming beyond CSS variables ([#13](https://github.com/squid-farm/talewright/issues/13)).

## Character creation

An opt-in manifest `creation` section opens the shared accessible creation form
before play: names, allowed pronouns, presets and bounded ruleset stat choices.
Invalid input shows an alert and cannot start a session. Fixed protagonists retain
the existing direct-start behavior. Restart keeps the active character, including
one restored from a save. Library reopening offers creation anew; loading a save
then replaces that selection with the saved character.
Custom hosts may pass `StoryPlayer.Character` with a validated selection.
See [the manifest contract](../../../docs/reference/talemark-syntax.md#optional-character-creation).

## Regression testing

Image-specific validation uses the existing test project and Node's built-in test runner:

```sh
node --test player/tests/media.test.mjs
dotnet test player/tests/Talewright.Player.E2E.Tests -c Release --filter 'FullyQualifiedName~StoryImagesTests'
python3 player/tests/run_e2e.py web --browser all --filter 'FullyQualifiedName~ImageRenderingTests'
python3 player/tests/run_e2e.py linux --filter 'FullyQualifiedName~NativeImageTests'
```

The native suite opens real image-bearing folders through the actual player and debugger
entry points, including all narrative surfaces, live image refresh/failure, retained history,
narrow layout and keyboard choice activation. Browser tests cover actual embedded images,
PNG/JPEG/WebP decoding, viewport deferral, failed/successful replacement and unsupported
archive states. Node tests cover stale stream/decode completion and URL lifetime.
Windows WebView2 and macOS WKWebView are not exercised by the Linux harness.

The shared UI is exercised against the production web host in Chromium, Firefox, and WebKit and against
the real Linux Photino host via AT-SPI. The current passage, transcript, choices, and archive input have
accessible names shared by assistive technology and the tests. Test stories are defined as C# strings and
uploaded/opened through normal user workflows; no game-state test hook is exposed.
See [testing guidance](../../../tests/README.md) for commands and the per-host coverage matrix.
