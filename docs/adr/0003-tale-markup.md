# ADR 0003: `.tale` Markdown-superset authoring compiled to a typed model

**Status:** Accepted

Authors need something light. The engine wants strong typing and no string parsing at runtime.

**Decision:** `.tale` uses inline Markdown (parsed with Markdig) plus line directives. A single compiler library validates every reference against the story schema and produces an immutable typed model. That compiler is reused by the player (no-code stories), by MSBuild (C# story projects, M2) and by the VS Code language server (M3).
