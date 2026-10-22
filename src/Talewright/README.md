# Talewright: the headless engine

Packaging shell for the headless engine: it bundles the six projects below (with their XML docs) into
one NuGet package. Source folders, namespaces, assembly names and dependency boundaries are unchanged.
No Blazor or Photino dependencies.

| Module | Responsibility |
|---|---|
| [Core](../Talewright.Core/README.md) | Typed state, events, ruleset contracts, deterministic randomness |
| [Rules](../Talewright.Rules/README.md) | Conditions, effects, checks, triggers, storylet selection |
| [Narrative](../Talewright.Narrative/README.md) | Templates, grammar, presentation-independent documents |
| [Rulesets.Generic](../Talewright.Rulesets.Generic/README.md) | The default, behaviorally replaceable d20 ruleset |
| [Tale](../Talewright.Tale/README.md) | Story source loading, compilation, validation, diagnostics |
| [Runtime](../Talewright.Runtime/README.md) | Story sessions, frames, event narration |

- **Dependencies:** Markdig and YamlDotNet are package dependencies, not bundled. Component project
  references are private, so consumers install one package, not six.
- **Which package:** `Talewright` for headless tooling or a custom frontend; `Talewright.Player` for the
  supplied Blazor UI (it brings this package in). Story authors need neither.
- **Build-time validation:** the package includes an opt-in MSBuild target. Set `TalewrightStoryProject`
  to the validator console app's project file (`.csproj`, not its compiled executable; the target
  restores and builds it) and `TalewrightStoryRoot` to the story folder, and call
  `StoryProjectValidator.Validate` with the same `CompilerOptions` factory as your hosts. Invalid Tale
  fails the build with source-positioned diagnostics. See the
  [C# story example](../../examples/CSharpStory/README.md).

More: [C# extensions and packages](../../docs/developer/csharp-extensions.md) ·
[architecture](../../docs/developer/architecture.md).
