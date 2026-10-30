using Talewright.Narrative.Grammar;

namespace Talewright.Narrative.Templates;

/// <summary>Grammar of the original substitution value, retained throughout the modifier chain.</summary>
public sealed record ModifierContext(IGrammar Grammar, bool IsPlural, bool IsUncountable = false);

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

public sealed record OneManyTextModifier(string Singular, string Plural) : ITextModifier
{
    public string Name => "one-many";

    public string Apply(string text, ModifierContext context) => context.IsPlural ? Plural : Singular;
}

/// <summary>The set of modifiers available to a story. <see cref="CreateDefault"/> contains the built-ins.</summary>
public sealed class TextModifierRegistry
{
    private readonly Dictionary<string, ITextModifier> _modifiers = new(StringComparer.Ordinal);

    private static readonly ITextModifier[] BuiltIns =
    [
        new DelegateTextModifier("possessive", (t, c) => c.Grammar.Possessive(t, c.IsPlural)),
        new DelegateTextModifier("capitalize", (t, c) => c.Grammar.Capitalize(t)),
        new DelegateTextModifier("upper", (t, _) => t.ToUpperInvariant()),
        new DelegateTextModifier("lower", (t, _) => t.ToLowerInvariant()),
        new DelegateTextModifier("a", (t, c) => c.IsPlural || c.IsUncountable ? "some " + t : c.Grammar.WithIndefiniteArticle(t)),
        new DelegateTextModifier("the", (t, _) => "the " + t),
        new DelegateTextModifier("this", (t, c) => (c.IsPlural ? "these " : "this ") + t),
        new DelegateTextModifier("that", (t, c) => (c.IsPlural ? "those " : "that ") + t),
    ];

    public static TextModifierRegistry CreateDefault()
    {
        var registry = new TextModifierRegistry();
        foreach (var modifier in BuiltIns)
        {
            registry.Register(modifier);
        }
        return registry;
    }

    /// <summary>Recognizes sealed engine selectors and engine-owned modifier instances, never names alone.</summary>
    public static bool IsBuiltIn(ITextModifier modifier) =>
        modifier is OneManyTextModifier || BuiltIns.Any(builtIn => ReferenceEquals(builtIn, modifier));

    public TextModifierRegistry Register(ITextModifier modifier)
    {
        _modifiers[modifier.Name] = modifier;
        return this;
    }

    public bool TryGet(string name, out ITextModifier modifier) => _modifiers.TryGetValue(name, out modifier!);

    public IEnumerable<string> Names => _modifiers.Keys;
}
