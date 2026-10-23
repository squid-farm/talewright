# C# extensions and NuGet packages

Story authors don't need any of this. Packages are for C# extensions (custom predicates, rulesets,
effects) and for apps that embed Talewright.

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
Rulesets.Generic, Tale, Runtime). They keep their own namespaces and assembly names, but they are not
published as separate packages.

## Install from GitHub Packages

The registry is GitHub Packages, **not nuget.org**, and it requires authentication even for public
packages. Create a classic personal access token with `read:packages` (authorize it for SSO if your
organization requires it), then:

```bash
dotnet nuget add source https://nuget.pkg.github.com/squid-farm/index.json --name talewright

# Keep the token out of the repository. This POSIX-shell example passes credentials through the environment.
export GITHUB_PACKAGES_TOKEN=...   # your PAT
export NuGetPackageSourceCredentials_talewright="Username=YOUR_GITHUB_USER;Password=$GITHUB_PACKAGES_TOKEN"

dotnet add package Talewright.Player --version VERSION
# or, engine only:
dotnet add package Talewright --version VERSION
```

Replace `VERSION` with a published version. Prereleases must be requested explicitly.

## Writing extensions

- [C# story example](../../examples/CSharpStory/README.md): registering a predicate and validating Tale at build time.
- [Extension points](architecture.md#6-extension-points): what can be replaced and where.
- [Writing your own ruleset](../../engine/src/Talewright.Rulesets.Generic/README.md#writing-your-own-ruleset).
- [Authoring metadata](authoring-analysis.md#c-project-registrations): export the same registrations for editor analysis without automatically loading project code.
