# Language server

`Talewright.LanguageServer` is a separate development-only .NET tool in
`tooling/language-server`. It references `Talewright.Authoring`; game libraries
and the existing CLI never reference the server. It runs one long-lived process,
using JSON-RPC/LSP 3.17 `Content-Length` framing over stdin/stdout. Stdout is
protocol-only; process failures go to stderr. No TCP listener, telemetry, project
build, assembly discovery, or gameplay execution is involved.

## Install and launch

Each platform-specific VS Code extension bundles a compatible self-contained server; normal VS Code
installation does **not** require .NET or a separate tool. See the
[extension README](../../tooling/vscode-talewright/README.md) for supported system libraries.

Other editors and explicit development overrides can use the separate .NET tool, which requires
the .NET 10 runtime. Public release packages are published to nuget.org. Install a matching release:

```sh
dotnet tool install --global Talewright.LanguageServer --version <version>
talewright-lsp --stdio
```

`--stdio` is required. `--help` prints usage without starting the protocol.
The server must not be invoked once per edit or as `talewright validate`.
For local development, use:

```sh
dotnet build tooling/language-server/src/Talewright.LanguageServer -c Release
dotnet tooling/language-server/src/Talewright.LanguageServer/bin/Release/net10.0/Talewright.LanguageServer.dll --stdio
```

Install the VSIX as described in its [README](../../tooling/vscode-talewright/README.md).
The client defaults to its matching bundled server. Linux x64 is runtime-tested; Windows
x64/ARM64, macOS Intel/Apple Silicon and Linux ARM64 are provisionally supported through
cross-built, inspected packages, not installed-editor runtime verification.
To explicitly use a development build, configure:

```json
{
  "talewright.languageServer.command": "dotnet",
  "talewright.languageServer.args": ["/absolute/path/to/Talewright.LanguageServer.dll", "--stdio"]
}
```

The extension does not install tools or build anything automatically. A launch
failure gives an actionable notification and a **Talewright Language Server**
output channel. Reload the window after changing server settings. Set
`talewright.languageServer.enabled` to `false` to retain only highlighting,
snippets, and YAML schema support. No process launches in untrusted workspaces.
Red Hat YAML remains responsible for schema-based structural feedback; compiler
diagnostics are separately identified by source `talewright`.

## Client contract and workspace behavior

The server supports initialize/initialized, shutdown/exit, `$/cancelRequest`, full-text
didOpen/didChange/didClose, didSave, textDocument/completion, textDocument/hover, textDocument/inlayHint,
textDocument/definition, and workspace/didChangeWatchedFiles. Documents are the
root `story.talewright.yaml`, supporting `*.talewright.yaml` files (case-sensitive
suffix), and `.tale` files; other YAML is ignored. Positions
are UTF-16 and zero-based. Compiler point locations become zero-width ranges,
clamped to the current file; codes, messages, and error/warning severities remain
the compiler's. Completion advertises `:`, `@`, `#`, `>`, and `(` triggers and
returns a complete list with explicit `textEdit` replacement ranges. Hover returns
source-aware help with authored-default previews, and definition returns the authored declaration;
signature help, rename, formatting, and autofixes are not advertised.
Unsupported requests return JSON-RPC method-not-found.

### Requests, analysis, and diagnostics

Document and filesystem notifications are handled in arrival order. Semantic
requests run separately, so a request waiting for whole-story analysis does not
hold up later notifications or diagnostic publication; responses can arrive out
of order. Output frames are serialized.

Each story revision has one shared analysis. Diagnostic analysis normally waits
150 ms for edits to settle; a request needing that revision ends the debounce
and waits for the same analysis instead of starting another. A running analysis
is not yet interrupted when superseded, and a newer analysis for that story
waits for it to finish. Superseded results do not publish diagnostics.

Hover and definition may use the last published analysis when the requested
document's text is unchanged. Cross-file semantic information may therefore be
older, but a definition does not use a previous target if that target's text
has changed or the target has been removed; it waits for current analysis
instead. Completion and inlay hints always wait for the requested revision.

Cancelling a request with `$/cancelRequest` returns RequestCancelled (`-32800`)
without cancelling analysis shared by other requests or diagnostics. If a
request's awaited revision is superseded, it returns ContentModified (`-32801`).
Shutdown cancels outstanding requests, and exit waits for running work to finish.

