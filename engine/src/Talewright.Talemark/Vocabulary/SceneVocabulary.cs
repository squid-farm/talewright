namespace Talewright.Talemark.Vocabulary;

internal enum SceneMetadataKey
{
    Title,
    ChoiceLabel,
    Continues,
    Requires,
    Tags,
    Priority,
    Once,
    Hub,
    HubLimit,
}

internal enum SceneHubMode
{
    All,
    Any,
}

/// <summary>Owned spellings for scene, choice, inline-condition and skill-check syntax.</summary>
public static class SceneVocabulary
{
    /// <summary>The source marker that begins a scene declaration.</summary>
    public const string SceneHeader = "===";
    internal const string Divert = "->";
    internal const string SkillCheckFence = ":::";
    internal const string SkillCheck = "skill-check";
    internal const string SkillCheckBlock = SkillCheckFence + " " + SkillCheck;
    internal const string Difficulty = "dc";
    internal const string DifficultyPrefix = " " + Difficulty;
    internal const string DifficultySeparator = DifficultyPrefix + " ";
    internal const string SuccessOutcome = "success:";
    internal const string FailureOutcome = "failure:";
    /// <summary>The reserved divert target that ends a story.</summary>
    public const string End = "END";

    internal const string If = "if";
    internal const string IfPrefix = "if ";
    internal const string Else = "else";
    internal const string EndIf = "/if";

    internal const string Title = "title";
    internal const string ChoiceLabel = "choice-label";
    internal const string Continues = "continues";
    internal const string Clear = "clear";
    internal const string Requires = "requires";
    internal const string Shown = "shown";
    internal const string Tags = "tags";
    internal const string Priority = "priority";
    internal const string Once = "once";
    internal const string Hub = "hub";
    internal const string HubLimit = "hub-limit";
    internal const string LegacyHubAllPrefix = "all(";
    internal const string LegacyHubAnyPrefix = "any(";
    internal const string ChoiceRequires = "(" + Requires;
    internal static readonly string ChoiceRequiresExample = "(" + Requires + " expression)";

    internal const char MetadataMarker = '@';
    internal const char SpeechMarker = '>';
    internal const char EffectsMarker = '~';
    internal const char ChoiceMarker = '*';
    internal const char InlineOpen = '{';
    internal const char InlineClose = '}';
    internal const char OpenBracket = '[';
    internal const char CloseBracket = ']';
    internal const char LegacyImageMarker = '!';
    // Prose and legacy Markdown are distinct from Talemark quoted-string syntax.
    internal const char ChoiceProseQuote = '"';
    internal const char LegacyImageEscape = '\\';
    internal const char LegacyImageQuote = '"';
    internal const char EmphasisMarker = '_';

    internal static readonly string[] MetadataSpellings = [Title, ChoiceLabel, Continues, Requires, Tags, Priority, Once, Hub, HubLimit];

    internal static bool TryGetMetadataKey(ReadOnlySpan<char> text, out SceneMetadataKey key)
    {
        switch (text)
        {
            case Title:
                key = SceneMetadataKey.Title;
                return true;
            case ChoiceLabel:
                key = SceneMetadataKey.ChoiceLabel;
                return true;
            case Continues:
                key = SceneMetadataKey.Continues;
                return true;
            case Requires:
                key = SceneMetadataKey.Requires;
                return true;
            case Tags:
                key = SceneMetadataKey.Tags;
                return true;
            case Priority:
                key = SceneMetadataKey.Priority;
                return true;
            case Once:
                key = SceneMetadataKey.Once;
                return true;
            case Hub:
                key = SceneMetadataKey.Hub;
                return true;
            case HubLimit:
                key = SceneMetadataKey.HubLimit;
                return true;
            default:
                key = default;
                return false;
        }
    }

    internal static bool TryGetLegacyHubMode(ReadOnlySpan<char> text, out SceneHubMode mode)
    {
        if (text.StartsWith(LegacyHubAllPrefix, StringComparison.Ordinal))
        {
            mode = SceneHubMode.All;
            return true;
        }

        if (text.StartsWith(LegacyHubAnyPrefix, StringComparison.Ordinal))
        {
            mode = SceneHubMode.Any;
            return true;
        }

        mode = default;
        return false;
    }

    internal static string LegacyHubModePrefix(SceneHubMode mode) => mode switch
    {
        SceneHubMode.All => LegacyHubAllPrefix,
        SceneHubMode.Any => LegacyHubAnyPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    internal static string Spelling(SceneMetadataKey key) => key switch
    {
        SceneMetadataKey.Title => Title,
        SceneMetadataKey.ChoiceLabel => ChoiceLabel,
        SceneMetadataKey.Continues => Continues,
        SceneMetadataKey.Requires => Requires,
        SceneMetadataKey.Tags => Tags,
        SceneMetadataKey.Priority => Priority,
        SceneMetadataKey.Once => Once,
        SceneMetadataKey.Hub => Hub,
        SceneMetadataKey.HubLimit => HubLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    internal static bool IsClearMetadata(ReadOnlySpan<char> content)
    {
        if (content.Length == 0 || content[0] != MetadataMarker)
            return false;

        var keyword = content[1..].TrimStart();
        var separator = keyword.IndexOfAny(' ', '\t');
        if (separator >= 0)
            keyword = keyword[..separator];
        return keyword.SequenceEqual(Clear);
    }
}
