using Talewright.Narrative.Templates;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;

namespace Talewright.Talemark.Parsing;

internal static class ImageSyntax
{
    internal static bool IsCall(ReadOnlySpan<char> text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("image", StringComparison.Ordinal)
            && trimmed[5..].TrimStart().StartsWith("(", StringComparison.Ordinal);
    }

    internal static MediaTemplate? Parse(SourceText source, int start, int end, bool closed, DiagnosticBag diagnostics,
        StorySource? imageSource, AnalysisCollector? analysis = null)
    {
        ArgumentNullException.ThrowIfNull(imageSource);
        var text = source.Text;
        var i = start;
        SkipSpace();
        i += "image".Length;
        SkipSpace();
        i++;
        SkipSpace();
        var pathOffset = i;
        if (!ReadString(out var path, true)) return null;
        if (string.IsNullOrWhiteSpace(path))
        {
            Error("An image needs a non-empty path.", pathOffset);
            return null;
        }

        SkipSpace();
        var alt = "";
        if (i < end && text[i] == ',')
        {
            i++;
            SkipSpace();
            if (!ReadString(out alt)) return null;
            SkipSpace();
        }

        if (i >= end || text[i] != ')')
        {
            Error("Expected ')' after the image arguments. Write {image(\"path\", \"alt text\")}.", i);
            return null;
        }

        i++;
        SkipSpace();
        if (i != end)
        {
            Error("Unexpected text after the image call. Close it with '}'.", i);
            return null;
        }

        if (!closed)
        {
            Error("The image call is missing its closing '}'.", end);
            return null;
        }

        var resolved = StoryImageResolver.Resolve(imageSource, path, alt, source.Locate(pathOffset), diagnostics);
        return resolved is null ? null : new(resolved.Path, resolved.AltText);

        void SkipSpace()
        {
            while (i < end && char.IsWhiteSpace(text[i])) i++;
        }

        bool ReadString(out string value, bool isPath = false)
        {
            value = "";
            if (i >= end || text[i] != '"')
            {
                Error("Image arguments must be in double quotes. Write {image(\"path\", \"alt text\")}.", i);
                return false;
            }

            var quote = i;
            var offsets = new List<int>();
            var literal = QuotedStringSyntax.Read(text, i, end, offsets);
            i = literal.End;
            if (literal.Error == QuotedStringSyntax.InvalidEscape)
            {
                Error("Unknown image string escape. Use '\\\"' for a quote or '\\\\' for a backslash.", literal.ErrorOffset);
                return false;
            }

            if (isPath)
                analysis?.ImagePath(literal.Value,
                    new(source.Locate(quote + 1), source.Locate(literal.Closed ? i - 1 : i)),
                    offsets.Select(source.Locate).ToArray());
            if (literal.Error is not null)
            {
                Error("The image argument is missing its closing double quote.", quote);
                return false;
            }
            value = literal.Value;
            return true;
        }

        void Error(string message, int offset) =>
            diagnostics.Error("TW1014", message, source.Locate(offset));
    }

    internal static string Quote(string text) => QuotedStringSyntax.Quote(text);
}
