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
    int ShownOffset,
    bool HasText,
    int TextOffset,
    int TextLength,
    IReadOnlyList<int> CommaOffsets,
    IReadOnlyList<int> ShownOffsets,
    IReadOnlyList<RequirementClauseText> TextSlots,
    IReadOnlyList<RequirementClauseError> Errors)
{
    public static RequirementClauseSyntax Parse(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkpoint = cancellationToken.CanBeCanceled ? new CancellationCheckpoint(cancellationToken) : null;
        var hasComma = false;
        for (var index = 0; index < text.Length; index++)
        {
            checkpoint?.Check();
            if (text[index] != ExpressionVocabulary.ArgumentSeparator)
                continue;
            hasComma = true;
            break;
        }

        if (!hasComma)
            return new(text.Length, false, 0, 0, false, -1, false, -1, 0,
                [], [], [], []);

        var scan = DelimiterScanner.Scan(text, DelimiterScanMode.CodeGroup, cancellationToken: cancellationToken);
        var commas = new List<int>();
        for (var index = 0; index < text.Length; index++)
        {
            checkpoint?.Check();
            if (text[index] is TextVocabulary.QuoteMark or ExpressionVocabulary.OpenParen or SceneVocabulary.InlineOpen)
            {
                if (!scan.TryGetClose(index, out var close))
                    break;
                index = close;
                continue;
            }

            if (text[index] == ExpressionVocabulary.ArgumentSeparator)
                commas.Add(index);
        }

        if (commas.Count == 0)
            return new(text.Length, false, 0, 0, false, -1, false, -1, 0,
                [], [], [], []);

        var expressionLength = commas[0];
        while (expressionLength > 0 && char.IsWhiteSpace(text[expressionLength - 1]))
            expressionLength--;

        var errors = new List<RequirementClauseError>();
        var hasShown = false;
        var shownOffset = -1;
        var hasText = false;
        var textOffset = -1;
        var textLength = 0;
        var shownOffsets = new List<int>();
        var textSlots = new List<RequirementClauseText>();
        var suffixStart = 0;
        var suffixLength = 0;
        for (var clauseIndex = 0; clauseIndex < commas.Count; clauseIndex++)
        {
            checkpoint?.Check();
            var start = commas[clauseIndex] + 1;
            var end = clauseIndex + 1 < commas.Count ? commas[clauseIndex + 1] : text.Length;
            while (start < end && char.IsWhiteSpace(text[start]))
                start++;
            while (end > start && char.IsWhiteSpace(text[end - 1]))
                end--;
            if (clauseIndex == 0)
            {
                suffixStart = start;
                while (suffixStart + suffixLength < end &&
                       !char.IsWhiteSpace(text[suffixStart + suffixLength]))
                    suffixLength++;
            }

            if (start == end || !text.AsSpan(start).StartsWith(SceneVocabulary.Shown, StringComparison.Ordinal) ||
                !IsWordEnd(text, start + SceneVocabulary.Shown.Length, end))
            {
                errors.Add(new(RequirementClauseErrorKind.ExpectedShown, start));
                continue;
            }

            shownOffsets.Add(start);
            if (hasShown)
                errors.Add(new(RequirementClauseErrorKind.DuplicateShown, start));
            else
            {
                hasShown = true;
                shownOffset = start;
            }

            var valueStart = start + SceneVocabulary.Shown.Length;
            while (valueStart < end && char.IsWhiteSpace(text[valueStart]))
                valueStart++;
            if (valueStart == end)
                continue;

            if (text[valueStart] != TextVocabulary.QuoteMark ||
                !scan.TryGetClose(valueStart, out var quoteEnd))
            {
                errors.Add(text[valueStart] == TextVocabulary.QuoteMark
                    ? new(RequirementClauseErrorKind.MissingTextQuote, valueStart)
                    : new(RequirementClauseErrorKind.UnexpectedAfterShown, valueStart));
                continue;
            }
            if (quoteEnd >= end)
            {
                errors.Add(new(RequirementClauseErrorKind.UnexpectedAfterShown, valueStart));
                continue;
            }

            var trailing = quoteEnd + 1;
            while (trailing < end && char.IsWhiteSpace(text[trailing]))
                trailing++;
            if (trailing < end)
                errors.Add(new(RequirementClauseErrorKind.UnexpectedAfterShown, trailing));
            var contentStart = valueStart + 1;
            var contentLength = quoteEnd - contentStart;
            textSlots.Add(new(contentStart, contentLength));
            if (!hasText)
            {
                hasText = true;
                textOffset = contentStart;
                textLength = contentLength;
            }
        }

        return new(expressionLength, true, suffixStart, suffixLength, hasShown, shownOffset,
            hasText, textOffset, textLength, commas, shownOffsets, textSlots, errors);
    }

    private static bool IsWordEnd(string text, int offset, int end) =>
        offset == end || char.IsWhiteSpace(text[offset]);
}

internal readonly record struct RequirementClauseText(int Offset, int Length);