For each file, the server publishes at most 200 compiler and lint diagnostics,
in source order when the limit is exceeded, followed by informational `TWLSP005`
at the first hidden position with the number omitted. Files at or below the
limit are unchanged. This limit applies only to editor publication, not compiler
results or CLI validation and lint output.

### Semantic completion

The compiler records reference slots using its Talemark parser, expression lexer,
and existing YAML representation tree. `Talewright.Authoring` matches those slots
against the current whole-story partial symbol table; clients contain no
completion rules. A request uses the shared analysis for the latest accepted
document versions, without waiting for the diagnostic debounce.

Supported slots include scene diverts (including `END`), speech characters,
scene metadata directives, skill-check actors/stats, and expression namespaces
for stats, items, factions, flags, and variables. `visited(` offers scene IDs.
`item:` references offer item names, and `give`/`take` quantities offer numeric
expression operands after the actor and item.
`@` in conditions offers explicitly registered predicates, and `#` in
substitutions offers registered text modifiers. After `<character>:pronoun:`,
completion offers `subject`, `object`, `possessive-pronoun` and `reflexive`;
`possessive-pronoun:` offers `determiner` and `independent`, both explicit.
These property slots work in substitutions, conditions and assignment expressions.
Ordinary prose, comments, expression
string literals, and unknown namespaces do not receive story-ID suggestions.
Identifiers replace the whole current token, including text after the cursor;
existing `@`, `#`, quotes, and namespace colons are preserved.

In `story.talewright.yaml` and supporting configuration files, completion additionally resolves `game.start`,
`game.ruleset.base`, `game.ruleset.disable`, character inventory/stat references, item
modifier stats, and `game.creation` stats/preset stats. Trigger conditions
and effects use the same expression contexts as Talemark. Quoted and escaped YAML
scalars map decoded prefixes back to authored replacement ranges. Red Hat YAML
continues to provide structural key/schema suggestions independently.

`game.ruleset.disable` uses the selected base ruleset's stat IDs before disables and
YAML additions; ordinary stat references use the final story stat table.
Empty inventory/disabled-stat flow lists offer insertion edits inside the
brackets, including when there is whitespace between them.
Completion preserves existing namespace/property delimiters as well as effect
operators, including when YAML escapes encode the delimiters. Faction IDs and
their `:name`/`:tier` properties have separate completion slots.

Limitations: YAML must remain parseable (an empty scalar is fine, an unclosed
quote or collection is not). Identifiers split across physical lines, such as a
quoted YAML escaped line continuation, are omitted from LSP completion because
the primary replacement edit must be single-line. The server does not repair YAML or propose arbitrary
new declarations. Suggestions are reference/syntax names, not evaluated gameplay
values. Required missing or invalid project metadata returns an explicit
completion-unavailable JSON-RPC error (`-32001`), in addition to its diagnostic,
rather than misleading built-in suggestions. Completion requires an open document
and a valid UTF-16 position; malformed/out-of-bounds requests return `-32602`.

### Source-aware hover

`textDocument/hover` uses the same current whole-story snapshot as completion.
The shared authoring provider resolves parser-recognized references and
declarations for scenes, characters, stats, items, factions, flags, variables,
rulesets, registered predicates, and modifiers. Results include the symbol kind
and type, a declaration location and authored source line when available, or
explicit registration/ruleset provenance when there is no authored declaration.
Scene metadata directives, conditional text, scene/choice/divert/speech/effect
markers, and expression keywords/operators supply authoring help too.

Keys in every `*.talewright.yaml` file use descriptions from the same schema
bundled in the VSIX, embedded as a resource in `Talewright.Authoring`. There is no
second schema or editor-specific semantic parser. Parsed YAML key paths and
decoded scalar mappings preserve authored UTF-16 ranges, including quoted and
escaped identifiers. Red Hat YAML can independently show structural help.

