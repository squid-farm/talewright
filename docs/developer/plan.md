# Design decisions

The thinking behind how Talewright is built. For how the code works today, read the
[architecture tour](architecture.md). For where the project is going, see the [roadmap](../roadmap.md).
Live work is tracked in [GitHub Issues](https://github.com/squid-farm/talewright/issues) and the
[project board](https://github.com/orgs/squid-farm/projects/1).

## What Talewright is for

An engine for text-first RPGs and visual novels where:

- what happens next depends on **rules over typed game state**: stats, levels, inventory, factions,
  relationships and history;
- authors write **plain text files** with no code, and can add **C#** when they need more power;
- the layers stay **decoupled**: presentation, narrative rendering, rules and core state;
- stories ship to the **web and desktop**, including one-click play on itch.io.

## Principles

- **Easy to start, legible as stories grow.** Talemark syntax favours readable stories over
  implementation-driven uniformity. New syntax and reserved characters are added only when real stories
  need them; configuration belongs in `story.talewright.yaml`.
- **Only report what's certain.** Authoring checks flag mistakes that can be detected with certainty.
  Problems that depend on what happens during play belong in playtesting and the debugger.
- **Off until used.** Gameplay systems stay out of the way until a story declares or uses them.
- **One compiler, many front ends.** The CLI, MSBuild, the language server and the players share the same
  compiler and diagnostics.

## Decisions

| # | Decision |
|---|---|
| D1 | .NET 10, C# and Blazor. Desktop uses Photino; web uses Blazor WebAssembly. |
| D2 | The name is **Talewright**; namespaces and packages are `Talewright.*`. |
| D3 | License: [MPL-2.0](../../LICENSE). |
| D4 | Stories are written in **Talemark** (`.tale` files): prose with bold and italic formatting, plus story directives and expressions. Backticks are ordinary text. `story.talewright.yaml` holds story configuration. The compiler is `Talewright.Talemark`; see the [operator registry](../reference/talemark-operators.md). |
| D5 | Two authoring paths share **one compiler**: a no-code story folder or ZIP compiled when the player loads it, and a C# story project compiled at build time with errors as build diagnostics. The runtime never parses text during play. |
| D6 | The narrative layer produces a **presentation-agnostic document model**, not HTML. |
| D7 | Conditions can be inline expressions or named C# predicates. Both compile to the same predicate tree and run in one evaluator. |
| D8 | Randomness is **optional** and seeded: `randomness: off | checks-only`, used only by skill checks. |
| D9 | State is **event-sourced**: a fold over events, which makes rewind, debugging and visit conditions possible. Player saves store replay inputs (seed, choices, cursor and character selection), not the event log. |
| D10 | Combat is not built in. It is planned as an optional module. |
| D11 | Names, pronouns (including custom sets), possessives and a/an are handled by the engine through an `IGrammar` seam, with English first. Possessives follow Chicago style (`Burns's`). |
| D12 | Superseded by D20. |
| D13 | Saves record format and story identity. Incompatible saves are rejected without being modified. Migrating saves between versions is separate, planned work. |
| D14 | Stories are **never built into the engine**. Samples are standalone story folders in `samples/`. |
| D15 | `talewright publish` bakes a story into the player to produce web and desktop ZIPs. |
| D16 | Authoring tools are **editor-neutral**. The VS Code extension is a thin client over the C# language server, not a second implementation. |
| D17 | Validation checks correctness. Lint adds configurable style advice. Automatic fixes never guess at the author's intent. |
| D18 | Reliability comes before breadth: core correctness, saves and authoring come before new gameplay systems. |
| D19 | Milestones are release targets, not dependency chains. Real dependencies are recorded as GitHub blockers. |
| D20 | Gameplay systems are off until a story uses them: ready-made RPG stats and levelling with `ruleset: rpg`, items with `items`, factions with `factions`, skill checks by writing `skill-check`. Future systems follow the same rule. |

## Extending with code

- **Story files only:** `.tale` plus YAML.
- **C# story project:** reference the `Talewright` NuGet packages, register named predicates, stats,
  modifiers, grammar modifiers and rulesets, and build your own web and desktop app. See
  [C# extensions](csharp-extensions.md).
- **Runtime plugins (planned):** plugins loaded by the standard player. Sandboxed scripts are preferred on
  the web; trusted DLLs on desktop.
