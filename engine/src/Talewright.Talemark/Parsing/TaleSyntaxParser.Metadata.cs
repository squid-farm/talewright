using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal static partial class TaleSyntaxParser
{
    /// <summary>Reads an <c>@hub</c> value: one tag or a tag list joined consistently by <c>and</c> or <c>or</c>.</summary>
    internal static bool TryReadHub(string value, out string[] tags, out bool all)
    {
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        all = false;
        if (words.Length == 0)
        {
            tags = [];
            return false;
        }

        if (words.Length > 1 &&
            (words.Length % 2 == 0 || words[1] is not (ExpressionVocabulary.And or ExpressionVocabulary.Or)))
        {
            tags = [];
            return false;
        }

        var separator = words.Length > 1 ? words[1] : null;
        tags = words.Where((_, index) => index % 2 == 0).ToArray();
        if ((separator is not null && words.Where((_, index) => index % 2 == 1).Any(word => word != separator)) ||
            !IsValidHubTags(tags))
            return false;

        all = separator == ExpressionVocabulary.And;
        return true;
    }

    /// <summary>Reads a well-formed legacy call-form selector for its replacement diagnostic.</summary>
    internal static bool TryReadLegacyHub(string value, out string[] tags, out bool all)
    {
        var hasMode = SceneVocabulary.TryGetLegacyHubMode(value, out var mode);
        var prefix = hasMode ? SceneVocabulary.LegacyHubModePrefix(mode) : string.Empty;
        if (!hasMode || !value.EndsWith(ExpressionVocabulary.CloseParen))
        {
            tags = [];
            all = false;
            return false;
        }

        tags = value[prefix.Length..^1].Split(ExpressionVocabulary.ArgumentSeparator,
            StringSplitOptions.TrimEntries);
        all = mode == SceneHubMode.All;
        return IsValidHubTags(tags);
    }

    private static bool IsValidHubTags(string[] tags) =>
        tags.Length > 0 &&
        tags.All(tag => !string.IsNullOrWhiteSpace(tag) &&
                        tag is not (ExpressionVocabulary.And or ExpressionVocabulary.Or) &&
                        !tag.Any(c => char.IsWhiteSpace(c) || c is ExpressionVocabulary.OpenParen or
                            ExpressionVocabulary.CloseParen or ExpressionVocabulary.ArgumentSeparator)) &&
        tags.Distinct(StringComparer.Ordinal).Count() == tags.Length;

    internal static string[] ReadTags(string value) => value.Split(ExpressionVocabulary.ArgumentSeparator,
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int TagOffset(string value, string tag)
    {
        var start = 0;
        while (start <= value.Length)
        {
            var comma = value.IndexOf(ExpressionVocabulary.ArgumentSeparator, start);
            var end = comma < 0 ? value.Length : comma;
            var segment = value.AsSpan(start, end - start);
            if (segment.Trim().SequenceEqual(tag.AsSpan()))
                return start + segment.Length - segment.TrimStart().Length;
            if (comma < 0)
                break;
            start = comma + 1;
        }

        return value.Length;
    }


    private sealed partial class SceneState
    {
        private TaleLineSyntax Metadata(TaleLineSyntax line, ReadOnlySpan<char> content)
        {
            var rest = content[1..];
            var space = rest.IndexOf(' ');
            var value = space < 0 ? ReadOnlySpan<char>.Empty : rest[(space + 1)..];
            line = WithPart(line, new(line.Indent + 1, space < 0 ? rest.Length : space,
                space < 0 ? line.Indent + content.Length : line.Indent + space + 2 + Lead(value), value.Trim().Length));
            if (line.MetadataKey is not { } key)
                return line;

            if (key is SceneMetadataKey.Hub or SceneMetadataKey.HubLimit)
            {
                ref var seen = ref key == SceneMetadataKey.Hub ? ref _hasHub : ref _hasHubLimit;
                if (seen)
                {
                    Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                        $"Duplicate @{SceneVocabulary.Spelling(key)} directive.");
                    return line with { IsInvalid = true };
                }

                seen = true;
            }

            value = value.Trim();
            switch (key)
            {
                case SceneMetadataKey.Tags:
                {
                    var tagText = value.ToString();
                    var reservedTag = ReadTags(tagText).FirstOrDefault(tag =>
                        tag is ExpressionVocabulary.And or ExpressionVocabulary.Or);
                    if (reservedTag is not null)
                    {
                        var tagOffset = TagOffset(tagText, reservedTag);
                        Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                            $"'{reservedTag}' is reserved as an @hub selector operator and cannot be a tag name.",
                            _parts[^1].MetadataValueColumn(line) + tagOffset);
                        return line with { IsInvalid = true };
                    }

                    return line;
                }
                case SceneMetadataKey.Priority when !int.TryParse(value, out _):
                    Error(line, CompilerDiagnosticCodes.NumberTooLarge,
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.Priority} needs a whole number.",
                        _parts[^1].MetadataValueColumn(line));
                    return line with { IsInvalid = true };
                case SceneMetadataKey.Hub:
                {
                    var selector = value.ToString();
                    if (TryReadLegacyHub(selector, out var legacyTags, out var legacyAll))
                    {
                        var replacement = AuthoringSyntaxVocabulary.FormatHubSelector(legacyTags, legacyAll);
                        Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                            $"'{SceneVocabulary.MetadataMarker}{SceneVocabulary.Hub} {selector}' is no longer supported; " +
                            $"use '{SceneVocabulary.MetadataMarker}{SceneVocabulary.Hub} {replacement}'.");
                        return line with { IsInvalid = true };
                    }

                    if (!TryReadHub(selector, out _, out _))
                    {
                        Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                            $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.Hub} needs a tag, a tag conjunction " +
                            $"such as 'a {ExpressionVocabulary.And} b', or a tag disjunction such as " +
                            $"'a {ExpressionVocabulary.Or} b', with distinct nonempty tags. Do not mix " +
                            $"'{ExpressionVocabulary.And}' and '{ExpressionVocabulary.Or}'.");
                        return line with { IsInvalid = true };
                    }

                    return line;
                }
                case SceneMetadataKey.HubLimit when !int.TryParse(value, out var limit) || limit <= 0:
                    Error(line, CompilerDiagnosticCodes.InvalidSceneMetadata,
                        $"{SceneVocabulary.MetadataMarker}{SceneVocabulary.HubLimit} needs a positive whole number.");
                    return line with { IsInvalid = true };
                default:
                    return line;
            }
        }

    }
}
