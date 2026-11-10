# Explicit C# story extensions

A small story with C# extensions, played by its own web and desktop hosts. Use it as a template when a
story needs a custom condition, text modifier or ruleset. Most stories don't; plain Talemark and the
`talewright` CLI are enough.

Requires .NET 10. The projects restore `Talewright` and `Talewright.Player` from nuget.org; pass the
version you want with `TalewrightVersion`, using the same version for both:

```sh
dotnet build Web -p:TalewrightVersion=VERSION
dotnet build Desktop -p:TalewrightVersion=VERSION
dotnet run --project Web -p:TalewrightVersion=VERSION
dotnet run --project Desktop -p:TalewrightVersion=VERSION
```

Replace `VERSION` with a published version, such as `0.5.0-preview.1`.

How it fits together:

- **One factory.** `StoryExtensions.Create()` in the Story library registers a predicate, a text modifier
  and a ruleset. Both hosts pass its options to the shared player, and the Validator uses the same
  factory at build time. Nothing is discovered by reflection.
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

More: [C# extensions and packages](../../docs/developer/csharp-extensions.md).
