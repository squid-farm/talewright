using Talewright.Core.Identity;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

/// <param name="Marker">The clause marker to suggest placing <c>]</c> before, when exactly one is plausible.</param>
/// <param name="Offset">Offset of <paramref name="Marker"/>, or -1.</param>
/// <param name="WorkUnits">Characters examined and delimiter-pair comparisons, for linear-time tests.</param>
/// <param name="LabelEnd">Where the intended label most likely ends: the first clause-like marker, or the end of the text.</param>
internal readonly record struct ChoiceLabelHint(string? Marker, int Offset, int WorkUnits, int LabelEnd);

internal static class ChoiceSyntax
{
    public static ChoiceLabelHint FindMissingBracketHint(string text, int open, DelimiterScanResult scan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkpoint = cancellationToken.CanBeCanceled ? new CancellationCheckpoint(cancellationToken) : null;
        var work = 0;
        var candidateOffset = -1;
        string? candidate = null;
        var inProseQuote = false;
        var inUnclosedEffect = false;
        var pairIndex = 0;

        bool TryGetClose(int openOffset, out int closeOffset)
        {
            while (pairIndex < scan.Pairs.Count)
            {
                checkpoint?.Check();
                work++;
                var pair = scan.Pairs[pairIndex];
                if (pair.OpenOffset < openOffset)
                {
                    pairIndex++;
                    continue;
                }
                if (pair.OpenOffset == openOffset)
                {
                    closeOffset = pair.CloseOffset;
                    return true;
                }
                break;
            }

            closeOffset = -1;
            return false;
        }

        for (var index = open + 1; index < text.Length; index++)
        {
            checkpoint?.Check();
            work++;
            var current = text[index];
            if (inUnclosedEffect && current == TextVocabulary.QuoteMark)
            {
                if (!TryGetClose(index, out var quoteEnd))
                    return Ambiguous();
                index = quoteEnd;
                continue;
            }
            if (!inUnclosedEffect && current == TextVocabulary.EscapeMark && index + 1 < text.Length &&
                DelimiterScanner.IsReaderTextEscape(text[index + 1]))
            {
                index++;
                work++;
                continue;
            }
            if (!inUnclosedEffect && current == SceneVocabulary.ChoiceProseQuote)
            {
                inProseQuote = !inProseQuote;
                continue;
            }
            if (inProseQuote)
                continue;

            if (current == SceneVocabulary.OpenBracket)
            {
                if (!TryGetClose(index, out var bracketEnd))
                    return Ambiguous();
                index = bracketEnd;
                continue;
            }

            string? marker = null;
            var markerOffset = index;
            if (current == SceneVocabulary.InlineOpen)
            {
                var closed = TryGetClose(index, out var braceEnd);
                if (!inUnclosedEffect && IsBoundary(text, index) &&
                    LooksLikeEffect(text, index + 1, closed ? braceEnd : text.Length, scan, checkpoint, ref work))
                {
                    marker = SceneVocabulary.InlineOpen.ToString();
                }
                else if (!closed)
                {
                    return Ambiguous();
                }

                if (closed)
                {
                    index = braceEnd;
                }
                else
                {
                    inUnclosedEffect = true;
                }
            }
            else if (IsBoundary(text, index) &&
                     text.AsSpan(index).StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal))
            {
                marker = SceneVocabulary.Divert;
            }
            else if (IsBoundary(text, index) &&
                     TryReadChoiceRequires(text, index, cancellationToken, out _))
            {
                marker = SceneVocabulary.ChoiceRequiresExample;
            }

            if (marker is null)
                continue;
            if (candidate is not null)
                return Ambiguous();
            candidate = marker;
            candidateOffset = markerOffset;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(candidate, candidateOffset, work, candidate is null ? text.Length : candidateOffset);

        ChoiceLabelHint Ambiguous() => new(null, -1, work, candidate is null ? text.Length : candidateOffset);
    }

