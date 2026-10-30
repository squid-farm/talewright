using Talewright.Core.Identity;
using Talewright.Rules.Expressions;

namespace Talewright.Narrative.Templates;

/// <summary>Inline text styling. Flags so styles nest (<c>***strong emphasis***</c>).</summary>
[Flags]
public enum TextStyle
{
    None = 0,
    Emphasis = 1,
    Strong = 2,
    Code = 4,
}

/// <summary>
/// A compiled narrative template: what the author wrote, with every reference already resolved to a typed
/// <see cref="Expr"/>. Rendering it against game state produces a <see cref="Rendering.RenderedDocument"/>.
/// </summary>
public abstract record TemplateBlock;

public sealed record ParagraphTemplate(IReadOnlyList<TemplateInline> Inlines) : TemplateBlock;

/// <summary>A line of dialogue (<c>&gt; guide: …</c>).</summary>
public sealed record SpeechTemplate(EntityId Speaker, IReadOnlyList<TemplateInline> Inlines) : TemplateBlock;

/// <summary>Block-level conditional (<c>{if}</c> … <c>{/if}</c> on their own lines).</summary>
public sealed record ConditionalBlockTemplate(Expr Condition, IReadOnlyList<TemplateBlock> Then, IReadOnlyList<TemplateBlock> Else) : TemplateBlock;

public abstract record TemplateInline;

public sealed record TextTemplate(string Text) : TemplateInline;

public sealed record StyledTemplate(TextStyle Style, IReadOnlyList<TemplateInline> Children) : TemplateInline;

public sealed record LineBreakTemplate : TemplateInline;

/// <summary>Image media with a resolved story-root-relative asset path and effective alt text; presentation is separate.</summary>
public sealed record MediaTemplate(string MediaId, string AltText) : TemplateInline;

/// <summary>
/// <c>{player:first-name#possessive}</c>: a value plus a chain of text modifiers.
/// </summary>
/// <param name="Value">Expression producing the value.</param>
/// <param name="Modifiers">Resolved modifiers applied left to right.</param>
/// <param name="IsPlural">Grammatical number of the value. Pronoun and item references resolve their original grammar from state at render time.</param>
public sealed record SubstitutionTemplate(Expr Value, IReadOnlyList<ITextModifier> Modifiers, bool IsPlural = false) : TemplateInline;

/// <summary>Inline conditional inside a paragraph.</summary>
public sealed record ConditionalInlineTemplate(Expr Condition, IReadOnlyList<TemplateInline> Then, IReadOnlyList<TemplateInline> Else) : TemplateInline;
