namespace Talewright.Core.World;

/// <summary>
/// A full pronoun set. Use the built-ins or construct custom sets (neopronouns are first-class).
/// </summary>
/// <param name="Subject">she / he / they</param>
/// <param name="Object">her / him / them</param>
/// <param name="Possessive">Possessive determiner: her / his / their</param>
/// <param name="PossessivePronoun">Independent possessive: hers / his / theirs</param>
/// <param name="Reflexive">herself / himself / themselves</param>
/// <param name="IsPlural">Grammatical number for verb agreement ("they are", not "they is").</param>
public sealed record Pronouns(
    string Subject,
    string Object,
    string Possessive,
    string PossessivePronoun,
    string Reflexive,
    bool IsPlural = false)
{
    public static Pronouns She { get; } = new("she", "her", "her", "hers", "herself");
    public static Pronouns He { get; } = new("he", "him", "his", "his", "himself");
    public static Pronouns They { get; } = new("they", "them", "their", "theirs", "themselves", IsPlural: true);
    public static Pronouns It { get; } = new("it", "it", "its", "its", "itself");

    /// <summary>Resolves a built-in set by its subject form, or null.</summary>
    public static Pronouns? FromKeyword(string keyword) => keyword.ToLowerInvariant() switch
    {
        "she" => She,
        "he" => He,
        "they" => They,
        "it" => It,
        _ => null,
    };
}
