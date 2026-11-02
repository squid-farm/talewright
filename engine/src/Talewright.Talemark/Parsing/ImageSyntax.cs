using System.Text;
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
        StorySymbols symbols, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(symbols.ImageSource);
        var text = source.Text;
        var i = start;
        SkipSpace();
        i += "image".Length;
        SkipSpace();
        i++;
        SkipSpace();
        var pathOffset = i;
        if (!ReadString(out var path)) return null;
        if (string.IsNullOrWhiteSpace(path))
        {
            Error("An image needs a non-empty path.", pathOffset);
            return null;
        }

        SkipSpace();
        var alt = "";
        IReadOnlyList<TemplateInline>? altTemplate = null;
        if (i < end && text[i] == ',')
        {
            i++;
            SkipSpace();
            if (!ReadAlt(out alt, out altTemplate)) return null;
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

        var resolved = StoryImageResolver.Resolve(symbols.ImageSource, path, alt, source.Locate(pathOffset), diagnostics);
        if (resolved is null) return null;
        return altTemplate is null
            ? new MediaTemplate(resolved.Path, resolved.AltText)
            : new MediaTemplate(resolved.Path, resolved.AltText) { AltTemplate = altTemplate };

        void SkipSpace()
        {
            while (i < end && char.IsWhiteSpace(text[i]))
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                i++;
            }
        }

        bool ReadString(out string value)
        {
            value = "";
            if (i >= end || text[i] != '"')
            {
                Error("Image arguments must be in double quotes. Write {image(\"path\", \"alt text\")}.", i);
                return false;
            }

            var quote = i;
            var offsets = new List<int>();
            var literal = QuotedStringSyntax.Read(text, i, end, offsets, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            i = literal.End;
            if (literal.Error == QuotedStringSyntax.InvalidEscape)
            {
                Error("Unknown image string escape. Use '\\\"' for a quote or '\\\\' for a backslash.", literal.ErrorOffset);
                return false;
            }

            symbols.Analysis?.ImagePath(literal.Value,
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

        bool ReadAlt(out string decoded, out IReadOnlyList<TemplateInline>? template)
        {
            decoded = "";
            template = null;
            if (i >= end || text[i] != '"')
            {
                Error("Image arguments must be in double quotes. Write {image(\"path\", \"alt text\")}.", i);
                return false;
            }

            var quote = i;
            i++;
            var templateText = new StringBuilder();
            var plain = new StringBuilder();
            var locations = new List<SourceLocation>();
            var nextCancellationCheck = i;
            while (i < end)
            {
                if (i >= nextCancellationCheck)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    nextCancellationCheck = i + Math.Min(1024, end - i);
                }
                var c = text[i];
                if (c == '"')
                {
                    i++;
                    locations.Add(source.Locate(i));
                    decoded = plain.ToString();
                    var authored = templateText.ToString();
                    var parsed = InlineParser.Parse(SourceText.Mapped(authored, locations), symbols, diagnostics,
                        allowImages: false, cancellationToken: cancellationToken);
                    if (parsed.Count > 0 && !IsPlain(parsed, decoded)) template = parsed;

                    return true;
                }

                if (c == '\\')
                {
                    if (i + 1 >= end || text[i + 1] is not ('"' or '\\' or '{' or '}' or '[' or ']' or '*' or '_' or '`'))
                    {
                        AnalyzePartial(trimTrailingCall: false);
                        Error("Unknown image string escape. Use '\\\"', '\\\\', or a reader-text escape such as '\\{' or '\\*'.", i);
                        return false;
                    }

                    var next = text[++i];
                    i++;
                    if (next is '"' or '\\')
                    {
                        templateText.Append(next);
                        plain.Append(next);
                        locations.Add(source.Locate(i - 1));
                    }
                    else
                    {
                        templateText.Append('\\').Append(next);
                        plain.Append(next);
                        locations.Add(source.Locate(i - 2));
                        locations.Add(source.Locate(i - 1));
                    }

                    continue;
                }

                templateText.Append(c);
                plain.Append(c);
                locations.Add(source.Locate(i));
                i++;
            }

            AnalyzePartial(trimTrailingCall: true);
            Error("The image argument is missing its closing double quote.", quote);
            return false;

            // Completions need the alt template even when the quote never closes. That parse is not
            // the story: a scratch bag drops unclosed-brace and half-typed-name diagnostics so the
            // author still sees only the missing-quote (or bad-escape) error, and no media is returned.
            // A final ')' is the image call's closer typed before the quote, not alt text.
            void AnalyzePartial(bool trimTrailingCall)
            {
                if (symbols.Analysis is null) return;
                var authored = templateText.ToString();
                var count = locations.Count;
                var endAt = i;
                if (trimTrailingCall && authored.EndsWith(')'))
                {
                    authored = authored[..^1];
                    count--;
                    endAt--;
                }

                if (!HasTemplate(authored, cancellationToken)) return;
                var mapped = new List<SourceLocation>(count + 1);
                for (var index = 0; index < count; index++)
                {
                    if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    mapped.Add(locations[index]);
                }
                mapped.Add(source.Locate(endAt));
                InlineParser.Parse(SourceText.Mapped(authored, mapped), symbols, new DiagnosticBag(),
                    allowImages: false, cancellationToken: cancellationToken);
            }
        }

        void Error(string message, int offset) =>
            diagnostics.Error("TW1014", message, source.Locate(offset));
    }

    private static bool HasTemplate(string text, CancellationToken cancellationToken)
    {
        var nextCancellationCheck = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (i >= nextCancellationCheck)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = i + Math.Min(1024, text.Length - i);
            }
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '{' or '}')
            {
                i++;
                continue;
            }

            if (text[i] == '{') return true;
        }

        return false;
    }

    private static bool IsPlain(IReadOnlyList<TemplateInline> inlines, string decoded) =>
        inlines.Count == 1 && inlines[0] is TextTemplate text && text.Text == decoded;

    internal static string Quote(string text) => QuotedStringSyntax.Quote(text);
}
