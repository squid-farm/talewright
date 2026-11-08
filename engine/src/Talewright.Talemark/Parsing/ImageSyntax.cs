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
        {
            Add(index, index + 1, TalemarkClassificationKind.Punctuation);
            index++;
            SkipSpace();
        }

        if (index < end && text[index] == ExpressionVocabulary.ModifierMarker)
        {
            Add(index, index + 1, TalemarkClassificationKind.Punctuation);
            index++;
            var modifierStart = index;
            var modifierWork = 0;
            while (index < end && !char.IsWhiteSpace(text[index]))
            {
                if ((++modifierWork & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                index++;
            }
            Add(modifierStart, index, TalemarkClassificationKind.Modifier);
        }

        void ClassifyAlt()
        {
            if (index >= end || text[index] != TextVocabulary.QuoteMark)
                return;

            var alt = QuotedReaderTextSyntax.Read(source, index, end, cancellationToken);
            Add(index, index + 1, TalemarkClassificationKind.Punctuation);
            foreach (var escape in alt.QuoteEscapes)
                Add(escape, escape + 2, TalemarkClassificationKind.Escape);
            foreach (var classification in InlineParser.Classify(alt.ToSource(), cancellationToken))
                output.Add(classification);
            if (alt.Closed)
                Add(alt.End - 1, alt.End, TalemarkClassificationKind.Punctuation);
            index = alt.End;
        }

        void SkipSpace() => index = TextScanning.SkipWhitespace(text, index, end, cancellationToken);

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
        var accessibility = MediaAccessibility.Descriptive;
        var modifierOffset = -1;
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
        if (i < end && text[i] == ExpressionVocabulary.ModifierMarker)
        {
            modifierOffset = i;
            i++;
            var modifierStart = i;
            if (!text.AsSpan(i, end - i).StartsWith(ExpressionVocabulary.Decorative, StringComparison.Ordinal))
            {
                Error($"Unexpected text after the image call. Use '{ExpressionVocabulary.ModifierMarker}{ExpressionVocabulary.Decorative}' for a purely decorative image.", modifierOffset);
                return null;
            }

            i += ExpressionVocabulary.Decorative.Length;
            if (i < end && !char.IsWhiteSpace(text[i]))
            {
                Error($"Unexpected text after the image call. Use '{ExpressionVocabulary.ModifierMarker}{ExpressionVocabulary.Decorative}' for a purely decorative image.", modifierOffset);
                return null;
            }

            accessibility = MediaAccessibility.Decorative;
            symbols.Analysis?.Reference(ExpressionVocabulary.Decorative, source.Locate(modifierStart),
                AuthoringSymbolKind.Modifier, ExpressionVocabulary.Decorative);
            SkipSpace();
        }

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

        var hasAltConflict = accessibility == MediaAccessibility.Decorative && !string.IsNullOrWhiteSpace(alt);
        var resolved = StoryImageResolver.Resolve(symbols.ImageSource, path, alt, source.Locate(pathOffset), diagnostics,
            accessibility);
        if (hasAltConflict)
        {
            diagnostics.Error(CompilerDiagnosticCodes.DecorativeImageHasAlt,
                $"An image marked '{ExpressionVocabulary.ModifierMarker}{ExpressionVocabulary.Decorative}' cannot also have descriptive alt text. Remove the alt text or the modifier.",
                source.Locate(modifierOffset));
        }
        if (resolved is null || hasAltConflict) return null;
        return altTemplate is null
            ? new MediaTemplate(resolved.Path, resolved.AltText) { Accessibility = accessibility }
            : new MediaTemplate(resolved.Path, resolved.AltText)
            {
                AltTemplate = altTemplate,
                Accessibility = accessibility,
            };

        void SkipSpace() => i = TextScanning.SkipWhitespace(text, i, end, cancellationToken);

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
            var alt = QuotedReaderTextSyntax.Read(source, i, end, cancellationToken);
            i = alt.End;
            if (alt.Closed)
            {
                decoded = alt.Plain;
                var parsed = InlineParser.Parse(alt.ToSource(), symbols, diagnostics,
                    allowImages: false, cancellationToken: cancellationToken, allowFormattedOutput: false);
                if (parsed.Count > 0 && !IsPlain(parsed, decoded)) template = parsed;

                return true;
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
                var authored = alt.Authored;
                var count = authored.Length;
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
                    mapped.Add(alt.Locations[index]);
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