Hover ranges are half-open token ranges and may span physical lines (unlike
completion's primary edit). Unknown or partial identifiers and ordinary prose,
comments, and string literals outside complete substitutions return `null`, not invented symbol
documentation. Already-recognized tokens in incomplete expressions still work;
syntactically broken YAML has no fabricated schema locations. Registered
predicates/modifiers expose their names and types, not undocumented signatures
or runtime results. Source context is authored text, never a current gameplay
value or evaluated custom registration. Complete substitutions additionally show authored-default
examples and built-in manipulator steps. Symbol cards list authored facts only:
characters show their default name and pronoun forms; items their name, stat
modifiers and starting holders; stats their name, category, range, default and
per-character starting values; factions their name, initial standing and tier;
scenes their authored title, tags, priority, `once` and hub settings. Each kind's
facts come from one list in `StoryHover`, where future authored metadata (such as
custom item properties) can be added.

Clients advertising Markdown receive formatted help with authored names, paths,
and source lines isolated in code spans; no authored text is promoted into links,
HTML, or commands. Other clients receive plaintext. Required missing/invalid
metadata returns `-32001` explicitly, just like completion. An open document and
valid UTF-16 request position are required (`-32602` otherwise). Hover does not
save files, build projects, load assemblies, or execute game code.

### Substitution inlay hints

`textDocument/inlayHint` returns final authored-default text after complete supported
substitutions, positioned immediately after `}`. The request requires an open document
and an ordered, in-bounds UTF-16 range; only hint positions within that range are returned.
Labels are quoted/escaped and limited to 40 UTF-16 code units; plaintext tooltips include
the full result, `default` provenance, and manipulator steps. No text edits are supplied.
Names, pronoun forms, built-in manipulator chains/arguments, item/faction names,
constant literals (text, numbers, booleans, and reputation tiers), and
explicit manifest variable defaults are supported. Implicit variable assignments and
gameplay-dependent expressions are not evaluated. Unsupported/custom operations supply
no hint and explain the limitation on hover.

Previews reuse typed compiler substitutions and engine expressions/modifiers, not a second
editor parser. Only engine-owned built-in instances can execute; exported metadata
identifies these explicitly with `BuiltInModifiers`. Older metadata without this field
remains valid but its modifier placeholders cannot be previewed. No project assemblies
or custom delegates execute. Configuration errors retain normal metadata error behavior.

Clients advertising `workspace.inlayHint.refreshSupport` receive a
`workspace/inlayHint/refresh` request after updated story analysis, including cross-file
unsaved changes. VS Code uses native inlay rendering/settings; its Talewright preview
toggle changes only the Talemark language's `editor.inlayHints.enabled`.

#### Beginner hover check

1. Install/configure the server as above and open a trusted story folder.
2. Add `=== destination` in another Talemark file and type `-> destination` in the
   first file. Hover the target (or use VS Code **Show Hover**, **Ctrl+K Ctrl+I**).
   Expect **Scene**, its type, and the other file's declaration location/source.
3. Without saving, rename the declaration and update the reference. Hover should
   immediately reflect the unsaved name/location. An unknown target has no
   semantic hover. Closing the buffer restores the latest disk context.
4. Hover `hp` in `{player:hp}`, `requires` in a scene header's
   `@requires player:hp > 0`, and `capitalize` in `{player:first-name#capitalize}`.
   Check symbol/type or syntax help and, for complete supported substitutions, a result tagged `default`.
5. Hover `start` in `story.talewright.yaml` for its schema description; hover its
   scene value for declaration context. Quoted values keep quotes outside the
   identifier range.
6. With explicitly exported custom metadata configured, hover a registered
   predicate/modifier. Remove/corrupt the file and confirm an unavailable error
   instead of fallback documentation.
7. Neovim and other clients use the same `textDocument/hover` request. The
   headless smoke below verifies hover without the VS Code extension.

### Go to definition

`textDocument/definition` uses the same snapshot and lookup as hover. A
reference, or the declaration itself, resolves to the declaring file and range:
characters, items, factions, flags, and variables declared in a supporting
configuration file navigate to that file, and scenes navigate to their Talemark
header. Unsaved declarations, including those in newly created file-backed
buffers, are used immediately. Built-in ruleset stats, registrations, unknown
identifiers, and prose return `null`. When an identifier is declared more than
once, navigation targets the first source cited by the duplicate diagnostic.
Required missing/invalid metadata returns `-32001`, as for hover.

### Story ownership

The outermost ancestor `story.talewright.yaml` identifies each story. That basename
is reserved for its single root manifest: any additional file with that basename
inside the story produces TW4002 at the original file, whether saved or unsaved.
It neither contributes declarations nor establishes a nested story.
An unsaved manifest can establish a root only when there is no containing story;
otherwise a containing initialized workspace folder is the fallback. Separate
non-nested story roots are analyzed independently.
Document and diagnostic ownership is recalculated from the current root manifests
when files are opened, closed, or refreshed. Creating the first manifest in a
manifestless workspace preserves existing overlays. Creating a containing
manifest makes any additional manifest within its scope a reserved-name error.
Discarding an unsaved root returns its overlays to the workspace fallback and
clears obsolete diagnostics. Open manifest overlays survive disk deletion.
Every folder load includes all recursive content and configuration inputs; editor
scope registration never excludes directories from loading or loader protections.
Only the owning scope publishes diagnostics for a file.
Closing a document discards its overlay even if the subsequent disk reload fails.
Only file URIs are supported. Open overlays win over disk, and close reloads disk. Versions must
be 32-bit integers and increase; invalid versions report an error without closing
the connection. Whole-story analysis is debounced by 150 ms and superseded snapshots
cannot publish. Diagnostics for fixed or deleted files are explicitly cleared.
Disk changes re-read the bounded shared story source loader; no project code is
executed. File-loading errors are reported, not treated as successful analysis.

Supporting configuration files belong to the containing story's single root manifest,
like Talemark files; they never establish a story root themselves. Opening,
editing, creating, renaming, deleting, or closing one re-analyzes the whole story
and clears diagnostics it no longer owns.

Clients must forward create/change/delete events for `**/*.tale`, `**/images/**`, `**/*.talewright.yaml`,
`**/talewright.lint.json`, project files, and configured authoring metadata. The VS Code client installs
these watchers, including the lint configuration and configured metadata path. Other clients
can use didSave for saved open documents, but need watched-file events for
closed-file additions/deletions. The server does not install its own filesystem
watcher or recursively open unrelated stories at initialization.

### Image paths

Inside the first quoted argument of `{image("path", "alt")}`, completion lists
safe PNG, JPEG and WebP files from the owning story's `images/` catalogue. Labels
and inserted paths omit `images/`, use case-sensitive prefixes and replace the
whole string content without replacing its quotes or alt text. Embedded quotes
are escaped on insertion. Incomplete quoted paths also support completion.
Alt text is not an image-path slot. It offers the same expression completions as prose
substitutions, and no image-path suggestions. Every
narrative-template surface, including dialogue and choice labels, uses the same
behavior; plain-string fields such as titles do not acquire image syntax.

Editor analysis uses the same compiler image lookup for complete, well-formed calls,
publishing TW2010–TW2014 at the path's opening quote. File notifications rebuild
the catalogue and refresh suggestions and diagnostics while preserving unsaved
buffers. Report a rename as deletion of the old path and creation of the new path.
No image bytes are read and no images are rendered. Ordinary compilation and
`talewright validate` report the same resolver diagnostics; compiled media carries
the selected asset path and either literal alt text or an alt template.

Use `initializationOptions.metadataPath` for an explicit data-only metadata file
(absolute or relative to each story root), and
`initializationOptions.requiresProjectMetadata: true` for external/nested C#
project layouts. VS Code exposes matching `talewright.languageServer` settings.
A project file at the story root also requires metadata. Missing registrations
produce `TW5001`; unreadable/invalid explicitly configured metadata produces
`TW5002` and a notification, with no built-in-only success fallback. Metadata
watch events recreate the workspace and retain overlays. See
[exporting trusted registrations](authoring-analysis.md#c-project-registrations).
Do not configure a project DLL as metadata: no build target, assembly, ruleset
factory, or arbitrary C# code is automatically run.

The shared Talemark-aware lint rules and configuration API are implemented in #150.
The language server uses the same `talewright.lint.json` configuration and shared
rule implementation on each current analysis snapshot. Lint findings preserve
their source ranges; compiler diagnostics are published once. Invalid lint
configuration is reported on the configuration file as `TWLSP004` while compiler
analysis continues, and correcting the file clears that diagnostic without
restarting the server. LSP integration and CLI parity are tracked in #151.

## Neovim (another LSP client)

Neovim 0.11 or later can launch the installed tool:

```lua
vim.api.nvim_create_autocmd({ "BufReadPost", "BufNewFile" }, {
  pattern = { "*.tale", "*.talewright.yaml" },
  callback = function(event)
    vim.lsp.start({
      name = "talewright",
      cmd = { "talewright-lsp", "--stdio" },
      root_dir = vim.fs.root(event.buf, "story.talewright.yaml"),
      init_options = { requiresProjectMetadata = false },
    })
  end,
})
```

Open a Talemark file in a story, change a choice target to a nonexistent scene,
and run `:lua vim.diagnostic.open_float()`. Restore the target without saving:
the compiler diagnostic should disappear. Save/close/reopen and confirm it
remains absent. For cross-file disk changes, configure the client to forward
`workspace/didChangeWatchedFiles` as described above.

The reproducible headless client smoke exercises initialization, compiler
diagnostics, an unsaved fix, clearing, shared completion and hover with exact
source ranges, and clean shutdown:

```sh
TALEWRIGHT_LSP_DLL="$PWD/tooling/language-server/src/Talewright.LanguageServer/bin/Release/net10.0/Talewright.LanguageServer.dll" \
  nvim --headless -u NONE -l tooling/language-server/tests/neovim-smoke.lua
```

## Verification

```sh
dotnet test tooling/language-server/tests/Talewright.LanguageServer.Tests -c Release
cd tooling/vscode-talewright
npm ci
npm test
npm run package
npm run test:package
xvfb-run -a npm run test:editor
```

The stdio process tests cover lifecycle, incomplete Talemark/invalid YAML, unsaved
buffers, cross-file create/change/delete, close-to-disk, diagnostic clearing,
superseded versions, metadata failures/recovery, and exact saved-input diagnostic
parity with the CLI. Completion tests cover every symbol category, irrelevant
contexts, incomplete input, UTF-16/CRLF and escaped YAML ranges, versioned
overlays, cross-file additions/changes/deletions, and metadata failure/recovery.
The installed-VSIX smoke preserves the existing syntax/snippet/schema checks
and exercises unsaved Talemark and canonical manifest completion alongside diagnostics.
Hover tests cover all symbol categories, syntax/schema descriptions, exact
UTF-16/CRLF/escaped YAML ranges, unknown and incomplete input, current overlays,
cross-file changes/deletions, Markdown/plaintext negotiation, and metadata
failure/recovery. The installed VSIX displays Markdown symbol/declaration help
for unsaved references, and Neovim requests the same hover directly.

### Beginner manual check

1. Install/configure the server and extension above; open a trusted story folder
   containing `story.talewright.yaml` and a Talemark file with `=== start`.
2. Add another file with `=== destination` and `-> END`.
3. In the first file type `-> dest`, press **Ctrl+Space**, and select
   `destination`. Only `dest` should be replaced. Do not save.
4. In a header type `@req` and request completion: `requires` should appear.
   In `@requires player:h`, request completion for registered stats such as `hp`.
   Try `{player:first-name#cap` for the `capitalize` modifier.
5. In the manifest change `start` to `"dest"` and request completion inside the
   quotes: `destination` should appear without removing the quotes.
6. Rename the second scene in an unsaved buffer, then delete its file after
   closing it. Repeat the divert completion to confirm the list updates.
7. Type the same prefixes in ordinary prose: no story-symbol suggestions should
   appear (VS Code's own word/snippet suggestions may still appear).
8. For custom C# registrations, export metadata explicitly and configure its
   path. Verify `@YourPredicate` and `#yourModifier`; remove/corrupt the metadata
   and confirm an explicit unavailable error instead of invented registrations.
   Neovim clients can request the same `textDocument/completion` method or use
   the headless smoke above; no VS Code extension is needed.

Verified on Linux on 2026-10-23: stdio integration tests against both the build
and an installed local NuGet tool; the installed VSIX in an isolated VS Code
1.100.3 profile; and the headless Neovim 0.11.5 smoke above. Both editors received
compiler diagnostics and cleared them after an unsaved fix; completion was also
verified in both editors, including unsaved Talemark token replacement and canonical
manifest values in the installed VSIX. Source-aware hover with declaration context
and exact ranges was also verified in both editors. This is local test
evidence, not a claim that the package has already been released.
