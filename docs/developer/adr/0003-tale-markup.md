# ADR 0003: `.tale` Markdown-superset authoring compiled to a typed model

**Status:** Accepted

Authors need something light. The engine wants strong typing and no string parsing at runtime.

**Decision:** `.tale` uses inline Markdown (parsed with Markdig) plus line directives. A single compiler library validates every reference against the story schema and produces an immutable typed model. That compiler is reused by the player (no-code stories), by MSBuild (C# story projects, M2) and by the VS Code language server (M3).

## Planned authoring tools

The CLI and editors share C# Tale analysis, not just an output format. `talewright validate` (M2) exposes
compiler checks with human-readable and JSON diagnostics plus an explicit exit policy. `talewright lint`
(M3) adds configurable Tale-aware authoring/style rules without duplicating compiler checks.
Validation is static checking, not proof that every playthrough succeeds.

A C# Language Server Protocol (LSP) server (M3; proposed CLI host: `talewright lsp`) exposes live diagnostics,
context-aware completion, and hover/syntax help to any LSP-capable editor. The VS Code extension provides
highlighting, language configuration, snippets, and a thin LSP client; it does not contain a second parser
for semantic analysis. Highlighting/snippets can ship before the language server.

The compiler currently produces a source-free runtime model and no story on errors. Editor tooling needs
a separate partial-analysis surface with symbols and source ranges even for incomplete input, unsaved
buffer overlays, and invalidation when Tale/YAML files change. Do not burden the runtime AST with editor
state. C# custom registrations require explicit project integration or exported metadata shared across
CLI, MSBuild, and LSP; editing must not implicitly execute arbitrary project code.

`talewright lint --fix` is planned for M9, lowest priority, not an M3 or M4 release gate.
Only safe, deterministic, non-overlapping edits are eligible: preserve prose/comments, detect files
changed since analysis, reanalyze after edits, and
require idempotence. Ambiguous story repairs and general formatting are not part of this decision.
Generic Markdown linting/formatting cannot be applied wholesale because Tale has different structural
semantics.

These are planned capabilities, not implemented interfaces. See [the plan](../plan.md#45-tale-compiler-talewrighttale)
for scope, relative effort, dependencies, and acceptance checks.
