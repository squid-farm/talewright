# Tale for VS Code

Highlight Talewright `.tale` stories and insert common story structures without
running your story, the CLI, or a language server.

## Install

This extension is distributed as a local VSIX, not through the Marketplace.
Use VS Code 1.100 or later.

1. Obtain `talewright.vsix` from the **Editor tooling** workflow artifact, or build
   it using the commands below.
2. In VS Code, open **Extensions**, choose **... > Install from VSIX...**, and
   select the file. Reload VS Code if prompted.
3. Open a `.tale` file. The language indicator at the bottom should say **Tale**.

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

## Not included

- `story.yaml` schema/completion: tracked separately in
  [#119](https://github.com/squid-farm/talewright/issues/119).
- Compiler diagnostics, semantic completion, hover, lint, formatting or LSP.
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
`/usr/share/code/code`, not the `bin/code` CLI wrapper). Tokenization tests use
that editor's built-in Markdown grammar with real TextMate/Oniguruma engines,
not mocked regex matching. Compiler tests expand every snippet in a minimal
story with the referenced scenes and default generic ruleset.

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
8. Open `story.yaml`: this slice should not claim to provide YAML diagnostics or
   completion. The extension must not launch a CLI, debugger, or story session.

The automated installed-VSIX smoke covers association, the five default snippet
expansions and placeholder navigation, prose typing, and comment toggling. It
does not substitute for visual/theme checks or testing every operating system.
