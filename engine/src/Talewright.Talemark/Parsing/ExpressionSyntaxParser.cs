using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Parsing;

internal sealed class ExpressionSyntaxParser
{
    internal const int MaximumNestingDepth = 256;

    private readonly TokenBuffer _tokens;
    private readonly int _endIndex;
    private readonly int _endOffset;
    private readonly Func<int, SourceLocation> _locate;
    private readonly DiagnosticBag _diagnostics;
    private int _position;
    private int _nestingDepth;
    private int _failureOffset;
    private bool _hasErrors;

    private ExpressionSyntaxParser(TokenBuffer tokens, int endOffset, Func<int, SourceLocation> locate,
        DiagnosticBag diagnostics, int? endIndex = null)
    {
        _tokens = tokens;
        _endIndex = endIndex ?? tokens.Count - 1;
        _endOffset = endOffset;
        _locate = locate;
        _diagnostics = diagnostics;
    }

    private sealed class ParseException : Exception;

    internal static ExpressionSyntaxTree ParseExpression(TokenBuffer tokens, int endOffset,
        Func<int, SourceLocation> locate, DiagnosticBag diagnostics, bool sumOnly = false)
    {
        var parser = new ExpressionSyntaxParser(tokens, endOffset, locate, diagnostics,
            FindEndIndex(tokens, endOffset));
        try
        {
            var root = sumOnly ? parser.ParseSum() : parser.ParseOr();
            parser.ExpectEnd();
            return new(tokens, root, parser._hasErrors);
        }
        catch (ParseException)
        {
            return new(tokens, new ErrorExpressionNode(new(parser._failureOffset, 0)), true);
        }
    }

    internal static SubstitutionSyntaxTree ParseSubstitution(TokenBuffer tokens, int expressionLength,
        Func<int, SourceLocation> locate, DiagnosticBag diagnostics)
    {
        var hashIndex = FindHash(tokens, expressionLength);
        var endOffset = hashIndex < 0 ? expressionLength : tokens[hashIndex].Offset;
        var endIndex = hashIndex < 0 ? FindEndIndex(tokens, expressionLength) : hashIndex;
        var parser = new ExpressionSyntaxParser(tokens, endOffset, locate, diagnostics, endIndex);
        ExpressionNode value;
        try
        {
            value = parser.ParseOr();
            parser.ExpectEnd();
        }
        catch (ParseException)
        {
            value = new ErrorExpressionNode(new(parser._failureOffset, 0));
            return new(tokens, value, ImmutableArray<ModifierCallSyntax>.Empty, true);
        }

        if (hashIndex < 0) return new(tokens, value, ImmutableArray<ModifierCallSyntax>.Empty, parser._hasErrors);
        try
        {
            var modifiers = parser.ParseModifiers(endOffset, expressionLength);
            return new(tokens, value, modifiers, parser._hasErrors);
        }
        catch (ParseException)
        {
            return new(tokens, value, ImmutableArray<ModifierCallSyntax>.Empty, true);
        }
    }

    internal static EffectsSyntaxTree ParseEffects(TokenBuffer tokens, int expressionLength,
        Func<int, SourceLocation> locate, DiagnosticBag diagnostics)
    {
        var parser = new ExpressionSyntaxParser(tokens, expressionLength, locate, diagnostics,
            FindEndIndex(tokens, expressionLength));
        if (parser.ReportStringErrors())
        {
            return new(tokens,
                ImmutableArray.Create<EffectSyntax>(new ErrorEffectSyntax(new(0, tokens.Source.Length))), true);
        }

        try
        {
            var effects = ImmutableArray.CreateBuilder<EffectSyntax>();
            while (parser.Peek.Kind != TokenKind.End)
            {
                effects.Add(parser.ParseEffect());
                if (parser.Peek.Kind == TokenKind.Semicolon)
                {
                    parser._position++;
                }
                else
                {
                    parser.ExpectEnd();
                }
            }

            return new(tokens, effects.ToImmutable(), parser._hasErrors);
        }
        catch (ParseException)
        {
            return new(tokens,
                ImmutableArray.Create<EffectSyntax>(new ErrorEffectSyntax(new(parser._failureOffset, 0))), true);
        }
    }

    private bool ReportStringErrors()
    {
        var invalid = false;
        foreach (var token in _tokens)
        {
            if (token.ErrorOffset is not { } offset) continue;
            _diagnostics.Error("TW1001", token.Value, _locate(offset));
            invalid = true;
        }

        _hasErrors |= invalid;
        return invalid;
    }

