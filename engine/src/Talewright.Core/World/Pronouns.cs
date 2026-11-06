using System.Collections.ObjectModel;

namespace Talewright.Core.World;

/// <summary>
/// A full pronoun set. Use the built-ins or construct custom sets (neopronouns are first-class).
/// </summary>
/// <param name="Subject">she / he / they</param>
/// <param name="Object">her / him / them</param>
/// <param name="Possessive">Possessive determiner: her / his / their</param>
/// <param name="PossessivePronoun">Independent possessive: hers / his / theirs</param>
/// <param name="Reflexive">herself / himself / themselves</param>
/// <param name="Agreement">Grammatical agreement ("they are", not "they is"), not population.</param>
public sealed record Pronouns(
    string Subject,
    string Object,
    string Possessive,
    string PossessivePronoun,
    string Reflexive,
    GrammaticalNumber Agreement)
{
    public static Pronouns She { get; } = new("she", "her", "her", "hers", "herself", GrammaticalNumber.Singular);
    public static Pronouns He { get; } = new("he", "him", "his", "his", "himself", GrammaticalNumber.Singular);
    public static Pronouns They { get; } = new("they", "them", "their", "theirs", "themselves", GrammaticalNumber.Plural);
    public static Pronouns It { get; } = new("it", "it", "its", "its", "itself", GrammaticalNumber.Singular);

    /// <summary>Canonical keywords mapped to their built-in pronoun sets.</summary>
    public static IReadOnlyDictionary<string, Pronouns> BuiltInByKeyword { get; } =
        new ReadOnlyDictionary<string, Pronouns>(new Dictionary<string, Pronouns>(StringComparer.Ordinal)
        {
            ["she"] = She,
            ["he"] = He,
            ["they"] = They,
            ["it"] = It,
        });

    /// <summary>Resolves a built-in set by its subject form, or null.</summary>
    public static Pronouns? FromKeyword(string keyword) =>
        BuiltInByKeyword.TryGetValue(keyword.ToLowerInvariant(), out var pronouns) ? pronouns : null;
}
