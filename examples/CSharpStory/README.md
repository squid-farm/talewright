# Explicit C# story extensions

Requires .NET 10. Restore `Talewright` and `Talewright.Player` at the same version
from an authorized feed (or a local package directory); no reflection discovery.

```sh
dotnet build Web -p:TalewrightVersion=0.1.0-preview.1
dotnet build Desktop -p:TalewrightVersion=0.1.0-preview.1
dotnet run --project Web -p:TalewrightVersion=0.1.0-preview.1
dotnet run --project Desktop -p:TalewrightVersion=0.1.0-preview.1
```

The Story library owns one `StoryExtensions.Create()` factory registering a
predicate, text modifier and ruleset. Both hosts pass its options into the shared
player; the Validator executable uses the same factory at build time. Package-provided
`buildTransitive/Talewright.targets` runs the project configured by
`TalewrightStoryProject` against `TalewrightStoryRoot` before host compilation.
Invalid Tale emits `file(line,column): error TWxxxx: message` and fails the build.
The Web project includes the private WebAssembly development-server package for
`dotnet run`; deploy its `dotnet publish` output with a static web server.
Design-time IDE builds do not launch validation.
The opt-in properties may also be supplied globally with `-p:`; the nested
validator build strips them to avoid recursively invoking itself. Embedded story
resources set `WithCulture="false"` so filenames such as `chapter.fr.tale` remain
in the story assembly rather than being moved to a satellite assembly.

The explicit compatibility ID must change when extension behavior changes.
Predicates/modifiers must remain deterministic for replay and saves. Arbitrary
C# is trusted author code, not a sandbox or plugin-loading format. No-code CLI
publishing does not discover extensions; publish these hosts with `dotnet publish`.
