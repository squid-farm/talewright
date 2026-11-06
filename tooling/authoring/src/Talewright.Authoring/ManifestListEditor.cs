using Talewright.Core.Identity;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Talewright.Authoring;

internal sealed record ManifestTextEdit(SourceRange Range, string NewText);
internal sealed record ManifestEditResult(ManifestTextEdit? Edit, string? Reason);

internal static class ManifestListEditor
{
    internal static ManifestEditResult Append(string text, string file, string section, string item)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException)
        {
            return new(null, $"Cannot safely edit '{file}' because it contains invalid YAML.");
        }

        var scalar = FormatScalar(item);
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return new(null, $"Cannot safely edit '{file}' because it must contain one YAML mapping.");
        if (root.Style == MappingStyle.Flow)
            return new(null, $"Cannot safely edit '{file}' because its root mapping uses flow style.");

        if (!root.Children.TryGetValue(new YamlScalarNode(section), out var node))
        {
            if (HasDocumentEndMarker(text))
                return new(null, $"Cannot safely add '{section}' to '{file}' because it has an explicit YAML document end marker.");

            var newline = DetectNewline(text);
            var hasFinalNewline = EndsInNewline(text);
            var separator = text.Length == 0 ? "" : hasFinalNewline ? newline : newline + newline;
            var ending = hasFinalNewline ? newline : "";
            var insertion = $"{separator}{section}:{newline}  - {scalar}{ending}";
            var point = LocationAt(file, text, text.Length);
            return new(new(new(point, point), insertion), null);
        }

        if (node is not YamlSequenceNode sequence)
            return new(null, $"Cannot safely edit '{file}' because '{section}' is not a YAML list.");

        foreach (var child in sequence.Children)
        {
            if (child is not YamlScalarNode entry ||
                entry.Style is not (ScalarStyle.Plain or ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted) ||
                !IdentifierRules.IsValid(entry.Value ?? ""))
                return new(null, $"Cannot safely edit '{file}' because '{section}' contains an unsupported or invalid entry.");
        }
        if (sequence.Children.OfType<YamlScalarNode>().Any(entry => entry.Value == item))
            return new(null, $"Cannot safely edit '{file}' because '{item}' is already declared.");

        return sequence.Style switch
        {
            SequenceStyle.Flow => AppendFlow(text, file, section, sequence, scalar),
            SequenceStyle.Block => AppendBlock(text, file, section, sequence, scalar),
            _ => new(null, $"Cannot safely edit '{file}' because '{section}' uses an unsupported YAML list style.")
        };
    }

    internal static ManifestEditResult AppendMappingEntry(
        string text, string file, string section, string key, string blockValue, string flowValue)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException)
        {
            return new(null, $"Cannot safely edit '{file}' because it contains invalid YAML.");
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return new(null, $"Cannot safely edit '{file}' because it must contain one YAML mapping.");
        if (root.Style == MappingStyle.Flow)
            return new(null, $"Cannot safely edit '{file}' because its root mapping uses flow style.");
        if (!IdentifierRules.IsValid(key))
            return new(null, $"Cannot safely add '{key}' to '{file}' because it is not a valid identifier.");

        var formattedKey = FormatScalar(key);
        if (!root.Children.TryGetValue(new YamlScalarNode(section), out var node))
        {
            if (HasDocumentEndMarker(text))
                return new(null, $"Cannot safely add '{section}' to '{file}' because it has an explicit YAML document end marker.");

            var newline = DetectNewline(text);
            var hasFinalNewline = EndsInNewline(text);
            var separator = text.Length == 0 ? "" : hasFinalNewline ? newline : newline + newline;
            var sectionEnding = hasFinalNewline ? newline : "";
            var rootIndent = root.Children.Count == 0 ? 0 : checked((int)root.Children.First().Key.Start.Column - 1);
            var entry = BlockEntry(formattedKey, blockValue, rootIndent + 2, newline);
            var insertion = $"{separator}{new string(' ', rootIndent)}{FormatScalar(section)}:{newline}{entry}{sectionEnding}";
            var point = LocationAt(file, text, text.Length);
            return new(new(new(point, point), insertion), null);
        }

        if (node is not YamlMappingNode mapping)
            return new(null, $"Cannot safely edit '{file}' because '{section}' is not a YAML mapping.");
        if (!HasSupportedMappingKeys(mapping))
            return new(null, $"Cannot safely edit '{file}' because '{section}' contains an unsupported or invalid entry.");
        if (mapping.Children.Keys.OfType<YamlScalarNode>().Any(existing => existing.Value == key))
            return new(null, $"Cannot safely edit '{file}' because '{key}' is already declared.");

        if (mapping.Style == MappingStyle.Flow)
        {
            int offset;
            string insertion;
            if (mapping.Children.Count == 0)
            {
                offset = checked((int)mapping.Start.Index + 1);
                insertion = $"{formattedKey}: {flowValue}";
            }
            else
            {
                var last = mapping.Children.Last();
                offset = checked((int)last.Value.End.Index);
                var closingBrace = text.IndexOf('}', offset);
                if (closingBrace < 0)
                    return new(null, $"Cannot safely edit '{file}' because the '{section}' source range is unavailable.");
                var suffix = text[offset..closingBrace];
                if (suffix.Contains('#'))
                    return new(null, $"Cannot safely edit '{file}' because '{section}' has a comment inside its flow mapping.");
                insertion = $", {formattedKey}: {flowValue}";
            }

            if (offset < 0 || offset > text.Length)
                return new(null, $"Cannot safely edit '{file}' because the '{section}' source range is unavailable.");
            var point = LocationAt(file, text, offset);
            return new(new(new(point, point), insertion), null);
        }

        if (mapping.Style != MappingStyle.Block || mapping.Children.Count == 0)
            return new(null, $"Cannot safely edit '{file}' because '{section}' uses an unsupported YAML mapping style.");

        var lastEntry = mapping.Children.Last();
        var indent = checked((int)lastEntry.Key.Start.Column - 1);
        var lineEnd = OffsetAfterLine(text, checked((int)lastEntry.Value.End.Line - 1));
        if (indent < 0 || lineEnd < 0 || lineEnd > text.Length)
            return new(null, $"Cannot safely edit '{file}' because the last '{section}' entry has no source line.");

        var newlineForEntry = DetectNewline(text);
        var hasFollowingContent = lineEnd < text.Length;
        var finalNewline = EndsInNewline(text);
        var insertionOffset = lineEnd;
        var prefix = lineEnd == text.Length && !finalNewline ? newlineForEntry : "";
        var ending = hasFollowingContent || finalNewline ? newlineForEntry : "";
        var blockEntry = BlockEntry(formattedKey, blockValue, indent, newlineForEntry);
        var blockInsertion = $"{prefix}{blockEntry}{ending}";
        var insertionPoint = LocationAt(file, text, insertionOffset);
        return new(new(new(insertionPoint, insertionPoint), blockInsertion), null);
    }

    private static ManifestEditResult AppendFlow(string text, string file, string section, YamlSequenceNode sequence, string item)
    {
        int offset;
        string insertion;
        if (sequence.Children.Count == 0)
        {
            offset = checked((int)sequence.Start.Index + 1);
            insertion = item;
        }
        else
        {
            var last = sequence.Children[^1];
            offset = checked((int)last.End.Index);
            insertion = $", {item}";
        }

        if (offset < 0 || offset > text.Length)
            return new(null, $"Cannot safely edit '{file}' because the '{section}' source range is unavailable.");
        var point = LocationAt(file, text, offset);
        return new(new(new(point, point), insertion), null);
    }

    private static ManifestEditResult AppendBlock(string text, string file, string section, YamlSequenceNode sequence, string item)
    {
        if (sequence.Children.Count == 0)
            return new(null, $"Cannot safely edit '{file}' because the empty '{section}' list has no block items.");

        var last = sequence.Children[^1];
        if (last.Start.Line != last.End.Line)
            return new(null, $"Cannot safely edit '{file}' because the last '{section}' item spans multiple lines.");

        var lineStarts = LineStarts(text);
        var line = checked((int)last.Start.Line - 1);
        if (line < 0 || line >= lineStarts.Length)
            return new(null, $"Cannot safely edit '{file}' because the last '{section}' item has no source line.");

        var lineStart = lineStarts[line];
        var dash = lineStart;
        while (dash < text.Length && text[dash] is ' ' or '\t') dash++;
        if (dash >= text.Length || text[dash] != '-' ||
            dash + 1 < text.Length && !char.IsWhiteSpace(text[dash + 1]))
            return new(null, $"Cannot safely edit '{file}' because the last '{section}' item has an unsupported layout.");

        var lineEnd = dash;
        while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n')) lineEnd++;
        var childEnd = checked((int)last.End.Index);
        if (childEnd > lineEnd || text[childEnd..lineEnd].Trim() is { Length: > 0 } trailing &&
            !trailing.StartsWith('#'))
            return new(null, $"Cannot safely edit '{file}' because the last '{section}' item has an unsupported layout.");

        var newline = DetectNewline(text);
        var indentation = text[lineStart..dash];
        var hasLineEnding = lineEnd < text.Length;
        var insertionOffset = hasLineEnding
            ? lineEnd + (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1)
            : lineEnd;
        var insertion = hasLineEnding
            ? $"{indentation}- {item}{newline}"
            : $"{newline}{indentation}- {item}";
        var point = LocationAt(file, text, insertionOffset);
        return new(new(new(point, point), insertion), null);
    }

    // YAML 1.2 core resolves these plain spellings as booleans or null.
    private static readonly HashSet<string> ReservedScalars = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null"
    };

    internal static string FormatScalar(string item) => ReservedScalars.Contains(item) ? $"'{item}'" : item;

    private static bool HasSupportedMappingKeys(YamlMappingNode mapping) =>
        mapping.Children.Keys.All(key =>
            key is YamlScalarNode scalar && IdentifierRules.IsValid(scalar.Value ?? ""));

    private static string BlockEntry(string key, string value, int indent, string newline)
    {
        var prefix = $"{new string(' ', indent)}{key}:";
        return value.StartsWith('\n')
            ? prefix + value.Replace("\n", $"{newline}{new string(' ', indent + 2)}")
            : $"{prefix} {value}";
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                starts.Add(index + 1);
            }
            else if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }
        return starts.ToArray();
    }

    private static int OffsetAfterLine(string text, int line)
    {
        var starts = LineStarts(text);
        if (line < 0 || line >= starts.Length) return -1;

        var end = starts[line];
        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        if (end < text.Length && text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n')
            return end + 2;
        return end < text.Length ? end + 1 : end;
    }

    private static SourceLocation LocationAt(string file, string text, int offset)
    {
        var line = 1;
        var column = 1;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < offset && text[index + 1] == '\n') index++;
                line++;
                column = 1;
            }
            else if (text[index] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return new(file, line, column);
    }

    private static bool HasDocumentEndMarker(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Any(line => line.Trim() == "...");

    private static bool EndsInNewline(string text) => text.EndsWith('\n') || text.EndsWith('\r');

    private static string DetectNewline(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
                return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
            if (text[index] == '\n') return "\n";
        }
        return "\n";
    }
}
