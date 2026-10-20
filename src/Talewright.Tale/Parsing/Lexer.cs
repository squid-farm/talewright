namespace Talewright.Tale.Parsing;

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

internal readonly record struct Token(TokenKind Kind, string Text, int Offset);

/// <summary>
/// Tokenizer for expressions and effects. Identifiers may contain <c>-</c> and <c>.</c> (kebab-case ids and
/// dotted paths), so subtraction needs spaces: <c>player:hp - 3</c>.
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
            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.'))
                {
                    i++;
                }

                // Trailing '-' or '.' belong to the next token (e.g. "hp-=3").
                while (i > start + 1 && text[i - 1] is '-' or '.')
                {
                    i--;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..i], start));
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
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    i++;
                }

                if (i >= text.Length)
                {
                    tokens.Add(new Token(TokenKind.Error, "unterminated string", start));
                    break;
                }

                tokens.Add(new Token(TokenKind.String, text[(start + 1)..i], start));
                i++;
                continue;
            }

            if (c == '@')
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.NamedPredicate, text[(start + 1)..i], start));
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
