using System.Text;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Grammar;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;

namespace Talewright.Narrative.Rendering;

/// <summary>
/// Renders template ASTs against a read-only game state. Pure: rendering never changes state, so the same
/// template + state always produces the same document.
/// </summary>
public sealed class TemplateRenderer(IGrammar grammar, NamedPredicateRegistry predicates)
{
    public TemplateRenderer()
        : this(EnglishGrammar.Instance, NamedPredicateRegistry.Empty)
    {
    }

    public IGrammar Grammar => grammar;

    public RenderedDocument Render(IEnumerable<TemplateBlock> blocks, IGameStateView state)
    {
        var output = new List<RenderedBlock>();
        RenderBlocks(blocks, new EvaluationContext(state, predicates), output);
        return new RenderedDocument(output);
    }

    /// <summary>Renders inline content only (choice labels, titles).</summary>
    public IReadOnlyList<RenderedInline> RenderInlines(IEnumerable<TemplateInline> inlines, IGameStateView state)
    {
        var output = new List<RenderedInline>();
        RenderInlines(inlines, TextStyle.None, new EvaluationContext(state, predicates), output);
        return Normalize(output);
    }

    private void RenderBlocks(IEnumerable<TemplateBlock> blocks, EvaluationContext context, List<RenderedBlock> output)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ParagraphTemplate p:
                    var inlines = Render(p.Inlines, context);
                    if (inlines.Count > 0)
                    {
                        output.Add(new RenderedParagraph(inlines));
                    }

                    break;
                case SpeechTemplate s:
                    var speaker = context.State.GetCharacterDefinition(s.Speaker);
                    output.Add(new RenderedSpeech(s.Speaker, speaker.FirstName, Render(s.Inlines, context)));
                    break;
                case ConditionalBlockTemplate c:
                    RenderBlocks(ExpressionEvaluator.IsTrue(c.Condition, context) ? c.Then : c.Else, context, output);
                    break;
                default:
                    throw new NotSupportedException($"Unknown block {block.GetType().Name}.");
            }
        }
    }

    private IReadOnlyList<RenderedInline> Render(IEnumerable<TemplateInline> inlines, EvaluationContext context)
    {
        var output = new List<RenderedInline>();
        RenderInlines(inlines, TextStyle.None, context, output);
        return Normalize(output);
    }

    private void RenderInlines(IEnumerable<TemplateInline> inlines, TextStyle style, EvaluationContext context, List<RenderedInline> output)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextTemplate t:
                    output.Add(new RenderedText(t.Text, style));
                    break;
                case StyledTemplate s:
                    RenderInlines(s.Children, style | s.Style, context, output);
                    break;
                case LineBreakTemplate:
                    output.Add(new RenderedLineBreak());
                    break;
                case MediaTemplate m:
                    output.Add(new RenderedMedia(m.MediaId, m.AltText) { Style = style });
                    break;
                case SubstitutionTemplate s:
                    output.Add(new RenderedText(Substitute(s, context), style));
                    break;
                case ConditionalInlineTemplate c:
                    RenderInlines(ExpressionEvaluator.IsTrue(c.Condition, context) ? c.Then : c.Else, style, context, output);
                    break;
                default:
                    throw new NotSupportedException($"Unknown inline {inline.GetType().Name}.");
            }
        }
    }

    private string Substitute(SubstitutionTemplate substitution, EvaluationContext context)
    {
        var text = ExpressionEvaluator.Evaluate(substitution.Value, context).ToString();
        var item = substitution.Value is ItemNameRef itemName ? context.State.World.Items[itemName.Item] : null;
        var isPlural = item is not null
            ? item.NameAgreement == GrammaticalNumber.Plural
            : substitution.Value is PronounRef pronoun
                ? context.State.GetCharacterDefinition(pronoun.Character).Pronouns.Agreement == GrammaticalNumber.Plural
                : substitution.IsPlural;
        var modifierContext = new ModifierContext(grammar, isPlural, item?.Uncountable ?? false);
        foreach (var modifier in substitution.Modifiers)
        {
            text = modifier.Apply(text, modifierContext);
        }

        return text;
    }

    /// <summary>
    /// Merges adjacent runs with the same style, collapses whitespace left behind by false conditionals and
    /// trims the edges. Returns an empty list if nothing visible remains.
    /// </summary>
    private static IReadOnlyList<RenderedInline> Normalize(List<RenderedInline> inlines)
    {
        var merged = new List<RenderedInline>();
        var buffer = new StringBuilder();
        TextStyle? bufferStyle = null;

        void Flush()
        {
            if (bufferStyle is { } s && buffer.Length > 0)
            {
                merged.Add(new RenderedText(buffer.ToString(), s));
            }

            buffer.Clear();
            bufferStyle = null;
        }

        foreach (var inline in inlines)
        {
            if (inline is RenderedText text)
            {
                if (bufferStyle != text.Style)
                {
                    Flush();
                    bufferStyle = text.Style;
                }

                buffer.Append(text.Text);
            }
            else
            {
                Flush();
                merged.Add(inline);
            }
        }

        Flush();

        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i] is RenderedText t)
            {
                var collapsed = CollapseSpaces(t.Text);
                if (i == 0)
                {
                    collapsed = collapsed.TrimStart();
                }

                if (i == merged.Count - 1)
                {
                    collapsed = collapsed.TrimEnd();
                }

                merged[i] = t with { Text = collapsed };
            }
        }

        merged.RemoveAll(i => i is RenderedText { Text.Length: 0 });
        return merged;
    }

    private static string CollapseSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            var isSpace = c == ' ';
            if (!(isSpace && lastWasSpace))
            {
                sb.Append(c);
            }

            lastWasSpace = isSpace;
        }

        return sb.ToString();
    }
}
