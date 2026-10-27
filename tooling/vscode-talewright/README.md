# Talemark for VS Code

Write Talewright stories in **Talemark** (`.tale` files), insert common story structures, and get
schema support, shared semantic completion/hover, and live compiler diagnostics without
running your story.

## Install

**Optional:** You do not need this extension to write a story. A plain-text editor
and the browser player are enough. The Linux x64 VSIX includes a compatible self-contained
language server: no separate Talewright tool, .NET SDK/runtime, server-path setting or build is needed.
The Marketplace publishing pipeline is configured, but a live listing is not yet verified.

Use VS Code 1.100 or later on a supported Linux x64 extension host.

1. Install the [Red Hat YAML extension](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-yaml)
   from the VS Code Marketplace. It provides the YAML editor features used by the schema.
2. After the first public publication, install **Talewright** by **squid-farm** from
   [the Marketplace](https://marketplace.visualstudio.com/items?itemName=squid-farm.talewright).
   For a preview, choose **Switch to Pre-Release Version**.
   Until then, or as a fallback, obtain the Linux x64 VSIX from
   [GitHub Releases](https://github.com/squid-farm/talewright/releases) or the
   **talewright-vscode** artifact of a successful **Build** run. Contributors can build it below.
3. In VS Code, open **Extensions**, choose **... > Install from VSIX...**, and
   select the file. Reload VS Code if prompted.
4. Open a `.tale` file. The language indicator at the bottom should say **Talemark**.
   The language ID remains `tale`.
5. Trust the story folder to enable live compiler diagnostics and story-aware completion/hover.
   The extension launches its bundled server using stdio; it never downloads tools, builds a story
   project, discovers assemblies or executes story-project code automatically.
   Leave `talewright.languageServer.command` empty for normal installation.
   Configure `talewright.languageServer.command`/`args` only for a development override, or set
   `talewright.languageServer.enabled` to `false` for schema/syntax-only use.
   Reload the window after changing settings. Launch errors appear in the
   **Talewright Language Server** output channel. Untrusted workspaces never
   launch a process.

This package identifier is `squid-farm.talewright`. Only `linux-x64` is packaged; Windows, macOS,
Linux ARM, Alpine/musl, browser-hosted VS Code and universal fallback packages are not supplied.
Remote development needs a supported Linux x64 extension host.

### Supported Linux environment

The CI packaging and installed-editor checks run on **Ubuntu 24.04 x64**; local installed-editor
checks also run on Linux Mint 22.3 (Ubuntu 24.04-based). This is not a claim of support for every
Linux distribution. The bundled .NET runtime still requires OS libraries:
`libc6`, `libgcc-s1`, `libstdc++6`, `libicu74`, `libssl3t64` and `zlib1g` on Ubuntu 24.04,
in addition to [VS Code's native Linux prerequisites](https://code.visualstudio.com/docs/supporting/requirements).
Install missing system libraries with your distribution's package manager. No .NET installation
is required. Missing executable/runtime/system-library failures produce a notification and details in
**Talewright Language Server** output; no external-server fallback is attempted.

### Development server override

To run a local server build instead of the bundled version:

```json
{
  "talewright.languageServer.command": "dotnet",
  "talewright.languageServer.args": ["/absolute/path/to/Talewright.LanguageServer.dll", "--stdio"]
}
```

Only this override requires your own matching runtime/server. Clear the command and restore args to
`["--stdio"]` to return to the bundled server. Reload VS Code after changing settings or granting
workspace trust. See [server setup and other editors](https://github.com/squid-farm/talewright/blob/main/docs/developer/language-server.md).

## CLI actions (separate CLI installation required)

The Command Palette provides **Talewright: Validate Story**, **Talewright: Publish Story ZIP**
and **Talewright: Open Author Debugger**. These run the existing `talewright` CLI in a visible,
interactive task terminal. The bundled language server provides live editor help; it is **not**
the CLI and does not include the native debugger.

Install the CLI separately and put `talewright` on PATH, or set `talewright.cli.executablePath`
to its absolute executable path (paths with spaces are supported). No source builds, downloads
or installation run automatically. Until a public CLI release is available, source users need the
.NET 10 SDK: `dotnet build tooling/cli/src/Talewright.Cli -c Release`, then select the generated
`Talewright.Cli` executable (`Talewright.Cli.exe` on Windows), not its DLL, under
`tooling/cli/src/Talewright.Cli/bin/Release/net10.0/`.

Open the story folder in VS Code. Actions prefer the active file's containing story, use the only
story in the workspace otherwise, or ask you to choose. Nested `story.talewright.yaml` manifests
remain errors inside the parent story, not separate selectable stories. Dirty documents inside
the chosen story must be saved with your approval; cancellation or a failed save starts nothing.
Untrusted workspaces cannot run CLI actions or custom executables. CLI output and exit status
remain in the task terminal; a started task is not a success notification.

- **Validate Story** runs `talewright validate <story>`, using the CLI's compiler diagnostics.
- **Publish Story ZIP** asks for a ZIP destination, then runs
  `talewright publish <story> --output <file.zip>`. This contains story files for a Talewright
  player, **not** a standalone website or desktop game. The terminal asks whether to replace an
  existing ZIP; answer there. The extension never adds `--force`.
- **Open Author Debugger** runs `talewright debug <story>`. This opens the native Author Debugger,
  not VS Code breakpoints, a debug adapter, or F5 support. The task stays running while the window
  is open; use **Tasks: Terminate Task** to stop it. A graphical desktop and platform webview
  prerequisites are required: WebKitGTK 4.1 on Linux, WebView2 on Windows, WKWebView on macOS.
  Linux x64 is the installed-extension verification platform; other hosts are not verified here.
  See [CLI/debugger prerequisites](https://github.com/squid-farm/talewright/blob/main/docs/reference/cli.md).

For repeated debugging, manually add this task to your workspace's `.vscode/tasks.json`;
the extension never creates or edits that file:

```json
{
  "version": "2.0.0",
  "tasks": [{
    "label": "Debug my story",
    "type": "talewright",
    "command": "debug",
    "story": ".",
    "problemMatcher": []
  }]
}
```

`story` is relative to that workspace folder (or an absolute canonical story-root path within the
workspace). Tasks in a multi-root `.code-workspace` file are also supported: a relative `story`
must identify exactly one canonical story root across the open folders. If it matches more than
one, use an absolute path to disambiguate. Global/user tasks are not supported.
Run it using **Tasks: Run Task**. The same trust, CLI and save checks apply.

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
Brackets can be matched, but Talemark editing defaults disable automatic quote/bracket closing:
ordinary prose and literal braces should remain comfortable to type. You can
override these language-specific defaults in your VS Code settings.

The grammar distinguishes scene headers, metadata, speech, effects, conditions,
choices, substitutions, and checks. It also highlights inline Markdown emphasis,
strong text, code, `{image("path", "alt text")}` calls, and links. Old Markdown images
are highlighted as deprecated and reported with replacement guidance. Substitutions and conditionals inside a
link label keep their Talemark colors, because the compiler renders the label text
and drops the target. These colors describe syntax, not whether
your story compiles. The renderer still determines what appears during play;
images are currently placeholders, not image rendering support.
Use the `tale-image` snippet or complete `image(` inside a template. Hover describes the arguments;
inside the first quoted argument, completion suggests supported files from the
story's `images/` folder, including nested folders. Paths are case-sensitive.
Completion replaces the path content and escapes embedded quotes, leaving alt
text alone. It also works in unfinished calls, choice labels and dialogue.
Adding, renaming or deleting image files refreshes suggestions and image lookup
diagnostics without reopening the story. These editor checks use file paths, not
image bytes. Compilation and `talewright validate` use the same lookup diagnostics;
player rendering remains separate work.

Incomplete templates and emphasis recover at line boundaries so an unfinished
edit does not color the rest of the story. A `*` or `_` with no closing
delimiter later on the line stays plain text. Highlighting of formatting that
spans multiple lines, and some ambiguous delimiter runs, is consequently approximate.

## Story configuration support

Every story's root manifest is named `story.talewright.yaml` at the story root.
Supporting declarations may live in other `*.talewright.yaml` files in story
subfolders; they are part of the containing story. The basename
`story.talewright.yaml` is reserved for its single root manifest; a nested file
with that name produces a compiler error, not a separate story or fragment.
With Red Hat YAML installed, the extension associates every `*.talewright.yaml`
file with the bundled JSON Schema. It validates file structure, offers known keys,
and shows field descriptions without requiring a supporting file to repeat
whole-story settings. It does not know story-specific IDs or whether a referenced
scene, stat, item, or faction exists. The optional language server analyzes the
root manifest, supporting files, and `.tale` files as one story, including unsaved
edits, and supplies reference completion, hover, go-to-definition into the
declaring file, and compiler checks; use `talewright validate <story-folder>` for
command-line validation.

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
ruleset registration, integer bounds and Talemark expressions. YAML tooling can also
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
Talemark assignments can also introduce variables: `~ var:weather = "stormy"`.
Completion and hover use the same story-wide inference as validation, including
forward references, assignment chains and unsaved files. Incomplete or unresolved
declarations retain symbols marked `unresolved`, not a guessed integer type.

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
[Talemark reference](https://github.com/squid-farm/talewright/blob/main/docs/reference/talemark-syntax.md)
for supported syntax.

## Build and test

Contributors packaging on Linux x64 need Node.js 22 or later, npm, the .NET 10 SDK, Python 3 and `unzip` for package
inspection. Linux editor smoke tests need a display (or `xvfb-run`) and the normal
VS Code native prerequisites.

From this directory:

```bash
npm ci
npm test
dotnet test ../../engine/tests/Talewright.Talemark.Tests -c Release
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
`artifacts/talewright.vsix`, with Linux x64 target metadata, the bundled client and a matching
self-contained server/runtime plus .NET license/notices. It runs `dotnet publish` for the
language server within the existing package target, never a story project.
Pinned dependency license texts in `tests/server-notices.txt` are included in the VSIX notices;
updating a shipped server dependency requires reviewing/updating its corresponding notice.
`npm run test:package` verifies client assets, server/runtime files, native architecture,
executable permissions, target metadata and the self-contained runtime configuration.
`npm run test:editor` installs that VSIX into a temporary isolated profile,
verifies file association, actual snippet expansion/Tab navigation, prose typing
and comment toggling, then exercises compiler diagnostics and clearing on an
unsaved edit using the installed bundled server, with an external `dotnet` sentinel and runtime
lookup disabled. A separate restricted-mode pass in the same runner proves that the bundled
server does not start in an untrusted workspace. The runner
removes the profile. It does not install anything into your normal VS Code
profile. Run packaging first.

## Beginner-friendly manual test plan

1. Install the VSIX in a new VS Code profile with no other language extensions.
   Open a sample `.tale` file and confirm the language indicator says **Talemark**.
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

The automated installed-VSIX smoke covers Talemark file association, the five default
snippet expansions and placeholder navigation, prose typing, and comment
toggling. Automated package tests also verify the bundled schema and YAML
contribution. They do not substitute for manually checking Red Hat YAML
completion/diagnostics, visual/theme checks, or testing every operating system.
