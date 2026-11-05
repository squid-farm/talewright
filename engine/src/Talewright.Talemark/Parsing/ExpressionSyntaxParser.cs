using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal sealed class ExpressionSyntaxParser
{
    internal const int MaximumNestingDepth = 256;

    private readonly TokenBuffer _tokens;
    private readonly int _endIndex;
    private readonly int _endOffset;
    private readonly Func<int, SourceLocation> _locate;
    private readonly DiagnosticBag _diagnostics;
    private readonly bool _highlightUnexpectedTokens;
    private int _position;
    private int _nestingDepth;
    private int _failureOffset;
    private bool _hasErrors;

    private ExpressionSyntaxParser(TokenBuffer tokens, int endOffset, Func<int, SourceLocation> locate,
        DiagnosticBag diagnostics, int? endIndex = null, bool highlightUnexpectedTokens = false)
    {
        _tokens = tokens;
        _endIndex = endIndex ?? tokens.Count - 1;
        _endOffset = endOffset;
        _locate = locate;
        _diagnostics = diagnostics;
        _highlightUnexpectedTokens = highlightUnexpectedTokens;
    }

    private sealed class ParseException : Exception;

    internal static ExpressionSyntaxTree ParseExpression(TokenBuffer tokens, int endOffset,
        Func<int, SourceLocation> locate, DiagnosticBag diagnostics, bool sumOnly = false,
        bool highlightUnexpectedTokens = false)
    {
        var parser = new ExpressionSyntaxParser(tokens, endOffset, locate, diagnostics,
            FindEndIndex(tokens, endOffset), highlightUnexpectedTokens);
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
            _diagnostics.Error(CompilerDiagnosticCodes.InvalidExpression, token.Value, _locate(offset));
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
            if (token.ErrorOffset is { } offset) Fail(CompilerDiagnosticCodes.InvalidExpression, token.Value, offset);
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
        var token = Peek;
        if (token.Kind != TokenKind.End)
        {
            // Error tokens can be a single UTF-16 surrogate rather than a whole character.
            Fail(CompilerDiagnosticCodes.InvalidExpression, $"Unexpected '{token.Text}'.", token.Offset,
                token.Kind == TokenKind.Error ? 0 : token.Length);
        }
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (Peek.Kind != kind)
        {
            var found = Peek.Kind == TokenKind.End ? "end of expression" : Peek.Value;
            Fail(CompilerDiagnosticCodes.InvalidExpression, $"Expected {what} but found '{found}'.", Peek.Offset);
        }

        return Next();
    }

    [DoesNotReturn]
    private void Fail(string code, string message, int offset, int length = 0)
    {
        _hasErrors = true;
        _failureOffset = offset;
        if (_highlightUnexpectedTokens && length > 0)
            _diagnostics.Error(code, message, _locate(offset), _locate(offset + length));
        else
            _diagnostics.Error(code, message, _locate(offset));
        throw new ParseException();
    }

    private T Fail<T>(string code, string message, int offset, int length = 0)
    {
        Fail(code, message, offset, length);
        return default!;
    }

    private ExpressionNode ParseNested(Token opener, Func<ExpressionNode> parse)
    {
        if (_nestingDepth == MaximumNestingDepth)
        {
            Fail(CompilerDiagnosticCodes.NestingLimitExceeded, $"Expression is nested more than {MaximumNestingDepth} levels deep.", opener.Offset);
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
        while (IsKeyword(ExpressionVocabulary.Or))
        {
            var op = Next();
            left = Binary(BinaryOp.Or, op, left, ParseAnd());
        }

        return left;
    }

    private ExpressionNode ParseAnd()
    {
        var left = ParseNot();
        while (IsKeyword(ExpressionVocabulary.And))
        {
            var op = Next();
            left = Binary(BinaryOp.And, op, left, ParseNot());
        }

        return left;
    }

    private ExpressionNode ParseNot()
    {
        if (IsKeyword(ExpressionVocabulary.Not))
        {
            var op = Next();
            var operand = ParseNested(op, ParseNot);
            return new UnaryExpressionNode(UnaryOp.Not, operand, Span(op),
                new(op.Offset, operand.Span.End - op.Offset));
        }

        return ParseComparison();
    }

    private ExpressionNode ParseComparison()
    {
        var left = ParseSum();
        BinaryOp? comparison = Peek.Kind switch
        {
            TokenKind.Equal => BinaryOp.Equal,
            TokenKind.NotEqual => BinaryOp.NotEqual,
            TokenKind.Less => BinaryOp.Less,
            TokenKind.LessOrEqual => BinaryOp.LessOrEqual,
            TokenKind.Greater => BinaryOp.Greater,
            TokenKind.GreaterOrEqual => BinaryOp.GreaterOrEqual,
            _ => null,
        };
        if (comparison is not { } kind)
        {
            return left;
        }

        var op = Next();
        return Binary(kind, op, left, ParseSum());
    }

    private ExpressionNode ParseSum()
    {
        var left = ParseTerm();
        while (Peek.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = Next();
            left = Binary(op.Kind == TokenKind.Plus ? BinaryOp.Add : BinaryOp.Subtract, op, left, ParseTerm());
        }

        return left;
    }

    private ExpressionNode ParseTerm()
    {
        var left = ParseUnary();
        while (Peek.Kind is TokenKind.Star or TokenKind.Slash)
        {
            var op = Next();
            left = Binary(op.Kind == TokenKind.Star ? BinaryOp.Multiply : BinaryOp.Divide, op, left, ParseUnary());
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
            return new UnaryExpressionNode(UnaryOp.Negate, operand, Span(op),
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
                    : Fail<ExpressionNode>(CompilerDiagnosticCodes.NumberTooLarge, $"Number '{token.Text}' is too large.", token.Offset);
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
                return Fail<ExpressionNode>(CompilerDiagnosticCodes.InvalidExpression, $"Unexpected '{token.Text}'.", token.Offset);
            default:
                return Fail<ExpressionNode>(CompilerDiagnosticCodes.InvalidExpression,
                    token.Kind == TokenKind.End ? "Expression is incomplete." : $"Unexpected '{token.Text}'.",
                    token.Offset, token.Length);
        }
    }

    private ExpressionNode ParseIdentifier()
    {
        var token = Next();
        if (token.Is(ExpressionVocabulary.True)) return new BooleanExpressionNode(true, Span(token));
        if (token.Is(ExpressionVocabulary.False)) return new BooleanExpressionNode(false, Span(token));
        if (IsKeyword(ExpressionVocabulary.Visited))
        {
            Next();
            var scene = Expect(TokenKind.Identifier, "a scene id after 'visited'");
            return new PlayerVisitedExpressionNode(Span(token), Span(scene),
                new(token.Offset, scene.Offset + scene.Length - token.Offset));
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
                if (name.Is(ExpressionVocabulary.Pronoun) && member.Value.Is(ExpressionVocabulary.PossessivePronoun))
                {
                    Expect(TokenKind.Colon, $"':' after '{ExpressionVocabulary.PossessivePronoun}'");
                    subMember = Expect(TokenKind.Identifier, "a possessive pronoun property");
                }
            }

            var end = subMember ?? member ?? name;
            return new ReferenceExpressionNode(ExpressionVocabulary.Namespace(token.Text), Span(token), Span(name),
                member is null ? null : Span(member.Value),
                subMember is null ? null : Span(subMember.Value),
                new(token.Offset, end.Offset + end.Length - token.Offset));
        }

        if (IsKeyword(ExpressionVocabulary.Has))
        {
            Next();
            var item = Expect(TokenKind.Identifier, "an item id after 'has'");
            return new HasItemExpressionNode(Span(token), Span(item),
                new(token.Offset, item.Offset + item.Length - token.Offset));
        }

        return new NameExpressionNode(Span(token), Span(token));
    }

    private static ExpressionNode Binary(BinaryOp kind, Token op, ExpressionNode left, ExpressionNode right) =>
        new BinaryExpressionNode(kind, left, right, Span(op),
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
            if (tokens[index].Kind == TokenKind.Hash) return index;
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
            while (index < endOffset && text[index] is not (ExpressionVocabulary.ModifierMarker or ExpressionVocabulary.OpenParen))
            {
                index++;
            }

            var name = text[nameStart..index].Trim();
            string? argument = null;
            string? pluralArgument = null;
            if (index < text.Length && text[index] == ExpressionVocabulary.OpenParen)
            {
                index = SkipSpace(text, index + 1, endOffset);
                if (index >= endOffset || text[index] != TextVocabulary.QuoteMark)
                {
                    Fail(CompilerDiagnosticCodes.InvalidModifier,
                        $"Write the argument to '#{name}' in double quotes, such as #{name}(\"value\").", index);
                }

                var literal = QuotedStringSyntax.Read(text, index, endOffset);
                if (literal.Error is { } error)
                {
                    Fail(CompilerDiagnosticCodes.InvalidModifier, error, literal.ErrorOffset);
                }

                argument = literal.Value;
                index = SkipSpace(text, literal.End, endOffset);
                if (name == OneManyTextModifier.ModifierName)
                {
                    if (index >= endOffset || text[index] != ExpressionVocabulary.ArgumentSeparator)
                    {
                        Fail(CompilerDiagnosticCodes.InvalidModifier,
                            "'#one-many' requires exactly two quoted strings: #one-many(\"singular\", \"plural\").",
                            index);
                    }

                    index = SkipSpace(text, index + 1, endOffset);
                    if (index >= endOffset || text[index] != TextVocabulary.QuoteMark)
                    {
                        Fail(CompilerDiagnosticCodes.InvalidModifier, "Write the plural (second) argument to '#one-many' in double quotes.", index);
                    }

                    literal = QuotedStringSyntax.Read(text, index, endOffset);
                    if (literal.Error is { } pluralError)
                    {
                        Fail(CompilerDiagnosticCodes.InvalidModifier, pluralError, literal.ErrorOffset);
                    }

                    pluralArgument = literal.Value;
                    index = SkipSpace(text, literal.End, endOffset);
                }

                if (index >= endOffset || text[index] != ExpressionVocabulary.CloseParen)
                {
                    Fail(CompilerDiagnosticCodes.InvalidModifier, $"Expected ')' after the argument to '#{name}'.", index);
                }

                index = SkipSpace(text, index + 1, endOffset);
                if (index < endOffset && text[index] != ExpressionVocabulary.ModifierMarker)
                {
                    Fail(CompilerDiagnosticCodes.InvalidModifier, $"Expected '#' or the end of the substitution after '#{name}(…)'.", index);
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
        if (first.Is(ExpressionVocabulary.Set) || first.Is(ExpressionVocabulary.Clear))
        {
            var ns = Expect(TokenKind.Identifier, $"'{ExpressionVocabulary.Flag}'");
            if (!ns.Is(ExpressionVocabulary.Flag))
            {
                Fail(CompilerDiagnosticCodes.MalformedEffect, $"'{first.Text}' only works on flags: {first.Text} flag:<id>.", ns.Offset);
            }

            Expect(TokenKind.Colon, "':'");
            var flag = Expect(TokenKind.Identifier, "a flag id");
            return new SetFlagEffectSyntax(first.Is(ExpressionVocabulary.Set), Span(flag),
                new(first.Offset, flag.Offset + flag.Length - first.Offset));
        }

        if (first.Is(ExpressionVocabulary.Give) || first.Is(ExpressionVocabulary.Take))
        {
            var who = Expect(TokenKind.Identifier, "a character id");
            var item = Expect(TokenKind.Identifier, "an item id");
            ExpressionNode? quantity = null;
            if (Peek.Kind is not (TokenKind.End or TokenKind.Semicolon))
            {
                quantity = ParseSum();
            }

            return new GiveTakeEffectSyntax(first.Is(ExpressionVocabulary.Give), Span(who), Span(item), quantity,
                new(first.Offset, (quantity?.Span.End ?? item.Offset + item.Length) - first.Offset));
        }

        Expect(TokenKind.Colon, "':' (effects look like 'player:hp -= 3', 'set flag:x', 'give player rope')");
        var target = Expect(TokenKind.Identifier, "a name");
        Token? member = null;
        if (first.Is(ExpressionVocabulary.Faction) && Peek.Kind == TokenKind.Colon)
        {
            Next();
            member = Expect(TokenKind.Identifier, "a faction property");
        }

        var op = Next();
        AssignOp? assignment = op.Kind switch
        {
            TokenKind.Assign => AssignOp.Set,
            TokenKind.PlusAssign => AssignOp.Add,
            TokenKind.MinusAssign => AssignOp.Subtract,
            _ => null,
        };
        if (assignment is not { } kind)
        {
            Fail(CompilerDiagnosticCodes.MalformedEffect, $"Expected '=', '+=' or '-=' but found '{op.Text}'.", op.Offset);
            return default!;
        }

        var amount = ParseSum();
        return new AssignmentEffectSyntax(ExpressionVocabulary.Namespace(first.Text), Span(first), Span(target),
            member is null ? null : Span(member.Value), kind, amount, new(first.Offset, amount.Span.End - first.Offset));
    }
}
