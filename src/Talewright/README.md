# Talewright: the headless engine

This is a packaging shell, not the engine's source project. It references the six
existing headless projects and includes their original assemblies and XML docs in
one NuGet package. Source folders, namespaces, assembly identities, and project
dependency boundaries remain unchanged. It has no Blazor or Photino dependencies.

| Module | Responsibility |
|---|---|
| [Core](../Talewright.Core/README.md) | Typed state, events, ruleset contracts, deterministic randomness |
| [Rules](../Talewright.Rules/README.md) | Conditions, effects, checks, triggers, storylet selection |
| [Narrative](../Talewright.Narrative/README.md) | Templates, grammar, presentation-independent documents |
| [Rulesets.Generic](../Talewright.Rulesets.Generic/README.md) | The default, behaviorally replaceable d20 ruleset |
| [Tale](../Talewright.Tale/README.md) | Story source loading, compilation, validation, diagnostics |
| [Runtime](../Talewright.Runtime/README.md) | Story sessions, frames, event narration |

The shell declares Markdig and YamlDotNet as package dependencies rather than
bundling their assemblies. Component project references are private to packaging,
so consumers do not need six component packages. The runtime still consumes the compiler's
story model, and the compiler supplies the generic ruleset by default; these are
not optional assembly dependencies within the package.

Use `Talewright` for headless tooling or a custom frontend. For the supplied Blazor
UI, install only `Talewright.Player`: its dependency brings in this engine.
Ordinary story authors do not need either package in a C# project.

The [C# authoring example](../../examples/CSharpStory/README.md) builds web and desktop
hosts from these packages with explicitly registered predicates, modifiers and rulesets.
The engine package includes an opt-in MSBuild target: set `TalewrightStoryProject`
to a validator executable project and `TalewrightStoryRoot` to the source folder.
Use `StoryProjectValidator.Validate` with the same `CompilerOptions` factory as
the hosts to emit stable source-position diagnostics and fail invalid builds.

See the root [installation and release guidance](../../README.md#c-customization-packages)
and [architecture](../../docs/ARCHITECTURE.md).
