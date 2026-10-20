# Architecture

Section 4 of [PLAN.md](PLAN.md#4-architecture) has the full design. This page covers the rules the code has to follow.

## Layers

```
Player (Blazor RCL) ──► Runtime (IStorySession) ──► Narrative ──► Rules ──► Core
                                    └──► Tale compiler ──┘
```

- **Core** is the RPG engine. It knows nothing about text, templates or UI.
- **Rules** evaluates typed expression trees against a read-only `IGameStateView`. It does no reflection and no runtime code generation, which keeps it safe under WASM trimming.
- **Narrative** turns template ASTs into a `RenderedDocument` (blocks and styled runs plus media placeholders). It never produces HTML.
- **Tale** is the only place that parses text, and it runs once per story load or build.
- **Runtime** is the only orchestrator: command → events → state → triggers → selection → render → `RenderedFrame`.
- **Player** depends only on Runtime contracts and renders frames.

## Invariants

1. State changes **only** by applying `GameEvent`s. State is `fold(initial, events)`.
2. Dice results live in events. Replaying the log never re-rolls.
3. Stats, tiers, skill-check resolution and leveling come from the **ruleset**, never hard-coded.
4. Stories live outside the engine (`samples/`, or user folders).
5. There is no fixed "next scene" pointer. Movement is an event, which leaves room for open-world locations and hubs.
