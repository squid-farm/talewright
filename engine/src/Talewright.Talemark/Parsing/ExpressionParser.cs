using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Analysis;

namespace Talewright.Talemark.Parsing;

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
    internal const int MaximumNestingDepth = 256;

    private static readonly string[] Keywords = ["and", "or", "not", "has", "true", "false", "visited"];

    private readonly TokenBuffer _tokens;
    private readonly int _endIndex;
    private readonly int _endOffset;
    private readonly Func<int, SourceLocation> _locate;
    private readonly StorySymbols _symbols;
    private readonly DiagnosticBag _diagnostics;
    private int _pos;
    private int _nestingDepth;

    private ExpressionParser(TokenBuffer tokens, int endOffset, SourceLocation location, StorySymbols symbols,
        DiagnosticBag diagnostics, Func<int, SourceLocation>? locate = null, int? endIndex = null)
    {
        _tokens = tokens;
        _endIndex = endIndex ?? tokens.Count - 1;
        _endOffset = endOffset;
        _locate = locate ?? location.Offset;
        _symbols = symbols;
        _diagnostics = diagnostics;
    }

    private sealed class ParseException : Exception;

    internal static bool ReportStringErrors(string text, Func<int, SourceLocation> locate, DiagnosticBag diagnostics) =>
        ReportStringErrors(Lexer.Tokenize(text), locate, diagnostics);

    internal static bool ReportStringErrors(TokenBuffer tokens, Func<int, SourceLocation> locate, DiagnosticBag diagnostics)
    {
        var invalid = false;
        foreach (var token in tokens)
        {
            if (token.ErrorOffset is not { } offset) continue;
            diagnostics.Error("TW1001", token.Value, locate(offset));
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
                { Kind: TokenKind.Identifier, Text: "set" or "clear" },
                { Kind: TokenKind.Identifier, Text: "flag" },
                { Kind: TokenKind.Colon },
                { Kind: TokenKind.Identifier } flag])
            {
                var name = flag.Value;
                flags.Add(new FlagId(name));
                declaration?.Invoke(name, true, flag.Offset);
            }
            else if (statement is [
                { Kind: TokenKind.Identifier, Text: "var" },
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
                typedDeclaration?.Invoke(new(new(name), tokens.Source[amount[0].Offset..Math.Min(tokens[end].Offset, expressionLength)],
                    numeric, location.Offset(variable.Offset)));
            }

            start = end + 1;
        }
    }

    /// <summary>Parses a boolean condition. Returns null (with diagnostics) on error.</summary>
    public static Expr? ParseCondition(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics,
        SourceLocation? authoringLocation = null, Func<int, SourceLocation>? locate = null) =>
        ParseTyped(text, location, symbols, diagnostics, ExprType.Bool, authoringLocation, locate);

    /// <summary>Parses a numeric expression. Returns null (with diagnostics) on error.</summary>
    public static Expr? ParseNumber(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics) =>
        ParseTyped(text, location, symbols, diagnostics, ExprType.Number);

    internal static Expr? ParseValue(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics)
    {
        var parser = new ExpressionParser(Lexer.Tokenize(text), text.Length, location, symbols, diagnostics);
        try
        {
            var value = parser.ParseSum();
            parser.ExpectEnd();
            return value;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    /// <summary>Parses the inside of <c>{reference#modifier…}</c>.</summary>
    public static SubstitutionTemplate? ParseSubstitution(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics,
        Func<int, SourceLocation>? locate = null)
    {
        var tokens = symbols.Analysis?.Expression(ExpressionContextKind.Substitution, text, location, locate)
            ?? Lexer.Tokenize(text);
        var hashToken = tokens.FindHash();
        var hash = hashToken < 0 ? -1 : tokens[hashToken].Offset;
        var parser = new ExpressionParser(tokens, hash < 0 ? text.Length : hash, location, symbols, diagnostics,
            locate, hashToken < 0 ? null : hashToken);
        try
        {
            var value = parser.ParseOr();
            parser.ExpectEnd();
            var calls = hash < 0 ? [] : ModifierCall.Scan(text, hash, parser._locate, diagnostics);
            if (calls is null)
            {
                return null;
            }

            var modifiers = new List<ITextModifier>();
            foreach (var call in calls)
            {
                if (call.Name == "one-many")
                {
                    if (value is not (PronounRef or ItemNameRef) || call.Argument is null || call.PluralArgument is null)
                    {
                        diagnostics.Error("TW2006",
                            "Use '#one-many(\"singular\", \"plural\")': two quoted strings, singular first, on pronoun or item-name references only.",
                            parser._locate(call.Offset));
                        return null;
                    }

                    modifiers.Add(new OneManyTextModifier(call.Argument, call.PluralArgument));
                    continue;
                }

                if (call.Argument is not null)
                {
                    diagnostics.Error("TW2006", $"Modifier '#{call.Name}' does not take an argument.", parser._locate(call.Offset));
                    return null;
                }

                if (!symbols.Modifiers.TryGet(call.Name, out var modifier))
                {
                    diagnostics.Error("TW2006", $"Unknown modifier '#{call.Name}'. Known: {string.Join(", ", symbols.Modifiers.Names.Select(n => "#" + n))}.", parser._locate(call.Offset));
                    return null;
                }

                modifiers.Add(modifier);
            }

            var isPlural = value switch
            {
                NameRef n => symbols.World.Characters[n.Character].NameAgreement == GrammaticalNumber.Plural,
                PronounRef p => symbols.World.Characters[p.Character].Pronouns.Agreement == GrammaticalNumber.Plural,
                ItemNameRef i => symbols.World.Items[i.Item].NameAgreement == GrammaticalNumber.Plural,
                _ => false,
            };
            return new SubstitutionTemplate(value, modifiers, isPlural);
        }
        catch (ParseException)
        {
            return null;
        }
    }

    internal static readonly string[] PronounProperties = ["subject", "object", "possessive-pronoun", "reflexive"];
    internal static readonly string[] PossessiveProperties = ["determiner", "independent"];

    /// <summary>A modifier call, with a second string supported only for <c>#one-many</c>.</summary>
    private sealed record ModifierCall(string Name, int Offset, string? Argument, string? PluralArgument)
    {
        /// <summary>Scans the modifier chain starting at the first <c>#</c>. Returns null after reporting a diagnostic.</summary>
        public static List<ModifierCall>? Scan(string text, int start, Func<int, SourceLocation> locate, DiagnosticBag diagnostics)
        {
            var calls = new List<ModifierCall>();
            var i = start;
            while (i < text.Length)
            {
                var offset = i;
                i++;
                var nameStart = i;
                while (i < text.Length && text[i] is not ('#' or '('))
                {
                    i++;
                }

                var name = text[nameStart..i].Trim();
                string? argument = null;
                string? pluralArgument = null;
                if (i < text.Length && text[i] == '(')
                {
                    i = SkipSpace(text, i + 1);
                    if (i >= text.Length || text[i] != '"')
                    {
                        return Error($"Write the argument to '#{name}' in double quotes, such as #{name}(\"value\").", i);
                    }

                    var literal = QuotedStringSyntax.Read(text, i, text.Length);
                    if (literal.Error is { } error)
                    {
                        return Error(error, literal.ErrorOffset);
                    }

                    argument = literal.Value;
                    i = SkipSpace(text, literal.End);
                    if (name == "one-many")
                    {
                        if (i >= text.Length || text[i] != ',')
                        {
                            return Error("'#one-many' requires exactly two quoted strings: #one-many(\"singular\", \"plural\").", i);
                        }

                        i = SkipSpace(text, i + 1);
                        if (i >= text.Length || text[i] != '"')
                        {
                            return Error("Write the plural (second) argument to '#one-many' in double quotes.", i);
                        }

                        literal = QuotedStringSyntax.Read(text, i, text.Length);
                        if (literal.Error is { } pluralError)
                        {
                            return Error(pluralError, literal.ErrorOffset);
                        }

                        pluralArgument = literal.Value;
                        i = SkipSpace(text, literal.End);
                    }
                    if (i >= text.Length || text[i] != ')')
                    {
                        return Error($"Expected ')' after the argument to '#{name}'.", i);
                    }

                    i = SkipSpace(text, i + 1);
                    if (i < text.Length && text[i] != '#')
                    {
                        return Error($"Expected '#' or the end of the substitution after '#{name}(…)'.", i);
                    }
                }

                calls.Add(new ModifierCall(name, offset, argument, pluralArgument));
            }

            return calls;

            List<ModifierCall>? Error(string message, int at)
            {
                diagnostics.Error("TW2006", message, locate(Math.Min(at, text.Length)));
                return null;
            }
        }

        private static int SkipSpace(string text, int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            return i;
        }
    }

    /// <summary>Parses <c>;</c>-separated effects. Returns null (with diagnostics) on error.</summary>
    public static List<Effect>? ParseEffects(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics)
    {
        var tokens = symbols.Analysis?.Expression(ExpressionContextKind.Effects, text, location)
            ?? Lexer.Tokenize(text);
        if (ReportStringErrors(tokens, location.Offset, diagnostics)) return null;
        var parser = new ExpressionParser(tokens, text.Length, location, symbols, diagnostics);
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

    private static Expr? ParseTyped(string text, SourceLocation location, StorySymbols symbols, DiagnosticBag diagnostics, ExprType type,
        SourceLocation? authoringLocation = null, Func<int, SourceLocation>? locate = null)
    {
        var tokens = symbols.Analysis?.Expression(type == ExprType.Bool ? ExpressionContextKind.Condition : ExpressionContextKind.Number,
            text, authoringLocation ?? location, locate) ?? Lexer.Tokenize(text);
        var parser = new ExpressionParser(tokens, text.Length, location, symbols, diagnostics, locate);
        try
        {
            var expr = parser.ParseOr();
            parser.ExpectEnd();
            if (expr.Type != type)
            {
                parser.Fail("TW2003", $"Expected a {Describe(type)} expression but found a {Describe(expr.Type)}.", parser.TokenAt(0).Offset);
            }

            return expr;
        }
        catch (ParseException)
        {
            return null;
        }
    }

    private Token Peek
    {
        get
        {
            var token = TokenAt(_pos);
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
        _pos++;
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

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string code, string message, int offset)
    {
        _diagnostics.Error(code, message, _locate(offset));
        throw new ParseException();
    }

    private Expr ParseNested(Token opener, Func<Expr> parse)
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
            var operand = ParseNested(op, ParseNot);
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
            if (TryReadMinimumMagnitude())
            {
                return new NumberLiteral(int.MinValue);
            }

            var operand = ParseNested(op, ParseUnary);
            if (operand.Type != ExprType.Number)
            {
                Fail("TW2003", "'-' needs a number.", op.Offset);
            }

            return operand is NumberLiteral n
                ? new NumberLiteral(n.Value == int.MinValue ? int.MaxValue : -n.Value)
                : new UnaryExpr(UnaryOp.Negate, operand);
        }

        return ParsePrimary();
    }

    private bool TryReadMinimumMagnitude()
    {
        var position = _pos;
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

        _pos = position;
        return true;
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
                return new TextLiteral(token.Value);
            case TokenKind.NamedPredicate:
            {
                Next();
                var name = token.Value;
                if (!_symbols.Predicates.Contains(name))
                {
                    Fail("TW2005", $"Named predicate '@{token.Text}' is not registered. Named predicates are provided by C# story projects.", token.Offset);
                }

                return new NamedPredicate(name);
            }
            case TokenKind.LeftParen:
                var open = Next();
                return ParseNested(open, () =>
                {
                    var inner = ParseOr();
                    Expect(TokenKind.RightParen, "')'");
                    return inner;
                });
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
                var sceneId = new SceneId(scene.Value);
                if (!_symbols.Scenes.Contains(sceneId))
                {
                    Fail("TW2002", $"Unknown scene '{scene.Text}'.", scene.Offset);
                }

                return new Visited(sceneId);
        }

        if (Peek.Kind == TokenKind.Colon)
        {
            Next();
            var path = Expect(TokenKind.Identifier, $"a name after '{token.Text}:'");
            Token? member = null;
            if (Peek.Kind == TokenKind.Colon)
            {
                Next();
                member = Expect(TokenKind.Identifier, $"a property after '{token.Text}:{path.Text}:'");
            }

            return ResolveReference(token, path, member);
        }

        if (IsKeyword("has"))
        {
            Next();
            var who = Character(token);
            var item = Expect(TokenKind.Identifier, "an item id after 'has'");
            return new HasItem(who, Item(item));
        }

        if (_symbols.World.Ruleset.FindTier(token.Value) is { } tier)
        {
            return new TierLiteral(tier);
        }

        return Fail<Expr>("TW2001", $"Unknown name '{token.Text}'. References look like 'player:dex', 'faction:{{id}}', 'flag:{{id}}' or 'var:{{id}}'.", token.Offset);
    }

    private Expr ResolveReference(Token ns, Token path, Token? member)
    {
        switch (ns.Text)
        {
            case "faction":
            {
                var faction = new FactionId(path.Value);
                if (!_symbols.World.Factions.ContainsKey(faction))
                {
                    var dot = path.Text.LastIndexOf('.');
                    var oldMember = dot < 0 ? string.Empty : faction.Value[(dot + 1)..];
                    var oldId = dot < 0 ? string.Empty : faction.Value[..dot];
                    if (member is null && oldMember is "name" or "tier")
                    {
                        Fail("TW2001",
                            $"Unknown faction '{path.Text}'. Dotted property access is no longer supported; use 'faction:{oldId}:{oldMember}' for its {oldMember} property.",
                            path.Offset + dot);
                    }

                    Fail("TW2001", $"Unknown faction '{path.Text}'.", path.Offset);
                }

                if (member is null)
                {
                    return new StandingRef(faction);
                }

                return member.Value.Text switch
                {
                    "tier" => new StandingTierRef(faction),
                    "name" => new FactionNameRef(faction),
                    _ => Fail<Expr>("TW2001", $"Unknown faction property '{member.Value.Text}'. Use 'faction:{path.Text}:tier' or 'faction:{path.Text}:name'.", member.Value.Offset),
                };
            }

            case "item":
                if (member is not null)
                {
                    Fail("TW2001", $"Item names are referenced as 'item:{path.Text}'; '.' only groups words inside names.", member.Value.Offset);
                }

                return new ItemNameRef(Item(path));
            case "flag":
            {
                if (member is not null)
                {
                    Fail("TW2001", $"Flag references do not have properties; use 'flag:{path.Text}'.", member.Value.Offset);
                }

                var flag = new FlagId(path.Value);
                if (!_symbols.Flags.Contains(flag))
                {
                    Fail("TW2004", $"Flag '{path.Text}' is never set by any effect and is not declared in story.talewright.yaml.", path.Offset);
                }

                return new FlagRef(flag);
            }
            case "var":
            {
                if (member is not null)
                {
                    Fail("TW2001", $"Variable references do not have properties; use 'var:{path.Text}'.", member.Value.Offset);
                }

                var variable = new VarId(path.Value);
                if (_symbols.UnresolvedVariables.Contains(variable))
                {
                    Fail("TW2007", $"Cannot infer the type of variable '{path.Text}'. Declare an initial value under 'vars' in story.talewright.yaml.", path.Offset);
                }
                if (!_symbols.Variables.Contains(variable))
                {
                    Fail("TW2004", $"Variable '{path.Text}' is never assigned by any effect and is not declared in story.talewright.yaml.", path.Offset);
                }

                return _symbols.World.TextVariables.ContainsKey(variable)
                    ? new TextVarRef(variable)
                    : new VarRef(variable);
            }
        }

        var character = Character(ns);
        if (member is not null)
        {
            if (!path.Is("pronoun"))
            {
                Fail("TW2001", $"Unknown character property '{path.Text}:{member.Value.Text}'.", member.Value.Offset);
            }

            return new PronounRef(character, ParsePronounForm(member.Value));
        }

        return path.Text switch
        {
            "first-name" => new NameRef(character, NamePart.First),
            "last-name" => new NameRef(character, NamePart.Last),
            "name" => new NameRef(character, NamePart.Full),
            "pronoun" => new PronounRef(character, PronounForm.Subject),
            _ => ResolveStat(character, path, ns),
        };
    }

    private Expr ResolveStat(EntityId character, Token path, Token ns)
    {
        var stat = new StatId(path.Value);
        return _symbols.World.Ruleset.HasStat(stat)
            ? new StatRef(character, stat)
            : Fail<Expr>("TW2001", $"Unknown property '{path.Text}' on '{ns.Text}'.", path.Offset);
    }

    private PronounForm ParsePronounForm(Token property)
    {
        if (property.Is("possessive-pronoun"))
        {
            Expect(TokenKind.Colon, "':' after 'possessive-pronoun'");
            var form = Expect(TokenKind.Identifier, "a possessive pronoun property");
            return form.Text switch
            {
                "determiner" => PronounForm.Possessive,
                "independent" => PronounForm.PossessivePronoun,
                _ => Fail<PronounForm>("TW2001",
                    $"Unknown possessive pronoun property '{form.Text}'. Use 'determiner' or 'independent'.", form.Offset),
            };
        }
        return property.Text switch
        {
            "subject" => PronounForm.Subject,
            "object" => PronounForm.Object,
            "reflexive" => PronounForm.Reflexive,
            _ => Fail<PronounForm>("TW2001",
                $"Unknown pronoun property '{property.Text}'. Use {string.Join(", ", PronounProperties)}.", property.Offset),
        };
    }

    private EntityId Character(Token token)
    {
        var id = new EntityId(token.Value);
        if (!_symbols.World.Characters.ContainsKey(id))
        {
            Fail("TW2001", $"Unknown character '{token.Text}'.", token.Offset);
        }

        return id;
    }

    private ItemId Item(Token token)
    {
        var id = new ItemId(token.Value);
        if (!_symbols.World.Items.ContainsKey(id))
        {
            Fail("TW2001", $"Unknown item '{token.Text}'.", token.Offset);
        }

        return id;
    }

    private StatId Stat(Token token)
    {
        var id = new StatId(token.Value);
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
                if (!ns.Is("flag"))
                {
                    Fail("TW1003", $"'{first.Text}' only works on flags: {first.Text} flag:<id>.", ns.Offset);
                }

                Expect(TokenKind.Colon, "':'");
                var flag = Expect(TokenKind.Identifier, "a flag id");
                return new SetFlag(new FlagId(flag.Value), first.Is("set"));
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

                return first.Is("give") ? new GiveItem(who, item, quantity) : new TakeItem(who, item, quantity);
            }
        }

        Expect(TokenKind.Colon, $"':' (effects look like 'player:hp -= 3', 'set flag:x', 'give player rope')");
        var target = Expect(TokenKind.Identifier, "a name");
        var targetName = target.Value;
        if (first.Is("var") && _symbols.UnresolvedVariables.Contains(new VarId(targetName)))
        {
            Fail("TW2007", $"Cannot infer the type of variable '{target.Text}'. Declare an initial value under 'vars' in story.talewright.yaml.", target.Offset);
        }
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
        if (first.Is("var") && _symbols.World.TextVariables.ContainsKey(new VarId(targetName)))
        {
            if (op != AssignOp.Set)
            {
                Fail("TW2003", $"Text variable '{target.Text}' supports '=' only, not '{opToken.Text}'.", opToken.Offset);
            }
            if (amount.Type != ExprType.Text)
            {
                Fail("TW2003", $"Text variable '{target.Text}' needs text, not {Describe(amount.Type)}.", amountStart);
            }
            return new SetTextVariable(new VarId(targetName), amount);
        }
        if (amount.Type != ExprType.Number)
        {
            Fail("TW2003", first.Is("var")
                ? $"Variable '{target.Text}' has a fixed integer type and needs a number, not {Describe(amount.Type)}."
                : "Only numbers can be assigned to stats or faction standings.", amountStart);
        }

        return first.Text switch
        {
            "faction" => _symbols.World.Factions.ContainsKey(new FactionId(targetName))
                ? new ChangeStanding(new FactionId(targetName), op, amount)
                : Fail<Effect>("TW2001", $"Unknown faction '{target.Text}'.", target.Offset),
            "var" => new ChangeVariable(new VarId(targetName), op, amount),
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
