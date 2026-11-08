namespace Talewright.Talemark.Parsing;

internal static class TextScanning
{
    /// <summary>The first non-whitespace offset at or after <paramref name="index"/>, or <paramref name="end"/>.</summary>
    public static int SkipWhitespace(string text, int index, int end)
    {
        while (index < end && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    public static int SkipWhitespace(string text, int index) => SkipWhitespace(text, index, text.Length);

    /// <summary>The offset after the last non-whitespace character before <paramref name="end"/>, or <paramref name="start"/>.</summary>
    public static int TrimWhitespaceEnd(string text, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;
        return end;
    }

    /// <summary>Whether <paramref name="word"/> starts at <paramref name="index"/> and ends at whitespace or <paramref name="end"/>.</summary>
    public static bool IsWordAt(string text, int index, int end, string word) =>
        end - index >= word.Length &&
        text.AsSpan(index, word.Length).SequenceEqual(word) &&
        (index + word.Length == end || char.IsWhiteSpace(text[index + word.Length]));
}
