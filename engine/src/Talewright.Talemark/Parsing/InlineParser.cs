using System.Text;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

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
        LineBreak,
        Construct,
        FormatDelimiter,
        FormatOpen,
        FormatClose,
    }

    private sealed class Token(TokenKind kind, int start, int length, string? text = null)
    {
        public TokenKind Kind { get; set; } = kind;
        public int Start { get; } = start;
        public int Length { get; } = length;
        public int End => Start + Length;
        public string? Text { get; set; } = text;
        public Piece? Piece { get; set; }
        public int RunLength { get; set; }
        public TextStyle Style { get; set; }
        public bool CanOpen { get; set; }
        public bool CanClose { get; set; }
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
        PairFormatting(context, text, tokens);
        var inlines = BuildTemplate(context, text, tokens);
        cancellationToken.ThrowIfCancellationRequested();
        return new(inlines, context.WorkUnits);
    }

    internal static void AnalyzePartial(SourceText source, StorySymbols symbols, CancellationToken cancellationToken)
    {
        _ = ParseWithWork(source, symbols, new DiagnosticBag(), cancellationToken: cancellationToken);
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
            if (current == TextVocabulary.EscapeMark)
            {
                if (index + 1 < text.Length)
                {
                    var next = text[index + 1];
                    if (DelimiterScanner.IsReaderTextEscape(next))
                    {
                        AppendText(next, index, 2);
                        index += 2;
                        context.CheckCharacters(2);
                        continue;
                    }

                    if (next == '\n')
                    {
                        ReportTrailingBackslash(context, index);
                    }
                }
                else
                {
                    ReportTrailingBackslash(context, index);
                }

                AppendText(current, index, 1);
                index++;
                context.CheckCharacters(1);
                continue;
            }

            if (current == SceneVocabulary.InlineOpen)
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

            if (current == SceneVocabulary.LegacyImageMarker &&
                TryReadLegacyImage(context, text, index, out var imageEnd))
            {
                context.Diagnostics.Error(CompilerDiagnosticCodes.LegacyImageSyntax, "Unexpected syntax in reader text.",
                    context.Source.Locate(index));
                AddToken(new(TokenKind.Construct, index, imageEnd - index)
                {
                    Piece = new InlinePiece(new TextTemplate(text[index..imageEnd])),
                });
                context.CheckCharacters(imageEnd - index);
                index = imageEnd;
                continue;
            }

            if (current == '\n')
            {
                AddToken(new(TokenKind.LineBreak, index, 1));
                index++;
                context.CheckCharacters(1);
                continue;
            }

            if (current is SceneVocabulary.ChoiceMarker or SceneVocabulary.EmphasisMarker)
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
                        Style = current == SceneVocabulary.ChoiceMarker
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
                context.Diagnostics.Error(CompilerDiagnosticCodes.InvalidImageCall,
                    $"Image alt text cannot contain {SceneVocabulary.InlineOpen}{ExpressionVocabulary.ImageCall}..." +
                    $"{ExpressionVocabulary.CloseParen}{SceneVocabulary.InlineClose}. Write the description as text.",
                    context.Source.Locate(open));
            }

            else
            {
                ImageSyntax.Parse(context.Source, start, text.Length, false, context.Diagnostics,
                    context.Symbols, context.CancellationToken);
            }

            return;
        }

        TokenBuffer? expressionTokens = null;
        var errorStart = start;
        if (context.Symbols.Analysis is { } analysis)
        {
            var incomplete = text[start..];
            var lead = incomplete.Length - incomplete.TrimStart().Length;
            var condition = incomplete[lead..].StartsWith(SceneVocabulary.IfPrefix, StringComparison.Ordinal);
            var expressionOffset = condition ? lead + SceneVocabulary.IfPrefix.Length : 0;
            if (condition)
            {
                analysis.Tokens.Add(new(SceneVocabulary.If,
                    new(context.Source.Locate(start + lead),
                        context.Source.Locate(start + lead + SceneVocabulary.If.Length)))
                    { Kind = AuthoringTokenKind.If });
            }

            var expressionStart = start + expressionOffset;
            var expressionSource = context.Source;
            expressionTokens = analysis.Expression(condition ? ExpressionContextKind.Condition : ExpressionContextKind.Substitution,
                incomplete[expressionOffset..], expressionSource.Locate(expressionStart),
                offset => expressionSource.Locate(expressionStart + offset));
            errorStart = expressionStart;
        }

        if (expressionTokens is null)
            ExpressionParser.ReportStringErrors(text[start..], offset => context.Source.Locate(start + offset),
                context.Diagnostics);
        else
            ExpressionParser.ReportStringErrors(expressionTokens, offset => context.Source.Locate(errorStart + offset),
                context.Diagnostics);
        context.Diagnostics.Error(CompilerDiagnosticCodes.UnclosedInlineConstruct,
            $"Unclosed '{SceneVocabulary.InlineOpen}'. Use " +
            $"'{TextVocabulary.EscapeMark}{SceneVocabulary.InlineOpen}' for a literal brace.",
            context.Source.Locate(open));
    }

    private static void ReportTrailingBackslash(Context context, int offset) =>
        context.Diagnostics.Error(CompilerDiagnosticCodes.TrailingReaderTextBackslash,
            $"Line breaks now render as written; remove the trailing '{TextVocabulary.EscapeMark}'.",
            context.Source.Locate(offset));

    private static Piece? ParseConstruct(Context context, string text, int open, int close)
    {
        var sourceOffset = open + 1;
        var inner = text[sourceOffset..close];
        var location = context.Source.Locate(sourceOffset);
        if (ImageSyntax.IsCall(inner))
        {
            if (!context.AllowImages)
            {
                context.Diagnostics.Error(CompilerDiagnosticCodes.InvalidImageCall,
                    $"Image alt text cannot contain {SceneVocabulary.InlineOpen}{ExpressionVocabulary.ImageCall}..." +
                    $"{ExpressionVocabulary.CloseParen}{SceneVocabulary.InlineClose}. Write the description as text.",
                    context.Source.Locate(open));
                return null;
            }

            var lead = inner.Length - inner.TrimStart().Length;
            context.Symbols.Analysis?.Tokens.Add(new(ExpressionVocabulary.Image,
                new(context.Source.Locate(sourceOffset + lead),
                    context.Source.Locate(sourceOffset + lead + ExpressionVocabulary.Image.Length)))
                { Kind = AuthoringTokenKind.Image });
            var media = ImageSyntax.Parse(context.Source, sourceOffset, close, true, context.Diagnostics,
                context.Symbols, context.CancellationToken);
            return media is null ? null : new InlinePiece(media);
        }

        var trimmed = inner.Trim();
        var leading = inner.Length - inner.TrimStart().Length;
        var keyword = trimmed.StartsWith(SceneVocabulary.IfPrefix, StringComparison.Ordinal) ? SceneVocabulary.If :
            trimmed is SceneVocabulary.Else or SceneVocabulary.EndIf ? trimmed : null;
        if (keyword is not null)
        {
            context.Symbols.Analysis?.Tokens.Add(new(keyword,
                new(context.Source.Locate(sourceOffset + leading),
                    context.Source.Locate(sourceOffset + leading + keyword.Length)))
                { Kind = AuthoringSyntaxVocabulary.ConditionalKind(keyword) });
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
        // Analysis retains these mappings for on-request completion; capture only the source text.
        var source = context.Source;
        if (trimmed == SceneVocabulary.Else) return new ElsePiece(location);
        if (trimmed == SceneVocabulary.EndIf) return new EndIfPiece(location);

        if (trimmed.StartsWith(SceneVocabulary.IfPrefix, StringComparison.Ordinal))
        {
            var condition = ExpressionParser.ParseCondition(trimmed[SceneVocabulary.IfPrefix.Length..],
                location.Offset(lead + SceneVocabulary.IfPrefix.Length),
                context.Symbols, context.Diagnostics,
                locate: offset => source.Locate(sourceOffset + lead + SceneVocabulary.IfPrefix.Length + offset));
            return condition is null
                ? null
                : new IfPiece(condition, location, context.Source.Locate(sourceOffset - 1));
        }

        var substitution = ExpressionParser.ParseSubstitution(inner, location, context.Symbols, context.Diagnostics,
            offset => source.Locate(sourceOffset + offset));
        return substitution is null ? null : new InlinePiece(substitution);
    }

    private static bool TryReadLegacyImage(Context context, string text, int start, out int end)
    {
        end = start;
        if (start + 1 >= text.Length || text[start] != SceneVocabulary.LegacyImageMarker ||
            text[start + 1] != SceneVocabulary.OpenBracket) return false;

        var cursor = start + 2;
        while (cursor < text.Length && text[cursor] != SceneVocabulary.CloseBracket)
        {
            context.Check();
            var c = text[cursor];
            if (c is SceneVocabulary.LegacyImageEscape or SceneVocabulary.OpenBracket or SceneVocabulary.InlineOpen or
                SceneVocabulary.InlineClose or '\r' or '\n')
                return false;
            cursor++;
        }

        if (cursor >= text.Length || cursor + 1 >= text.Length ||
            text[cursor + 1] != ExpressionVocabulary.OpenParen) return false;
        var pathStart = cursor + 2;
        cursor = pathStart;
        while (cursor < text.Length && text[cursor] != ExpressionVocabulary.CloseParen)
        {
            context.Check();
            var c = text[cursor];
            if (char.IsWhiteSpace(c) ||
                c is ExpressionVocabulary.OpenParen or SceneVocabulary.LegacyImageEscape or
                    SceneVocabulary.LegacyImageQuote or SceneVocabulary.InlineOpen or SceneVocabulary.InlineClose or
                    '\r' or '\n')
                return false;
            cursor++;
        }

        if (cursor >= text.Length || cursor == pathStart) return false;
        end = cursor + 1;
        return true;
    }

    private static void PairFormatting(Context context, string text, List<Token> tokens)
    {
        var openers = new List<(int Token, int PreviousOfKind)>();
        Span<int> topOfKind = [-1, -1, -1, -1];

        for (var index = 0; index < tokens.Count; index++)
        {
            context.Check();
            var token = tokens[index];
            if (token.Kind != TokenKind.FormatDelimiter) continue;
            var delimiter = text[token.Start];
            var kind = (delimiter == SceneVocabulary.EmphasisMarker ? 2 : 0) + token.RunLength - 1;
            token.CanOpen = !IsWordLikeBefore(tokens, index) && IsNonWhitespaceNonBoundaryAfter(tokens, index);
            token.CanClose = IsNonWhitespaceNonBoundaryBefore(tokens, index) && !IsWordLikeAfter(tokens, index);

            if (token.CanClose && topOfKind[kind] >= 0)
            {
                var matching = topOfKind[kind];
                if (matching != openers.Count - 1) continue;
                var opener = openers[^1];
                openers.RemoveAt(openers.Count - 1);
                topOfKind[kind] = opener.PreviousOfKind;
                tokens[opener.Token].Kind = TokenKind.FormatOpen;
                token.Kind = TokenKind.FormatClose;
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

                context.Diagnostics.Error(CompilerDiagnosticCodes.NestingLimitExceeded,
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
            _ => false,
        };
    }

    private static bool IsNonWhitespaceNonBoundaryBefore(IReadOnlyList<Token> tokens, int index)
    {
        if (index == 0) return false;
        var previous = tokens[index - 1];
        return previous.Kind switch
        {
            TokenKind.Text => EndsWithNonWhitespaceCharacter(previous.Text!),
            TokenKind.Construct => previous.Piece is InlinePiece,
            TokenKind.FormatDelimiter or TokenKind.FormatOpen or TokenKind.FormatClose => true,
            _ => false,
        };
    }

    private static bool IsNonWhitespaceNonBoundaryAfter(IReadOnlyList<Token> tokens, int index)
    {
        if (index + 1 >= tokens.Count) return false;
        var next = tokens[index + 1];
        return next.Kind switch
        {
            TokenKind.Text => StartsWithNonWhitespaceCharacter(next.Text!),
            TokenKind.Construct => next.Piece is InlinePiece or IfPiece,
            TokenKind.FormatDelimiter or TokenKind.FormatOpen or TokenKind.FormatClose => true,
            _ => false,
        };
    }

    private static bool StartsWithNonWhitespaceCharacter(string text)
    {
        if (text.Length == 0) return false;
        if (text.Length > 1 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1]) &&
            Rune.TryCreate(text[0], text[1], out var rune))
            return !Rune.IsWhiteSpace(rune);
        return !char.IsWhiteSpace(text[0]);
    }

    private static bool EndsWithNonWhitespaceCharacter(string text)
    {
        if (text.Length == 0) return false;
        var last = text[^1];
        if (char.IsLowSurrogate(last) && text.Length > 1 && char.IsHighSurrogate(text[^2]) &&
            Rune.TryCreate(text[^2], last, out var rune))
            return !Rune.IsWhiteSpace(rune);
        return !char.IsWhiteSpace(last);
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
                    AddText(token, Current());
                    break;
                case TokenKind.LineBreak:
                    Current().Add(new InlinePiece(new LineBreakTemplate()));
                    break;
                case TokenKind.Construct:
                    if (token.Piece is { } construct) Current().Add(construct);
                    break;
                case TokenKind.FormatOpen:
                    frames.Push(new FormattingFrame(token.Style));
                    break;
                case TokenKind.FormatClose:
                    CloseFrame();
                    break;
                case TokenKind.FormatDelimiter:
                    Current().Add(new InlinePiece(new TextTemplate(text.Substring(token.Start, token.Length))));
                    break;
            }
        }

        return Fold(root, context);
    }

    private static void AddText(Token token, List<Piece> pieces)
    {
        var value = token.Text!;
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
                        context.Diagnostics.Error(CompilerDiagnosticCodes.NestingLimitExceeded,
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
                        context.Diagnostics.Error(CompilerDiagnosticCodes.UnbalancedCondition,
                            $"{SceneVocabulary.InlineOpen}{SceneVocabulary.Else}{SceneVocabulary.InlineClose} without an " +
                            $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose} in the same " +
                            "paragraph and formatting.", alternative.Location);
                    }
                    else if (frame.InElse)
                    {
                        context.Diagnostics.Error(CompilerDiagnosticCodes.UnbalancedCondition,
                            $"A second {SceneVocabulary.InlineOpen}{SceneVocabulary.Else}{SceneVocabulary.InlineClose} " +
                            $"in the same {SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose}.",
                            alternative.Location);
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
                        context.Diagnostics.Error(CompilerDiagnosticCodes.UnbalancedCondition,
                            $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose} without an " +
                            $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose} in the same " +
                            "paragraph and formatting.", end.Location);
                    }
                    break;
            }
        }

        if (suppressedDepth > 0)
        {
            context.Diagnostics.Error(CompilerDiagnosticCodes.UnbalancedCondition,
                $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose} has no matching " +
                $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose} here. Inline " +
                $"conditionals must open and close inside the same paragraph and the same " +
                $"{SceneVocabulary.ChoiceMarker}emphasis{SceneVocabulary.ChoiceMarker}.",
                suppressedLocation);
        }
        while (frames.TryPop(out var unclosed))
        {
            context.Diagnostics.Error(CompilerDiagnosticCodes.UnbalancedCondition,
                $"{SceneVocabulary.InlineOpen}{SceneVocabulary.If}{SceneVocabulary.InlineClose} has no matching " +
                $"{SceneVocabulary.InlineOpen}{SceneVocabulary.EndIf}{SceneVocabulary.InlineClose} here. Inline " +
                $"conditionals must open and close inside the same paragraph and the same " +
                $"{SceneVocabulary.ChoiceMarker}emphasis{SceneVocabulary.ChoiceMarker}.",
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
