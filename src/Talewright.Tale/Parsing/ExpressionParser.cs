using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Tale.Diagnostics;

namespace Talewright.Tale.Parsing;

/// <summary>
/// Recursive-descent parser for expressions, substitutions and effects. Resolves every reference against
/// <see cref="StorySymbols"/> and type-checks as it goes, so the result is a typed tree or a diagnostic.
/// </summary>
/// <remarks>
/// Grammar (lowest to highest precedence):
/// <code>
/// or      := and ('or' and)*
/// and     := not ('and' not)*
/// not     := 'not' not | compare
/// compare := sum (('=='|'!='|'&lt;'|'&lt;='|'&gt;'|'&gt;=') sum)?
/// sum     := term (('+'|'-') term)*
/// term    := unary (('*'|'/') unary)*
/// unary   := '-' unary | primary
/// primary := number | "text" | true | false | @Name | '(' or ')' | visited(scene.id)
///          | who 'has' item | ns ':' path | tier-name
/// </code>
/// </remarks>
internal sealed class ExpressionParser
{
    private static readonly string[] Keywords = ["and", "or", "not", "has", "true", "false", "visited"];

    private readonly List<Token> _tokens;
    private readonly SourceLocation _location;
    private readonly StorySymbols _symbols;
    private readonly DiagnosticBag _diagnostics;
    private int _pos;

    private ExpressionParser(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics)
    {
        _tokens = Lexer.Tokenize(text);
        _location = location;
        _symbols = symbols;
        _diagnostics = diagnostics;
    }

    private sealed class ParseException : Exception;

    internal static void CollectDeclarations(string text, ISet<FlagId> flags, IDictionary<VarId, int> variables)
    {
        var tokens = Lexer.Tokenize(text);
        for (var start = 0; start < tokens.Count - 1;)
        {
            var end = start;
            while (tokens[end].Kind is not (TokenKind.Semicolon or TokenKind.End))
            {
                end++;
            }

            var statement = tokens.GetRange(start, end - start).ToArray();
            if (statement is [
                { Kind: TokenKind.Identifier, Text: "set" or "clear" },
                { Kind: TokenKind.Identifier, Text: "flag" },
                { Kind: TokenKind.Colon },
                { Kind: TokenKind.Identifier } flag])
            {
                flags.Add(new FlagId(flag.Text));
            }
            else if (statement is [
                { Kind: TokenKind.Identifier, Text: "var" },
                { Kind: TokenKind.Colon },
                { Kind: TokenKind.Identifier } variable,
                { Kind: TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign },
                .. var amount] && amount.Length > 0)
            {
                variables.TryAdd(new VarId(variable.Text), 0);
            }

            start = end + 1;
        }
    }

    /// <summary>Parses a boolean condition. Returns null (with diagnostics) on error.</summary>
    public static Expr? ParseCondition(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics) =>
        ParseTyped(text, location, symbols, diagnostics, ExprType.Bool);

    /// <summary>Parses a numeric expression. Returns null (with diagnostics) on error.</summary>
    public static Expr? ParseNumber(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics) =>
        ParseTyped(text, location, symbols, diagnostics, ExprType.Number);

