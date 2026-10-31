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

internal readonly record struct Token(TokenKind Kind, string Text, int Offset,
    int? StringEnd = null, int? ErrorOffset = null);

/// <summary>
/// Tokenizer for expressions and effects. Identifiers follow <see cref="IdentifierRules"/>; a dot is only
/// part of a name, never a property separator.
/// </summary>
internal static class Lexer
{
    public static List<Token> Tokenize(string text)
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
                var value = text[start..i];
                tokens.Add(new Token(IdentifierRules.IsValid(value) ? TokenKind.Identifier : TokenKind.Error, value, start));
                continue;
            }

            if (char.IsDigit(c))
            {
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, text[start..i], start));
                continue;
            }

            if (c == '"')
            {
                var literal = QuotedStringSyntax.Read(text, i, text.Length);
                tokens.Add(literal.Error is { } error
                    ? new(TokenKind.Error, error, start, literal.End, literal.ErrorOffset)
                    : new(TokenKind.String, literal.Value, start, literal.End));
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
                var name = text[nameStart..i];
                tokens.Add(new Token(IdentifierRules.IsValid(name) ? TokenKind.NamedPredicate : TokenKind.Error, name, start));
                continue;
            }

            var two = i + 1 < text.Length ? text.Substring(i, 2) : string.Empty;
            var kind = two switch
            {
                "==" => TokenKind.Equal,
                "!=" => TokenKind.NotEqual,
                "<=" => TokenKind.LessOrEqual,
                ">=" => TokenKind.GreaterOrEqual,
                "+=" => TokenKind.PlusAssign,
                "-=" => TokenKind.MinusAssign,
                _ => TokenKind.Error,
            };
            if (kind != TokenKind.Error)
            {
                tokens.Add(new Token(kind, two, start));
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
            tokens.Add(new Token(kind, c.ToString(), start));
            i++;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
        return tokens;
    }
}
