# C# extensions and NuGet packages

Story authors don't need any of this. Packages are for C# extensions (custom predicates, rulesets,
effects) and for apps that embed Talewright.

Extensions run as executable code in your host, not in a Talewright sandbox.
Custom hosts and registrations can change or bypass framework boundaries; see
[distribution and runtime safety](../reference/safety.md) before making safety
claims about a customized game.

## Which package?

| Package | Use |
|---|---|
| `Talewright.Player` | The usual choice. Supplied Blazor UI; brings in `Talewright` automatically. |
| `Talewright` | Headless engine, compiler, default ruleset and runtime only. For tooling or a replacement frontend. |
| `Talewright.Cli` | The `talewright` .NET tool. Not a library dependency. |

Both libraries share one version. `Talewright.Player` is a UI library, not a runnable app: your
application still supplies the host (Photino, Blazor WebAssembly, …) and a story library. Hosts,
samples and test helpers are not packaged.

`Talewright` is a packaging shell around the six headless projects (Core, Rules, Narrative,
Rulesets.Generic, Talemark, Runtime). They keep their own namespaces and assembly names, but they are not
published as separate packages.

## Older-runtime compatibility spike

The six engine libraries and aggregate `Talewright` NuGet package target **net8.0, net9.0
and net10.0**. .NET 8 is the compatibility floor; .NET 5/6 and .NET Standard are not targets.
The package includes all six assemblies and their XML documentation in every framework group.

This is for developers embedding the headless engine, not a prerequisite for Talemark authors
or players. The repository SDK, supplied Blazor Player, hosts, CLI, debugger, language server
and C# examples remain on .NET 10. Browser builds and self-contained downloads carry their runtime;
users do not need a matching .NET installation. Building standalone games still requires SDK 10.

No compatibility polyfills, legacy collection implementations, old test adapters or extra JSON
package are needed at this floor. Frozen collections, generated regular expressions, deterministic
dice and strict save/scenario serialization retain their original implementations.
Three test calls use explicit `Enumerable.Reverse` to avoid array/span overload binding differences
when the current compiler targets an older framework.

CI runs the normal .NET 10 suite and separately builds/runs the five engine suites for .NET 8.
See [Contributing](../../CONTRIBUTING.md#set-up) for commands. Release verification checks every
engine framework's assembly assets and dependency groups; clean package-consumer checks compile
against all three targets. These checks do not certify every operating system or architecture.

Compatibility is not a vendor-support extension. Microsoft support for .NET 8 ends November 10,
2026; prefer .NET 10 for new deployments. See the
[.NET lifecycle table](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

## Compiler identity migration

[Issue #231](https://github.com/squid-farm/talewright/issues/231) renames the compiler's namespace,
project, and assembly from `Talewright.Tale` to `Talewright.Talemark`. This is an intentional
source and binary breaking change for C# consumers, with no compatibility shim or type forwarding.

- Change imports and fully qualified names from `Talewright.Tale.*` to `Talewright.Talemark.*`.
- For source integrations, update project references to
  `engine/src/Talewright.Talemark/Talewright.Talemark.csproj`.
- For direct assembly integrations, replace `Talewright.Tale.dll` with `Talewright.Talemark.dll`.
- Rebuild extensions and hosts against the same updated Talewright packages. The aggregate NuGet
  package IDs remain `Talewright` and `Talewright.Player`; there is no new compiler package to install.

The compiler tests and their project directory are now `Talewright.Talemark.Tests`.
Individual API names such as `TaleParser` and `TaleFiles` are unchanged. So are `.tale` stories,
`story.talewright.yaml`, the editor language ID/code-fence name `tale`, and grammar scope `text.tale`.
The rename does not change story syntax, runtime behavior, or the save format; existing saves
retain the same story/source compatibility requirements.

## Install from nuget.org

Public releases publish to **nuget.org**; consuming them does not require a GitHub PAT or a
GitHub Packages feed. After the first public release:

```bash
dotnet add package Talewright.Player --version VERSION
# or, engine only:
dotnet add package Talewright --version VERSION
```

Replace `VERSION` with a published version, such as `0.1.0-preview.1`.
Prereleases must be requested explicitly. If nuget.org is not enabled in your NuGet configuration,
add `https://api.nuget.org/v3/index.json` as a source. Old GitHub Packages versions do not migrate
automatically; choose a version published by the new public pipeline.

## Writing extensions

- [C# story example](../../examples/CSharpStory/README.md): registering a predicate and validating Talemark at build time.
- [Extension points](architecture.md#6-extension-points): what can be replaced and where.
- [Writing your own ruleset](../../engine/src/Talewright.Rulesets.Generic/README.md#writing-your-own-ruleset).
- [Authoring metadata](authoring-analysis.md#c-project-registrations): export the same registrations for editor analysis without automatically loading project code.