    /// <summary>Parses the inside of <c>{reference#modifier…}</c>.</summary>
    public static SubstitutionTemplate? ParseSubstitution(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics)
    {
        var parts = text.Split('#');
        var parser = new ExpressionParser(parts[0], location, symbols, diagnostics);
        try
        {
            var value = parser.ParseOr();
            parser.ExpectEnd();
            var modifiers = new List<ITextModifier>();
            var offset = parts[0].Length;
            foreach (var name in parts.Skip(1))
            {
                if (!symbols.Modifiers.TryGet(name.Trim(), out var modifier))
                {
                    diagnostics.Error("TW2006", $"Unknown modifier '#{name.Trim()}'. Known: {string.Join(", ", symbols.Modifiers.Names.Select(n => "#" + n))}.", location.Offset(offset));
                    return null;
                }

                modifiers.Add(modifier);
                offset += name.Length + 1;
            }

            var isPlural = value switch
            {
                NameRef n => symbols.World.Characters[n.Character].IsPluralName,
                PronounRef p => symbols.World.Characters[p.Character].Pronouns.IsPlural,
                _ => false,
            };
            return new SubstitutionTemplate(value, modifiers, isPlural);
        }
        catch (ParseException)
        {
            return null;
        }
    }

    /// <summary>Parses <c>;</c>-separated effects. Returns null (with diagnostics) on error.</summary>
    public static List<Effect>? ParseEffects(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics)
    {
        var parser = new ExpressionParser(text, location, symbols, diagnostics);
        try
        {
            var effects = new List<Effect>();
            while (parser.Peek.Kind != TokenKind.End)
            {
                effects.Add(parser.ParseEffect());
                if (parser.Peek.Kind == TokenKind.Semicolon)
                {
                    parser._pos++;
                }
                else
                {
                    parser.ExpectEnd();
                }
            }

            return effects;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    private static Expr? ParseTyped(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics, ExprType type)
    {
        var parser = new ExpressionParser(text, location, symbols, diagnostics);
        try
        {
            var expr = parser.ParseOr();
            parser.ExpectEnd();
            if (expr.Type != type)
            {
                parser.Fail("TW2003", $"Expected a {Describe(type)} expression but found a {Describe(expr.Type)}.", parser._tokens[0].Offset);
            }

            return expr;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    private Token Peek => _tokens[_pos];

    private Token Next() => _tokens[_pos++];

    private bool IsKeyword(string keyword) => Peek.Kind == TokenKind.Identifier && Peek.Text == keyword;

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
            Fail("TW1001", $"Expected {what} but found '{(Peek.Kind == TokenKind.End ? "end of expression" : Peek.Text)}'.", Peek.Offset);
        }

        return Next();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string code, string message, int offset)
    {
        _diagnostics.Error(code, message, _location.Offset(offset));
        throw new ParseException();
    }

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (IsKeyword("or"))
        {
            var op = Next();
            left = Logical(BinaryOp.Or, left, ParseAnd(), op.Offset);
        }

        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (IsKeyword("and"))
        {
            var op = Next();
            left = Logical(BinaryOp.And, left, ParseNot(), op.Offset);
        }

        return left;
    }

    private Expr Logical(BinaryOp op, Expr left, Expr right, int offset)
    {
        if (left.Type != ExprType.Bool || right.Type != ExprType.Bool)
        {
            Fail("TW2003", $"'{op.ToString().ToLowerInvariant()}' needs true/false conditions on both sides.", offset);
        }

        return new BinaryExpr(op, left, right);
    }

    private Expr ParseNot()
    {
        if (IsKeyword("not"))
        {
            var op = Next();
            var operand = ParseNot();
            if (operand.Type != ExprType.Bool)
            {
                Fail("TW2003", "'not' needs a true/false condition.", op.Offset);
            }

            return new UnaryExpr(UnaryOp.Not, operand);
        }

        return ParseComparison();
    }

    private Expr ParseComparison()
    {
        var left = ParseSum();
        var op = Peek.Kind switch
        {
            TokenKind.Equal => BinaryOp.Equal,
            TokenKind.NotEqual => BinaryOp.NotEqual,
            TokenKind.Less => BinaryOp.Less,
            TokenKind.LessOrEqual => BinaryOp.LessOrEqual,
            TokenKind.Greater => BinaryOp.Greater,
            TokenKind.GreaterOrEqual => BinaryOp.GreaterOrEqual,
            _ => (BinaryOp?)null,
        };
        if (op is null)
        {
            return left;
        }

        var opToken = Next();
        var right = ParseSum();

        // faction:guild >= friendly  →  compare tiers rather than numbers.
        if (left.Type == ExprType.Tier && right is StandingRef rs)
        {
            right = new StandingTierRef(rs.Faction);
        }
        else if (right.Type == ExprType.Tier && left is StandingRef ls)
        {
            left = new StandingTierRef(ls.Faction);
        }

        if (left.Type != right.Type)
        {
            Fail("TW2003", $"Cannot compare a {Describe(left.Type)} with a {Describe(right.Type)}.", opToken.Offset);
        }

        if (left.Type is ExprType.Bool or ExprType.Text && op is not (BinaryOp.Equal or BinaryOp.NotEqual))
        {
            Fail("TW2003", $"Only == and != can compare {Describe(left.Type)} values.", opToken.Offset);
        }

        return new BinaryExpr(op.Value, left, right);
    }

    private Expr ParseSum()
    {
        var left = ParseTerm();
        while (Peek.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = Next();
            left = Arithmetic(op.Kind == TokenKind.Plus ? BinaryOp.Add : BinaryOp.Subtract, left, ParseTerm(), op.Offset);
        }

        return left;
    }

    private Expr ParseTerm()
    {
        var left = ParseUnary();
        while (Peek.Kind is TokenKind.Star or TokenKind.Slash)
        {
            var op = Next();
            left = Arithmetic(op.Kind == TokenKind.Star ? BinaryOp.Multiply : BinaryOp.Divide, left, ParseUnary(), op.Offset);
        }

        return left;
    }

    private Expr Arithmetic(BinaryOp op, Expr left, Expr right, int offset)
    {
        if (left.Type != ExprType.Number || right.Type != ExprType.Number)
        {
            Fail("TW2003", "Arithmetic needs numbers on both sides.", offset);
        }

        return new BinaryExpr(op, left, right);
    }

    private Expr ParseUnary()
    {
        if (Peek.Kind == TokenKind.Minus)
        {
            var op = Next();
            var operand = ParseUnary();
            if (operand.Type != ExprType.Number)
            {
                Fail("TW2003", "'-' needs a number.", op.Offset);
            }

            return operand is NumberLiteral n ? new NumberLiteral(-n.Value) : new UnaryExpr(UnaryOp.Negate, operand);
        }

        return ParsePrimary();
    }

    private Expr ParsePrimary()
    {
        var token = Peek;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Next();
                return int.TryParse(token.Text, out var n) ? new NumberLiteral(n) : Fail<Expr>("TW1002", $"Number '{token.Text}' is too large.", token.Offset);
            case TokenKind.String:
                Next();
                return new TextLiteral(token.Text);
            case TokenKind.NamedPredicate:
                Next();
                if (!_symbols.Predicates.Contains(token.Text))
                {
                    Fail("TW2005", $"Named predicate '@{token.Text}' is not registered. Named predicates are provided by C# story projects.", token.Offset);
                }

                return new NamedPredicate(token.Text);
            case TokenKind.LeftParen:
                Next();
                var inner = ParseOr();
                Expect(TokenKind.RightParen, "')'");
                return inner;
            case TokenKind.Identifier:
                return ParseIdentifier();
            case TokenKind.Error:
                return Fail<Expr>("TW1001", $"Unexpected '{token.Text}'.", token.Offset);
            default:
                return Fail<Expr>("TW1001", token.Kind == TokenKind.End ? "Expression is incomplete." : $"Unexpected '{token.Text}'.", token.Offset);
        }
    }

    private T Fail<T>(string code, string message, int offset)
    {
        Fail(code, message, offset);
        return default!;
    }

    private Expr ParseIdentifier()
    {
        var token = Next();
        switch (token.Text)
        {
            case "true":
                return new BoolLiteral(true);
            case "false":
                return new BoolLiteral(false);
            case "visited":
                Expect(TokenKind.LeftParen, "'(' after visited");
                var scene = Expect(TokenKind.Identifier, "a scene id");
                Expect(TokenKind.RightParen, "')'");
                if (!_symbols.Scenes.Contains(new SceneId(scene.Text)))
                {
                    Fail("TW2002", $"Unknown scene '{scene.Text}'.", scene.Offset);
                }

                return new Visited(new SceneId(scene.Text));
        }

        if (Peek.Kind == TokenKind.Colon)
        {
            Next();
            var path = Expect(TokenKind.Identifier, $"a name after '{token.Text}:'");
            return ResolveReference(token, path);
        }

        if (IsKeyword("has"))
        {
            Next();
            var who = Character(token);
            var item = Expect(TokenKind.Identifier, "an item id after 'has'");
            return new HasItem(who, Item(item));
        }

        if (_symbols.World.Ruleset.FindTier(token.Text) is { } tier)
        {
            return new TierLiteral(tier);
        }

        return Fail<Expr>("TW2001", $"Unknown name '{token.Text}'. References look like 'player:dex', 'faction:{{id}}', 'flag:{{id}}' or 'var:{{id}}'.", token.Offset);
    }

    private Expr ResolveReference(Token ns, Token path)
    {
        switch (ns.Text)
        {
            case "faction":
            {
                var (id, member) = SplitMember(path.Text);
                var faction = new FactionId(id);
                if (!_symbols.World.Factions.ContainsKey(faction))
                {
                    Fail("TW2001", $"Unknown faction '{id}'.", path.Offset);
                }

                return member switch
                {
                    null => new StandingRef(faction),
                    "tier" => new StandingTierRef(faction),
                    "name" => new FactionNameRef(faction),
                    _ => Fail<Expr>("TW2001", $"Unknown faction property '.{member}'. Use '.tier' or '.name'.", path.Offset),
                };
            }

            case "item":
                return new ItemNameRef(Item(path));
            case "flag":
                if (!_symbols.Flags.Contains(new FlagId(path.Text)))
                {
                    Fail("TW2004", $"Flag '{path.Text}' is never set by any effect and is not declared in story.yaml.", path.Offset);
                }

                return new FlagRef(new FlagId(path.Text));
            case "var":
                if (!_symbols.Variables.Contains(new VarId(path.Text)))
                {
                    Fail("TW2004", $"Variable '{path.Text}' is never assigned by any effect and is not declared in story.yaml.", path.Offset);
                }

                return new VarRef(new VarId(path.Text));
        }

        var character = Character(ns);
        return path.Text switch
        {
            "first-name" => new NameRef(character, NamePart.First),
            "last-name" => new NameRef(character, NamePart.Last),
            "name" => new NameRef(character, NamePart.Full),
            "pronoun.subject" => new PronounRef(character, PronounForm.Subject),
            "pronoun.object" => new PronounRef(character, PronounForm.Object),
            "pronoun.possessive" => new PronounRef(character, PronounForm.Possessive),
            "pronoun.possessive-pronoun" => new PronounRef(character, PronounForm.PossessivePronoun),
            "pronoun.reflexive" => new PronounRef(character, PronounForm.Reflexive),
            _ when _symbols.World.Ruleset.HasStat(new StatId(path.Text)) => new StatRef(character, new StatId(path.Text)),
            _ => Fail<Expr>("TW2001", $"Unknown property '{path.Text}' on '{ns.Text}'. Expected a stat, first-name, last-name, name or pronoun.<form>.", path.Offset),
        };
    }

    private static (string Id, string? Member) SplitMember(string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            return (path, null);
        }

        var member = path[(dot + 1)..];
        return member is "tier" or "name" ? (path[..dot], member) : (path, null);
    }

