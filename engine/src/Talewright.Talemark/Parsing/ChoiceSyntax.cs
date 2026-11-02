namespace Talewright.Talemark.Parsing;

/// <param name="Marker">The clause marker to suggest placing <c>]</c> before, when exactly one is plausible.</param>
/// <param name="Offset">Offset of <paramref name="Marker"/>, or -1.</param>
/// <param name="WorkUnits">Characters examined, for linear-time tests.</param>
/// <param name="LabelEnd">Where the intended label most likely ends: the first clause-like marker, or the end of the text.</param>
internal readonly record struct ChoiceLabelHint(string? Marker, int Offset, int WorkUnits, int LabelEnd);

internal static class ChoiceSyntax
{
    private static readonly string[] EffectVerbs = ["set", "clear", "give", "take"];

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

        for (var index = open + 1; index < text.Length; index++)
        {
            checkpoint?.Check();
            work++;
            var current = text[index];
            if (inUnclosedEffect && current == '"')
            {
                if (!scan.TryGetClose(index, out var quoteEnd))
                    return Ambiguous();
                index = quoteEnd;
                continue;
            }
            if (!inUnclosedEffect && current == '\\' && index + 1 < text.Length &&
                text[index + 1] is '\\' or '{' or '}' or '[' or ']' or '*' or '_' or '`')
            {
                index++;
                work++;
                continue;
            }
            if (!inUnclosedEffect && current == '"')
            {
                inProseQuote = !inProseQuote;
                continue;
            }
            if (inProseQuote)
                continue;

            if (current == '[')
            {
                if (!scan.TryGetClose(index, out var bracketEnd))
                    return Ambiguous();
                index = bracketEnd;
                continue;
            }

            string? marker = null;
            var markerOffset = index;
            if (current == '{')
            {
                var closed = scan.TryGetClose(index, out var braceEnd);
                if (!inUnclosedEffect && IsBoundary(text, index) &&
                    LooksLikeEffect(text, index + 1, closed ? braceEnd : text.Length, scan, checkpoint, ref work))
                {
                    marker = "{";
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
            else if (IsBoundary(text, index) && text.AsSpan(index).StartsWith("->", StringComparison.Ordinal))
            {
                marker = "->";
            }
            else if (IsBoundary(text, index) && text.AsSpan(index).StartsWith("(requires:", StringComparison.Ordinal))
            {
                marker = "(requires:";
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

    public static bool TryReadSpacedRequires(string text, int open, CancellationToken cancellationToken,
        out int expressionStart)
    {
        cancellationToken.ThrowIfCancellationRequested();
        expressionStart = 0;
        if (text[open] != '(' || text.AsSpan(open).StartsWith("(requires:", StringComparison.Ordinal))
            return false;

        var checkpoint = cancellationToken.CanBeCanceled ? new CancellationCheckpoint(cancellationToken) : null;
        var index = open + 1;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            checkpoint?.Check();
            index++;
        }
        if (!text.AsSpan(index).StartsWith("requires", StringComparison.Ordinal))
            return false;
        index += "requires".Length;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            checkpoint?.Check();
            index++;
        }
        if (index >= text.Length || text[index] != ':')
            return false;

        expressionStart = index + 1;
        return true;
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
            if (current == '{' || text.AsSpan(index).StartsWith("->", StringComparison.Ordinal) ||
                text.AsSpan(index).StartsWith("(requires:", StringComparison.Ordinal) ||
                current == '(' && TryReadSpacedRequires(text, index, cancellationToken, out _))
                return index;
            if (current is '"' or '(' or '[')
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
        foreach (var verb in EffectVerbs)
        {
            if (remaining.StartsWith(verb, StringComparison.Ordinal) &&
                remaining.Length > verb.Length && char.IsWhiteSpace(remaining[verb.Length]))
                return true;
        }

        while (index < end && (char.IsAsciiLetterOrDigit(text[index]) || text[index] is '_' or '.' or '-'))
        {
            checkpoint?.Check();
            work++;
            index++;
        }
        if (index >= end || text[index] != ':')
            return false;

        for (index++; index < end; index++)
        {
            checkpoint?.Check();
            work++;
            if (text[index] == '"')
            {
                if (!scan.TryGetClose(index, out var quoteEnd))
                    return false;
                index = quoteEnd;
            }
            else if (text[index] == '=' && (index + 1 == end || text[index + 1] != '=') &&
                     text[index - 1] is not ('=' or '!' or '<' or '>'))
            {
                return true;
            }
        }

        return false;
    }
}
