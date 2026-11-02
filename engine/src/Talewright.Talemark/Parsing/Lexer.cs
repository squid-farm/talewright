using Talewright.Core.Identity;

namespace Talewright.Talemark.Parsing;

internal enum TokenKind
{
    Identifier,
    Number,
    String,
    /// <summary><c>@Name</c>; text excludes the <c>@</c>.</summary>
    NamedPredicate,
    Colon,
    Semicolon,
    LeftParen,
    RightParen,
    Plus,
    Minus,
    Star,
    Slash,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Assign,
    PlusAssign,
    MinusAssign,
    Error,
    End,
}

internal readonly record struct Token(TokenKind Kind, string Source, int Offset, int Length,
    string? Error = null, int ErrorAt = -1)
{
    internal int? ErrorOffset => ErrorAt < 0 ? null : ErrorAt;
    internal int? StringEnd => Kind == TokenKind.String || ErrorOffset is not null ? Offset + Length : null;

    internal ReadOnlySpan<char> Text
    {
        get
        {
            if (Error is not null) return Error.AsSpan();
            if (Kind == TokenKind.String)
                return QuotedStringSyntax.Read(Source, Offset, Offset + Length).Value.AsSpan();
            var start = Offset;
            if (Length > 0 && Source[start] == '@' && Kind is TokenKind.NamedPredicate or TokenKind.Error)
                start++;
            return Source.AsSpan(start, Offset + Length - start);
        }
    }

    internal bool Is(string text) => Text.SequenceEqual(text);

    internal string Value => Error ?? (Kind == TokenKind.String
        ? QuotedStringSyntax.Read(Source, Offset, Offset + Length).Value
        : Text.ToString());
}

internal sealed class TokenBuffer(string source, List<Token> tokens)
{
    private readonly List<Token> _tokens = tokens;

    internal string Source { get; } = source;
    internal int Count => _tokens.Count;
    internal Token this[int index] => _tokens[index];
    internal TokenWindow Slice(int start, int length) => new(this, start, length);
    public List<Token>.Enumerator GetEnumerator() => _tokens.GetEnumerator();

    internal int FindHash()
    {
        for (var index = 0; index < _tokens.Count; index++)
            if (_tokens[index] is { Kind: TokenKind.Error, Text: "#" }) return index;
        return -1;
    }
}

internal readonly struct TokenWindow(TokenBuffer buffer, int start, int length)
{
    public int Length { get; } = length;
    public Token this[int index] => buffer[start + index];
    public Token this[Index index] => buffer[start + index.GetOffset(Length)];
    public TokenWindow this[Range range]
    {
        get
        {
            var (offset, count) = range.GetOffsetAndLength(Length);
            return new(buffer, start + offset, count);
        }
    }
}

/// <summary>
/// Tokenizer for expressions and effects. Identifiers follow <see cref="IdentifierRules"/>; a dot is only
/// part of a name, never a property separator.
/// </summary>
internal static class Lexer
{
    public static TokenBuffer Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (IdentifierRules.IsStart(c))
            {
                while (i < text.Length && IdentifierRules.IsPart(text[i]))
                {
                    i++;
                }

                while (i > start && text[i - 1] is '.' or '-') i--;
                tokens.Add(new Token(IdentifierRules.IsValid(text.AsSpan(start, i - start))
                    ? TokenKind.Identifier : TokenKind.Error, text, start, i - start));
                continue;
            }

            if (char.IsDigit(c))
            {
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, text, start, i - start));
                continue;
            }

            if (c == '"')
            {
                var literal = QuotedStringSyntax.Scan(text, i, text.Length);
                tokens.Add(literal.Error is { } error
                    ? new(TokenKind.Error, text, start, literal.End - start, error, literal.ErrorOffset)
                    : new(TokenKind.String, text, start, literal.End - start));
                i = literal.End;
                continue;
            }

            if (c == '@')
            {
                i++;
                var nameStart = i;
                while (i < text.Length && IdentifierRules.IsPart(text[i]))
                {
                    i++;
                }

                while (i > nameStart && text[i - 1] is '.' or '-') i--;
                tokens.Add(new Token(IdentifierRules.IsValid(text.AsSpan(nameStart, i - nameStart))
                    ? TokenKind.NamedPredicate : TokenKind.Error, text, start, i - start));
                continue;
            }

            var kind = (c, i + 1 < text.Length ? text[i + 1] : '\0') switch
            {
                ('=', '=') => TokenKind.Equal,
                ('!', '=') => TokenKind.NotEqual,
                ('<', '=') => TokenKind.LessOrEqual,
                ('>', '=') => TokenKind.GreaterOrEqual,
                ('+', '=') => TokenKind.PlusAssign,
                ('-', '=') => TokenKind.MinusAssign,
                _ => TokenKind.Error,
            };
            if (kind != TokenKind.Error)
            {
                tokens.Add(new Token(kind, text, start, 2));
                i += 2;
                continue;
            }

            kind = c switch
            {
                ':' => TokenKind.Colon,
                ';' => TokenKind.Semicolon,
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                '+' => TokenKind.Plus,
                '-' => TokenKind.Minus,
                '*' => TokenKind.Star,
                '/' => TokenKind.Slash,
                '<' => TokenKind.Less,
                '>' => TokenKind.Greater,
                '=' => TokenKind.Assign,
                _ => TokenKind.Error,
            };
            tokens.Add(new Token(kind, text, start, 1));
            i++;
        }

        tokens.Add(new Token(TokenKind.End, text, text.Length, 0));
        return new TokenBuffer(text, tokens);
    }
}
