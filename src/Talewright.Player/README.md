# Talewright.Player — the shared Blazor UI

A Razor Class Library containing the whole user interface. Both hosts render a single component,
`<TalewrightApp />`, so desktop and web are identical. Its source dependency remains
`Talewright.Runtime`; its NuGet dependency is the `Talewright` packaging shell.
It uses the Runtime namespace and the read-only `IGameStateView`,
not engine implementation internals.

For C# customization, installing only `Talewright.Player` restores the engine automatically.
This is a Razor UI library, not an executable host. Ordinary story authors need neither package.
See [registry setup](../../README.md#c-customization-packages).

## Components (`Components/`)

| Component | Job |
|---|---|
| `TalewrightApp` | Root. Library screen listing `IStoryLibrary` stories plus a `.zip` upload (`InputFile`, buffered to memory because browser streams can't seek). Compiles the chosen story; shows `DiagnosticList` on errors, otherwise `StoryPlayer`. Parameters: `AutoStartStoryId`, `MaxUploadBytes` (20 MB). |
| `StoryPlayer` | Composes the play screen and owns `StoryHistory`, save operations, busy/error state and transcript focus. Passes state and callbacks to focused children rather than mixing their markup with session orchestration. Parameters: `Story`, `Seed`, `Character`, `OnExit`, `Visible`, optional `SaveStore`. |
| `PlayerToolbar` | Title and navigation/save/restart/library buttons; enabled states and actions come from the parent. |
| `SavedGamesPanel` | Slot-name input, slot list and explicit overwrite confirmation; all persistence and state live in the parent. |
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
| `EmbeddedStoryLibrary(assembly, prefix = "stories/")` | both hosts | Reads manifest resources named `stories/<Story>/<path>`; a story needs a `story.yaml`. No HTTP, works in WASM. |
| `FileSystemStoryLibrary(paths)` | desktop | Each path is a story folder or `.zip`. Unreadable entries are listed with the error rather than hidden. |
| `CompositeStoryLibrary(params libraries)` | desktop | Concatenates lists; ids become `"<index>|<inner id>"`. |

Register one as a singleton `IStoryLibrary` in the host.
Pass `TalewrightApp.Options` for custom `CompilerOptions`; its default preserves
built-in behavior. Use one explicit options factory for the player and build-time
validation, as in the [C# example](../../examples/CSharpStory/README.md).
`TalewrightApp.OnTitleChanged` reports the loaded/resumed story title or null on library return.
The web host uses `PageTitle`; the desktop host updates its native window title.

## Saves and history

Register `ISaveStore` for the host: `FileSaveStore` on desktop or the scoped
`IndexedDbSaveStore` from `Talewright.Player.Saves` on web. `TalewrightApp` supplies it to `StoryPlayer`;
custom consumers without a store still play normally but have no save controls.
Use **Saved games** to list slots, save a checkpoint or load one. Saving to an existing name requires
**Confirm overwrite**. Load failures preserve the current playthrough and show an alert. Operations
disable conflicting controls while in flight. Browser saves live only in that origin's IndexedDB:
clearing browser/site data removes them; no server account or cloud sync exists.

Back/Forward restores state, choices and transcript together. Choosing after Back discards redo.
At 10,000 choices, an explanatory alert replaces silent failure and disables further
choices. Save, Back/Forward (when allowed by the story), Restart and Library remain
usable. Going Back below the cap re-enables choices; returning Forward restores the notice.
Authors can disable these controls with `undo: false`. Restart starts a fresh playthrough but does not
delete manual saves. Autosave is a [separate follow-up](https://github.com/squid-farm/talewright/issues/54).
See the [runtime compatibility policy](../Talewright.Runtime/README.md#history-and-saves).

## Styling (`wwwroot/talewright.css`)

Hosts link `_content/Talewright.Player/talewright.css`. Colours and fonts are CSS variables on `:root`
(`--tw-bg`, `--tw-fg`, `--tw-muted`, `--tw-accent`, `--tw-panel`, `--tw-border`, `--tw-error`, `--tw-warning`,
`--tw-font`, `--tw-ui-font`), with a dark palette under `prefers-color-scheme: dark`. Override them in a host
stylesheet to re-theme. All classes are prefixed `tw-`. Layout is two columns (transcript + sheet) collapsing to
one below 52rem.

## Not here yet

Images (M4: Presentation),
settings (text size, theme picker; unscheduled). Core release verification takes precedence over new
presentation features; see [the roadmap](../../docs/PLAN.md#core-production-readiness-gates-m2).

## Character creation

An opt-in manifest `creation` section opens the shared accessible creation form
before play: names, allowed pronouns, presets and bounded ruleset stat choices.
Invalid input shows an alert and cannot start a session. Fixed protagonists retain
the existing direct-start behavior. Restart keeps the active character, including
one restored from a save. Library reopening offers creation anew; loading a save
then replaces that selection with the saved character.
Custom hosts may pass `StoryPlayer.Character` with a validated selection.
See [the manifest contract](../../docs/TALE-SYNTAX.md#optional-character-creation).

## Regression testing

The shared UI is exercised against the production web host in Chromium, Firefox, and WebKit and against
the real Linux Photino host via AT-SPI. The current passage, transcript, choices, and archive input have
accessible names shared by assistive technology and the tests. Test stories are defined as C# strings and
uploaded/opened through normal user workflows; no game-state test hook is exposed.
See [testing guidance](../../tests/README.md) for commands and the per-host coverage matrix.
