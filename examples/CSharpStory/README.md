# Explicit C# story extensions

Requires .NET 10. Restore `Talewright` and `Talewright.Player` at the same version
from an authorized feed (or a local package directory); no reflection discovery.

```sh
dotnet build Web -p:TalewrightVersion=0.1.0-preview.1
dotnet build Desktop -p:TalewrightVersion=0.1.0-preview.1
dotnet run --project Web
dotnet run --project Desktop
```

The Story library owns one `StoryExtensions.Create()` factory registering a
predicate, text modifier and ruleset. Both hosts pass its options into the shared
player; the Validator executable uses the same factory at build time. Package-provided
`buildTransitive/Talewright.targets` runs the project configured by
`TalewrightStoryProject` against `TalewrightStoryRoot` before host compilation.
Invalid Tale emits `file(line,column): error TWxxxx: message` and fails the build.
Design-time IDE builds do not launch validation.

The explicit compatibility ID must change when extension behavior changes.
Predicates/modifiers must remain deterministic for replay and saves. Arbitrary
C# is trusted author code, not a sandbox or plugin-loading format. No-code CLI
publishing does not discover extensions; publish these hosts with `dotnet publish`.
