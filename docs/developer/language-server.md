# Language server

`Talewright.LanguageServer` is a separate development-only .NET tool in
`tooling/language-server`. It references `Talewright.Authoring`; game libraries
and the existing CLI never reference the server. It runs one long-lived process,
using JSON-RPC/LSP 3.17 `Content-Length` framing over stdin/stdout. Stdout is
protocol-only; process failures go to stderr. No TCP listener, telemetry, project
build, assembly discovery, or gameplay execution is involved.

## Install and launch

Requires the .NET 10 runtime. Packages are released to the same authenticated
GitHub Packages feed as the CLI; configure that feed as described in the
[CLI reference](../reference/cli.md). Install a matching release:

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
The client defaults to `talewright-lsp --stdio`. Alternatively configure:

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

The server supports initialize/initialized, shutdown/exit, full-text
didOpen/didChange/didClose, didSave, and workspace/didChangeWatchedFiles. Positions
are UTF-16 and zero-based. Compiler point locations become zero-width ranges,
clamped to the current file; codes, messages, and error/warning severities remain
the compiler's. No completion, hover, formatting, or other semantic capability is
advertised. Unsupported requests return JSON-RPC method-not-found.

The nearest on-disk `story.yaml` identifies each story. An unsaved `story.yaml`
can establish its own root; otherwise a containing initialized workspace folder
is the fallback. Multiple story roots are analyzed independently.
An opened manifest always establishes its own directory as the root, including an
unsaved nested manifest. Existing Tale overlays and diagnostic ownership move
from ancestor workspaces to the new story; ancestor refreshes exclude that story.
Closing a document discards its overlay even if the subsequent disk reload fails.
Only file URIs are supported. Open overlays win over disk, and close reloads disk. Versions must
increase. Whole-story analysis is debounced by 150 ms and superseded snapshots
cannot publish. Diagnostics for fixed or deleted files are explicitly cleared.
Disk changes re-read the bounded shared story source loader; no project code is
executed. File-loading errors are reported, not treated as successful analysis.

Clients must forward create/change/delete events for `**/*.tale`, `**/story.yaml`,
project files, and configured authoring metadata. The VS Code client installs
these watchers, including the configured metadata path. Other clients
can use didSave for saved open documents, but need watched-file events for
closed-file additions/deletions. The server does not install its own filesystem
watcher or recursively open unrelated stories at initialization.

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

Shared lint is not integrated yet because #30 has no shared lint API. Compiler
diagnostics do not wait for it; future lint integration must deduplicate results.

## Neovim (another LSP client)

Neovim 0.11 or later can launch the installed tool:

```lua
vim.api.nvim_create_autocmd({ "BufReadPost", "BufNewFile" }, {
  pattern = { "*.tale", "story.yaml" },
  callback = function(event)
    vim.lsp.start({
      name = "talewright",
      cmd = { "talewright-lsp", "--stdio" },
      root_dir = vim.fs.root(event.buf, "story.yaml"),
      init_options = { requiresProjectMetadata = false },
    })
  end,
})
```

Open a Tale file in a story, change a choice target to a nonexistent scene,
and run `:lua vim.diagnostic.open_float()`. Restore the target without saving:
the compiler diagnostic should disappear. Save/close/reopen and confirm it
remains absent. For cross-file disk changes, configure the client to forward
`workspace/didChangeWatchedFiles` as described above.

The reproducible headless client smoke exercises initialization, compiler
diagnostics, an unsaved fix, clearing, and clean shutdown:

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

The stdio process tests cover lifecycle, incomplete Tale/invalid YAML, unsaved
buffers, cross-file create/change/delete, close-to-disk, diagnostic clearing,
superseded versions, metadata failures/recovery, and exact saved-input diagnostic
parity with the CLI. The installed-VSIX smoke preserves the existing
syntax/snippet/schema checks and adds compiler diagnostics/unsaved clearing.

Verified on Linux on 2026-10-23: stdio integration tests against both the build
and an installed local NuGet tool; the installed VSIX in an isolated VS Code
1.100.3 profile; and the headless Neovim 0.11.5 smoke above. Both editors received
compiler diagnostics and cleared them after an unsaved fix. This is local test
evidence, not a claim that the package has already been released.
