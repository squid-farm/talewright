using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Vocabulary;
using static Talewright.Talemark.Vocabulary.ExpressionVocabulary;

namespace Talewright.Talemark.Analysis;

/// <summary>
/// Parser-recognized reference slot, with decoded token boundaries mapped to authored UTF-16 positions.
/// <see cref="KeywordDetails"/> optionally describes individual keywords, such as which argument value is the default.
/// </summary>
public sealed record CompletionSite(string Text, SourceRange Range, IReadOnlyList<AuthoringSymbolKind> Kinds,
    IReadOnlyList<string> Keywords, IReadOnlyList<SourceLocation> Positions,
    char? FollowingDelimiter = null, IReadOnlyList<AuthoringSymbol>? Candidates = null,
    IReadOnlyDictionary<string, string>? KeywordDetails = null, bool IsImagePath = false);

internal static class ExpressionCompletion
{
    private static readonly IReadOnlyDictionary<string, string> PronounFormDetails =
        AuthoringSyntaxVocabulary.KeywordDetails(SubjectForm, ObjectForm, PossessivePronoun, ReflexiveForm,
            OneManyTextModifier.ModifierName);

    private static readonly IReadOnlyDictionary<string, string> PossessivePropertyDetails =
        AuthoringSyntaxVocabulary.KeywordDetails(Determiner, Independent);

    private static readonly string[] CharacterMemberKeywords = [.. CharacterProperties, Inventory];
    private static readonly string[] NumericCharacterMemberKeywords = [Inventory];
    private static readonly string[] InventoryItemKeywords = [Count, Name];
    private static readonly string[] NumericInventoryItemKeywords = [Count];

