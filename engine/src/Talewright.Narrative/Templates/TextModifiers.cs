using Talewright.Narrative.Grammar;

namespace Talewright.Narrative.Templates;

/// <summary>Grammar of the original substitution value, retained throughout the modifier chain.</summary>
public sealed record ModifierContext(IGrammar Grammar, bool IsPlural, bool IsUncountable = false);

/// <summary>The output kind used when validating a chain of text modifiers.</summary>
public enum TextModifierValueKind
{
    Text,
    FormattedText,
}

/// <summary>
/// A <c>#modifier</c> in a substitution. Register custom ones in <see cref="TextModifierRegistry"/>
/// (e.g. <c>#roman</c> for regnal numbers) before compiling a story.
/// </summary>
public interface ITextModifier
{
    string Name { get; }

    string Apply(string text, ModifierContext context);

    bool AcceptsInput(TextModifierValueKind input) => input == TextModifierValueKind.Text;

    TextModifierValueKind OutputKind => TextModifierValueKind.Text;

    TextStyle OutputStyle => TextStyle.None;
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
    /// <summary>The modifier name written after <c>#</c>.</summary>
    public const string ModifierName = "one-many";

    public string Name => ModifierName;

    public string Apply(string text, ModifierContext context) => context.IsPlural ? Plural : Singular;
}

internal sealed class FormattingTextModifier(string name, TextStyle style) : ITextModifier
{
    public string Name => name;

    public string Apply(string text, ModifierContext context) => text;

    public bool AcceptsInput(TextModifierValueKind input) => true;

    public TextModifierValueKind OutputKind => TextModifierValueKind.FormattedText;

    public TextStyle OutputStyle => style;
}

/// <summary>The set of modifiers available to a story. <see cref="CreateDefault"/> contains the built-ins.</summary>
public sealed class TextModifierRegistry
{
    /// <summary>The built-in possessive modifier name.</summary>
    public const string PossessiveName = "possessive";
    /// <summary>The built-in indefinite-article modifier name.</summary>
    public const string IndefiniteArticleName = "a";
    /// <summary>The built-in definite-article modifier name.</summary>
    public const string DefiniteArticleName = "the";
    /// <summary>The built-in proximal demonstrative modifier name.</summary>
    public const string ThisName = "this";
    /// <summary>The built-in distal demonstrative modifier name.</summary>
    public const string ThatName = "that";
    /// <summary>The built-in bold-formatting modifier name.</summary>
    public const string BoldName = "bold";
    /// <summary>The built-in italic-formatting modifier name.</summary>
    public const string ItalicName = "italic";

    private readonly Dictionary<string, ITextModifier> _modifiers = new(StringComparer.Ordinal);

    private static readonly ITextModifier[] BuiltIns =
    [
        new DelegateTextModifier(PossessiveName, (t, c) => c.Grammar.Possessive(t, c.IsPlural)),
        new DelegateTextModifier("capitalize", (t, c) => c.Grammar.Capitalize(t)),
        new DelegateTextModifier("upper", (t, _) => t.ToUpperInvariant()),
        new DelegateTextModifier("lower", (t, _) => t.ToLowerInvariant()),
        new DelegateTextModifier(IndefiniteArticleName, (t, c) => c.IsPlural || c.IsUncountable ? "some " + t : c.Grammar.WithIndefiniteArticle(t)),
        new DelegateTextModifier(DefiniteArticleName, (t, _) => "the " + t),
        new DelegateTextModifier(ThisName, (t, c) => (c.IsPlural ? "these " : "this ") + t),
        new DelegateTextModifier(ThatName, (t, c) => (c.IsPlural ? "those " : "that ") + t),
        new FormattingTextModifier(BoldName, TextStyle.Strong),
        new FormattingTextModifier(ItalicName, TextStyle.Emphasis),
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
