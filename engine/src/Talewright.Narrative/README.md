# Talewright.Narrative — the template renderer

Turns a **typed template AST** plus game state into a **presentation-agnostic document**. It never parses text
(the compiler builds the AST) and never emits HTML (the Player does that).

```
ParagraphTemplate[ Substitution(NameRef(player, First), [#possessive]), Styled(Strong, Text(" torch")) ]
            │  TemplateRenderer.Render(blocks, state)
            ▼
RenderedParagraph[ RenderedText("Mara's", None), RenderedText(" torch", Strong) ]
```

## Folders

| File | Contents |
|---|---|
| `Templates/Template.cs` | Input AST. Blocks: `ParagraphTemplate`, `SpeechTemplate`, `ConditionalBlockTemplate`. Inlines: `TextTemplate`, `StyledTemplate`, `LineBreakTemplate`, `MediaTemplate`, `SubstitutionTemplate`, `ConditionalInlineTemplate`. `TextStyle` flags (Emphasis, Strong, Code) nest. |
| `Templates/TextModifiers.cs` | `ITextModifier`, `DelegateTextModifier`, `TextModifierRegistry`. Defaults: `#possessive`, `#capitalize`, `#upper`, `#lower`, `#a` (a/an or some), `#the`, `#this`, `#that`. The compiler creates `OneManyTextModifier` for the pronoun/item-name two-string selector. Modifiers chain left to right while retaining the original reference's grammar. Text-variable assignment copies only text. |
| `Grammar/EnglishGrammar.cs` | `IGrammar` + `EnglishGrammar`: Chicago-style possessives (`Burns's`, `the guards'`, `the children's`), indefinite articles (`an hour`, `a unicorn`), capitalisation. |
| `Rendering/TemplateRenderer.cs` | `Render(blocks, state)` and `RenderInlines(inlines, state)`. Evaluates substitutions and conditionals, uses effective pronoun agreement for `#one-many`, applies modifiers, merges adjacent runs, collapses whitespace left by false conditionals, drops empty paragraphs. |
| `Rendering/RenderedDocument.cs` | Output model: `RenderedDocument` → `RenderedParagraph`, `RenderedSpeech`, `RenderedNote` (`NoteKind` Check/Change/Info); inlines `RenderedText`, `RenderedLineBreak`, `RenderedMedia`. `ToPlainText()` for tests and logs. |

## Extending

- **New `#modifier`**: `registry.Register(new DelegateTextModifier("shout", (text, ctx) => text.ToUpperInvariant() + "!"))`
  and pass the registry in `CompilerOptions.Modifiers` (the compiler validates modifier names at load time).
- **Different grammar/language**: implement `IGrammar` and pass it to `StorySession`.
- **Media**: `MediaTemplate` → `RenderedMedia(MediaId, AltText)`. Literal alt is copied. An alt template
  renders with the same state as the surrounding prose, then becomes the plain alt string. A front-end decides how
  (or whether) to show it.