    private Token Peek
    {
        get
        {
            var token = TokenAt(_position);
            if (token.ErrorOffset is { } offset) Fail("TW1001", token.Value, offset);
            return token;
        }
    }

    private Token TokenAt(int index) => index >= _endIndex
        ? new Token(TokenKind.End, _tokens.Source, _endOffset, 0)
        : _tokens[index];

    private Token Next()
    {
        var token = Peek;
        _position++;
        return token;
    }

    private bool IsKeyword(string keyword) => Peek.Kind == TokenKind.Identifier && Peek.Is(keyword);

    private void ExpectEnd()
    {
        if (Peek.Kind != TokenKind.End)
        {
            Fail("TW1001", $"Unexpected '{Peek.Text}'.", Peek.Offset);
        }
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (Peek.Kind != kind)
        {
            var found = Peek.Kind == TokenKind.End ? "end of expression" : Peek.Value;
            Fail("TW1001", $"Expected {what} but found '{found}'.", Peek.Offset);
        }

        return Next();
    }

    [DoesNotReturn]
    private void Fail(string code, string message, int offset)
    {
        _hasErrors = true;
        _failureOffset = offset;
        _diagnostics.Error(code, message, _locate(offset));
        throw new ParseException();
    }

    private T Fail<T>(string code, string message, int offset)
    {
        Fail(code, message, offset);
        return default!;
    }

    private ExpressionNode ParseNested(Token opener, Func<ExpressionNode> parse)
    {
        if (_nestingDepth == MaximumNestingDepth)
        {
            Fail("TW1016", $"Expression is nested more than {MaximumNestingDepth} levels deep.", opener.Offset);
        }

        _nestingDepth++;
        try
        {
            return parse();
        }
        finally
        {
            _nestingDepth--;
        }
    }

    private ExpressionNode ParseOr()
    {
        var left = ParseAnd();
        while (IsKeyword("or"))
        {
            var op = Next();
            left = Binary(op, left, ParseAnd());
        }

        return left;
    }

    private ExpressionNode ParseAnd()
    {
        var left = ParseNot();
        while (IsKeyword("and"))
        {
            var op = Next();
            left = Binary(op, left, ParseNot());
        }

        return left;
    }

    private ExpressionNode ParseNot()
    {
        if (IsKeyword("not"))
        {
            var op = Next();
            var operand = ParseNested(op, ParseNot);
            return new UnaryExpressionNode(TokenKind.Identifier, operand, Span(op),
                new(op.Offset, operand.Span.End - op.Offset));
        }

        return ParseComparison();
    }

    private ExpressionNode ParseComparison()
    {
        var left = ParseSum();
        if (Peek.Kind is not (TokenKind.Equal or TokenKind.NotEqual or TokenKind.Less or TokenKind.LessOrEqual or
            TokenKind.Greater or TokenKind.GreaterOrEqual))
        {
            return left;
        }

        var op = Next();
        return Binary(op, left, ParseSum());
    }

    private ExpressionNode ParseSum()
    {
        var left = ParseTerm();
        while (Peek.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = Next();
            left = Binary(op, left, ParseTerm());
        }

        return left;
    }

    private ExpressionNode ParseTerm()
    {
        var left = ParseUnary();
        while (Peek.Kind is TokenKind.Star or TokenKind.Slash)
        {
            var op = Next();
            left = Binary(op, left, ParseUnary());
        }

        return left;
    }

    private ExpressionNode ParseUnary()
    {
        if (Peek.Kind == TokenKind.Minus)
        {
            var op = Next();
            if (TryReadMinimumMagnitude())
            {
                return new NumberExpressionNode(int.MinValue,
                    new(op.Offset, TokenAt(_position - 1).Offset + TokenAt(_position - 1).Length - op.Offset));
            }

            var operand = ParseNested(op, ParseUnary);
            return new UnaryExpressionNode(TokenKind.Minus, operand, Span(op),
                new(op.Offset, operand.Span.End - op.Offset));
        }

        return ParsePrimary();
    }