    public static bool TryReadChoiceRequires(string text, int open, CancellationToken cancellationToken,
        out int expressionStart)
    {
        cancellationToken.ThrowIfCancellationRequested();
        expressionStart = 0;
        if (text[open] != ExpressionVocabulary.OpenParen ||
            !text.AsSpan(open).StartsWith(SceneVocabulary.ChoiceRequires, StringComparison.Ordinal))
            return false;

        var checkpoint = cancellationToken.CanBeCanceled ? new CancellationCheckpoint(cancellationToken) : null;
        var index = open + SceneVocabulary.ChoiceRequires.Length;
        // Let the expression parser report a separator at the offending character.
        if (index >= text.Length ||
            (!char.IsWhiteSpace(text[index]) && text[index] != ExpressionVocabulary.Separator))
            return false;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            checkpoint?.Check();
            index++;
        }

        expressionStart = index;
        return true;
    }

    public static bool TryReadShownWithoutRequirement(string text, int open,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (text[open] != ExpressionVocabulary.OpenParen)
            return false;

        var index = TextScanning.SkipWhitespace(text, open + 1);
        var wordEnd = index + SceneVocabulary.Shown.Length;
        var end = wordEnd < text.Length &&
                  text[wordEnd] is ExpressionVocabulary.CloseParen or ExpressionVocabulary.ArgumentSeparator
            ? wordEnd
            : text.Length;
        return TextScanning.IsWordAt(text, index, end, SceneVocabulary.Shown);
    }

    public static int FindTrailingClause(string text, int start, DelimiterScanResult scan, int scanOffset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkpoint = cancellationToken.CanBeCanceled ? new CancellationCheckpoint(cancellationToken) : null;
        for (var index = start; index < text.Length; index++)
        {
            checkpoint?.Check();
            var current = text[index];
            if (current == SceneVocabulary.InlineOpen ||
                text.AsSpan(index).StartsWith(SceneVocabulary.Divert, StringComparison.Ordinal) ||
                current == ExpressionVocabulary.OpenParen &&
                TryReadChoiceRequires(text, index, cancellationToken, out _))
                return index;
            if (current is TextVocabulary.QuoteMark or ExpressionVocabulary.OpenParen or SceneVocabulary.OpenBracket)
            {
                if (!scan.TryGetClose(scanOffset + index, out var close))
                    return -1;
                index = close - scanOffset;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return -1;
    }

    public static string Excerpt(ReadOnlySpan<char> text)
    {
        const int maximumLength = 80;
        if (text.Length <= maximumLength)
            return text.ToString();

        var length = maximumLength - 1;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            length--;
        return string.Concat(text[..length], "…");
    }

    private static bool IsBoundary(string text, int index) =>
        index > 0 && char.IsWhiteSpace(text[index - 1]);

    private static bool LooksLikeEffect(string text, int start, int end, DelimiterScanResult scan,
        CancellationCheckpoint? checkpoint, ref int work)
    {
        var index = start;
        while (index < end && char.IsWhiteSpace(text[index]))
        {
            checkpoint?.Check();
            work++;
            index++;
        }

        var remaining = text.AsSpan(index, end - index);
        foreach (var verb in ExpressionVocabulary.EffectVerbs)
        {
            if (remaining.StartsWith(verb, StringComparison.Ordinal) &&
                remaining.Length > verb.Length && char.IsWhiteSpace(remaining[verb.Length]))
                return true;
        }

        while (index < end && IdentifierRules.IsPart(text[index]))
        {
            checkpoint?.Check();
            work++;
            index++;
        }
        if (index >= end || text[index] != ExpressionVocabulary.Separator)
            return false;

        for (index++; index < end; index++)
        {
            checkpoint?.Check();
            work++;
            if (text[index] == TextVocabulary.QuoteMark)
            {
                if (!scan.TryGetClose(index, out var quoteEnd) || quoteEnd >= end)
                    return false;
                index = quoteEnd;
            }
            else if (text[index] == ExpressionVocabulary.Assign &&
                     (index + 1 == end || text[index + 1] != ExpressionVocabulary.Assign) &&
                     text[index - 1] is not (ExpressionVocabulary.Assign or ExpressionVocabulary.NotEqualMarker or
                         ExpressionVocabulary.Less or ExpressionVocabulary.Greater))
            {
                return true;
            }
        }

        return false;
    }
}
