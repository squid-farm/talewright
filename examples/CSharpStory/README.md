# Explicit C# story extensions

Requires .NET 10. Restore `Talewright` and `Talewright.Player` at the same version
from an authorized feed (or a local package directory); no reflection discovery.

```sh
dotnet build Web -p:TalewrightVersion=0.1.0-preview.1
dotnet build Desktop -p:TalewrightVersion=0.1.0-preview.1
dotnet run --project Web -p:TalewrightVersion=0.1.0-preview.1
dotnet run --project Desktop -p:TalewrightVersion=0.1.0-preview.1
```

How it fits together:

- **One factory.** `StoryExtensions.Create()` in the Story library registers a predicate, a text modifier
  and a ruleset. Both hosts pass its options to the shared player, and the Validator uses the same
  factory at build time.
- **Build-time validation.** The package's `buildTransitive/Talewright.targets` runs the
  `TalewrightStoryProject` validator against `TalewrightStoryRoot` before host compilation. Invalid Talemark
  emits `file(line,column): error TWxxxx: message` and fails the build. Design-time IDE builds skip it.
  The properties may also be passed globally with `-p:`; the nested validator build strips them to avoid
  recursion.
- **Culture-named files.** Embedded story resources set `WithCulture="false"`, so files like
  `chapter.fr.tale` stay in the story assembly instead of a satellite assembly.
- **Web.** Includes the private WebAssembly dev-server package for `dotnet run`. Deploy the
  `dotnet publish` output to any static host.

Rules for extensions:

- Change the explicit compatibility ID whenever extension behavior changes; otherwise old saves would
  replay against new logic.
- Predicates and modifiers must be deterministic, for replay and saves.
- Extensions are trusted author code, not a sandbox or plugin format.
- `talewright publish` doesn't discover extensions; publish these hosts with `dotnet publish`.
