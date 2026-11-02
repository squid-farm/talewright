using System.Text;

namespace Talewright.Talemark.Parsing;

internal static class QuotedStringSyntax
{
    internal const string InvalidEscape = "Unknown string escape. Use '\\\"' for a quote or '\\\\' for a backslash.";
    internal const string Unterminated = "The string is missing its closing double quote.";

    internal readonly record struct Result(string Value, int End, bool Closed, string? Error, int ErrorOffset);

    internal static Result Read(string text, int start, int end, ICollection<int>? positions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = new StringBuilder();
        var i = start + 1;
        string? error = null;
        var errorOffset = start;
        var nextCancellationCheck = i;
        positions?.Add(i);
        while (i < end)
        {
            if (i >= nextCancellationCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = i + 512;
            }

            var c = text[i++];
            if (c == '"') return new(value.ToString(), i, true, error, errorOffset);
            if (c == '\\')
            {
                if (i >= end || text[i] is not ('"' or '\\'))
                {
                    if (error is null)
                    {
                        error = InvalidEscape;
                        errorOffset = i - 1;
                    }
                }
                if (i < end) c = text[i++];
            }
            value.Append(c);
            positions?.Add(i);
        }
        return new(value.ToString(), i, false, error ?? Unterminated, errorOffset);
    }

    internal static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
