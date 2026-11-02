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

internal sealed class DelimiterScanResult(
    IReadOnlyList<DelimiterPair> pairs,
    IReadOnlyList<DelimiterIssue> issues,
    int workUnits)
{
    private readonly Dictionary<int, int> _closingOffsets = pairs.ToDictionary(pair => pair.OpenOffset, pair => pair.CloseOffset);
    private readonly Dictionary<int, int> _openingOffsets = pairs.ToDictionary(pair => pair.CloseOffset, pair => pair.OpenOffset);

    public IReadOnlyList<DelimiterPair> Pairs { get; } = pairs;

    public IReadOnlyList<DelimiterIssue> Issues { get; } = issues;

    public int WorkUnits { get; } = workUnits;

    public bool TryGetClose(int openOffset, out int closeOffset) =>
        _closingOffsets.TryGetValue(openOffset, out closeOffset);

    public bool TryGetOpen(int closeOffset, out int openOffset) =>
        _openingOffsets.TryGetValue(closeOffset, out openOffset);
}

internal static class DelimiterScanner
{
    private readonly record struct OpenDelimiter(DelimiterKind Kind, int Offset);

    public static DelimiterScanResult Scan(string text, DelimiterScanMode mode, int startOffset = 0)
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

        var pairs = new List<DelimiterPair>();
        var issues = new List<DelimiterIssue>();
        var openers = new List<OpenDelimiter>();
        var openersByKind = new List<int>[4];
        for (var index = 0; index < openersByKind.Length; index++)
        {
            openersByKind[index] = [];
        }

        var workUnits = 0;
        var braceDepth = 0;
        var inString = false;
        var stringStart = -1;

        for (var offset = startOffset; offset < text.Length; offset++)
        {
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
                    pairs.Add(new(DelimiterKind.QuotedString, stringStart, offset));
                    inString = false;
                    stringStart = -1;
                }

                continue;
            }

            if (UsesQuotedStrings(mode, braceDepth) && current == '"')
            {
                inString = true;
                stringStart = offset;
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
                openers.Add(new(kind, offset));
                openersByKind[(int)kind].Add(openers.Count - 1);
                if (kind == DelimiterKind.Brace)
                {
                    braceDepth++;
                }

                continue;
            }

            CloseDelimiter(kind, offset, openers, openersByKind, pairs, issues, ref braceDepth, ref workUnits);
        }

        if (inString)
        {
            issues.Add(new(DelimiterKind.QuotedString, stringStart, DelimiterIssueKind.UnclosedOpener));
        }

        while (openers.Count > 0)
        {
            workUnits++;
            var opener = PopOpener(openers, openersByKind, ref braceDepth);
            issues.Add(new(opener.Kind, opener.Offset, DelimiterIssueKind.UnclosedOpener));
        }

        return new(pairs, issues, workUnits);
    }

    private static void CloseDelimiter(
        DelimiterKind kind,
        int offset,
        List<OpenDelimiter> openers,
        List<int>[] openersByKind,
        List<DelimiterPair> pairs,
        List<DelimiterIssue> issues,
        ref int braceDepth,
        ref int workUnits)
    {
        var matching = openersByKind[(int)kind];
        if (matching.Count == 0)
        {
            var issue = openers.Count == 0
                ? DelimiterIssueKind.StrayCloser
                : DelimiterIssueKind.MismatchedCloser;
            issues.Add(new(kind, offset, issue));
            return;
        }

        var matchingIndex = matching[^1];
        while (openers.Count - 1 > matchingIndex)
        {
            workUnits++;
            var mismatched = PopOpener(openers, openersByKind, ref braceDepth);
            issues.Add(new(mismatched.Kind, mismatched.Offset, DelimiterIssueKind.MismatchedOpener));
        }

        var opener = PopOpener(openers, openersByKind, ref braceDepth);
        pairs.Add(new(kind, opener.Offset, offset));
    }

    private static OpenDelimiter PopOpener(
        List<OpenDelimiter> openers,
        List<int>[] openersByKind,
        ref int braceDepth)
    {
        var lastIndex = openers.Count - 1;
        var opener = openers[lastIndex];
        openers.RemoveAt(lastIndex);
        openersByKind[(int)opener.Kind].RemoveAt(openersByKind[(int)opener.Kind].Count - 1);
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
