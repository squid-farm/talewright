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
| `InlineView` | Styled runs → `<strong>`/`<em>`/`<code>` (nested for combined styles), `<br>`. `RenderedMedia` renders nothing yet. |
| `CharacterSheet` | Built from the ruleset: stats grouped by `StatCategory` (skills at 0 hidden), effective value with the modifier shown when items change it, inventory with counts, faction standings with tier names. A dating-sim ruleset gets a dating-sim sheet automatically. |
| `DiagnosticList` | Errors first, then warnings, each with `file:line:col`, code and message. |

## Story libraries (`Library/StoryLibraries.cs`)

`IStoryLibrary` has `ListAsync()` → `StoryEntry(Id, Title, Origin)` and `LoadAsync(id)` → `StorySource`.

| Implementation | Used by | Notes |
|---|---|---|
| `EmbeddedStoryLibrary(assembly, prefix = "stories/")` | both hosts | Reads manifest resources named `stories/<Story>/<path>`; a story needs a `story.talewright.yaml`. No HTTP, works in WASM. |
| `FileSystemStoryLibrary(paths)` | desktop | Each path is a story folder or `.zip`. Unreadable entries are listed with the error rather than hidden. |
| `CompositeStoryLibrary(params libraries)` | desktop | Concatenates lists; ids become `"<index>|<inner id>"`. |

Register one as a singleton `IStoryLibrary` in the host.

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

- Images ([#35](https://github.com/squid-farm/talewright/issues/35)).
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
See [the manifest contract](../../../docs/reference/tale-syntax.md#optional-character-creation).

## Regression testing

The shared UI is exercised against the production web host in Chromium, Firefox, and WebKit and against
the real Linux Photino host via AT-SPI. The current passage, transcript, choices, and archive input have
accessible names shared by assistive technology and the tests. Test stories are defined as C# strings and
uploaded/opened through normal user workflows; no game-state test hook is exposed.
See [testing guidance](../../../tests/README.md) for commands and the per-host coverage matrix.
