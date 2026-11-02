using System.Text;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;

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
    private string? _frozen;

    internal SourceText()
    {
    }

    private SourceText(string text, SourceLocation[] locations)
    {
        _text.Append(text);
        _frozen = text;
        _locations = locations;
    }

    public string Text => _frozen ??= _text.ToString();

    public bool IsEmpty => _text.Length == 0;

    internal long Probes { get; private set; }

    public void AppendLine(string line, SourceLocation location)
    {
        _frozen = null;
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

        var low = 0;
        var high = _lines.Count;
        while (low < high)
        {
            Probes++;
            var middle = low + (high - low) / 2;
            if (_lines[middle].Start <= offset) low = middle + 1;
            else high = middle;
        }

        if (low > 0) return _lines[low - 1].Location.Offset(offset - _lines[low - 1].Start);
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

internal sealed record InlineParseResult(IReadOnlyList<TemplateInline> Inlines, int WorkUnits);

/// <summary>Parses Talemark reader text directly into its inline template tree.</summary>
internal static class InlineParser
{
    private const int MaximumInlineConditionDepth = 64;
    private const int MaximumFormattingDepth = 64;
    private const int CancellationInterval = 512;

    private enum TokenKind
    {
        Text,
        SoftBreak,
        HardBreak,
        Construct,
        LegacyImage,
        FormatDelimiter,
        FormatOpen,
        FormatClose,
        CodeDelimiter,
        CodeOpen,
        CodeClose,
    }

    private sealed class Token(TokenKind kind, int start, int length, string? text = null)
    {
        public TokenKind Kind { get; set; } = kind;
        public int Start { get; } = start;
        public int Length { get; } = length;
        public int End => Start + Length;
        public string? Text { get; set; } = text;
        public string? LegacyAlt { get; set; }
        public string? LegacyPath { get; set; }
        public Piece? Piece { get; set; }
        public int RunLength { get; set; }
        public TextStyle Style { get; set; }
        public bool CanOpen { get; set; }
        public bool CanClose { get; set; }
        public bool InCode { get; set; }
        public int NextRun { get; set; } = -1;
        public int Mate { get; set; } = -1;
        public int TrimOffset { get; set; } = -1;
    }

    private abstract record Piece;

    private sealed record InlinePiece(TemplateInline Inline) : Piece;

    private sealed record IfPiece(Expr Condition, SourceLocation Location, SourceLocation StartLocation) : Piece;

    private sealed record ElsePiece(SourceLocation Location) : Piece;

    private sealed record EndIfPiece(SourceLocation Location) : Piece;

    private sealed class Context(
        SourceText source,
        StorySymbols symbols,
        DiagnosticBag diagnostics,
        bool allowImages,
        CancellationToken cancellationToken)
    {
        private int _remaining = CancellationInterval;

        public SourceText Source { get; } = source;
        public StorySymbols Symbols { get; } = symbols;
        public DiagnosticBag Diagnostics { get; } = diagnostics;
        public bool AllowImages { get; } = allowImages;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public int WorkUnits { get; set; }

        public void Check()
        {
            WorkUnits++;
            if (--_remaining != 0) return;
            CancellationToken.ThrowIfCancellationRequested();
            _remaining = CancellationInterval;
        }

        public void CheckCharacters(int count)
        {
            WorkUnits += count;
            _remaining -= count;
            if (_remaining > 0) return;
            CancellationToken.ThrowIfCancellationRequested();
            _remaining = CancellationInterval;
        }
    }

    private sealed class FormattingFrame(TextStyle style)
    {
        public TextStyle Style { get; } = style;
        public List<Piece> Pieces { get; } = [];
        public int TrimStart { get; set; } = -1;
        public int TrimEnd { get; set; } = -1;
    }

    internal static IReadOnlyList<TemplateInline> Parse(SourceText source, StorySymbols symbols, DiagnosticBag diagnostics,
        bool allowImages = true, CancellationToken cancellationToken = default) =>
        ParseWithWork(source, symbols, diagnostics, allowImages, cancellationToken).Inlines;

    internal static InlineParseResult ParseWithWork(SourceText source, StorySymbols symbols, DiagnosticBag diagnostics,
        bool allowImages = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new Context(source, symbols, diagnostics, allowImages, cancellationToken);
        var text = source.Text;
        var scan = DelimiterScanner.Scan(text, DelimiterScanMode.ReaderText, cancellationToken: cancellationToken);
        context.WorkUnits += scan.WorkUnits;
        var bracePairs = new List<DelimiterPair>();
        foreach (var pair in scan.Pairs)
        {
            context.Check();
            if (pair.Kind == DelimiterKind.Brace) bracePairs.Add(pair);
        }
        var tokens = Tokenize(context, text, bracePairs);
        PairCodeSpans(context, text, tokens);
        PairFormatting(context, text, tokens);
        var inlines = BuildTemplate(context, text, tokens);
        cancellationToken.ThrowIfCancellationRequested();
        return new(inlines, context.WorkUnits);
    }

    internal static void AnalyzePartial(SourceText source, StorySymbols symbols, CancellationToken cancellationToken)
    {
        _ = ParseWithWork(source, symbols, new DiagnosticBag(), cancellationToken: cancellationToken);
    }

    public static int FindClosingBrace(string text, int open)
    {
        var scan = DelimiterScanner.Scan(text, DelimiterScanMode.ReaderText, open);
        return scan.TryGetClose(open, out var close) ? close : -1;
    }

    private static List<Token> Tokenize(Context context, string text, IReadOnlyList<DelimiterPair> bracePairs)
    {
        var tokens = new List<Token>();
        var textBuffer = new StringBuilder();
        var textStart = -1;
        var textEnd = -1;
        var braceIndex = 0;

        void AppendText(char value, int sourceStart, int sourceLength)
        {
            if (textStart < 0) textStart = sourceStart;
            textBuffer.Append(value);
            textEnd = sourceStart + sourceLength;
        }

        void FlushText()
        {
            if (textBuffer.Length == 0) return;
            tokens.Add(new(TokenKind.Text, textStart, textEnd - textStart, textBuffer.ToString()));
            textBuffer.Clear();
            textStart = -1;
            textEnd = -1;
        }

        void AddToken(Token token)
        {
            FlushText();
            tokens.Add(token);
        }

        var nextCancellationCheck = 0;
        for (var index = 0; index < text.Length;)
        {
            if (index >= nextCancellationCheck)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                nextCancellationCheck = index + CancellationInterval;
            }

            var current = text[index];
            if (current == '\\' && index + 1 < text.Length)
            {
                var next = text[index + 1];
                if (next == '\\' || next is '{' or '}' or '[' or ']' or '*' or '_' or '`')
                {
                    AppendText(next, index, 2);
                    index += 2;
                    context.CheckCharacters(2);
                    continue;
                }

                if (next == '\n')
                {
                    AddToken(new(TokenKind.HardBreak, index, 2));
                    index += 2;
                    context.CheckCharacters(2);
                    continue;
                }

                AppendText(current, index, 1);
                index++;
                context.CheckCharacters(1);
                continue;
            }

            if (current == '{')
            {
                while (braceIndex < bracePairs.Count && bracePairs[braceIndex].OpenOffset < index)
                {
                    context.Check();
                    braceIndex++;
                }

                if (braceIndex < bracePairs.Count && bracePairs[braceIndex].OpenOffset == index)
                {
                    var close = bracePairs[braceIndex++].CloseOffset;
                    AddToken(new(TokenKind.Construct, index, close + 1 - index)
                    {
                        Piece = ParseConstruct(context, text, index, close),
                    });
                    index = close + 1;
                    while (braceIndex < bracePairs.Count && bracePairs[braceIndex].OpenOffset < index)
                    {
                        context.Check();
                        braceIndex++;
                    }

                    continue;
                }

                FlushText();
                ParseUnclosedConstruct(context, text, index);
                tokens.Add(new(TokenKind.Text, index, text.Length - index, text[index..]));
                return tokens;
            }

            if (current == '!' && TryReadLegacyImage(context, text, index,
                    out var imageEnd, out var alt, out var path))
            {
                AddToken(new(TokenKind.LegacyImage, index, imageEnd - index, text[index..imageEnd])
                {
                    LegacyAlt = alt,
                    LegacyPath = path,
                });
                context.CheckCharacters(imageEnd - index);
                index = imageEnd;
                continue;
            }

            if (current == '\n')
            {
                AddToken(new(TokenKind.SoftBreak, index, 1));
                index++;
                context.CheckCharacters(1);
                continue;
            }

            if (current is '*' or '_')
            {
                var end = index + 1;
                while (end < text.Length && text[end] == current)
                {
                    end++;
                    context.CheckCharacters(1);
                }
                var length = end - index;
                if (length <= 2)
                {
                    AddToken(new(TokenKind.FormatDelimiter, index, length)
                    {
                        RunLength = length,
                        Style = current == '*'
                            ? length == 1 ? TextStyle.Emphasis : TextStyle.Strong
                            : length == 1 ? TextStyle.Emphasis : TextStyle.Strong,
                        Text = current.ToString(),
                    });
                }
                else
                {
                    AddToken(new(TokenKind.Text, index, length, text[index..end]));
                }

                context.CheckCharacters(1);
                index = end;
                continue;
            }

            if (current == '`')
            {
                var end = index + 1;
                while (end < text.Length && text[end] == '`')
                {
                    end++;
                    context.CheckCharacters(1);
                }
                AddToken(new(TokenKind.CodeDelimiter, index, end - index)
                {
                    RunLength = end - index,
                });
                context.CheckCharacters(1);
                index = end;
                continue;
            }

            AppendText(current, index, 1);
            index++;
            context.CheckCharacters(1);
        }

        FlushText();
        return tokens;
    }

    private static void ParseUnclosedConstruct(Context context, string text, int open)
    {
        var start = open + 1;
        if (ImageSyntax.IsCall(text.AsSpan(start)))
        {
            if (!context.AllowImages)
            {
                context.Diagnostics.Error("TW1014",
                    "Image alt text cannot contain {image(...)}. Write the description as text.",
                    context.Source.Locate(open));
            }
            else
            {
                ImageSyntax.Parse(context.Source, start, text.Length, false, context.Diagnostics,
                    context.Symbols, context.CancellationToken);
            }

            return;
        }

        if (context.Symbols.Analysis is { } analysis)
        {
            var incomplete = text[start..];
            var lead = incomplete.Length - incomplete.TrimStart().Length;
            var condition = incomplete[lead..].StartsWith("if ", StringComparison.Ordinal);
            var expressionOffset = condition ? lead + 3 : 0;
            if (condition)
            {
                analysis.Tokens.Add(new("if",
                    new(context.Source.Locate(start + lead), context.Source.Locate(start + lead + 2))));
            }

            var expressionStart = start + expressionOffset;
            analysis.Expression(condition ? ExpressionContextKind.Condition : ExpressionContextKind.Substitution,
                incomplete[expressionOffset..], context.Source.Locate(expressionStart),
                offset => context.Source.Locate(expressionStart + offset));
        }

        ExpressionParser.ReportStringErrors(text[start..], offset => context.Source.Locate(start + offset),
            context.Diagnostics);
        context.Diagnostics.Error("TW1004", "Unclosed '{'. Use '\\{' for a literal brace.", context.Source.Locate(open));
    }

    private static Piece? ParseConstruct(Context context, string text, int open, int close)
    {
        var sourceOffset = open + 1;
        var inner = text[sourceOffset..close];
        var location = context.Source.Locate(sourceOffset);
        if (ImageSyntax.IsCall(inner))
        {
            if (!context.AllowImages)
            {
                context.Diagnostics.Error("TW1014",
                    "Image alt text cannot contain {image(...)}. Write the description as text.",
                    context.Source.Locate(open));
                return null;
            }

            var lead = inner.Length - inner.TrimStart().Length;
            context.Symbols.Analysis?.Tokens.Add(new("image",
                new(context.Source.Locate(sourceOffset + lead), context.Source.Locate(sourceOffset + lead + 5))));
            var media = ImageSyntax.Parse(context.Source, sourceOffset, close, true, context.Diagnostics,
                context.Symbols, context.CancellationToken);
            return media is null ? null : new InlinePiece(media);
        }

        var trimmed = inner.Trim();
        var leading = inner.Length - inner.TrimStart().Length;
        var keyword = trimmed.StartsWith("if ", StringComparison.Ordinal) ? "if" :
            trimmed is "else" or "/if" ? trimmed : null;
        if (keyword is not null)
        {
            context.Symbols.Analysis?.Tokens.Add(new(keyword,
                new(context.Source.Locate(sourceOffset + leading),
                    context.Source.Locate(sourceOffset + leading + keyword.Length))));
        }

        var fragment = ParseFragment(inner, location, context, sourceOffset);
        if (fragment is InlinePiece { Inline: SubstitutionTemplate substitution })
        {
            context.Symbols.Analysis?.Substitutions.Add(new(
                new(context.Source.Locate(open), context.Source.Locate(close + 1)), substitution));
        }

        return fragment;
    }

    private static Piece? ParseFragment(string inner, SourceLocation location, Context context, int sourceOffset)
    {
        var trimmed = inner.Trim();
        var lead = inner.Length - inner.TrimStart().Length;
        if (trimmed == "else") return new ElsePiece(location);
        if (trimmed == "/if") return new EndIfPiece(location);

        if (trimmed.StartsWith("if ", StringComparison.Ordinal))
        {
            var condition = ExpressionParser.ParseCondition(trimmed[3..], location.Offset(lead + 3),
                context.Symbols, context.Diagnostics,
                locate: offset => context.Source.Locate(sourceOffset + lead + 3 + offset));
            return condition is null
                ? null
                : new IfPiece(condition, location, context.Source.Locate(sourceOffset - 1));
        }

        var substitution = ExpressionParser.ParseSubstitution(inner, location, context.Symbols, context.Diagnostics,
            offset => context.Source.Locate(sourceOffset + offset));
        return substitution is null ? null : new InlinePiece(substitution);
    }

    private static bool TryReadLegacyImage(Context context, string text, int start,
        out int end, out string alt, out string path)
    {
        end = start;
        alt = "";
        path = "";
        if (start + 1 >= text.Length || text[start] != '!' || text[start + 1] != '[') return false;

        var altStart = start + 2;
        var cursor = altStart;
        while (cursor < text.Length && text[cursor] != ']')
        {
            context.Check();
            var c = text[cursor];
            if (c is '\\' or '[' or '{' or '}' or '\r' or '\n') return false;
            cursor++;
        }

        if (cursor >= text.Length || cursor + 1 >= text.Length || text[cursor + 1] != '(') return false;
        var altEnd = cursor;
        var pathStart = cursor + 2;
        cursor = pathStart;
        while (cursor < text.Length && text[cursor] != ')')
        {
            context.Check();
            var c = text[cursor];
            if (char.IsWhiteSpace(c) || c is '(' or '\\' or '"' or '{' or '}' or '\r' or '\n') return false;
            cursor++;
        }

        if (cursor >= text.Length || cursor == pathStart) return false;
        alt = text[altStart..altEnd];
        path = text[pathStart..cursor];
        end = cursor + 1;
        return true;
    }

    private static void PairCodeSpans(Context context, string text, List<Token> tokens)
    {
        var nextOfLength = new Dictionary<int, int>();
        for (var index = tokens.Count - 1; index >= 0; index--)
        {
            context.Check();
            var token = tokens[index];
            if (token.Kind != TokenKind.CodeDelimiter) continue;
            if (nextOfLength.TryGetValue(token.RunLength, out var next))
                token.NextRun = next;
            nextOfLength[token.RunLength] = index;
        }

        var activeClose = -1;
        for (var index = 0; index < tokens.Count; index++)
        {
            context.Check();
            var token = tokens[index];
            if (activeClose >= 0)
            {
                token.InCode = true;
                if (index == activeClose)
                {
                    token.Kind = TokenKind.CodeClose;
                    activeClose = -1;
                }
                else if (token.Kind == TokenKind.CodeDelimiter)
                {
                    token.Kind = TokenKind.Text;
                    token.Text = text.Substring(token.Start, token.Length);
                }

                continue;
            }

            if (token.Kind != TokenKind.CodeDelimiter || token.NextRun < 0) continue;
            var close = tokens[token.NextRun];
            token.Kind = TokenKind.CodeOpen;
            token.Mate = token.NextRun;
            close.Kind = TokenKind.CodeClose;
            close.Mate = index;
            activeClose = token.NextRun;

            var contentStart = token.End;
            var contentLength = close.Start - contentStart;
            if (contentLength > 1 && text[contentStart] == ' ' && text[close.Start - 1] == ' ' &&
                text.AsSpan(contentStart + 1, contentLength - 2).ContainsAnyExcept(' '))
            {
                token.TrimOffset = contentStart;
                close.TrimOffset = close.Start - 1;
            }
        }
    }

    private static void PairFormatting(Context context, string text, List<Token> tokens)
    {
        var openers = new List<(int Token, int PreviousOfKind)>();
        Span<int> topOfKind = [-1, -1, -1, -1];

        for (var index = 0; index < tokens.Count; index++)
        {
            context.Check();
            var token = tokens[index];
            if (token.Kind != TokenKind.FormatDelimiter || token.InCode) continue;
            var delimiter = text[token.Start];
            var kind = (delimiter == '_' ? 2 : 0) + token.RunLength - 1;
            token.CanOpen = !IsWordLikeBefore(tokens, index) && IsWordLikeAfter(tokens, index);
            token.CanClose = IsWordLikeBefore(tokens, index) && !IsWordLikeAfter(tokens, index);

            if (token.CanClose && topOfKind[kind] >= 0)
            {
                var matching = topOfKind[kind];
                if (matching != openers.Count - 1) continue;
                var opener = openers[^1];
                openers.RemoveAt(openers.Count - 1);
                topOfKind[kind] = opener.PreviousOfKind;
                tokens[opener.Token].Kind = TokenKind.FormatOpen;
                tokens[opener.Token].Mate = index;
                token.Kind = TokenKind.FormatClose;
                token.Mate = opener.Token;
                continue;
            }

            if (!token.CanOpen) continue;
            openers.Add((index, topOfKind[kind]));
            topOfKind[kind] = openers.Count - 1;
        }

        var depth = 0;
        var depthReported = false;
        foreach (var token in tokens)
        {
            context.Check();
            if (token.Kind == TokenKind.FormatClose)
            {
                if (depth > MaximumFormattingDepth) token.Kind = TokenKind.FormatDelimiter;
                depth--;
            }
            else if (token.Kind == TokenKind.FormatOpen)
            {
                depth++;
                if (depth <= MaximumFormattingDepth) continue;
                token.Kind = TokenKind.FormatDelimiter;
                if (depthReported) continue;

                context.Diagnostics.Error("TW1016",
                    $"Formatting spans are nested more than {MaximumFormattingDepth} levels deep.",
                    context.Source.Locate(token.Start));
                depthReported = true;
            }
        }
    }

    private static bool IsWordLikeBefore(IReadOnlyList<Token> tokens, int index)
    {
        if (index == 0) return false;
        var previous = tokens[index - 1];
        return previous.Kind switch
        {
            TokenKind.Text => EndsWithWordCharacter(previous.Text!),
            TokenKind.Construct => previous.Piece is InlinePiece,
            TokenKind.LegacyImage or TokenKind.CodeOpen or TokenKind.CodeClose => true,
            _ => false,
        };
    }

    private static bool IsWordLikeAfter(IReadOnlyList<Token> tokens, int index)
    {
        if (index + 1 >= tokens.Count) return false;
        var next = tokens[index + 1];
        return next.Kind switch
        {
            TokenKind.Text => StartsWithWordCharacter(next.Text!),
            TokenKind.Construct => next.Piece is InlinePiece or IfPiece,
            TokenKind.LegacyImage or TokenKind.CodeOpen or TokenKind.CodeClose => true,
            _ => false,
        };
    }

    private static bool StartsWithWordCharacter(string text)
    {
        if (text.Length == 0) return false;
        if (text.Length > 1 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1]) &&
            Rune.TryCreate(text[0], text[1], out var rune))
            return Rune.IsLetterOrDigit(rune);
        return Rune.TryCreate(text[0], out var first) && Rune.IsLetterOrDigit(first);
    }

    private static bool EndsWithWordCharacter(string text)
    {
        if (text.Length == 0) return false;
        var last = text[^1];
        if (char.IsLowSurrogate(last) && text.Length > 1 && char.IsHighSurrogate(text[^2]) &&
            Rune.TryCreate(text[^2], last, out var rune))
            return Rune.IsLetterOrDigit(rune);
        return Rune.TryCreate(last, out var final) && Rune.IsLetterOrDigit(final);
    }

    private static IReadOnlyList<TemplateInline> BuildTemplate(Context context, string text, List<Token> tokens)
    {
        var root = new List<Piece>();
        var frames = new Stack<FormattingFrame>();

        List<Piece> Current() => frames.TryPeek(out var frame) ? frame.Pieces : root;

        void CloseFrame()
        {
            var frame = frames.Pop();
            var children = Fold(frame.Pieces, context);
            Current().Add(new InlinePiece(new StyledTemplate(frame.Style, children)));
        }

        foreach (var token in tokens)
        {
            context.Check();
            switch (token.Kind)
            {
                case TokenKind.Text:
                    AddText(token, Current(), frames);
                    break;
                case TokenKind.SoftBreak:
                    Current().Add(new InlinePiece(new TextTemplate(" ")));
                    break;
                case TokenKind.HardBreak:
                    Current().Add(new InlinePiece(frames.TryPeek(out var frame) && frame.Style == TextStyle.Code
                        ? new TextTemplate(" ")
                        : new LineBreakTemplate()));
                    break;
                case TokenKind.Construct:
                    if (token.Piece is { } construct) Current().Add(construct);
                    break;
                case TokenKind.LegacyImage:
                    if (!frames.TryPeek(out var activeFrame) || activeFrame.Style != TextStyle.Code)
                    {
                        var replacement =
                            $"{{image({ImageSyntax.Quote(token.LegacyPath!)}, {ImageSyntax.Quote(token.LegacyAlt!)})}}";
                        context.Diagnostics.Error("TW1015",
                            $"Markdown images are no longer supported. Use {replacement} instead.",
                            context.Source.Locate(token.Start));
                    }
                    Current().Add(new InlinePiece(new TextTemplate(token.Text!)));
                    break;
                case TokenKind.FormatOpen:
                    frames.Push(new FormattingFrame(token.Style));
                    break;
                case TokenKind.CodeOpen:
                    frames.Push(new FormattingFrame(TextStyle.Code)
                    {
                        TrimStart = token.TrimOffset,
                        TrimEnd = tokens[token.Mate].TrimOffset,
                    });
                    break;
                case TokenKind.FormatClose:
                case TokenKind.CodeClose:
                    CloseFrame();
                    break;
                case TokenKind.FormatDelimiter:
                    Current().Add(new InlinePiece(new TextTemplate(text.Substring(token.Start, token.Length))));
                    break;
                case TokenKind.CodeDelimiter:
                    Current().Add(new InlinePiece(new TextTemplate(text.Substring(token.Start, token.Length))));
                    break;
            }
        }

        return Fold(root, context);
    }

    private static void AddText(Token token, List<Piece> pieces, Stack<FormattingFrame> frames)
    {
        var value = token.Text!;
        if (frames.TryPeek(out var frame) && frame.Style == TextStyle.Code)
        {
            if (value.Length > 0 && token.Start == frame.TrimStart && value[0] == ' ') value = value[1..];
            if (value.Length > 0 && token.End - 1 == frame.TrimEnd && value[^1] == ' ') value = value[..^1];
        }

        if (value.Length > 0) pieces.Add(new InlinePiece(new TextTemplate(value)));
    }

    private static IReadOnlyList<TemplateInline> Fold(List<Piece> pieces, Context context)
    {
        var output = new List<TemplateInline>();
        var frames = new Stack<InlineConditionalFrame>();
        var suppressedDepth = 0;
        SourceLocation suppressedLocation = SourceLocation.None;

        void Add(TemplateInline inline)
        {
            if (frames.TryPeek(out var frame))
                (frame.InElse ? frame.Else : frame.Then).Add(inline);
            else
                output.Add(inline);
        }

        void Close(InlineConditionalFrame frame) =>
            Add(new ConditionalInlineTemplate(frame.Open.Condition, frame.Then, frame.Else));

        foreach (var piece in pieces)
        {
            context.Check();
            switch (piece)
            {
                case InlinePiece inline:
                    if (suppressedDepth == 0) Add(inline.Inline);
                    break;
                case IfPiece open:
                    if (suppressedDepth > 0)
                    {
                        suppressedDepth++;
                        break;
                    }

                    if (frames.Count == MaximumInlineConditionDepth)
                    {
                        context.Diagnostics.Error("TW1016",
                            $"Conditions are nested more than {MaximumInlineConditionDepth} levels deep.", open.StartLocation);
                        suppressedDepth = 1;
                        suppressedLocation = open.Location;
                        break;
                    }

                    frames.Push(new InlineConditionalFrame(open));
                    break;
                case ElsePiece alternative when suppressedDepth == 0:
                    if (!frames.TryPeek(out var frame))
                    {
                        context.Diagnostics.Error("TW1005", "{else} without an {if} in the same paragraph and formatting.", alternative.Location);
                    }
                    else if (frame.InElse)
                    {
                        context.Diagnostics.Error("TW1005", "A second {else} in the same {if}.", alternative.Location);
                        frames.Pop();
                        Close(frame);
                    }
                    else
                    {
                        frame.InElse = true;
                    }
                    break;
                case EndIfPiece end:
                    if (suppressedDepth > 0)
                    {
                        suppressedDepth--;
                    }
                    else if (frames.TryPop(out var closed))
                    {
                        Close(closed);
                    }
                    else
                    {
                        context.Diagnostics.Error("TW1005", "{/if} without an {if} in the same paragraph and formatting.", end.Location);
                    }
                    break;
            }
        }

        if (suppressedDepth > 0)
        {
            context.Diagnostics.Error("TW1005",
                "{if} has no matching {/if} here. Inline conditionals must open and close inside the same paragraph and the same *emphasis*.",
                suppressedLocation);
        }
        while (frames.TryPop(out var unclosed))
        {
            context.Diagnostics.Error("TW1005",
                "{if} has no matching {/if} here. Inline conditionals must open and close inside the same paragraph and the same *emphasis*.",
                unclosed.Open.Location);
        }

        return output;
    }

    private sealed class InlineConditionalFrame(IfPiece open)
    {
        public IfPiece Open { get; } = open;
        public List<TemplateInline> Then { get; } = [];
        public List<TemplateInline> Else { get; } = [];
        public bool InElse { get; set; }
    }
}
