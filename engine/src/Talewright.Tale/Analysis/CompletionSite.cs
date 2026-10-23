using Talewright.Tale.Diagnostics;
using Talewright.Tale.Parsing;

namespace Talewright.Tale.Analysis;

/// <summary>Parser-recognized reference slot, with decoded token boundaries mapped to authored UTF-16 positions.</summary>
public sealed record CompletionSite(string Text, SourceRange Range, IReadOnlyList<AuthoringSymbolKind> Kinds,
    IReadOnlyList<string> Keywords, IReadOnlyList<SourceLocation> Positions);

internal static class ExpressionCompletion
{
    internal static IEnumerable<CompletionSite> Sites(string text, ExpressionContextKind kind, Func<int, int, SourceRange> map,
        IReadOnlyList<AuthoringSymbol> symbols)
    {
        var tokens = Lexer.Tokenize(text);
        var offsets = new HashSet<int> { text.Length };
        foreach (var token in tokens)
        {
            if (token.Kind is TokenKind.Identifier or TokenKind.NamedPredicate) offsets.Add(token.Offset);
        }
        for (var index = 0; index < text.Length; index++)
            if (char.IsWhiteSpace(text[index]) || text[index] is ':' or '(' or '#' or '@' or ';') offsets.Add(index + 1);
        var tokenIndex = 0;
        foreach (var offset in offsets.Order())
        {
            var start = offset;
            if (start < text.Length && text[start] == '@') start++;
            while (tokenIndex < tokens.Count && tokens[tokenIndex].Offset < start) tokenIndex++;
            if (tokenIndex > 0 && tokens[tokenIndex - 1] is { } previous &&
                (previous.Kind == TokenKind.String && start <= previous.Offset + previous.Text.Length + 1 ||
                 previous is { Kind: TokenKind.Error, Text: "unterminated string" })) continue;
            var end = start;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-' or '.')) end++;
            if (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '-' or '.')) continue;
            var before = tokens.GetRange(Math.Max(0, tokenIndex - 2), Math.Min(2, tokenIndex)).ToArray();
            AuthoringSymbolKind[] kinds = [];
            string[] keywords = [];
            if (start > 0 && text[start - 1] == '#' || before is [.., { Kind: TokenKind.Error, Text: "#" }])
            {
                if (kind == ExpressionContextKind.Substitution) kinds = [AuthoringSymbolKind.Modifier];
            }
            else if (start > 0 && text[start - 1] == '@')
            {
                if (kind == ExpressionContextKind.Condition) kinds = [AuthoringSymbolKind.Predicate];
            }
            else if (before is [.., { Kind: TokenKind.Identifier } ns, { Kind: TokenKind.Colon }])
            {
                kinds = ns.Text switch
                {
                    "flag" when kind != ExpressionContextKind.Number => [AuthoringSymbolKind.Flag],
                    "var" => [AuthoringSymbolKind.Variable],
                    "faction" => [AuthoringSymbolKind.Faction],
                    _ when symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Character && symbol.Name == ns.Text)
                        => [AuthoringSymbolKind.Stat],
                    _ => []
                };
                if (kind == ExpressionContextKind.Substitution && kinds.Contains(AuthoringSymbolKind.Stat))
                    keywords = ["first-name", "last-name", "name", "pronoun.subject", "pronoun.object",
                        "pronoun.possessive", "pronoun.possessive-pronoun", "pronoun.reflexive"];
            }
            else if (before is [.., { Text: "visited" }, { Kind: TokenKind.LeftParen }])
                kinds = kind == ExpressionContextKind.Condition ? [AuthoringSymbolKind.Scene] : [];
            else if (before is [.., { Text: "has" }])
                kinds = kind is ExpressionContextKind.Condition or ExpressionContextKind.Effects ? [AuthoringSymbolKind.Item] : [];
            else if (before is [.., { Text: "give" or "take" }, { Kind: TokenKind.Identifier }])
                kinds = kind == ExpressionContextKind.Effects ? [AuthoringSymbolKind.Item] : [];
            else if (before is [.., { Text: "give" or "take" }])
                kinds = kind == ExpressionContextKind.Effects ? [AuthoringSymbolKind.Character] : [];
            else if (before is [.., { Text: "set" or "clear" }])
                keywords = kind == ExpressionContextKind.Effects ? ["flag:"] : [];
            else if (before.Length == 0 || before[^1].Kind is TokenKind.LeftParen or TokenKind.Semicolon
                     or TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or TokenKind.Assign
                     or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.Equal or TokenKind.NotEqual
                     or TokenKind.Less or TokenKind.LessOrEqual or TokenKind.Greater or TokenKind.GreaterOrEqual ||
                     before[^1].Text is "and" or "or" or "not")
            {
                kinds = [AuthoringSymbolKind.Character];
                keywords = kind switch
                {
                    ExpressionContextKind.Effects when before.Length == 0 || before[^1].Kind == TokenKind.Semicolon
                        => ["set", "clear", "give", "take", "var:", "faction:"],
                    ExpressionContextKind.Effects => ["var:", "faction:"],
                    ExpressionContextKind.Number => ["var:"],
                    ExpressionContextKind.Substitution => ["var:", "flag:", "faction:"],
                    _ => ["flag:", "var:", "faction:", "visited(", "true", "false", "not"]
                };
            }
            if (kinds.Length == 0 && keywords.Length == 0) continue;
            yield return new(text[start..end], map(start, end - start), kinds, keywords,
                Enumerable.Range(start, end - start + 1).Select(index => map(index, 0).Start).ToArray());
        }
    }
}
