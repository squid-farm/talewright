using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal enum RequirementClauseErrorKind
{
    ExpectedShown,
    DuplicateShown,
    MissingTextQuote,
    UnexpectedAfterShown,
}

internal readonly record struct RequirementClauseError(RequirementClauseErrorKind Kind, int Offset);

internal readonly record struct RequirementClauseSyntax(
    int ExpressionLength,
    bool HasSuffix,
    int SuffixStart,
    int SuffixLength,
    bool HasShown,
    RequirementClauseText? Text,
    IReadOnlyList<int> CommaOffsets,
    IReadOnlyList<int> ShownOffsets,
    IReadOnlyList<RequirementClauseText> TextSlots,
    IReadOnlyList<RequirementClauseError> Errors)
{
    public static RequirementClauseSyntax Parse(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (text.IndexOf(ExpressionVocabulary.ArgumentSeparator) < 0)
            return WithoutSuffix(text);

        var scan = DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup, cancellationToken: cancellationToken);
        var commas = scan.FindTopLevel(text, ExpressionVocabulary.ArgumentSeparator, cancellationToken);
        if (commas.Count == 0)
            return WithoutSuffix(text);

        var checkpoint = cancellationToken.CanBeCanceled ? new CancellationCheckpoint(cancellationToken) : null;
        var errors = new List<RequirementClauseError>();
        var shownOffsets = new List<int>();
        var textSlots = new List<RequirementClauseText>();
        var suffixStart = 0;
        var suffixLength = 0;
        for (var clauseIndex = 0; clauseIndex < commas.Count; clauseIndex++)
        {
            checkpoint?.Check();
            var clauseEnd = clauseIndex + 1 < commas.Count ? commas[clauseIndex + 1] : text.Length;
            var start = TextScanning.SkipWhitespace(text, commas[clauseIndex] + 1, clauseEnd, cancellationToken);
            var end = TextScanning.TrimWhitespaceEnd(text, start, clauseEnd, cancellationToken);
            if (clauseIndex == 0)
            {
                suffixStart = start;
                while (suffixStart + suffixLength < end && !char.IsWhiteSpace(text[suffixStart + suffixLength]))
                    suffixLength++;
            }

            if (!TextScanning.IsWordAt(text, start, end, SceneVocabulary.Shown))
            {
                errors.Add(new(RequirementClauseErrorKind.ExpectedShown, start));
                continue;
            }

            if (shownOffsets.Count > 0)
                errors.Add(new(RequirementClauseErrorKind.DuplicateShown, start));
            shownOffsets.Add(start);

            var valueStart = TextScanning.SkipWhitespace(text, start + SceneVocabulary.Shown.Length, end, cancellationToken);
            if (valueStart == end)
                continue;
            if (text[valueStart] != TextVocabulary.QuoteMark)
            {
                errors.Add(new(RequirementClauseErrorKind.UnexpectedAfterShown, valueStart));
                continue;
            }
            if (!scan.TryGetClose(valueStart, out var closingQuote))
            {
                errors.Add(new(RequirementClauseErrorKind.MissingTextQuote, valueStart));
                continue;
            }

            var trailing = TextScanning.SkipWhitespace(text, closingQuote + 1, end, cancellationToken);
            if (trailing < end)
                errors.Add(new(RequirementClauseErrorKind.UnexpectedAfterShown, trailing));
            textSlots.Add(new(valueStart, closingQuote + 1));
        }

        return new(TextScanning.TrimWhitespaceEnd(text, 0, commas[0], cancellationToken), true, suffixStart, suffixLength,
            shownOffsets.Count > 0, textSlots.Count > 0 ? textSlots[0] : null, commas, shownOffsets, textSlots,
            errors);
    }

    private static RequirementClauseSyntax WithoutSuffix(string text) =>
        new(text.Length, false, 0, 0, false, null, [], [], [], []);
}

/// <param name="QuoteOffset">Offset of the opening quote.</param>
/// <param name="End">Offset after the closing quote.</param>
internal readonly record struct RequirementClauseText(int QuoteOffset, int End);
