# Talewright.Tale — the story compiler

Turns a story folder (`story.yaml` + any number of `.tale` files) into a typed `StoryDefinition`, or a list of
diagnostics with file, line and column. This is the **only** project that parses text, and it runs once per load.
The syntax itself is specified in the [Tale syntax reference](../../docs/reference/tale-syntax.md).

```csharp
var result = StoryCompiler.Compile(StorySource.FromDirectory("samples/LostRelic"));
if (!result.Success)
{
    foreach (var d in result.Errors) Console.WriteLine(d);   // chapters/01-camp.tale:12:5: error TW2002: Unknown scene 'camp.hubb'.
    return;
}

var session = new StorySession(result.Story!);
```

Folder and ZIP loaders both accept at most 1,000 story files, 5 MiB per file and
50 MiB total expanded text. Folder traversal rejects symbolic links inside the
story and paths deeper than 64 directories. Only `story.yaml` and `.tale` files
are loaded; unrelated files are not published as story content.

## Pipeline (`StoryCompiler.Compile`)

1. **Manifest.** `StoryYamlReader` reads `story.yaml` (YamlDotNet node model, so error positions are exact and
   nothing depends on reflection). Builds the ruleset (`base` → factory from `CompilerOptions.Rulesets`, then
   `disable`, then `stats`), characters, items, factions, flags, vars and triggers.
2. **Discover.** The Tale parser collects scene ids and actual effect blocks; the expression lexer collects
   assignment targets from those blocks and manifest effects. Comments, prose and choice labels do not
   declare flags/vars. Forward references across files remain supported.
3. **Parse.** `TaleParser` walks each file line by line (files in path order). Prose goes through `InlineParser`;
   conditions, substitutions and effects through `ExpressionParser`. Choices/effects/diverts inside `{if}` blocks
   get the condition ANDed in.
4. **Validate.** `StoryValidator` checks the whole story (start scene, dead ends, fall-through, hubs, reachability).
5. **Assemble.** If there were no errors, build the `StoryDefinition`. Warnings never block compilation.

## Files

| File | Contents |
|---|---|
| `Compilation/StoryCompiler.cs` | The pipeline above. |
| `Compilation/StorySource.cs` | Path → text map. `FromDirectory`, `FromZip(stream)` (strips one wrapping folder), `TaleFiles` (sorted), `ReadTitle()`. |
| `Compilation/CompilerOptions.cs` | `Predicates` (`@Name`), `Modifiers` (`#mod`), `Rulesets` (name → factory; defaults `generic` and `none`). Also `CompilationResult` (`Story`, `Diagnostics`, `Success`, `Errors`, `Warnings`). |
| `Compilation/StoryValidator.cs` | Whole-story structural checks (TW3xxx). Reachability is skipped when there are errors so one bad divert doesn't produce a wall of warnings. |
| `Parsing/StoryYamlReader.cs` | `story.yaml` → manifest + world. Unknown keys are warnings (TW4006) so typos are noticed. |
| `Parsing/TaleParser.cs` | Line classifier and scene builder. Handles `===`, `@directives`, `~` effects, `*` choices (+ `requires`, effects, checks, divert), `->` diverts, `::: skill-check` blocks, `{if}/{else}/{/if}` blocks, speech lines, paragraphs, and `\{` escapes. |
| `Parsing/InlineParser.cs` | Prose → `TemplateInline`s. Masks `{…}` as private-use placeholders, lets Markdig handle `*em*`/`**strong**`/`` `code` `` (paragraph parser only, autolinks off), then restores substitutions and inline `{if}`. Also `SourceText` for mapping offsets back to line/column. |
| `Parsing/ExpressionParser.cs` | Recursive descent with type checking: conditions (`player:str >= 12 and player has rope`), numbers, substitutions (`player:first-name#possessive`), effects (`player:hp -= 2`). |
| `Parsing/Lexer.cs` | Tokens for the expression parser. |
| `Parsing/StorySymbols.cs` | Declared names (stats, characters, items, factions, flags, vars, scenes) for resolution. |
| `Model/StoryDefinition.cs` | The compiled story: `StoryDefinition`, `SceneDefinition` (an `IStorylet`), `ChoiceDefinition`, `CheckBranch`, `CheckOutcome`, `ConditionalEffect`, `ConditionalDivert`, `DivertTarget` (incl. `End`). |
| `Diagnostics/Diagnostic.cs` | `Diagnostic`, `DiagnosticSeverity`, `SourceLocation`, `DiagnosticBag`. |

## Diagnostic codes

`TW1xxx` syntax, `TW2xxx` references and types, `TW3xxx` structure, `TW4xxx` story.yaml. The full table is in
the [error code reference](../../docs/reference/diagnostics.md). To find where a code is raised: `grep -rn TW2002 src/`.

## Semantics worth knowing

- A choice (or check outcome) with no `-> target` **falls through** to the scene's first matching `->`,
  evaluated after the choice's effects.
- A line starting with `*` is a choice only if followed by a space, tab or `[`, so `*emphasis*` at the start of a
  paragraph is still prose.
- Comparing a faction standing with a tier name (`faction:explorers-guild >= friendly`) compares tier order.
