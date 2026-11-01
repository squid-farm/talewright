using System.Text;
using Markdig;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Analysis;

namespace Talewright.Talemark.Parsing;

/// <summary>
/// A run of source text (one or more joined lines) that remembers where every character came from, so
/// diagnostics inside multi-line paragraphs still point at the right line and column.
/// </summary>
internal sealed class SourceText
{
    private readonly List<(int Start, SourceLocation Location)> _lines = [];
    private readonly StringBuilder _text = new();
    private readonly SourceLocation[]? _locations;

    internal SourceText()
    {
    }

    private SourceText(string text, SourceLocation[] locations)
    {
        _text.Append(text);
        _locations = locations;
    }

    public string Text => _text.ToString();

    public bool IsEmpty => _text.Length == 0;

    public void AppendLine(string line, SourceLocation location)
    {
        if (_text.Length > 0)
        {
            _text.Append('\n');
        }

        _lines.Add((_text.Length, location));
        _text.Append(line);
    }

    public static SourceText Single(string text, SourceLocation location)
    {
        var source = new SourceText();
        source.AppendLine(text, location);
        return source;
    }

    public SourceLocation Locate(int offset)
    {
        if (_locations is { Length: > 0 })
        {
            var index = offset < 0 ? 0 : Math.Min(offset, _locations.Length - 1);
            return _locations[index];
        }

        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (offset >= _lines[i].Start)
            {
                return _lines[i].Location.Offset(offset - _lines[i].Start);
            }
        }

        return _lines.Count > 0 ? _lines[0].Location : SourceLocation.None;
    }

    /// <summary>
    /// A string whose characters are not a 1:1 slice of a source line.
    /// <paramref name="locations"/> has one entry per character, plus a final entry for the position after the text.
    /// </summary>
    public static SourceText Mapped(string text, IReadOnlyList<SourceLocation> locations)
    {
        if (locations.Count != text.Length + 1)
        {
            throw new ArgumentException("A mapped source needs one location per character and one trailing location.", nameof(locations));
        }

        return new SourceText(text, locations as SourceLocation[] ?? locations.ToArray());
    }
}

/// <summary>
/// Turns one paragraph (or choice label, or speech line) of prose into <see cref="TemplateInline"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Strategy: every balanced <c>{…}</c> is swapped for a private-use placeholder (<c>U+E000 n U+E001</c>) so
/// Markdig never sees our syntax, Markdig parses the inline Markdown (emphasis, code, line breaks, images),
/// and then the walker splits text runs on the placeholders and swaps the parsed substitutions or
/// <c>{if}</c>/<c>{else}</c>/<c>{/if}</c> markers back in.
/// </para>
/// <para>
/// Inline conditionals are folded per formatting container, so <c>*{if x}a*{/if}</c> (a conditional that
/// crosses an emphasis boundary) is reported as an error rather than guessed at.
/// </para>
/// </remarks>
internal static class InlineParser
{
    private const char PlaceholderStart = '\uE000';
    private const char PlaceholderEnd = '\uE001';

    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

    private static MarkdownPipeline BuildPipeline()
    {
        var builder = new MarkdownPipelineBuilder();
        builder.UsePreciseSourceLocation();

        // Block structure is handled by TaleParser; Markdig only ever sees one paragraph.
        builder.BlockParsers.RemoveAll(p => p is not ParagraphBlockParser);

        // No raw HTML or autolinks in story text.
        builder.InlineParsers.RemoveAll(p => p is AutolinkInlineParser);
        return builder.Build();
    }

    private abstract record Piece;

    private sealed record InlinePiece(TemplateInline Inline) : Piece;

    private sealed record IfPiece(Expr Condition, SourceLocation Location) : Piece;

    private sealed record ElsePiece(SourceLocation Location) : Piece;

    private sealed record EndIfPiece(SourceLocation Location) : Piece;

