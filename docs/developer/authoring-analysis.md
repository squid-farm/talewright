# Shared authoring analysis

`Talewright.Tale.Analysis` is the compiler API for source-aware analysis.
It uses the same Tale parser, manifest reader, and compiler checks as runtime
compilation. It does not add editor state to `StoryDefinition` or change
`StoryCompiler.Compile` diagnostics.

`Talewright.Authoring` is a separate development-tool library containing workspace
management and project metadata integration. It references the compiler, never the
reverse, and is not included in the shipped `Talewright` or `Talewright.Player`
packages. Tool hosts reference its project explicitly; it is not a separately
published NuGet package. Its source and tests live in `tooling/authoring`, alongside
the other development tools. Only the parser-backed analysis API remains in the
compiler, so tools reuse parsing and semantic checks without shipping workspace
or project-metadata infrastructure with games.

## Snapshots and partial results

```csharp
var source = StorySource.FromDirectory(storyDirectory);
var analysis = StoryAnalyzer.Analyze(source);
```

The result contains the immutable source snapshot, diagnostics, declaration
symbols, and parser-recognized expression contexts. Symbols include the manifest
story ID, cross-file scenes, manifest characters/items/factions/stats/flags/variables,
effect-declared flags/variables, and available predicates/modifiers/rulesets. A
declaration range is absent for registered or built-in symbols that have no authored
location.
Ranges are half-open, with 1-based lines and UTF-16 columns; protocol hosts must
convert these to their own position conventions.

Incomplete expressions retain their original text and context even when parsing
reports an error. Semantically invalid YAML still contributes available
declarations. Syntactically invalid YAML cannot provide a trustworthy manifest
tree: Tale declarations and contexts are retained independently, but the API
does not invent a repaired manifest or reuse stale symbols from a prior snapshot.
Diagnostics remain the compiler's diagnostics for that exact input.

Recovery does not require a project ruleset named `generic` or invoke project
ruleset factories when the manifest is missing or syntactically invalid. It
retains dialogue substitutions even when the speaker cannot be resolved, plus
unfinished choice conditions/effects. Authoring ranges track the original Tale
whitespace and YAML scalar encoding: quotes and block headers are excluded;
escapes and folded whitespace map back to their authored spans rather than
being treated as offsets in decoded text. Compiler diagnostic points are unchanged.
YAML source mapping supports LF, CRLF, and CR line endings. Available trigger
conditions and effects are retained even when another field is missing or the
trigger ID is missing/duplicated. Skill-check difficulties are collected before
ruleset, actor, or stat resolution, and a bare `@requires` exposes an empty
condition range at the insertion point after the keyword.
Block and choice skill checks ending in `dc` similarly retain an empty numeric
context immediately after `dc`, including when trailing spaces were trimmed.

The shared `Talewright.Authoring.StoryLintAnalyzer` consumes these source ranges
for stable Tale-aware lint findings. The CLI integration is delivered in #150;
the language server also consumes the same rule results and configuration in #151.

The [language server](language-server.md) hosts this API for editor diagnostics.
This API exposes declaration ranges and expression extents, not a complete
reference-binding or formatting syntax tree. It does not provide completion,
hover, LSP transport, or evaluated gameplay values itself.

## Workspace lifecycle

`Talewright.Authoring.StoryWorkspace` owns one story's disk snapshot and unsaved document overlays.
`FindStoryRoot` finds the nearest ancestor containing `story.talewright.yaml`; a host can
instead construct a workspace explicitly for a new, unsaved story.

```csharp
var workspace = StoryWorkspace.FromDirectory(storyDirectory);
workspace.Open("chapters/start.tale", version: 1, unsavedText);
workspace.Change("chapters/start.tale", version: 2, editedText);
var update = await workspace.AnalyzeAsync(TimeSpan.FromMilliseconds(150), cancellationToken);
if (update.Published)
{
    Publish(update.Analysis);
}
```

Hosts forward filesystem add/change/delete notifications through `UpdateFile`
(null text means deletion). Open overlays take precedence, including for
`story.talewright.yaml`. Closing an overlay restores the latest disk content supplied by
the host. `Change` returns false for superseded document versions; lifecycle
misuse and invalid paths throw rather than silently succeed.

Any accepted change invalidates `Current`. Analysis is whole-story, debounced,
and performed on an immutable snapshot. A result superseded by any file/document
change has `Published == false` and cannot replace `Current`. Cancellation is
propagated. Hosts should cancel superseded requests to avoid unnecessary work;
there is no speculative incremental compiler, hidden watcher, or background
process in the library.

## C# project registrations

CLI/MSBuild validation and authoring must use the same project registrations.
Trusted in-process hosts may explicitly pass their `CompilerOptions` to
`StoryAnalyzer` or `StoryWorkspace`; do not mutate those registries while analysis
is running. Recreate the workspace when registrations change.
Editors must not discover and execute project
assemblies, build targets, or ruleset factories automatically.

For out-of-process editors, explicitly export a data-only manifest from the
project's existing registration factory in an author-invoked build/utility:

```csharp
var options = StoryExtensions.Create(); // The same factory used by project validation.
File.WriteAllText(outputPath, AuthoringProjectMetadata.Export(options).ToJson());
```

The authoring host reads the explicitly configured file:

```csharp
var metadata = AuthoringProjectMetadata.FromJson(File.ReadAllText(metadataPath));
var analysis = metadata.Analyze(source);
var workspace = StoryWorkspace.FromMetadata(source, metadata);
```

`AuthoringProjectMetadata` is also in `Talewright.Authoring`.
Version 1 carries predicate/modifier names and ruleset stat/tier/check-support
contracts, not executable code. Export runs the supplied ruleset factories only
as an explicit trusted operation; import never loads assemblies or evaluates
predicates, modifiers, checks, or gameplay state. A future host must provide
an explicit metadata path/configuration; there is no auto-build or automatic
assembly resolution. Refresh the workspace when registration metadata changes.

When project metadata is required but absent, construct the workspace with
`requiresProjectMetadata: true`. `FromDirectory` also detects a project file at
the story root. Analysis reports `TW5001` instead of claiming that built-in-only
analysis fully represents that project. Nested or external project layouts must
set this requirement explicitly. Missing/invalid metadata files should be
reported by the host; malformed/unsupported metadata throws an explicit
JSON/data exception.
