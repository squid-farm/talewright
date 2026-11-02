namespace Talewright.Talemark.Parsing;

internal enum DelimiterScanMode
{
    ReaderText,
    ChoiceLabel,
    CodeGroup,
}

internal enum DelimiterKind
{
    Brace,
    Bracket,
    Parenthesis,
    QuotedString,
}

internal enum DelimiterIssueKind
{
    UnclosedOpener,
    StrayCloser,
    MismatchedOpener,
    MismatchedCloser,
}

internal readonly record struct DelimiterPair(DelimiterKind Kind, int OpenOffset, int CloseOffset);

internal readonly record struct DelimiterIssue(DelimiterKind Kind, int Offset, DelimiterIssueKind Issue);

internal sealed class DelimiterScanResult
{
    private readonly DelimiterPair[] _pairs;
    private readonly List<DelimiterIssue>? _issues;

    /// <param name="pairs">Matched pairs ordered by <see cref="DelimiterPair.OpenOffset"/>.</param>
    /// <param name="issues">Unmatched or mismatched delimiters.</param>
    /// <param name="workUnits">Characters and stack operations examined, for linear-time tests.</param>
    public DelimiterScanResult(DelimiterPair[] pairs, List<DelimiterIssue>? issues, int workUnits)
    {
        _pairs = pairs;
        _issues = issues;
        WorkUnits = workUnits;
    }

    /// <summary>Matched pairs ordered by opening offset.</summary>
    public IReadOnlyList<DelimiterPair> Pairs => _pairs;

    public IReadOnlyList<DelimiterIssue> Issues => _issues is null ? [] : _issues;

    public int WorkUnits { get; }

    public bool TryGetClose(int openOffset, out int closeOffset)
    {
        var low = 0;
        var high = _pairs.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var candidate = _pairs[middle].OpenOffset;
            if (candidate == openOffset)
            {
                closeOffset = _pairs[middle].CloseOffset;
                return true;
            }
            if (candidate < openOffset)
                low = middle + 1;
            else
                high = middle - 1;
        }

        closeOffset = -1;
        return false;
    }

    /// <remarks>Linear in the number of pairs; parsing paths should look up by opening offset instead.</remarks>
    public bool TryGetOpen(int closeOffset, out int openOffset)
    {
        foreach (var pair in _pairs)
        {
            if (pair.CloseOffset == closeOffset)
            {
                openOffset = pair.OpenOffset;
                return true;
            }
        }

        openOffset = -1;
        return false;
    }
}

internal static class DelimiterScanner
{
    /// <summary>An open delimiter, its reserved slot in the pair list and the next open delimiter of the same kind.</summary>
    private readonly record struct OpenDelimiter(DelimiterKind Kind, int Offset, int Slot, int PreviousOfKind);

    private const int Unclosed = -1;
    private const int MaxRetainedBuffer = 256;

    // Every choice line is scanned, so the working lists are reused per thread rather than allocated per scan.
    [ThreadStatic]
    private static List<DelimiterPair>? s_pairBuffer;

    [ThreadStatic]
    private static List<OpenDelimiter>? s_openerBuffer;

