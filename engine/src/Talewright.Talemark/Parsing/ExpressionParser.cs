using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

internal static class ExpressionParser
{
    internal static bool ReportStringErrors(string text, Func<int, SourceLocation> locate, DiagnosticBag diagnostics) =>
        ReportStringErrors(Lexer.Tokenize(text), locate, diagnostics);

    internal static bool ReportStringErrors(TokenBuffer tokens, Func<int, SourceLocation> locate,
        DiagnosticBag diagnostics)
    {
        var invalid = false;
        foreach (var token in tokens)
        {
            if (token.ErrorOffset is not { } offset) continue;
            diagnostics.Error(CompilerDiagnosticCodes.InvalidExpression, token.Value, locate(offset));
            invalid = true;
        }

        return invalid;
    }

    internal static void CollectDeclarations(string text, ISet<FlagId> flags, IDictionary<VarId, int> variables,
        Action<string, bool, int>? declaration = null,
        Action<VariableDeclaration>? typedDeclaration = null, SourceLocation location = default) =>
        CollectDeclarations(Lexer.Tokenize(text), text.Length, flags, variables, declaration, typedDeclaration, location);

    internal static void CollectDeclarations(TokenBuffer tokens, int expressionLength, ISet<FlagId> flags,
        IDictionary<VarId, int> variables, Action<string, bool, int>? declaration = null,
        Action<VariableDeclaration>? typedDeclaration = null, SourceLocation location = default)
    {
        for (var start = 0; start < tokens.Count - 1;)
        {
            var end = start;
            while (tokens[end].Kind is not (TokenKind.Semicolon or TokenKind.End))
            {
                end++;
            }

            var statement = tokens.Slice(start, end - start);
            if (statement is [
                { Kind: TokenKind.Identifier, Text: ExpressionVocabulary.Set or ExpressionVocabulary.Clear },
                { Kind: TokenKind.Identifier, Text: ExpressionVocabulary.Flag },
                { Kind: TokenKind.Colon },
                { Kind: TokenKind.Identifier } flag])
            {
                var name = flag.Value;
                flags.Add(new FlagId(name));
                declaration?.Invoke(name, true, flag.Offset);
            }
            else if (statement is [
                { Kind: TokenKind.Identifier, Text: ExpressionVocabulary.Var },
                { Kind: TokenKind.Colon },
                { Kind: TokenKind.Identifier } variable,
                { Kind: TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign },
                .. var amount])
            {
                var name = variable.Value;
                variables.TryAdd(new VarId(name), 0);
                declaration?.Invoke(name, false, variable.Offset);
                if (amount.Length == 0)
                {
                    start = end + 1;
                    continue;
                }

                var numeric = statement[3].Kind is TokenKind.PlusAssign or TokenKind.MinusAssign;
                for (var index = 0; index < amount.Length && !numeric; index++)
                    numeric = amount[index].Kind is TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash;
                typedDeclaration?.Invoke(new(new(name),
                    tokens.Source[amount[0].Offset..Math.Min(tokens[end].Offset, expressionLength)], numeric,
                    location.Offset(variable.Offset)));
            }

            start = end + 1;
        }
    }

    /// <summary>Parses and binds a boolean condition.</summary>
    public static Expr? ParseCondition(string text, SourceLocation location, StorySymbols symbols,
        DiagnosticBag diagnostics, SourceLocation? authoringLocation = null, Func<int, SourceLocation>? locate = null,
        bool highlightUnexpectedTokens = false)
    {
        var tokens = symbols.Analysis?.Expression(ExpressionContextKind.Condition, text,
            authoringLocation ?? location, locate) ?? Lexer.Tokenize(text);
        var syntax = ExpressionSyntaxParser.ParseExpression(tokens, text.Length, locate ?? location.Offset, diagnostics,
            highlightUnexpectedTokens: highlightUnexpectedTokens);
        return ExpressionBinder.Bind(syntax, symbols, diagnostics, locate ?? location.Offset, ExprType.Bool);
    }

    /// <summary>Parses and binds a numeric expression.</summary>
    public static Expr? ParseNumber(string text, SourceLocation location, StorySymbols symbols,
        DiagnosticBag diagnostics)
    {
        var tokens = symbols.Analysis?.Expression(ExpressionContextKind.Number, text, location)
            ?? Lexer.Tokenize(text);
        var syntax = ExpressionSyntaxParser.ParseExpression(tokens, text.Length, location.Offset, diagnostics);
        return ExpressionBinder.Bind(syntax, symbols, diagnostics, location.Offset, ExprType.Number);
    }

    /// <summary>Parses and binds the inside of <c>{reference#modifier…}</c>.</summary>
    public static SubstitutionTemplate? ParseSubstitution(string text, SourceLocation location, StorySymbols symbols,
        DiagnosticBag diagnostics, Func<int, SourceLocation>? locate = null)
    {
        var tokens = symbols.Analysis?.Expression(ExpressionContextKind.Substitution, text, location, locate)
            ?? Lexer.Tokenize(text);
        var syntax = ExpressionSyntaxParser.ParseSubstitution(tokens, text.Length, locate ?? location.Offset,
            diagnostics);
        return ExpressionBinder.Bind(syntax, symbols, diagnostics, locate ?? location.Offset);
    }

    /// <summary>Parses and binds semicolon-separated effects.</summary>
    public static List<Effect>? ParseEffects(string text, SourceLocation location, StorySymbols symbols,
        DiagnosticBag diagnostics)
    {
        var tokens = symbols.Analysis?.Expression(ExpressionContextKind.Effects, text, location)
            ?? Lexer.Tokenize(text);
        var syntax = ExpressionSyntaxParser.ParseEffects(tokens, text.Length, location.Offset, diagnostics);
        return ExpressionBinder.Bind(syntax, symbols, diagnostics, location.Offset);
    }

    /// <summary>True for words that can't be used as ids.</summary>
    public static bool IsReserved(string id) =>
        id is ExpressionVocabulary.And or ExpressionVocabulary.Or or ExpressionVocabulary.Not or ExpressionVocabulary.Has or
            ExpressionVocabulary.True or ExpressionVocabulary.False or ExpressionVocabulary.Visited or
            ExpressionVocabulary.Faction or ExpressionVocabulary.Item or ExpressionVocabulary.Flag or ExpressionVocabulary.Var or
            SceneVocabulary.End;
}
