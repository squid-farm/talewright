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
| `Talewright` | Headless engine, compiler, opt-in RPG ruleset and runtime only. For tooling or a replacement frontend. |
| `Talewright.Cli` | The `talewright` .NET tool. Not a library dependency. |

Both libraries share one version. `Talewright.Player` is a UI library, not a runnable app: your
application still supplies the host (Photino, Blazor WebAssembly, …) and a story library. Hosts,
samples and test helpers are not packaged.

`Talewright` is a packaging shell around the seven headless projects (Core, Graphs, Rules, Narrative,
Rulesets.Generic, Talemark, Runtime). They keep their own namespaces and assembly names, but they are not
published as separate packages.

<a id="older-runtime-compatibility-spike"></a>

## Supported .NET versions

The seven engine libraries and the `Talewright` package target **net8.0, net9.0 and net10.0**.
.NET 8 is the oldest supported runtime; .NET 5/6 and .NET Standard are not targets. Every
framework group in the package contains all seven assemblies and their XML documentation.

This matters only if you embed the headless engine. Authors and players don't need it. The
repository SDK, the supplied Blazor player, the hosts, CLI, debugger, language server and C#
examples all stay on .NET 10. Browser builds and self-contained downloads carry their own runtime,
so players don't need .NET installed. Building a standalone game still requires SDK 10.

The engine needs no polyfills or extra packages on .NET 8. Three test calls use an explicit
`Enumerable.Reverse` so that older targets bind the same overload.

CI runs the normal .NET 10 suite and also builds and runs the six engine test suites on .NET 8;
see [Contributing](../../CONTRIBUTING.md#set-up) for the commands. Release verification checks every
framework's assemblies and dependency groups, and the package-consumer check compiles against all
three targets. These checks don't cover every operating system or architecture.

Supporting a runtime is not the same as Microsoft supporting it. Microsoft support for .NET 8 ends
November 10, 2026; use .NET 10 for new deployments. See the
[.NET lifecycle table](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

<a id="compiler-identity-migration"></a>

## Compiler assembly name

The Talemark compiler lives in the `Talewright.Talemark` namespace and assembly
(`engine/src/Talewright.Talemark`). Very early builds called it `Talewright.Tale`; if you have code
from then, change `Talewright.Tale.*` to `Talewright.Talemark.*` and rebuild. There is no
compatibility shim. Some API names keep the shorter form (`TaleBinder`, `TaleFiles`), as do the
`.tale` file extension, the editor language ID `tale` and the grammar scope `text.tale`.

## Install from nuget.org

Releases are published to **nuget.org**; you don't need a GitHub token or a GitHub Packages feed.

```bash
dotnet add package Talewright.Player --version VERSION
# or, engine only:
dotnet add package Talewright --version VERSION
```

Replace `VERSION` with a published version, such as `0.5.0-preview.1`. Preview versions must be
requested explicitly. If nuget.org isn't in your NuGet configuration, add
`https://api.nuget.org/v3/index.json` as a source.

## Writing extensions

- [C# story example](../../examples/CSharpStory/README.md): registering a predicate and validating Talemark at build time.
- [Extension points](architecture.md#6-extension-points): what can be replaced and where.
- [Writing your own ruleset](../../engine/src/Talewright.Rulesets.Generic/README.md#writing-your-own-ruleset).
- [Authoring metadata](authoring-analysis.md#c-project-registrations): export the same registrations for editor analysis without automatically loading project code.
