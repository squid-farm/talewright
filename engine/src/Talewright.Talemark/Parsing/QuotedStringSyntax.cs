using System.Text;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal static class QuotedStringSyntax
{
    internal static readonly string InvalidEscape =
        $"Unknown string escape. Use '{TextVocabulary.EscapeMark}{TextVocabulary.QuoteMark}' for a quote or " +
        $"'{TextVocabulary.EscapeMark}{TextVocabulary.EscapeMark}' for a backslash.";
    internal const string Unterminated = "The string is missing its closing double quote.";

    internal readonly record struct Result(string Value, int End, bool Closed, string? Error, int ErrorOffset);

    internal readonly record struct ScanResult(int End, bool Closed, string? Error, int ErrorOffset, int WorkUnits);

    internal readonly record struct Unit(char Character, int Start, int End, bool Escaped, bool ClosingQuote);

    internal static Result Read(string text, int start, int end, ICollection<int>? positions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = new StringBuilder();
        var scan = Scan(text, start, end, value, positions, cancellationToken);
        return new(value.ToString(), scan.End, scan.Closed, scan.Error, scan.ErrorOffset);
    }

    internal static ScanResult Scan(string text, int start, int end, StringBuilder? value = null,
        ICollection<int>? positions = null, CancellationToken cancellationToken = default,
        CancellationCheckpoint? checkpoint = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var i = start + 1;
        string? error = null;
        var errorOffset = start;
        var nextCancellationCheck = i;
        var workUnits = 0;
        positions?.Add(i);
        while (i < end)
        {
            checkpoint?.Check();
            if (i >= nextCancellationCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = i + 512;
            }

            var unit = ReadUnit(text, i, end);
            workUnits += unit.End - i;
            i = unit.End;
            if (unit.ClosingQuote) return new(i, true, error, errorOffset, workUnits);
            if (unit.Escaped)
            {
                if (unit.End == unit.Start + 1 ||
                    unit.Character is not (TextVocabulary.QuoteMark or TextVocabulary.EscapeMark))
                {
                    if (error is null)
                    {
                        error = InvalidEscape;
                        errorOffset = unit.Start;
                    }
                }
            }
            value?.Append(unit.Character);
            positions?.Add(i);
        }
        return new(i, false, error ?? Unterminated, errorOffset, workUnits);
    }

    internal static Unit ReadUnit(string text, int start, int end)
    {
        var character = text[start];
        if (character == TextVocabulary.QuoteMark)
            return new(character, start, start + 1, false, true);
        if (character == TextVocabulary.EscapeMark)
        {
            if (start + 1 < end)
                return new(text[start + 1], start, start + 2, true, false);
            return new(character, start, start + 1, true, false);
        }

        return new(character, start, start + 1, false, false);
    }

    internal static string Quote(string text) =>
        TextVocabulary.QuoteMark + text.Replace(TextVocabulary.EscapeMark.ToString(),
                $"{TextVocabulary.EscapeMark}{TextVocabulary.EscapeMark}")
            .Replace(TextVocabulary.QuoteMark.ToString(),
                $"{TextVocabulary.EscapeMark}{TextVocabulary.QuoteMark}") + TextVocabulary.QuoteMark;
}