    private EntityId Character(Token token)
    {
        var id = new EntityId(token.Text);
        if (!_symbols.World.Characters.ContainsKey(id))
        {
            Fail("TW2001", $"Unknown character '{token.Text}'.", token.Offset);
        }

        return id;
    }

    private ItemId Item(Token token)
    {
        var id = new ItemId(token.Text);
        if (!_symbols.World.Items.ContainsKey(id))
        {
            Fail("TW2001", $"Unknown item '{token.Text}'.", token.Offset);
        }

        return id;
    }

    private StatId Stat(Token token)
    {
        var id = new StatId(token.Text);
        if (!_symbols.World.Ruleset.HasStat(id))
        {
            Fail("TW2001", $"Unknown stat '{token.Text}' (not in ruleset '{_symbols.World.Ruleset.Id}').", token.Offset);
        }

        return id;
    }

    private Effect ParseEffect()
    {
        var first = Expect(TokenKind.Identifier, "an effect");
        switch (first.Text)
        {
            case "set" or "clear":
            {
                var ns = Expect(TokenKind.Identifier, "'flag'");
                if (ns.Text != "flag")
                {
                    Fail("TW1003", $"'{first.Text}' only works on flags: {first.Text} flag:<id>.", ns.Offset);
                }

                Expect(TokenKind.Colon, "':'");
                var flag = Expect(TokenKind.Identifier, "a flag id");
                return new SetFlag(new FlagId(flag.Text), first.Text == "set");
            }

            case "give" or "take":
            {
                var who = Character(Expect(TokenKind.Identifier, "a character id"));
                var item = Item(Expect(TokenKind.Identifier, "an item id"));
                Expr quantity = new NumberLiteral(1);
                if (Peek.Kind is not (TokenKind.End or TokenKind.Semicolon))
                {
                    var start = Peek.Offset;
                    quantity = ParseSum();
                    if (quantity.Type != ExprType.Number)
                    {
                        Fail("TW2003", "Item quantity must be a number.", start);
                    }
                }

                return first.Text == "give" ? new GiveItem(who, item, quantity) : new TakeItem(who, item, quantity);
            }
        }

        Expect(TokenKind.Colon, $"':' (effects look like 'player:hp -= 3', 'set flag:x', 'give player rope')");
        var target = Expect(TokenKind.Identifier, "a name");
        var opToken = Next();
        var op = opToken.Kind switch
        {
            TokenKind.Assign => AssignOp.Set,
            TokenKind.PlusAssign => AssignOp.Add,
            TokenKind.MinusAssign => AssignOp.Subtract,
            _ => Fail<AssignOp>("TW1003", $"Expected '=', '+=' or '-=' but found '{opToken.Text}'.", opToken.Offset),
        };
        var amountStart = Peek.Offset;
        var amount = ParseSum();
        if (amount.Type != ExprType.Number)
        {
            Fail("TW2003", "Only numbers can be assigned.", amountStart);
        }

        return first.Text switch
        {
            "faction" => _symbols.World.Factions.ContainsKey(new FactionId(target.Text))
                ? new ChangeStanding(new FactionId(target.Text), op, amount)
                : Fail<Effect>("TW2001", $"Unknown faction '{target.Text}'.", target.Offset),
            "var" => new ChangeVariable(new VarId(target.Text), op, amount),
            "flag" => Fail<Effect>("TW1003", "Use 'set flag:x' or 'clear flag:x' for flags.", first.Offset),
            _ => new ChangeStat(Character(first), Stat(target), op, amount),
        };
    }

    private static string Describe(ExprType type) => type switch
    {
        ExprType.Bool => "true/false",
        ExprType.Number => "number",
        ExprType.Text => "text",
        _ => "reputation tier",
    };

    /// <summary>True for words that can't be used as ids.</summary>
    public static bool IsReserved(string id) => Keywords.Contains(id) || id is "faction" or "item" or "flag" or "var" or "END";
}
