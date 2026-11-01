# ADR 0003: Talemark authoring compiled to a typed model

**Status:** Accepted

Authors need something light. The engine wants strong typing and no string parsing at runtime.

**Decision:** Talemark uses inline Markdown (parsed with Markdig) plus line directives and inline expressions, written in `.tale` files. A single compiler library validates every reference against the story schema and produces an immutable typed model. That compiler is reused by the player (no-code stories), by MSBuild (C# story projects, M2) and by the VS Code language server (M3).

## Language name

[Issue #55](https://github.com/squid-farm/talewright/issues/55) adopts **Talemark** (tale + markup)
as the user-facing **story-authoring language** name. Talewright remains the engine, player, and
tooling brand. Use “Talemark syntax” for language constructs, “Talemark source” for authored text,
and “`.tale` files” for files. The separate `story.talewright.yaml` manifest is not Talemark source.

The original display-name decision did not rename APIs or formats. The separate
[compiler identity migration (#231)](../csharp-extensions.md#compiler-identity-migration) now names
the compiler namespace and assembly `Talewright.Talemark`. Retain `.tale`, code fences and editor
language ID `tale`, grammar scope `text.tale`, aggregate package IDs, and individual types such as
`TaleParser`. Editor display names, debugger labels, and author-facing help use Talemark too;
neither naming change alters story syntax or runtime behavior.

An unrelated [Talemark film/book diary app](https://talemark.app/en/) already uses the name.
The project lead accepted that discoverability trade-off; use “Talewright's Talemark” where context
is needed. The name is not claimed to be unique or legally cleared.

## Authoring tools (M2/M3 shipped; M9 planned)

The CLI and editors share C# Talemark analysis, not just an output format. `talewright validate` (M2, shipped) exposes
compiler checks with human-readable and JSON diagnostics plus an explicit exit policy. `talewright lint`
(M3, shipped) adds configurable Talemark-aware authoring/style rules without duplicating compiler checks.
Validation is static checking, not proof that every playthrough succeeds.

A C# Language Server Protocol (LSP) server (M3, shipped) exposes live diagnostics,
context-aware completion, and hover/syntax help to any LSP-capable editor. The VS Code extension provides
highlighting, language configuration, snippets, and a thin LSP client; it does not contain a second parser
for semantic analysis. The extension also supports syntax highlighting and snippets independently of the server.

The runtime compiler model remains source-free. Authoring tooling provides a separate partial-analysis
surface with symbols and source ranges for incomplete input, unsaved-buffer overlays, and invalidation when
Talemark/YAML files change. This keeps editor state out of the runtime AST. C# custom registrations require
explicit project integration or exported metadata shared across
CLI, MSBuild, and LSP; editing must not implicitly execute arbitrary project code.

`talewright lint --fix` is planned for M9, lowest priority, not an M3 or M4 release gate.
Only safe, deterministic, non-overlapping edits are eligible: preserve prose/comments, detect files
changed since analysis, reanalyze after edits, and
require idempotence. Ambiguous story repairs and general formatting are not part of this decision.
Generic Markdown linting/formatting cannot be applied wholesale because Talemark has different structural
semantics.

These are planned capabilities, not implemented interfaces. See [the plan](../plan.md#45-talemark-compiler-talewrighttalemark)
for scope, relative effort, dependencies, and acceptance checks.
