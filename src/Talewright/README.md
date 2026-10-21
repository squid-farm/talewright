# Talewright: the headless engine

One .NET library and NuGet package containing the complete engine. It has no
Blazor or Photino dependencies. Existing `Talewright.*` namespaces are preserved;
the folders below are logical modules, not separate packages or assemblies.

| Module | Responsibility |
|---|---|
| [Core](Core/README.md) | Typed state, events, ruleset contracts, deterministic randomness |
| [Rules](Rules/README.md) | Conditions, effects, checks, triggers, storylet selection |
| [Narrative](Narrative/README.md) | Templates, grammar, presentation-independent documents |
| [Rulesets.Generic](Rulesets.Generic/README.md) | The default, behaviorally replaceable d20 ruleset |
| [Tale](Tale/README.md) | Story source loading, compilation, validation, diagnostics |
| [Runtime](Runtime/README.md) | Story sessions, frames, event narration |

The compiler uses Markdig and YamlDotNet. The runtime still consumes the compiler's
story model, and the compiler supplies the generic ruleset by default; these are
not optional assembly dependencies within the package.

Use `Talewright` for headless tooling or a custom frontend. For the supplied Blazor
UI, install only `Talewright.Player`: its dependency brings in this engine.
Ordinary story authors do not need either package in a C# project.

See the root [installation and release guidance](../../README.md#c-customization-packages)
and [architecture](../../docs/ARCHITECTURE.md).