    public static DelimiterScanResult Scan(string text, DelimiterScanMode mode, int startOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        if (startOffset > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset));
        }
        if (mode is not (DelimiterScanMode.ReaderText or DelimiterScanMode.ChoiceLabel or DelimiterScanMode.CodeGroup))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var checkpoint = cancellationToken.CanBeCanceled ? new CancellationCheckpoint(cancellationToken) : null;
        // Slots are reserved in opening order so the finished list is sorted for binary search.
        var pairs = s_pairBuffer ?? [];
        var openers = s_openerBuffer ?? [];
        s_pairBuffer = null;
        s_openerBuffer = null;
        pairs.Clear();
        openers.Clear();
        List<DelimiterIssue>? issues = null;
        Span<int> topOfKind = [-1, -1, -1, -1];
        var unclosedSlots = 0;

        var workUnits = 0;
        var braceDepth = 0;
        var inString = false;
        var stringSlot = -1;

        for (var offset = startOffset; offset < text.Length; offset++)
        {
            checkpoint?.Check();
            workUnits++;
            var current = text[offset];

            if (inString)
            {
                if (current == '\\' && offset + 1 < text.Length)
                {
                    offset++;
                    workUnits++;
                }
                else if (current == '"')
                {
                    pairs[stringSlot] = pairs[stringSlot] with { CloseOffset = offset };
                    inString = false;
                    stringSlot = -1;
                }

                continue;
            }

            if (UsesQuotedStrings(mode, braceDepth) && current == '"')
            {
                inString = true;
                stringSlot = pairs.Count;
                pairs.Add(new(DelimiterKind.QuotedString, offset, Unclosed));
                continue;
            }

            if (IsEscapedInMode(text, offset, mode, braceDepth))
            {
                offset++;
                workUnits++;
                continue;
            }

            if (!TryGetDelimiter(current, mode, braceDepth, out var kind, out var isOpen))
            {
                continue;
            }

            if (isOpen)
            {
                openers.Add(new(kind, offset, pairs.Count, topOfKind[(int)kind]));
                topOfKind[(int)kind] = openers.Count - 1;
                pairs.Add(new(kind, offset, Unclosed));
                if (kind == DelimiterKind.Brace)
                {
                    braceDepth++;
                }

                continue;
            }

            var matchingIndex = topOfKind[(int)kind];
            if (matchingIndex < 0)
            {
                var issue = openers.Count == 0
                    ? DelimiterIssueKind.StrayCloser
                    : DelimiterIssueKind.MismatchedCloser;
                (issues ??= []).Add(new(kind, offset, issue));
                continue;
            }

            while (openers.Count - 1 > matchingIndex)
            {
                checkpoint?.Check();
                workUnits++;
                var mismatched = PopOpener(openers, topOfKind, ref braceDepth);
                unclosedSlots++;
                (issues ??= []).Add(new(mismatched.Kind, mismatched.Offset, DelimiterIssueKind.MismatchedOpener));
            }

            var opener = PopOpener(openers, topOfKind, ref braceDepth);
            pairs[opener.Slot] = pairs[opener.Slot] with { CloseOffset = offset };
        }

        if (inString)
        {
            unclosedSlots++;
            (issues ??= []).Add(new(DelimiterKind.QuotedString, pairs[stringSlot].OpenOffset, DelimiterIssueKind.UnclosedOpener));
        }

        while (openers.Count > 0)
        {
            checkpoint?.Check();
            workUnits++;
            var opener = PopOpener(openers, topOfKind, ref braceDepth);
            unclosedSlots++;
            (issues ??= []).Add(new(opener.Kind, opener.Offset, DelimiterIssueKind.UnclosedOpener));
        }

        var matched = pairs.Count == unclosedSlots ? [] : new DelimiterPair[pairs.Count - unclosedSlots];
        var next = 0;
        foreach (var pair in pairs)
        {
            if (pair.CloseOffset != Unclosed)
                matched[next++] = pair;
        }

        if (pairs.Capacity <= MaxRetainedBuffer)
            s_pairBuffer = pairs;
        if (openers.Capacity <= MaxRetainedBuffer)
            s_openerBuffer = openers;
        cancellationToken.ThrowIfCancellationRequested();
        return new DelimiterScanResult(matched, issues, workUnits);
    }

    private static OpenDelimiter PopOpener(List<OpenDelimiter> openers, Span<int> topOfKind, ref int braceDepth)
    {
        var lastIndex = openers.Count - 1;
        var opener = openers[lastIndex];
        openers.RemoveAt(lastIndex);
        topOfKind[(int)opener.Kind] = opener.PreviousOfKind;
        if (opener.Kind == DelimiterKind.Brace)
        {
            braceDepth--;
        }

        return opener;
    }

    private static bool UsesQuotedStrings(DelimiterScanMode mode, int braceDepth) =>
        mode == DelimiterScanMode.CodeGroup || braceDepth > 0;

    private static bool IsEscapedInMode(string text, int offset, DelimiterScanMode mode, int braceDepth)
    {
        if (text[offset] != '\\' || offset + 1 >= text.Length)
        {
            return false;
        }

        var next = text[offset + 1];
        return mode switch
        {
            DelimiterScanMode.ReaderText => braceDepth == 0 && next is '\\' or '{' or '}',
            DelimiterScanMode.ChoiceLabel => braceDepth == 0 && next is '\\' or '{' or '}' or '[' or ']' or '*' or '_' or '`',
            _ => false,
        };
    }

    private static bool TryGetDelimiter(
        char character,
        DelimiterScanMode mode,
        int braceDepth,
        out DelimiterKind kind,
        out bool isOpen)
    {
        if (mode == DelimiterScanMode.ReaderText)
        {
            kind = DelimiterKind.Brace;
            isOpen = character == '{';
            return character is '{' or '}';
        }

        if (mode == DelimiterScanMode.ChoiceLabel)
        {
            if (braceDepth > 0)
            {
                kind = DelimiterKind.Brace;
                isOpen = character == '{';
                return character is '{' or '}';
            }

            if (character is '[' or ']')
            {
                kind = DelimiterKind.Bracket;
                isOpen = character == '[';
                return true;
            }

            if (character is '{' or '}')
            {
                kind = DelimiterKind.Brace;
                isOpen = character == '{';
                return true;
            }

            kind = default;
            isOpen = false;
            return false;
        }

        (kind, isOpen) = character switch
        {
            '{' => (DelimiterKind.Brace, true),
            '}' => (DelimiterKind.Brace, false),
            '[' => (DelimiterKind.Bracket, true),
            ']' => (DelimiterKind.Bracket, false),
            '(' => (DelimiterKind.Parenthesis, true),
            ')' => (DelimiterKind.Parenthesis, false),
            _ => (default, false),
        };
        return character is '{' or '}' or '[' or ']' or '(' or ')';
    }
}