    private sealed class Context(SourceText source, StorySymbols symbols, DiagnosticBag diagnostics, bool allowImages)
    {
        public SourceText Source { get; } = source;
        public StorySymbols Symbols { get; } = symbols;
        public DiagnosticBag Diagnostics { get; } = diagnostics;
        public List<Piece?> Fragments { get; } = [];
        // Map Markdig's masked offsets back to authored text for migration diagnostics.
        public List<(int Offset, int Delta, int Start, int Length)> MaskedOffsets { get; } = [];
        public bool AllowImages { get; } = allowImages;
    }

    /// <summary>Parses prose. Errors are reported to <paramref name="diagnostics"/>; the result is still usable.</summary>
    public static IReadOnlyList<TemplateInline> Parse(SourceText source, StorySymbols symbols, DiagnosticBag diagnostics,
        bool allowImages = true)
    {
        var context = new Context(source, symbols, diagnostics, allowImages);
        var masked = Mask(context);
        var document = Markdown.Parse(masked, Pipeline);
        var pieces = new List<Piece>();
        foreach (var block in document)
        {
            if (block is ParagraphBlock { Inline: { } inline })
            {
                if (pieces.Count > 0)
                {
                    pieces.Add(new InlinePiece(new TextTemplate(" ")));
                }

                Walk(inline, pieces, context);
            }
        }

        return Fold(pieces, context);
    }

    /// <summary>Replaces each balanced <c>{…}</c> with a placeholder and parses its contents.</summary>
    private static string Mask(Context context)
    {
        var text = context.Source.Text;
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '{' or '}')
            {
                // Keep the escape for Markdig, which turns "\{" into a literal "{".
                result.Append(c).Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c != '{')
            {
                result.Append(c);
                i++;
                continue;
            }

            var close = FindClosingBrace(text, i);
            if (close < 0)
            {
                if (ImageSyntax.IsCall(text.AsSpan(i + 1)))
                {
                    if (!context.AllowImages)
                    {
                        context.Diagnostics.Error("TW1014",
                            "Image alt text cannot contain {image(...)}. Write the description as text.",
                            context.Source.Locate(i));
                    }
                    else
                    {
                        ImageSyntax.Parse(context.Source, i + 1, text.Length, false, context.Diagnostics, context.Symbols);
                    }

                    break;
                }

                if (context.Symbols.Analysis is { } analysis)
                {
                    analysis.InlineRange(context.Source, i + 1, text.Length);
                    var incomplete = text[(i + 1)..];
                    var lead = incomplete.Length - incomplete.TrimStart().Length;
                    var condition = incomplete[lead..].StartsWith("if ", StringComparison.Ordinal);
                    var offset = condition ? lead + 3 : 0;
                    if (condition)
                        analysis.Tokens.Add(new("if", new(context.Source.Locate(i + 1 + lead), context.Source.Locate(i + 3 + lead))));
                    analysis.Expression(condition ? ExpressionContextKind.Condition : ExpressionContextKind.Substitution,
                        incomplete[offset..], context.Source.Locate(i + 1).Offset(offset));
                }
                ExpressionParser.ReportStringErrors(text[(i + 1)..],
                    offset => context.Source.Locate(i + 1 + offset), context.Diagnostics);
                context.Diagnostics.Error("TW1004", "Unclosed '{'. Use '\\{' for a literal brace.", context.Source.Locate(i));
                result.Append(text, i, text.Length - i);
                break;
            }

