using Talewright.Tale.Compilation;
using Talewright.Tale.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Talewright.Tale.Analysis;

/// <summary>Maps decoded scalar characters to their authored extents, including escapes and folded whitespace.</summary>
internal sealed class YamlScalarSource
{
    private readonly string _source;
    private readonly int[] _lines;
    private readonly (int Start, int End)[] _characters;
    private readonly int _start;

    internal YamlScalarSource(string source, YamlScalarNode scalar, int[] lineStarts)
    {
        _source = source;
        _lines = lineStarts;
        var start = _lines[(int)scalar.Start.Line - 1] + (int)scalar.Start.Column - 1;
        var end = _lines[(int)scalar.End.Line - 1] + (int)scalar.End.Column - 1;
        var scanner = new Scanner(new StringReader(source[start..end]));
        while (scanner.MoveNext())
        {
            if (scanner.Current is not YamlDotNet.Core.Tokens.Scalar token) continue;
            start += (int)token.Start.Index;
            break;
        }
        var quoted = scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted;
        if (quoted)
        {
            start++;
            end--;
        }
        else if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
        {
            var newline = source.AsSpan(start, end - start).IndexOfAny('\r', '\n');
            if (newline >= 0) newline += start;
            start = newline < 0 ? end : newline + 1;
            if (newline >= 0 && source[newline] == '\r' && start < end && source[start] == '\n') start++;
            while (start < end && source[start] is ' ' or '\t') start++;
        }
        _start = start;

        var units = new List<(char Character, int Start, int End)>();
        for (var position = start; position < end;)
        {
            var unitStart = position;
            var character = source[position++];
            if (scalar.Style == ScalarStyle.SingleQuoted && character == '\'' && position < end && source[position] == '\'')
            {
                position++;
            }
            else if (scalar.Style == ScalarStyle.DoubleQuoted && character == '\\')
            {
                if (source[position] is '\r' or '\n')
                {
                    while (position < end && char.IsWhiteSpace(source[position])) position++;
                    continue;
                }
                var length = source[position] switch { 'x' => 3, 'u' => 5, 'U' => 9, _ => 1 };
                position += length;
                // Let the YAML parser decode escape syntax, including supplementary Unicode characters.
                var escape = new YamlStream();
                escape.Load(new StringReader("\"" + source[unitStart..position] + "\""));
                var decoded = ((YamlScalarNode)escape.Documents[0].RootNode).Value!;
                foreach (var value in decoded) units.Add((value, unitStart, position));
                continue;
            }
            units.Add((character, unitStart, position));
        }

        var valueText = scalar.Value ?? string.Empty;
        _characters = new (int, int)[valueText.Length];
        var cursor = 0;
        for (var index = 0; index < valueText.Length; index++)
        {
            if (char.IsWhiteSpace(valueText[index])) continue;
            while (cursor < units.Count && char.IsWhiteSpace(units[cursor].Character)) cursor++;
            if (cursor == units.Count || units[cursor].Character != valueText[index])
                throw new InvalidDataException("Decoded YAML scalar does not match its source characters.");
            _characters[index] = (units[cursor].Start, units[cursor].End);
            cursor++;
        }
        // YAML folding/chomping changes whitespace counts, but each decoded whitespace run still
        // corresponds to the source gap between its adjacent decoded non-whitespace characters.
        for (var index = 0; index < valueText.Length;)
        {
            if (!char.IsWhiteSpace(valueText[index]))
            {
                index++;
                continue;
            }
            var first = index;
            while (index < valueText.Length && char.IsWhiteSpace(valueText[index])) index++;
            var gapStart = first == 0 ? start : _characters[first - 1].End;
            var gapEnd = index == valueText.Length ? end : _characters[index].Start;
            for (var whitespace = first; whitespace < index; whitespace++)
                _characters[whitespace] = (gapStart, gapEnd);
        }
    }

    internal SourceRange Range(int offset, int length)
    {
        var start = offset < _characters.Length ? _characters[offset].Start :
            _characters.Length > 0 ? _characters[^1].End : _start;
        var end = length > 0 ? _characters[offset + length - 1].End : start;
        return new(Locate(start), Locate(end));
    }

    private SourceLocation Locate(int offset)
    {
        var line = Array.BinarySearch(_lines, offset);
        if (line < 0) line = ~line - 1;
        // CRLF occupies two UTF-16 code units; source columns include neither line-ending character.
        var column = offset - _lines[line] + 1;
        if (offset < _source.Length && _source[offset] == '\n' && offset > 0 && _source[offset - 1] == '\r') column--;
        return new(StorySource.ManifestFileName, line + 1, column);
    }
}