    private bool TryReadMinimumMagnitude()
    {
        var position = _position;
        var parentheses = 0;
        while (TokenAt(position).Kind == TokenKind.LeftParen)
        {
            parentheses++;
            if (parentheses + _nestingDepth + 1 > MaximumNestingDepth)
            {
                return false;
            }

            position++;
        }

        if (TokenAt(position) is not { Kind: TokenKind.Number } number ||
            !long.TryParse(number.Text, out var magnitude) || magnitude != -(long)int.MinValue)
        {
            return false;
        }

        position++;
        for (var index = 0; index < parentheses; index++, position++)
        {
            if (TokenAt(position).Kind != TokenKind.RightParen)
            {
                return false;
            }
        }

        _position = position;
        return true;
    }

    private ExpressionNode ParsePrimary()
    {
        var token = Peek;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Next();
                return int.TryParse(token.Text, out var number)
                    ? new NumberExpressionNode(number, Span(token))
                    : Fail<ExpressionNode>("TW1002", $"Number '{token.Text}' is too large.", token.Offset);
            case TokenKind.String:
                Next();
                return new TextExpressionNode(token.Value, Span(token));
            case TokenKind.NamedPredicate:
                Next();
                return new NamedPredicateExpressionNode(new(token.Offset + 1, token.Length - 1), Span(token));
            case TokenKind.LeftParen:
            {
                var open = Next();
                var inner = ParseNested(open, ParseOr);
                var close = Expect(TokenKind.RightParen, "')'");
                return new ParenthesizedExpressionNode(inner,
                    new(open.Offset, close.Offset + close.Length - open.Offset));
            }
            case TokenKind.Identifier:
                return ParseIdentifier();
            case TokenKind.Error:
                return Fail<ExpressionNode>("TW1001", $"Unexpected '{token.Text}'.", token.Offset);
            default:
                return Fail<ExpressionNode>("TW1001",
                    token.Kind == TokenKind.End ? "Expression is incomplete." : $"Unexpected '{token.Text}'.",
                    token.Offset);
        }
    }

    private ExpressionNode ParseIdentifier()
    {
        var token = Next();
        if (token.Is("true")) return new BooleanExpressionNode(true, Span(token));
        if (token.Is("false")) return new BooleanExpressionNode(false, Span(token));
        if (token.Is("visited"))
        {
            Expect(TokenKind.LeftParen, "'(' after visited");
            var scene = Expect(TokenKind.Identifier, "a scene id");
            var close = Expect(TokenKind.RightParen, "')'");
            return new VisitedExpressionNode(Span(scene),
                new(token.Offset, close.Offset + close.Length - token.Offset));
        }

        if (Peek.Kind == TokenKind.Colon)
        {
            Next();
            var name = Expect(TokenKind.Identifier, $"a name after '{token.Text}:'");
            Token? member = null;
            Token? subMember = null;
            if (Peek.Kind == TokenKind.Colon)
            {
                Next();
                member = Expect(TokenKind.Identifier, $"a property after '{token.Text}:{name.Text}:'");
                if (name.Is("pronoun") && member.Value.Is("possessive-pronoun"))
                {
                    Expect(TokenKind.Colon, "':' after 'possessive-pronoun'");
                    subMember = Expect(TokenKind.Identifier, "a possessive pronoun property");
                }
            }

            var end = subMember ?? member ?? name;
            return new ReferenceExpressionNode(Span(token), Span(name),
                member is null ? null : Span(member.Value),
                subMember is null ? null : Span(subMember.Value),
                new(token.Offset, end.Offset + end.Length - token.Offset));
        }

        if (IsKeyword("has"))
        {
            Next();
            var item = Expect(TokenKind.Identifier, "an item id after 'has'");
            return new HasItemExpressionNode(Span(token), Span(item),
                new(token.Offset, item.Offset + item.Length - token.Offset));
        }

        return new NameExpressionNode(Span(token), Span(token));
    }

    private ExpressionNode Binary(Token op, ExpressionNode left, ExpressionNode right) =>
        new BinaryExpressionNode(op.Kind, left, right, Span(op),
            new(left.Span.Start, right.Span.End - left.Span.Start));

    private static ExpressionSpan Span(Token token) => new(token.Offset, token.Length);

    private static int FindEndIndex(TokenBuffer tokens, int endOffset)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == TokenKind.End || tokens[index].Offset >= endOffset) return index;
        }

        return tokens.Count - 1;
    }

    private static int FindHash(TokenBuffer tokens, int endOffset)
    {
        for (var index = 0; index < tokens.Count && tokens[index].Offset < endOffset; index++)
        {
            if (tokens[index] is { Kind: TokenKind.Error } token && token.Is("#")) return index;
        }

        return -1;
    }

    private ImmutableArray<ModifierCallSyntax> ParseModifiers(int start, int endOffset)
    {
        var text = _tokens.Source;
        var calls = ImmutableArray.CreateBuilder<ModifierCallSyntax>();
        var index = start;
        while (index < endOffset)
        {
            var offset = index;
            index++;
            var nameStart = index;
            while (index < endOffset && text[index] is not ('#' or '('))
            {
                index++;
            }

            var name = text[nameStart..index].Trim();
            string? argument = null;
            string? pluralArgument = null;
            if (index < text.Length && text[index] == '(')
            {
                index = SkipSpace(text, index + 1, endOffset);
                if (index >= endOffset || text[index] != '"')
                {
                    Fail("TW2006",
                        $"Write the argument to '#{name}' in double quotes, such as #{name}(\"value\").", index);
                }

                var literal = QuotedStringSyntax.Read(text, index, endOffset);
                if (literal.Error is { } error)
                {
                    Fail("TW2006", error, literal.ErrorOffset);
                }

                argument = literal.Value;
                index = SkipSpace(text, literal.End, endOffset);
                if (name == "one-many")
                {
                    if (index >= endOffset || text[index] != ',')
                    {
                        Fail("TW2006",
                            "'#one-many' requires exactly two quoted strings: #one-many(\"singular\", \"plural\").",
                            index);
                    }

                    index = SkipSpace(text, index + 1, endOffset);
                    if (index >= endOffset || text[index] != '"')
                    {
                        Fail("TW2006", "Write the plural (second) argument to '#one-many' in double quotes.", index);
                    }

                    literal = QuotedStringSyntax.Read(text, index, endOffset);
                    if (literal.Error is { } pluralError)
                    {
                        Fail("TW2006", pluralError, literal.ErrorOffset);
                    }

                    pluralArgument = literal.Value;
                    index = SkipSpace(text, literal.End, endOffset);
                }

                if (index >= endOffset || text[index] != ')')
                {
                    Fail("TW2006", $"Expected ')' after the argument to '#{name}'.", index);
                }

                index = SkipSpace(text, index + 1, endOffset);
                if (index < endOffset && text[index] != '#')
                {
                    Fail("TW2006", $"Expected '#' or the end of the substitution after '#{name}(…)'.", index);
                }
            }

            calls.Add(new(name, offset, argument, pluralArgument));
        }

        return calls.ToImmutable();
    }

    private static int SkipSpace(string text, int index, int endOffset)
    {
        while (index < endOffset && char.IsWhiteSpace(text[index])) index++;
        return index;
    }

    private EffectSyntax ParseEffect()
    {
        var first = Expect(TokenKind.Identifier, "an effect");
        if (first.Is("set") || first.Is("clear"))
        {
            var ns = Expect(TokenKind.Identifier, "'flag'");
            if (!ns.Is("flag"))
            {
                Fail("TW1003", $"'{first.Text}' only works on flags: {first.Text} flag:<id>.", ns.Offset);
            }

            Expect(TokenKind.Colon, "':'");
            var flag = Expect(TokenKind.Identifier, "a flag id");
            return new SetFlagEffectSyntax(first.Is("set"), Span(flag),
                new(first.Offset, flag.Offset + flag.Length - first.Offset));
        }

        if (first.Is("give") || first.Is("take"))
        {
            var who = Expect(TokenKind.Identifier, "a character id");
            var item = Expect(TokenKind.Identifier, "an item id");
            ExpressionNode? quantity = null;
            if (Peek.Kind is not (TokenKind.End or TokenKind.Semicolon))
            {
                quantity = ParseSum();
            }

            return new GiveTakeEffectSyntax(first.Is("give"), Span(who), Span(item), quantity,
                new(first.Offset, (quantity?.Span.End ?? item.Offset + item.Length) - first.Offset));
        }

        Expect(TokenKind.Colon, "':' (effects look like 'player:hp -= 3', 'set flag:x', 'give player rope')");
        var target = Expect(TokenKind.Identifier, "a name");
        var op = Next();
        if (op.Kind is not (TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign))
        {
            Fail("TW1003", $"Expected '=', '+=' or '-=' but found '{op.Text}'.", op.Offset);
        }

        var amount = ParseSum();
        return new AssignmentEffectSyntax(Span(first), Span(target), op.Kind, amount,
            new(first.Offset, amount.Span.End - first.Offset));
    }
}
