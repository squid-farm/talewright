# Talewright.Talemark — the Talemark compiler

Turns a story folder (`story.talewright.yaml` + any number of `.tale` files) into a typed `StoryDefinition`, or a list of
diagnostics with file, line and column. This is the **only** project that parses text, and it runs once per load.
The compiler's assembly and namespace are `Talewright.Talemark`; it ships inside the aggregate
`Talewright` package, not as a separately published package. See the
[C# migration note](../../../docs/developer/csharp-extensions.md#compiler-identity-migration).
The syntax itself is specified in the [Talemark syntax reference](../../../docs/reference/talemark-syntax.md).

```csharp
var result = StoryCompiler.Compile(StorySource.FromDirectory("samples/LostRelic"));
if (!result.Success)
{
    foreach (var d in result.Errors) Console.WriteLine(d);   // chapters/01-camp.tale:12:5: error TW2002: Unknown scene 'camp.hubb'.
    return;
}

var session = new StorySession(result.Story!);
```

The root `story.talewright.yaml` keeps core settings under `game:`; world declarations remain at the root.
Folder loading has no authored file-count, per-file or total-text ceiling. It still rejects
symbolic links inside the story and paths deeper than 64 directories. ZIP text loading retains
its separate defenses: at most 1,000 source files, 5 MiB per file and 50 MiB total expanded text,
enforced while reading actual bytes. These archive-text bounds do not include images.
The retained image owner enforces declared entry sizes during lazy reads, without a combined
text/image budget. Neither source kind is certified or trusted.
The root manifest, supporting `.talewright.yaml` fragments and `.tale` files are loaded as text;
images have a separate path catalogue. Unrelated files are not published as story content.

## Pipeline (`StoryCompiler.Compile`)

1. **Manifest.** `StoryYamlReader` reads `story.talewright.yaml` (YamlDotNet node model, so error positions are exact and
   nothing depends on reflection). Builds the ruleset (`base` → factory from `CompilerOptions.Rulesets`, then
   `disable`, then `stats`), characters, items, factions, flags, vars and triggers.
2. **Discover.** The Talemark parser collects scene ids and actual effect blocks; the expression lexer collects
   assignment targets from those blocks and manifest effects. Comments, prose and choice labels do not
   declare flags/vars. Forward references across files remain supported.
3. **Parse.** `TaleParser` walks each file line by line (files in path order). Prose goes through `InlineParser`;
   conditions, substitutions and effects through `ExpressionParser`. `SceneDefinition.Steps` preserves source
   order, effect batches and nested conditions. Every step has a deterministic source-position ID and half-open
   UTF-16 source span; choice keys remain `c0`, `c1`, ... in source order.
4. **Validate.** `StoryValidator` checks final choice blocks, explicit choice targets, dead ends, unreachable
   steps, hubs and reachability. Shared `SceneSteps` walks describe possible flow without evaluating game state.
5. **Assemble.** If there were no errors, build the `StoryDefinition`. Warnings never block compilation.

## Accepted source provenance

Successful `CompilationResult.SourceMap` pairs the immutable input `StorySource` snapshot with a
`SceneId` to `SceneSourceRange` map from the existing parser. Ranges use inclusive 1-based whole lines:
declaration through the line before the next declaration, or EOF. Inter-scene comments/blank lines
belong to the preceding scene; a terminal newline creates an empty final line. Line counting uses
`String.ReplaceLineEndings`, matching parsing and the debugger display.

Errors produce no accepted map. Manually constructed results/C# story models may also have no map;
consumers must report absent provenance, not infer it from IDs or reparse source independently.
The map stays outside `StoryDefinition` and does not affect fingerprints, runtime execution or saves.
This is a narrow reusable scene-provenance seam, not the partial-symbol/overlay analysis API in #29
and remains separate from the step-level execution spans carried by `SceneDefinition.Steps`.

## Diverts and hub offers (Preview)

Preview: these types may change before v1. `StoryDiverts.From(story)` lists every divert (`->`) and hub offer
in a compiled story, so tools don't walk scene steps themselves. It uses the same reachable-steps walk as
`SceneSteps.Targets`: lines that can never run (TW3012) produce no divert, and a conditional divert is listed
whatever its condition.

- **`StoryDivert`**: from-scene, target (a scene or END), `DivertKind` (scene divert, choice, choice skill
  check outcome, entry check outcome), source order and, for choices, the choice key, label template (none
  for a textless choice) and `requires`. Its enclosing block `{if}`s are kept as compiled expressions.
  `Condition` exposes their formatted parts, a combined Talemark expression and the state reads in first-use
  order. An `{else}` part is formatted as a negation. Conditions are described, not evaluated.
  The id is the scene, the step id of the divert's line and `success`/`failure` for a skill check outcome,
  e.g. `camp/step:12:1/success`; like step ids, it changes when lines move.
- **`HubOffer`**: a hub scene to each other scene matching its tags, ignoring `@hub-limit`, with the hub's
  tag query. `Condition` describes the offered scene's `@requires`; `Once` reports whether it has `@once`.
  A hub that always diverts before reaching its choices offers nothing. The id is `hub/hub:offered`,
  e.g. `camp/hub:camp.well`.
- **`StoryCondition`**, **`StoryStateRead`** and **`StoryStateReadKind`**: `Parts` keeps the conditions
  separate; `Text` combines them with `and` while preserving grouping. `Reads` lists distinct references
  to stats, standings and tiers, flags, number and text variables, character names and pronouns, faction
  and item names, held items, visited scenes and named predicates. A named predicate is marked opaque
  because its C# implementation cannot be inspected.

## Story map (Preview)

`StoryMap.Build(story)` returns the map for a successfully compiled story. `Nodes` contains every scene
in declaration order followed by END; each node includes its id, title, tags and reachability.
`Diverts` and `HubOffers` reuse the compiler's shared map data and include the conditions described above.
Reachability follows diverts and hub offers only. It does not evaluate conditions or decide whether a
player can meet them.

## Scene details (Preview)

`StoryMap.SceneDetails(scene)` returns Preview details for a `SceneDefinition` from a successfully compiled
story. For a scene node, resolve its `SceneId` against that same story and pass the resulting scene; END has
no scene details. The API takes a scene object rather than a free-form id, so it has no unknown-id case.

The result includes title, tags, `@requires`, `@once`, `@priority`, hub settings, ordered prose templates,
effects with enclosing `{if}` conditions and entry check or choice skill check outcomes, plus choices and
entry checks. Choice labels and prose remain compiled templates; nothing is rendered or evaluated.
Speakers are unique in first-seen order. Block conditions are described, not evaluated, and an `{else}`
context is formatted as a negation.

## Files

| File | Contents |
|---|---|
| `Compilation/StoryCompiler.cs` | The pipeline above. |
| `Compilation/StorySource.cs` | Path → text map. `FromDirectory`, `FromZip(stream)` (strips one wrapping folder), `TaleFiles` (sorted), `ReadTitle()`. |
| `Compilation/CompilerOptions.cs` | `Predicates` (`@Name`), `Modifiers` (`#mod`), `Rulesets` (name → factory; defaults `generic` and `none`). Also `CompilationResult` (`Story`, `Diagnostics`, `Success`, `Errors`, `Warnings`). |
| `Compilation/StoryValidator.cs` | Whole-story structural checks (TW3xxx). Reachability is skipped when there are errors so one bad divert doesn't produce a wall of warnings. |
| `Parsing/StoryYamlReader.cs` | `story.talewright.yaml` → manifest + world. Unknown keys are warnings (TW4006) so typos are noticed. |
| `Parsing/TaleParser.cs` | Line classifier and scene builder. Handles `===`, `@directives`, `~` effects, `*` choices (+ `requires`, effects, checks, divert), `->` diverts, `::: skill-check` blocks, `{if}/{else}/{/if}` blocks, speech lines, paragraphs, and `\{` escapes. |
| `Parsing/InlineParser.cs` | Prose → `TemplateInline`s. Masks `{…}` as private-use placeholders, lets Markdig handle `*em*`/`**strong**`/`` `code` `` (paragraph parser only, autolinks off), then restores substitutions and inline `{if}`. Also `SourceText` for mapping offsets back to line/column. |
| `Parsing/ExpressionParser.cs` | Recursive descent with type checking: conditions (`player:str >= 12 and player has rope`), numbers, substitutions (`player:first-name#possessive`), effects (`player:hp -= 2`). |
| `Parsing/Lexer.cs` | Tokens for the expression parser. |
| `Parsing/StorySymbols.cs` | Declared names (stats, characters, items, factions, flags, vars, scenes) for resolution. |
| `Model/StoryDefinition.cs` | The compiled story: `StoryDefinition`, `SceneDefinition` (an `IStorylet` with ordered `Steps`; `Choices` is derived), `ChoiceDefinition`, `CheckBranch`, `CheckOutcome`, `DivertTarget` (incl. `End`). |
| `Model/SceneStep.cs` | Ordered scene steps (prose, effect, `{if}` branch, choice, divert, check), each with a stable `step:<line>:<column>` ID and source span, plus the shared `SceneSteps` walks for successors and reachability. |
| `Model/StoryDiverts.cs` | Preview: `StoryDiverts`, every divert and hub offer in a story. |
| `Model/StoryCondition.cs` | Preview: formatted conditions and unique state reads for diverts and hub offers. |
| `Model/StoryMap.cs` | Preview: public read-only story map API with nodes and reachability. |
| `Model/StorySceneDetails.cs` | Preview: compiled metadata, prose, effects, choices, entry checks and speakers for one scene. |
| `Diagnostics/Diagnostic.cs` | `Diagnostic`, `DiagnosticSeverity`, `SourceLocation`, `DiagnosticBag`. |

## Diagnostic codes

`TW1xxx` syntax, `TW2xxx` references and types, `TW3xxx` structure, `TW4xxx` story.talewright.yaml. The full table is in
the [error code reference](../../../docs/reference/diagnostics.md). To find where a code is raised: `grep -rn TW2002 src/`.

## Semantics worth knowing

- A scene runs in source order: each line sees only the lines above it, a block `{if}` is decided once, and a
  reached `->` leaves immediately. Every choice and choice-check outcome needs its own `-> target`; there is no
  scene fallback.
- A line starting with `*` is a choice only if followed by a space, tab or `[`, so `*emphasis*` at the start of a
  paragraph is still prose.
- Comparing a faction standing with a tier name (`faction:explorers-guild >= friendly`) compares tier order.