            var inner = text[(i + 1)..close];
            var location = context.Source.Locate(i + 1);
            var image = ImageSyntax.IsCall(inner);
            Piece? fragment;
            if (image)
            {
                if (!context.AllowImages)
                {
                    context.Diagnostics.Error("TW1014",
                        "Image alt text cannot contain {image(...)}. Write the description as text.",
                        context.Source.Locate(i));
                    fragment = null;
                }
                else
                {
                    var lead = inner.Length - inner.TrimStart().Length;
                    context.Symbols.Analysis?.Tokens.Add(new("image",
                        new(context.Source.Locate(i + 1 + lead), context.Source.Locate(i + 1 + lead + 5))));
                    var media = ImageSyntax.Parse(context.Source, i + 1, close, true, context.Diagnostics, context.Symbols);
                    fragment = media is null ? null : new InlinePiece(media);
                }
            }
            else
            {
                if (context.Symbols.Analysis is { } collector)
                {
                    var trimmed = inner.Trim();
                    var lead = inner.Length - inner.TrimStart().Length;
                    var keyword = trimmed.StartsWith("if ", StringComparison.Ordinal) ? "if" :
                        trimmed is "else" or "/if" ? trimmed : null;
                    if (keyword is not null)
                        collector.Tokens.Add(new(keyword, new(context.Source.Locate(i + 1 + lead),
                            context.Source.Locate(i + 1 + lead + keyword.Length))));
                }
                context.Symbols.Analysis?.InlineRange(context.Source, i + 1, close);
                fragment = ParseFragment(inner, location, context, i + 1);
                if (fragment is InlinePiece { Inline: SubstitutionTemplate substitution })
                    context.Symbols.Analysis?.Substitutions.Add(new(
                        new(context.Source.Locate(i), context.Source.Locate(close + 1)), substitution));
            }
            context.Fragments.Add(fragment);
            var placeholder = $"{PlaceholderStart}{context.Fragments.Count - 1}{PlaceholderEnd}";
            result.Append(placeholder);
            context.MaskedOffsets.Add((result.Length, close + 1 - result.Length, i, close + 1 - i));
            i = close + 1;
        }

        return result.ToString();
    }

    /// <summary>Index of the <c>}</c> matching the <c>{</c> at <paramref name="open"/>, honouring nesting and strings.</summary>
    public static int FindClosingBrace(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString && c == '\\')
            {
                i++;
            }
            else if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString && c == '{')
            {
                depth++;
            }
            else if (!inString && c == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static Piece? ParseFragment(string inner, SourceLocation location, Context context, int sourceOffset)
    {
        var trimmed = inner.Trim();
        var lead = inner.Length - inner.TrimStart().Length;
        if (trimmed == "else")
        {
            return new ElsePiece(location);
        }

        if (trimmed == "/if")
        {
            return new EndIfPiece(location);
        }

        if (trimmed.StartsWith("if ", StringComparison.Ordinal))
        {
            var condition = ExpressionParser.ParseCondition(trimmed[3..], location.Offset(lead + 3), context.Symbols, context.Diagnostics,
                locate: offset => context.Source.Locate(sourceOffset + lead + 3 + offset));
            return condition is null ? null : new IfPiece(condition, location);
        }

        var substitution = ExpressionParser.ParseSubstitution(inner, location, context.Symbols, context.Diagnostics,
            offset => context.Source.Locate(sourceOffset + offset));
        return substitution is null ? null : new InlinePiece(substitution);
    }

    private static void Walk(ContainerInline container, List<Piece> pieces, Context context)
    {
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AddText(literal.Content.ToString(), pieces, context);
                    break;
                case HtmlEntityInline entity:
                    AddText(entity.Transcoded.ToString(), pieces, context);
                    break;
                case CodeInline code:
                    var codePieces = new List<Piece>();
                    AddText(code.Content, codePieces, context);
                    pieces.Add(new InlinePiece(new StyledTemplate(TextStyle.Code, Fold(codePieces, context))));
                    break;
                case LineBreakInline lineBreak:
                    pieces.Add(new InlinePiece(lineBreak.IsHard ? new LineBreakTemplate() : new TextTemplate(" ")));
                    break;
                case EmphasisInline emphasis:
                {
                    var style = emphasis.DelimiterCount >= 2 ? TextStyle.Strong : TextStyle.Emphasis;
                    var children = new List<Piece>();
                    Walk(emphasis, children, context);
                    pieces.Add(new InlinePiece(new StyledTemplate(style, Fold(children, context))));
                    break;
                }

                case LinkInline { IsImage: true } image:
                    var alt = PlainText(image);
                    for (var index = 0; index < context.MaskedOffsets.Count; index++)
                    {
                        var fragment = context.MaskedOffsets[index];
                        alt = alt.Replace($"{PlaceholderStart}{index}{PlaceholderEnd}", context.Source.Text.Substring(fragment.Start, fragment.Length));
                    }
                    var replacement = $"{{image({ImageSyntax.Quote(image.Url ?? "")}, {ImageSyntax.Quote(alt)})}}";
                    context.Diagnostics.Error("TW1015",
                        $"Markdown images are no longer supported. Use {replacement} instead.",
                        context.Source.Locate(image.Span.Start + context.MaskedOffsets
                            .LastOrDefault(offset => offset.Offset <= image.Span.Start).Delta));
                    break;
                case ContainerInline other:
                    // Links (and anything else with children): keep the text, drop the target. Stories have no links in v0.
                    Walk(other, pieces, context);
                    break;
                default:
                    AddText(inline.ToString() ?? string.Empty, pieces, context);
                    break;
            }
        }
    }

    private static void AddText(string text, List<Piece> pieces, Context context)
    {
        var start = 0;
        while (start < text.Length)
        {
            var open = text.IndexOf(PlaceholderStart, start);
            if (open < 0)
            {
                pieces.Add(new InlinePiece(new TextTemplate(text[start..])));
                return;
            }

            if (open > start)
            {
                pieces.Add(new InlinePiece(new TextTemplate(text[start..open])));
            }

            var close = text.IndexOf(PlaceholderEnd, open);
            var index = int.Parse(text.AsSpan(open + 1, close - open - 1));
            if (context.Fragments[index] is { } piece)
            {
                pieces.Add(piece);
            }

            start = close + 1;
        }
    }

    private static string PlainText(ContainerInline container)
    {
        var builder = new StringBuilder();
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            builder.Append(inline switch
            {
                LiteralInline l => l.Content.ToString(),
                ContainerInline c => PlainText(c),
                _ => string.Empty,
            });
        }

        return builder.ToString();
    }

    /// <summary>Folds <c>{if}</c>/<c>{else}</c>/<c>{/if}</c> markers into <see cref="ConditionalInlineTemplate"/>s.</summary>
    private static IReadOnlyList<TemplateInline> Fold(List<Piece> pieces, Context context)
    {
        var position = 0;
        var result = FoldUntilEnd(pieces, ref position, context, nested: false, out _);
        return result;
    }

    private static List<TemplateInline> FoldUntilEnd(List<Piece> pieces, ref int position, Context context, bool nested, out Piece? terminator)
    {
        var output = new List<TemplateInline>();
        while (position < pieces.Count)
        {
            var piece = pieces[position++];
            switch (piece)
            {
                case InlinePiece inline:
                    output.Add(inline.Inline);
                    break;
                case IfPiece open:
                {
                    var then = FoldUntilEnd(pieces, ref position, context, nested: true, out var end);
                    var otherwise = new List<TemplateInline>();
                    if (end is ElsePiece)
                    {
                        otherwise = FoldUntilEnd(pieces, ref position, context, nested: true, out end);
                        if (end is ElsePiece extra)
                        {
                            context.Diagnostics.Error("TW1005", "A second {else} in the same {if}.", extra.Location);
                        }
                    }

                    if (end is null)
                    {
                        context.Diagnostics.Error("TW1005", "{if} has no matching {/if} here. Inline conditionals must open and close inside the same paragraph and the same *emphasis*.", open.Location);
                    }

                    output.Add(new ConditionalInlineTemplate(open.Condition, then, otherwise));
                    break;
                }

                case ElsePiece or EndIfPiece when nested:
                    terminator = piece;
                    return output;
                case ElsePiece stray:
                    context.Diagnostics.Error("TW1005", "{else} without an {if} in the same paragraph and formatting.", stray.Location);
                    break;
                case EndIfPiece stray:
                    context.Diagnostics.Error("TW1005", "{/if} without an {if} in the same paragraph and formatting.", stray.Location);
                    break;
            }
        }

        terminator = null;
        return output;
    }
}