    internal static IEnumerable<CompletionSite> Sites(TokenBuffer tokens, ExpressionContextKind kind, Func<int, int, SourceRange> map,
        IReadOnlyList<AuthoringSymbol> symbols)
    {
        var text = tokens.Source;
        var offsets = new HashSet<int> { text.Length };
        foreach (var token in tokens)
        {
            if (token.Kind is TokenKind.Identifier or TokenKind.NamedPredicate) offsets.Add(token.Offset);
        }
        for (var index = 0; index < text.Length; index++)
            if (char.IsWhiteSpace(text[index]) || text[index] is Separator or OpenParen or ModifierMarker or PredicateMarker or StatementSeparator) offsets.Add(index + 1);
        var tokenIndex = 0;
        foreach (var offset in offsets.Order())
        {
            var start = offset;
            if (start < text.Length && text[start] == PredicateMarker) start++;
            while (tokenIndex < tokens.Count && tokens[tokenIndex].Offset < start) tokenIndex++;
            if (tokenIndex > 0 && tokens[tokenIndex - 1] is { } previous &&
                previous.StringEnd is { } stringEnd &&
                (start < stringEnd || start == stringEnd && previous.ErrorOffset is not null)) continue;
            var end = start;
            if (tokenIndex < tokens.Count && tokens[tokenIndex] is { Kind: TokenKind.Identifier } identifier &&
                identifier.Offset == start)
            {
                end += identifier.Text.Length;
            }
            else if (tokenIndex > 0 && tokens[tokenIndex - 1] is { Kind: TokenKind.NamedPredicate } predicate &&
                     predicate.Offset + 1 == start)
                end += predicate.Text.Length;
            if (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || IdentifierRules.IsPart(text[start - 1]))) continue;
            var before = tokens.Slice(Math.Max(0, tokenIndex - 2), Math.Min(2, tokenIndex));
            AuthoringSymbolKind[] kinds = [];
            string[] keywords = [];
            IReadOnlyDictionary<string, string>? details = null;
            if (start > 0 && text[start - 1] == ModifierMarker || before is [.., { Kind: TokenKind.Hash }])
            {
                if (kind == ExpressionContextKind.Substitution)
                {
                    kinds = [AuthoringSymbolKind.Modifier];
                    if (IsAgreementReference(tokens, symbols))
                    {
                        keywords = [OneManyTextModifier.ModifierName];
                        details = PronounFormDetails;
                    }
                }
            }
            else if (kind != ExpressionContextKind.Number && tokenIndex >= 6 &&
                     tokens.Slice(tokenIndex - 6, 6) is [{ Kind: TokenKind.Identifier } possessor,
                         { Kind: TokenKind.Colon }, { Kind: TokenKind.Identifier, Text: Pronoun },
                         { Kind: TokenKind.Colon }, { Kind: TokenKind.Identifier, Text: PossessivePronoun },
                         { Kind: TokenKind.Colon }] &&
                     symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Character && possessor.Is(symbol.Name)))
            {
                keywords = [.. PossessiveProperties];
                details = PossessivePropertyDetails;
            }
            else if (tokenIndex >= 6 &&
                     tokens.Slice(tokenIndex - 6, 6) is [{ Kind: TokenKind.Identifier } inventoryCharacter,
                         { Kind: TokenKind.Colon }, { Kind: TokenKind.Identifier, Text: Inventory },
                         { Kind: TokenKind.Colon }, { Kind: TokenKind.Identifier } item,
                         { Kind: TokenKind.Colon }] &&
                     HasSymbol(symbols, AuthoringSymbolKind.Character, inventoryCharacter) &&
                     HasSymbol(symbols, AuthoringSymbolKind.Item, item))
            {
                keywords = kind == ExpressionContextKind.Number
                    ? NumericInventoryItemKeywords
                    : InventoryItemKeywords;
            }
            else if (tokenIndex >= 4 &&
                     tokens.Slice(tokenIndex - 4, 4) is [{ Kind: TokenKind.Identifier } inventoryOwner,
                         { Kind: TokenKind.Colon }, { Kind: TokenKind.Identifier, Text: Inventory },
                         { Kind: TokenKind.Colon }] &&
                     HasSymbol(symbols, AuthoringSymbolKind.Character, inventoryOwner))
            {
                kinds = [AuthoringSymbolKind.Item];
            }
            else if (start > 0 && text[start - 1] == PredicateMarker)
            {
                if (kind == ExpressionContextKind.Condition) kinds = [AuthoringSymbolKind.Predicate];
            }
            else if (tokenIndex > 0 && tokens[tokenIndex - 1].Is(SceneVocabulary.SkillCheck))
            {
                kinds = [AuthoringSymbolKind.Character, AuthoringSymbolKind.Stat];
                keywords = [SceneVocabulary.Difficulty];
            }
            else if (tokenIndex >= 2 && tokens[tokenIndex - 2].Is(SceneVocabulary.SkillCheck) &&
                     tokens[tokenIndex - 1].Kind == TokenKind.Identifier)
            {
                kinds = [AuthoringSymbolKind.Stat];
                keywords = [SceneVocabulary.Difficulty];
            }
            else if (tokenIndex >= 3 && tokens[tokenIndex - 1].Is(SceneVocabulary.Difficulty) &&
                     (tokens[tokenIndex - 3].Is(SceneVocabulary.SkillCheck) &&
                      tokens[tokenIndex - 2].Kind == TokenKind.Identifier ||
                      tokenIndex >= 4 && tokens[tokenIndex - 4].Is(SceneVocabulary.SkillCheck) &&
                      tokens[tokenIndex - 3].Kind == TokenKind.Identifier &&
                      tokens[tokenIndex - 2].Kind == TokenKind.Identifier))
            {
                kinds = [AuthoringSymbolKind.Character];
                keywords = [VarPrefix, ScenePrefix, SceneVocabulary.SkillCheck];
            }
            else if (tokenIndex >= 3 && tokens[tokenIndex - 3].Is(SceneVocabulary.SkillCheck) &&
                     tokens[tokenIndex - 2].Kind == TokenKind.Identifier &&
                     tokens[tokenIndex - 1].Kind == TokenKind.Identifier)
            {
                keywords = [SceneVocabulary.Difficulty];
            }
            else if (before is [.., { Kind: TokenKind.Comma }])
            {
                keywords = [SceneVocabulary.Hidden];
            }
            else if (before is [.., { Kind: TokenKind.Identifier } member, { Kind: TokenKind.Colon }] &&
                     tokenIndex >= 4 &&
                     tokens[tokenIndex - 4] is { Kind: TokenKind.Identifier } owner &&
                     tokens[tokenIndex - 3].Kind == TokenKind.Colon &&
                     (owner.Is(Faction) || owner.Is(Item) ||
                      kind != ExpressionContextKind.Number && member.Is(Pronoun) &&
                      symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Character && owner.Is(symbol.Name))))
            {
                if (owner.Is(Faction))
                    keywords = [.. FactionProperties];
                else if (owner.Is(Item))
                    keywords = [.. ItemProperties];
                else
                {
                    keywords = [.. PronounProperties];
                    details = PronounFormDetails;
                }
                if (member.Is(Player) && symbols.Any(symbol =>
                        symbol.Kind == AuthoringSymbolKind.Scene && owner.Is(symbol.Name)))
                    keywords = [.. keywords, Visits];
            }
            else if (before is [.., { Kind: TokenKind.Identifier, Text: Player }, { Kind: TokenKind.Colon }] &&
                     tokenIndex >= 4 &&
                     tokens[tokenIndex - 4] is { Kind: TokenKind.Identifier } sceneOwner &&
                     tokens[tokenIndex - 3].Kind == TokenKind.Colon &&
                     (sceneOwner.Is(Scene) || symbols.Any(symbol =>
                         symbol.Kind == AuthoringSymbolKind.Scene && sceneOwner.Is(symbol.Name))))
            {
                keywords = [Visits];
            }
            else if (before is [.., { Kind: TokenKind.Identifier } ns, { Kind: TokenKind.Colon }])
            {
                if (ns.Is(Scene))
                {
                    keywords = [PlayerPrefix];
                }
                else
                {
                    kinds = ns.Text switch
                    {
                        Flag when kind != ExpressionContextKind.Number => [AuthoringSymbolKind.Flag],
                        Var => [AuthoringSymbolKind.Variable],
                        Faction => [AuthoringSymbolKind.Faction],
                        Item when kind is ExpressionContextKind.Substitution or ExpressionContextKind.Condition
                            => [AuthoringSymbolKind.Item],
                        _ when symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Character && ns.Is(symbol.Name))
                            => [AuthoringSymbolKind.Stat],
                        _ => []
                    };
                    if (kinds.Contains(AuthoringSymbolKind.Stat))
                        keywords = kind == ExpressionContextKind.Number ? NumericCharacterMemberKeywords : CharacterMemberKeywords;
                    if (symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Scene && ns.Is(symbol.Name)))
                        keywords = [.. keywords, PlayerPrefix];
                }
            }
            else if (kind is ExpressionContextKind.Condition or ExpressionContextKind.Substitution &&
                     before is [.., { Kind: TokenKind.Identifier } visitedSubject, { Text: Visited }] &&
                     visitedSubject.Is(Player))
                kinds = [AuthoringSymbolKind.Scene];
            else if (kind is ExpressionContextKind.Condition or ExpressionContextKind.Substitution &&
                     before is [.., { Kind: TokenKind.Identifier } playerSubject] && playerSubject.Is(Player))
                keywords = [Visited];
            else if (before is [.., { Text: Has }])
                kinds = kind is ExpressionContextKind.Condition or ExpressionContextKind.Effects ? [AuthoringSymbolKind.Item] : [];
            else if (before is [.., { Text: Give or Take }, { Kind: TokenKind.Identifier }])
                kinds = kind == ExpressionContextKind.Effects ? [AuthoringSymbolKind.Item] : [];
            else if (before is [.., { Text: Give or Take }])
                kinds = kind == ExpressionContextKind.Effects ? [AuthoringSymbolKind.Character] : [];
            else if (kind == ExpressionContextKind.Effects && tokenIndex >= 3 &&
                     tokens[tokenIndex - 3] is { Kind: TokenKind.Identifier, Text: Give or Take } &&
                     before is [{ Kind: TokenKind.Identifier }, { Kind: TokenKind.Identifier }])
            {
                kinds = [AuthoringSymbolKind.Character];
                keywords = [VarPrefix, FactionPrefix, ScenePrefix];
            }
            else if (before is [.., { Text: Set or Clear }])
                keywords = kind == ExpressionContextKind.Effects ? [FlagPrefix] : [];
            else if (before.Length == 0 || before[^1].Kind is TokenKind.LeftParen or TokenKind.Semicolon
                     or TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or TokenKind.Assign
                     or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.Equal or TokenKind.NotEqual
                     or TokenKind.Less or TokenKind.LessOrEqual or TokenKind.Greater or TokenKind.GreaterOrEqual ||
                     before[^1].Text is And or Or or Not)
            {
                kinds = [AuthoringSymbolKind.Character];
                keywords = kind switch
                {
                    ExpressionContextKind.Effects when before.Length == 0 || before[^1].Kind == TokenKind.Semicolon
                        => [.. EffectVerbs, VarPrefix, FactionPrefix, SceneVocabulary.SkillCheck],
                    ExpressionContextKind.Effects => [VarPrefix, FactionPrefix, ScenePrefix, SceneVocabulary.SkillCheck],
                    ExpressionContextKind.Number => [VarPrefix, ScenePrefix, SceneVocabulary.SkillCheck],
                    ExpressionContextKind.Substitution when before.Length == 0 => [VarPrefix, FlagPrefix, FactionPrefix, ItemPrefix, ScenePrefix, ImageCall],
                    ExpressionContextKind.Substitution => [VarPrefix, FlagPrefix, FactionPrefix, ItemPrefix, ScenePrefix],
                    _ => [FlagPrefix, VarPrefix, FactionPrefix, ItemPrefix, ScenePrefix, True, False, Not,
                        SceneVocabulary.SkillCheck]
                };
            }
            if (kinds.Length == 0 && keywords.Length == 0) continue;
            // Numeric references retain a trailing subtraction token; name slots may recover an unfinished hyphen.
            var unfinishedEnd = end;
            while (unfinishedEnd < text.Length && (text[unfinishedEnd] == IdentifierRules.DotSeparator ||
                   text[unfinishedEnd] == IdentifierRules.HyphenSeparator && !kinds.Any(candidate => candidate is
                       AuthoringSymbolKind.Stat or AuthoringSymbolKind.Variable or AuthoringSymbolKind.Faction)))
                unfinishedEnd++;
            if (unfinishedEnd == text.Length) end = unfinishedEnd;
            yield return new(text[start..end], map(start, end - start), kinds, keywords,
                Enumerable.Range(start, end - start + 1).Select(index => map(index, 0).Start).ToArray(),
                end < text.Length && text[end] is Separator or OpenParen ? text[end] : null, null, details);
        }
    }

    private static bool HasSymbol(IReadOnlyList<AuthoringSymbol> symbols, AuthoringSymbolKind kind, Token token)
    {
        for (var index = 0; index < symbols.Count; index++)
            if (symbols[index].Kind == kind && token.Is(symbols[index].Name)) return true;
        return false;
    }

    private static bool IsAgreementReference(TokenBuffer tokens, IReadOnlyList<AuthoringSymbol> symbols)
    {
        var hash = tokens.FindHash();
        if (hash < 0) return false;
        Span<int> reference = stackalloc int[7];
        var count = 0;
        for (var index = 0; index < hash; index++)
        {
            if (tokens[index].Kind is TokenKind.LeftParen or TokenKind.RightParen) continue;
            if (count == reference.Length) return false;
            reference[count++] = index;
        }
        if (count < 3 || tokens[reference[0]].Kind != TokenKind.Identifier ||
            tokens[reference[1]].Kind != TokenKind.Colon ||
            tokens[reference[2]].Kind != TokenKind.Identifier)
            return false;
        var owner = tokens[reference[0]];
        var member = tokens[reference[2]];
        if (member.Is(Inventory) &&
            symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Character && owner.Is(symbol.Name)) &&
            count >= 5 && tokens[reference[3]].Kind == TokenKind.Colon &&
            tokens[reference[4]].Kind == TokenKind.Identifier)
        {
            var item = tokens[reference[4]];
            if (symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Item && item.Is(symbol.Name)))
            {
                if (count == 5) return true;
                return count == 7 && tokens[reference[5]].Kind == TokenKind.Colon &&
                    tokens[reference[6]].Kind == TokenKind.Identifier && tokens[reference[6]].Is(Name);
            }
        }
        if (owner.Is(Item) && count == 3)
            return symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Item && member.Is(symbol.Name));
        if (!member.Is(Pronoun) ||
            !symbols.Any(symbol => symbol.Kind == AuthoringSymbolKind.Character && owner.Is(symbol.Name)))
            return false;
        if (count == 3) return true;
        if (count < 5 || tokens[reference[3]].Kind != TokenKind.Colon) return false;
        var property = tokens[reference[4]];
        if (property.Kind != TokenKind.Identifier) return false;
        if (count == 5) return property.Text is SubjectForm or ObjectForm or ReflexiveForm;
        return count == 7 && property.Is(PossessivePronoun) &&
            tokens[reference[5]].Kind == TokenKind.Colon &&
            tokens[reference[6]] is { Kind: TokenKind.Identifier, Text: Determiner or Independent };
    }
}
