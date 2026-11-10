# Talewright: the headless engine

The Talewright story engine in one NuGet package: compile Talemark stories, run them, and save and
restore playthroughs from your own C# code. It bundles the seven projects below, with their XML docs.
Each keeps its own assembly and namespace. There are no Blazor or Photino dependencies.

```bash
dotnet add package Talewright --version VERSION
```

Replace `VERSION` with a published version, such as `0.5.0-preview.1`.

| Module | Responsibility |
|---|---|
| [Core](../../engine/src/Talewright.Core/README.md) | Typed state, events, ruleset contracts, deterministic randomness |
| [Graphs](../../engine/src/Talewright.Graphs/README.md) | Directed graphs and reachability, used for story analysis |
| [Rules](../../engine/src/Talewright.Rules/README.md) | Conditions, effects, checks, triggers, storylet selection |
| [Narrative](../../engine/src/Talewright.Narrative/README.md) | Templates, grammar, presentation-independent documents |
| [Rulesets.Generic](../../engine/src/Talewright.Rulesets.Generic/README.md) | The opt-in, replaceable d20 RPG ruleset (`ruleset: rpg`) |
| [Talewright.Talemark](../../engine/src/Talewright.Talemark/README.md) | Talemark source loading, compilation, validation, diagnostics |
| [Runtime](../../engine/src/Talewright.Runtime/README.md) | Story sessions, frames, event narration, history and saves |

- **Which package:** `Talewright` for headless tooling or a custom front end; `Talewright.Player` for the
  supplied Blazor UI (it brings this package in). Story authors need neither.
- **Dependencies:** YamlDotNet is a package dependency, not bundled.
- **Build-time validation:** the package includes an opt-in MSBuild target. Set `TalewrightStoryProject`
  to your validator console app's project file (`.csproj`, not its compiled executable; the target
  restores and builds it) and `TalewrightStoryRoot` to the story folder, and call
  `StoryProjectValidator.Validate` with the same `CompilerOptions` factory as your hosts. Invalid Talemark
  then fails the build with source-positioned diagnostics. See the
  [C# story example](../../examples/CSharpStory/README.md).

More: [C# extensions and packages](../../docs/developer/csharp-extensions.md) ·
[architecture](../../docs/developer/architecture.md).
