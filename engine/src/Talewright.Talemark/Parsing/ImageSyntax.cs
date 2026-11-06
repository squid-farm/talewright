using System.Text;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal static class ImageSyntax
{
    internal static bool IsCall(ReadOnlySpan<char> text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith(ExpressionVocabulary.Image, StringComparison.Ordinal)
            && trimmed[ExpressionVocabulary.Image.Length..].TrimStart() is [ExpressionVocabulary.OpenParen, ..];
    }

    internal static void Classify(SourceText source, int start, int end,
        ICollection<TalemarkClassification> output, CancellationToken cancellationToken)
    {
        var text = source.Text;
        var index = start;
        SkipSpace();
        var functionStart = index;
        if (text.AsSpan(index, end - index).StartsWith(ExpressionVocabulary.Image, StringComparison.Ordinal))
        {
            index += ExpressionVocabulary.Image.Length;
            Add(functionStart, index, TalemarkClassificationKind.Function);
        }

        SkipSpace();
        if (index < end && text[index] == ExpressionVocabulary.OpenParen)
        {
            Add(index, index + 1, TalemarkClassificationKind.Punctuation);
            index++;
        }
        SkipSpace();

        if (index < end && text[index] == TextVocabulary.QuoteMark)
        {
            var pathStart = index;
            var path = QuotedStringSyntax.Read(text, index, end, cancellationToken: cancellationToken);
            index = path.End;
            Add(pathStart, index, TalemarkClassificationKind.String);
        }

        SkipSpace();
        if (index < end && text[index] == ExpressionVocabulary.ArgumentSeparator)
        {
            Add(index, index + 1, TalemarkClassificationKind.Punctuation);
            index++;
            SkipSpace();
            ClassifyAlt();
        }

        SkipSpace();
        if (index < end && text[index] == ExpressionVocabulary.CloseParen)
            Add(index, index + 1, TalemarkClassificationKind.Punctuation);

        void ClassifyAlt()
        {
            if (index >= end || text[index] != TextVocabulary.QuoteMark)
                return;

            Add(index, index + 1, TalemarkClassificationKind.Punctuation);
            index++;
            var authored = new StringBuilder();
            var locations = new List<SourceLocation>();
            var work = 0;
            while (index < end)
            {
                if ((++work & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var unit = QuotedStringSyntax.ReadUnit(text, index, end);
                if (unit.ClosingQuote)
                {
                    index = unit.End;
                    Add(index - 1, index, TalemarkClassificationKind.Punctuation);
                    locations.Add(source.Locate(index));
                    AppendInlineClassifications(authored, locations);
                    return;
                }

                if (unit.Escaped)
                {
                    var next = unit.Character;
                    index = unit.End;
                    if (next == TextVocabulary.QuoteMark)
                    {
                        authored.Append(next);
                        locations.Add(source.Locate(index - 1));
                    }
                    else
                    {
                        authored.Append(TextVocabulary.EscapeMark).Append(next);
                        locations.Add(source.Locate(index - 2));
                        locations.Add(source.Locate(index - 1));
                    }
                    continue;
                }

                authored.Append(unit.Character);
                locations.Add(source.Locate(index));
                index = unit.End;
            }

            locations.Add(source.Locate(index));
            AppendInlineClassifications(authored, locations);

            void AppendInlineClassifications(StringBuilder value, List<SourceLocation> positions)
            {
                foreach (var classification in InlineParser.Classify(SourceText.Mapped(value.ToString(), positions),
                             cancellationToken))
                    output.Add(classification);
            }
        }

        void SkipSpace()
        {
            while (index < end && char.IsWhiteSpace(text[index]))
            {
                if ((index & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                index++;
            }
        }

        void Add(int from, int to, TalemarkClassificationKind kind)
        {
            if (to > from)
                output.Add(new(new SourceRange(source.Locate(from), source.Locate(to)), kind));
        }
    }

    internal static MediaTemplate? Parse(SourceText source, int start, int end, bool closed, DiagnosticBag diagnostics,
        StorySymbols symbols, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(symbols.ImageSource);
        var text = source.Text;
        var i = start;
        SkipSpace();
        i += ExpressionVocabulary.Image.Length;
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
        if (i < end && text[i] == ExpressionVocabulary.ArgumentSeparator)
        {
            i++;
            SkipSpace();
            if (!ReadAlt(out alt, out altTemplate)) return null;
            SkipSpace();
        }

        if (i >= end || text[i] != ExpressionVocabulary.CloseParen)
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
            if (i >= end || text[i] != TextVocabulary.QuoteMark)
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
                Error($"Unknown image string escape. Use '{TextVocabulary.EscapeMark}{TextVocabulary.QuoteMark}' " +
                    $"for a quote or '{TextVocabulary.EscapeMark}{TextVocabulary.EscapeMark}' for a backslash.",
                    literal.ErrorOffset);
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
            if (i >= end || text[i] != TextVocabulary.QuoteMark)
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
                var unit = QuotedStringSyntax.ReadUnit(text, i, end);
                if (unit.ClosingQuote)
                {
                    i = unit.End;
                    locations.Add(source.Locate(i));
                    decoded = plain.ToString();
                    var authored = templateText.ToString();
                    var parsed = InlineParser.Parse(SourceText.Mapped(authored, locations), symbols, diagnostics,
                        allowImages: false, cancellationToken: cancellationToken, allowFormattedOutput: false);
                    if (parsed.Count > 0 && !IsPlain(parsed, decoded)) template = parsed;

                    return true;
                }

                if (unit.Escaped)
                {
                    var next = unit.Character;
                    i = unit.End;
                    if (next == TextVocabulary.QuoteMark)
                    {
                        templateText.Append(next);
                        plain.Append(next);
                        locations.Add(source.Locate(i - 1));
                    }
                    else if (next == TextVocabulary.EscapeMark)
                    {
                        templateText.Append(TextVocabulary.EscapeMark).Append(TextVocabulary.EscapeMark);
                        plain.Append(next);
                        locations.Add(source.Locate(i - 2));
                        locations.Add(source.Locate(i - 1));
                    }
                    else if (DelimiterScanner.IsReaderTextEscape(next))
                    {
                        templateText.Append(TextVocabulary.EscapeMark).Append(next);
                        plain.Append(next);
                        locations.Add(source.Locate(i - 2));
                        locations.Add(source.Locate(i - 1));
                    }
                    else
                    {
                        templateText.Append(TextVocabulary.EscapeMark).Append(next);
                        plain.Append(TextVocabulary.EscapeMark).Append(next);
                        locations.Add(source.Locate(i - 2));
                        locations.Add(source.Locate(i - 1));
                    }

                    continue;
                }

                var c = unit.Character;
                templateText.Append(c);
                plain.Append(c);
                locations.Add(source.Locate(i));
                i = unit.End;
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
                if (trimTrailingCall && authored.EndsWith(ExpressionVocabulary.CloseParen))
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
                    allowImages: false, cancellationToken: cancellationToken, allowFormattedOutput: false);
            }
        }

        void Error(string message, int offset) =>
            diagnostics.Error(CompilerDiagnosticCodes.InvalidImageCall, message, source.Locate(offset));
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
            if (text[i] == TextVocabulary.EscapeMark && i + 1 < text.Length &&
                DelimiterScanner.IsReaderTextEscape(text[i + 1]))
            {
                i++;
                continue;
            }

            if (text[i] == SceneVocabulary.InlineOpen) return true;
        }

        return false;
    }

    private static bool IsPlain(IReadOnlyList<TemplateInline> inlines, string decoded) =>
        inlines.Count == 1 && inlines[0] is TextTemplate text && text.Text == decoded;
}
