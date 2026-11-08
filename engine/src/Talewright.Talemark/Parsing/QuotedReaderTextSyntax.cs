using System.Text;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

/// <param name="Authored">Reader text for <see cref="InlineParser"/>: <c>\"</c> is decoded, other escapes are kept.</param>
/// <param name="Plain">The fully decoded text, with reader-text escapes resolved.</param>
/// <param name="Locations">One source location per <paramref name="Authored"/> character, plus the content end.</param>
/// <param name="QuoteEscapes">Source offsets of each <c>\"</c> escape.</param>
/// <param name="End">Offset after the closing quote, or the scan end when unclosed.</param>
/// <param name="Closed">Whether the closing quote was found.</param>
internal readonly record struct QuotedReaderText(
    string Authored,
    string Plain,
    IReadOnlyList<SourceLocation> Locations,
    IReadOnlyList<int> QuoteEscapes,
    int End,
    bool Closed)
{
    public SourceText ToSource() => SourceText.Mapped(Authored, Locations);
}

/// <summary>A double-quoted string whose content is reader text, such as image alt text or <c>shown</c> text.</summary>
internal static class QuotedReaderTextSyntax
{
    public static QuotedReaderText Read(SourceText source, int quote, int end, CancellationToken cancellationToken)
    {
        var text = source.Text;
        var i = quote + 1;
        var authored = new StringBuilder();
        var plain = new StringBuilder();
        var locations = new List<SourceLocation>();
        var quoteEscapes = new List<int>();
        var nextCancellationCheck = i;
        while (i < end)
        {
            if (i >= nextCancellationCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = i + Math.Min(1024, end - i);
            }

            var unit = QuotedStringSyntax.ReadUnit(text, i, end);
            if (unit.ClosingQuote)
            {
                locations.Add(source.Locate(unit.Start));
                return new(authored.ToString(), plain.ToString(), locations, quoteEscapes, unit.End, true);
            }

            i = unit.End;
            if (!unit.Escaped)
            {
                authored.Append(unit.Character);
                plain.Append(unit.Character);
                locations.Add(source.Locate(unit.Start));
                continue;
            }

            var next = unit.Character;
            if (next == TextVocabulary.QuoteMark)
            {
                authored.Append(next);
                plain.Append(next);
                locations.Add(source.Locate(i - 1));
                quoteEscapes.Add(unit.Start);
                continue;
            }

            authored.Append(TextVocabulary.EscapeMark).Append(next);
            if (next == TextVocabulary.EscapeMark || DelimiterScanner.IsReaderTextEscape(next))
                plain.Append(next);
            else
                plain.Append(TextVocabulary.EscapeMark).Append(next);
            locations.Add(source.Locate(i - 2));
            locations.Add(source.Locate(i - 1));
        }

        locations.Add(source.Locate(i));
        return new(authored.ToString(), plain.ToString(), locations, quoteEscapes, i, false);
    }
}
