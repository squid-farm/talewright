using Talewright.Narrative.Grammar;

namespace Talewright.Narrative.Templates;

/// <summary>What a modifier knows besides the text it transforms.</summary>
public sealed record ModifierContext(IGrammar Grammar, bool IsPlural);

/// <summary>
/// A <c>#modifier</c> in a substitution. Register custom ones in <see cref="TextModifierRegistry"/>
/// (e.g. <c>#roman</c> for regnal numbers) before compiling a story.
/// </summary>
public interface ITextModifier
{
    string Name { get; }

    string Apply(string text, ModifierContext context);
}

/// <summary>A modifier backed by a delegate.</summary>
public sealed class DelegateTextModifier(string name, Func<string, ModifierContext, string> apply) : ITextModifier
{
    public string Name => name;

    public string Apply(string text, ModifierContext context) => apply(text, context);

    public override string ToString() => "#" + name;
}

/// <summary>The set of modifiers available to a story. <see cref="CreateDefault"/> contains the built-ins.</summary>
public sealed class TextModifierRegistry
{
    private readonly Dictionary<string, ITextModifier> _modifiers = new(StringComparer.Ordinal);

    public static TextModifierRegistry CreateDefault() => new TextModifierRegistry()
        .Register(new DelegateTextModifier("possessive", (t, c) => c.Grammar.Possessive(t, c.IsPlural)))
        .Register(new DelegateTextModifier("capitalize", (t, c) => c.Grammar.Capitalize(t)))
        .Register(new DelegateTextModifier("upper", (t, _) => t.ToUpperInvariant()))
        .Register(new DelegateTextModifier("lower", (t, _) => t.ToLowerInvariant()))
        .Register(new DelegateTextModifier("a", (t, c) => c.Grammar.WithIndefiniteArticle(t)));

    public TextModifierRegistry Register(ITextModifier modifier)
    {
        _modifiers[modifier.Name] = modifier;
        return this;
    }

    public bool TryGet(string name, out ITextModifier modifier) => _modifiers.TryGetValue(name, out modifier!);

    public IEnumerable<string> Names => _modifiers.Keys;
}
