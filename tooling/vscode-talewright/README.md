# Tale for VS Code

Highlight Talewright `.tale` stories, insert common story structures, and provide
structural validation and key completion for `story.yaml` without running your
story, the CLI, or a language server.

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

## `story.yaml` support

With Red Hat YAML installed, the extension associates `story.yaml` with the bundled
JSON Schema. It validates manifest structure, offers known keys, and shows field
descriptions. It does not know story-specific IDs or whether a referenced scene,
stat, item, or faction exists; use `talewright validate <story-folder>` for those
compiler checks.

The same schema can be used by another YAML language server with this modeline at
the top of `story.yaml`:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/squid-farm/talewright/main/tooling/vscode-talewright/schemas/story.schema.json
```

Or point `$schema` to a local copy of
[`schemas/story.schema.json`](./schemas/story.schema.json). SchemaStore discovery
is tracked separately and is not required for either option.

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

- Compiler diagnostics, story-specific completion, hover, lint, formatting or LSP.
- Any CLI execution, gameplay, network service, telemetry, or extension runtime.

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
dotnet test ../../tests/Talewright.Tale.Tests -c Release
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
`artifacts/talewright.vsix`, without runtime dependencies or test tooling.
`npm run test:package` verifies the exact archive contents.
`npm run test:editor` installs that VSIX into a temporary isolated profile,
verifies file association, actual snippet expansion/Tab navigation, prose typing
and comment toggling, then
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
8. Open `story.yaml` with Red Hat YAML installed. Confirm key completion,
   descriptions, unknown-key and wrong-type diagnostics. Remove a problem and
   confirm its diagnostic clears. Try the modeline in another YAML language
   server if available. The extension must not launch a CLI, debugger, or story
   session.

The automated installed-VSIX smoke covers Tale association, the five default
snippet expansions and placeholder navigation, prose typing, and comment
toggling. Automated package tests also verify the bundled schema and YAML
contribution. They do not substitute for manually checking Red Hat YAML
completion/diagnostics, visual/theme checks, or testing every operating system.
