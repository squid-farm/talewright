# Tale for VS Code

Highlight Talewright `.tale` stories, insert common story structures, and provide
schema support, shared semantic completion/hover, and live compiler diagnostics without
running your story.

## Install

This extension is distributed as a local VSIX, not through the Marketplace.
Use VS Code 1.100 or later.

1. Install the [Red Hat YAML extension](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-yaml)
   from the VS Code Marketplace. It provides the YAML editor features used by the schema.
2. Obtain `talewright.vsix` from the **Editor tooling** workflow artifact, or build
   it using the commands below.
3. In VS Code, open **Extensions**, choose **... > Install from VSIX...**, and
   select the file. Reload VS Code if prompted.
4. Open a `.tale` file. The language indicator at the bottom should say **Tale**.
5. For live compiler diagnostics and semantic completion, install the separate `Talewright.LanguageServer`
   .NET tool (requires .NET 10) from the Talewright package feed. The extension
   launches `talewright-lsp --stdio`; it never installs or builds it automatically.
   See [server setup and other editors](https://github.com/squid-farm/talewright/blob/main/docs/developer/language-server.md).
   Configure `talewright.languageServer.command`/`args` for a local build, or set
   `talewright.languageServer.enabled` to `false` for schema/syntax-only use.
   Reload the window after changing settings. Launch errors appear in the
   **Talewright Language Server** output channel. Untrusted workspaces never
   launch a process.

This package identifier is `squid-farm.talewright`; it does not imply a registered
Marketplace publisher or a published Marketplace extension.

## Write a scene

Type a prefix, then select its suggestion using **Insert Snippet** in the Command
Palette (or enable **Editor: Tab Completion** for prefix-plus-Tab expansion).
Use Tab to move between placeholders.

| Prefix | Inserts |
|---|---|
| `tale-scene` | Scene header, prose, and a divert |
| `tale-choice` | Choice label and target |
| `tale-if` | Conditional prose, alternate branch, and closing marker |
| `tale-choice-check` | Choice skill check with success/failure outcomes |
| `tale-check` | Entry skill check block with both outcomes |

Replace scene IDs and other placeholders with your story's actual declarations.
Use **Toggle Line Comment** to add/remove `//` comments.
Brackets can be matched, but Tale defaults disable automatic quote/bracket closing:
ordinary prose and literal braces should remain comfortable to type. You can
override these language-specific defaults in your VS Code settings.

The grammar distinguishes scene headers, metadata, speech, effects, conditions,
choices, substitutions, and checks. It also highlights inline Markdown emphasis,
strong text, code, images, and links. Substitutions and conditionals inside a
link label keep their Tale colors, because the compiler renders the label text
and drops the target. These colors describe syntax, not whether
your story compiles. The renderer still determines what appears during play;
images are currently placeholders, not a claim of image rendering support.

Incomplete templates and emphasis recover at line boundaries so an unfinished
edit does not color the rest of the story. A `*` or `_` with no closing
delimiter later on the line stays plain text. Highlighting of formatting that
spans multiple lines, and some ambiguous delimiter runs, is consequently approximate.

## `story.talewright.yaml` support

Every story's manifest is named `story.talewright.yaml` at the story root; the
engine, CLI, and player recognize no other manifest filename. With Red Hat YAML
installed, the extension associates `*.talewright.yaml` files with the bundled
JSON Schema. The wildcard is only an editor schema-discovery convention; it does
not make other basenames valid story manifests. It validates manifest structure, offers known keys, and shows field
descriptions. It does not know story-specific IDs or whether a referenced scene,
stat, item, or faction exists. The optional language server supplies story-specific
reference completion and compiler checks, including whole-story diagnostics for unsaved edits; use
`talewright validate <story-folder>` for command-line validation.

Editors that don't use the SchemaStore catalog can use the same schema with this
modeline at the top of `story.talewright.yaml`:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/squid-farm/talewright/main/tooling/vscode-talewright/schemas/story.schema.json
```

Or point `$schema` to a local copy of
[`schemas/story.schema.json`](schemas/story.schema.json). SchemaStore discovery
is tracked separately and is not required for either option.

### Schema distribution and compatibility

The canonical schema is maintained in this repository and bundled in the VSIX.
Its public `main` URL above is the rolling schema for the current manifest format.
SchemaStore registration
([SchemaStore/schemastore#6539](https://github.com/SchemaStore/schemastore/pull/6539))
uses that same URL with the `*.talewright.yaml` file pattern; it does not
maintain a second copy of the schema, so the schema file must not move. Submission alone does not mean
automatic discovery is available: SchemaStore must accept and publish the entry,
and an editor may need to refresh its catalog.

Keep changes at this URL backward-compatible with supported manifests. A future
breaking manifest change must retain the old schema at an immutable release/tag
URL and document migration and version selection before changing the rolling
schema. To pin validation today, replace `main` in the modeline URL with the
commit or release tag matching your Talewright version, or use a local copy.
The VSIX always uses its bundled schema, independent of catalog/network access.

After SchemaStore accepts the submission, verify discovery in another
SchemaStore-capable YAML editor with no Talewright extension or schema modeline:
open `story.talewright.yaml`, request root-key completion, introduce an unknown key, and
remove it to check diagnostics clear. Until that verification is recorded, use
the explicit modeline or local-file fallback rather than assume auto-discovery.

The schema reflects structural key/type rules and editor-friendly canonical YAML
forms. The compiler remains authoritative for story-specific references, custom
ruleset registration, integer bounds and Tale expressions. YAML tooling can also
report structural errors differently from compiler diagnostics. The reader
currently accepts some scalar spellings or YAML forms beyond the schema's
documented canonical values; use the forms in the examples for portable feedback.
For example, faction values must be mappings (`guild: {name: Guild}`): the
compiler silently ignores a scalar faction display name. Unknown keys are schema
errors but compiler warnings (`TW4006`). Creation preset IDs must contain a
non-whitespace character and be no longer than 80 characters.

Under `vars`, integers and quoted text (including `""` and `"12"`) are supported.
The initial value fixes the variable's type. Completion and hover identify
`integer` or `text` from compiler analysis, including unsaved manifest changes.
Live compiler diagnostics reject incompatible assignments and numeric operations
on text. Use quoted YAML text values for consistent compiler/schema feedback.

## Not included

- Signature help, inlay hints, rename, lint, formatting, or runtime debugging.
- Implicit project builds/assembly execution, gameplay, network service, or telemetry.

The language server uses only shared compiler/authoring analysis. Custom C#
registrations require explicitly exported data-only metadata; see
[project metadata](https://github.com/squid-farm/talewright/blob/main/docs/developer/language-server.md#client-contract-and-workspace-behavior).

Use **Ctrl+Space** in a divert (`-> dest`), speech character, metadata directive,
condition, or substitution to request story-aware completion. The shared server
offers scene, character, stat, item, faction, flag, variable, predicate, and
modifier references from unsaved whole-story analysis. It also completes
story-specific values such as manifest `start: "dest"` without replacing quotes.
Ordinary prose receives no semantic story suggestions; VS Code can still show
its own word/snippet suggestions. YAML must be parseable, and required custom
metadata must be available. See the
[capabilities and manual walkthrough](https://github.com/squid-farm/talewright/blob/main/docs/developer/language-server.md#semantic-completion)
for supported slots, exact replacement behavior, and limitations.

Hover a known reference or declaration for its kind/type and authored declaration
context. Scene directives and expressions also show syntax help; manifest keys use
the bundled schema's descriptions. Unsaved edits are included. Unknown/partial
symbols and ordinary prose return no semantic hover, and required missing custom
metadata is reported explicitly. Authored text is displayed as code, not active
Markdown links or HTML. No runtime values or C# signatures are invented. See the
[hover walkthrough](https://github.com/squid-farm/talewright/blob/main/docs/developer/language-server.md#source-aware-hover)
for a beginner-friendly check in VS Code and other LSP clients.

Keep using `talewright validate <story-folder>` for correctness checks. See the
[Tale reference](https://github.com/squid-farm/talewright/blob/main/docs/reference/tale-syntax.md)
for supported syntax.

## Build and test

Contributors need Node.js 22 or later, npm, the .NET 10 SDK, and `unzip` for package
inspection. Linux editor smoke tests need a display (or `xvfb-run`) and the normal
VS Code native prerequisites.

From this directory:

```bash
npm ci
npm test
dotnet test ../../engine/tests/Talewright.Tale.Tests -c Release
dotnet build ../language-server/src/Talewright.LanguageServer -c Release
npm run package
npm run test:package
npm run test:editor
# On a headless Linux machine, use: xvfb-run -a npm run test:editor
```

Tests download VS Code 1.100.3 into the ignored `.vscode-test` directory unless
`VSCODE_EXECUTABLE_PATH` points to an existing editor executable (for example,
`/usr/share/code/code`, not the `bin/code` CLI wrapper). The isolated editor smoke
also installs the Red Hat YAML extension from the Marketplace and tests schema
validation, diagnostic clearing, completion, hover, and modeline fallback.
Tokenization tests use
that editor's built-in Markdown grammar with real TextMate/Oniguruma engines,
not mocked regex matching. Compiler tests expand every snippet in a minimal
story with the referenced scenes and default generic ruleset.
Shared YAML fixtures under `tests/fixtures/manifests` run against both the schema
and `StoryCompiler`; compiler tests assert diagnostics and materialized values
and defaults, including documented schema/compiler differences.

`npm run package` bundles the repository MPL-2.0 license and produces
`artifacts/talewright.vsix`, including the bundled thin LSP client but no .NET
server binaries or test tooling.
`npm run test:package` verifies the exact archive contents.
`npm run test:editor` installs that VSIX into a temporary isolated profile,
verifies file association, actual snippet expansion/Tab navigation, prose typing
and comment toggling, then exercises compiler diagnostics and clearing on an
unsaved edit using the local Release server build, and
removes the profile. It does not install anything into your normal VS Code
profile. Run packaging first.

## Beginner-friendly manual test plan

1. Install the VSIX in a new VS Code profile with no other language extensions.
   Open a sample `.tale` file and confirm the language indicator says **Tale**.
2. Compare a scene header, `@requires`, `> guide:`, a `~` effect, a choice, and a
   `::: skill-check` against ordinary prose. They should be distinguishable.
3. Try `*emphasis*`, `**strong**`, inline backticks, a link, and an image
   placeholder. Confirm a line beginning `*emphasis*` is not styled as a choice.
4. Write `\{literal\}` and toggle a line comment. Literal braces should not start
   a template, and `//` should comment the whole line.
5. Use **Insert Snippet** to insert each of the five snippets. Tab through its
   fields and replace the example IDs. Validate a complete story with the CLI;
   referenced targets must exist.
6. Leave a template, quote, choice label, or emphasis unfinished, then add a new
   `=== next.scene` line. The new header should recover its normal color.
7. Type prose containing apostrophes and braces. Check that no unexpected closing
   characters are inserted. Try different light/dark themes; exact colors depend
   on the theme.
8. Open `story.talewright.yaml` with Red Hat YAML installed. Confirm key completion,
   descriptions, unknown-key and wrong-type diagnostics. Remove a problem and
   confirm its diagnostic clears. Try the modeline in another YAML language
   server if available. The extension must not launch a CLI, debugger, or story
   session.

The automated installed-VSIX smoke covers Tale association, the five default
snippet expansions and placeholder navigation, prose typing, and comment
toggling. Automated package tests also verify the bundled schema and YAML
contribution. They do not substitute for manually checking Red Hat YAML
completion/diagnostics, visual/theme checks, or testing every operating system.
