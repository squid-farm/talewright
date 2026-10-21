namespace Talewright.Narrative.Grammar;

/// <summary>
/// Language rules used by text modifiers. English ships first; the interface is the seam for localization
/// and for story-specific overrides (e.g. invented fantasy possessives) in a later phase.
/// </summary>
public interface IGrammar
{
    /// <summary>Possessive form of a noun or name.</summary>
    string Possessive(string word, bool isPlural);

    /// <summary>Prefixes "a" or "an".</summary>
    string WithIndefiniteArticle(string phrase);

    /// <summary>Upper-cases the first letter.</summary>
    string Capitalize(string text);
}

/// <summary>
/// English following the Chicago Manual of Style: singular nouns always take <c>'s</c> (Mara's, Burns's);
/// plurals ending in s take only an apostrophe (the guards'); irregular plurals take <c>'s</c> (the children's).
/// </summary>
public sealed class EnglishGrammar : IGrammar
{
    public static EnglishGrammar Instance { get; } = new();

    // Words starting with a silent h, or a vowel letter pronounced as a consonant.
    private static readonly string[] AnExceptions = ["hour", "honest", "honor", "honour", "heir"];
    private static readonly string[] AExceptions = ["uni", "use", "usu", "uti", "eu", "one", "once", "ewe", "uro"];

    public string Possessive(string word, bool isPlural)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        return isPlural && word.EndsWith('s') ? word + "'" : word + "'s";
    }

    public string WithIndefiniteArticle(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return phrase;
        }

        var lower = phrase.TrimStart().ToLowerInvariant();
        bool useAn;
        if (AnExceptions.Any(lower.StartsWith))
        {
            useAn = true;
        }
        else if (AExceptions.Any(lower.StartsWith))
        {
            useAn = false;
        }
        else
        {
            useAn = "aeiou".Contains(lower[0]);
        }

        return (useAn ? "an " : "a ") + phrase;
    }

    public string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
