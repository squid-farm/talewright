using System.Diagnostics.CodeAnalysis;
using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Parsing;

internal sealed class ExpressionBinder
{
    private readonly TokenBuffer _tokens;
    private readonly Func<int, SourceLocation> _locate;
    private readonly StorySymbols _symbols;
    private readonly DiagnosticBag _diagnostics;

    private ExpressionBinder(TokenBuffer tokens, Func<int, SourceLocation> locate, StorySymbols symbols,
        DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _locate = locate;
        _symbols = symbols;
        _diagnostics = diagnostics;
    }

    private sealed class BindingException : Exception;

    internal static Expr? Bind(ExpressionSyntaxTree syntax, StorySymbols symbols, DiagnosticBag diagnostics,
        Func<int, SourceLocation> locate, ExprType? expectedType = null)
    {
        if (syntax.HasErrors) return null;
        var binder = new ExpressionBinder(syntax.Tokens, locate, symbols, diagnostics);
        try
        {
            var value = binder.Bind(syntax.Root);
            if (expectedType is { } expected && value.Type != expected)
            {
                binder.Fail("TW2003",
                    $"Expected a {Describe(expected)} expression but found a {Describe(value.Type)}.",
                    syntax.Root.Span.Start);
            }

            return value;
        }
        catch (BindingException)
        {
            return null;
        }
    }

    internal static SubstitutionTemplate? Bind(SubstitutionSyntaxTree syntax, StorySymbols symbols,
        DiagnosticBag diagnostics, Func<int, SourceLocation> locate)
    {
        if (syntax.HasErrors) return null;
        var binder = new ExpressionBinder(syntax.Tokens, locate, symbols, diagnostics);
        try
        {
            var value = binder.Bind(syntax.Value);
            var modifiers = new List<ITextModifier>();
            foreach (var call in syntax.Modifiers)
            {
                if (call.Name == "one-many")
                {
                    if (value is not (PronounRef or ItemNameRef) || call.Argument is null || call.PluralArgument is null)
                    {
                        binder.Fail("TW2006",
                            "Use '#one-many(\"singular\", \"plural\")': two quoted strings, singular first, on pronoun or item-name references only.",
                            call.Offset);
                    }

                    modifiers.Add(new OneManyTextModifier(call.Argument, call.PluralArgument));
                    continue;
                }

                if (call.Argument is not null)
                {
                    binder.Fail("TW2006", $"Modifier '#{call.Name}' does not take an argument.", call.Offset);
                }

                if (!symbols.Modifiers.TryGet(call.Name, out var modifier))
                {
                    binder.Fail("TW2006",
                        $"Unknown modifier '#{call.Name}'. Known: {string.Join(", ", symbols.Modifiers.Names.Select(name => "#" + name))}.",
                        call.Offset);
                }

                modifiers.Add(modifier);
            }

            var isPlural = value switch
            {
                NameRef name => symbols.World.Characters[name.Character].NameAgreement == GrammaticalNumber.Plural,
                PronounRef pronoun => symbols.World.Characters[pronoun.Character].Pronouns.Agreement == GrammaticalNumber.Plural,
                ItemNameRef item => symbols.World.Items[item.Item].NameAgreement == GrammaticalNumber.Plural,
                _ => false,
            };
            return new SubstitutionTemplate(value, modifiers, isPlural);
        }
        catch (BindingException)
        {
            return null;
        }
    }

    internal static List<Effect>? Bind(EffectsSyntaxTree syntax, StorySymbols symbols, DiagnosticBag diagnostics,
        Func<int, SourceLocation> locate)
    {
        if (syntax.HasErrors) return null;
        var binder = new ExpressionBinder(syntax.Tokens, locate, symbols, diagnostics);
        try
        {
            var effects = new List<Effect>(syntax.Effects.Length);
            foreach (var effect in syntax.Effects)
            {
                effects.Add(binder.Bind(effect));
            }

            return effects;
        }
        catch (BindingException)
        {
            return null;
        }
    }

    private Expr Bind(ExpressionNode node) => node switch
    {
        ErrorExpressionNode => Fail<Expr>("TW1001", "Expression is incomplete.", node.Span.Start),
        NumberExpressionNode number => new NumberLiteral(number.Value),
        TextExpressionNode text => new TextLiteral(text.Value),
        BooleanExpressionNode boolean => new BoolLiteral(boolean.Value),
        NameExpressionNode name => BindName(name),
        NamedPredicateExpressionNode predicate => BindPredicate(predicate),
        VisitedExpressionNode visited => BindVisited(visited),
        ParenthesizedExpressionNode parenthesized => Bind(parenthesized.Inner),
        ReferenceExpressionNode reference => BindReference(reference),
        HasItemExpressionNode hasItem => new HasItem(Character(hasItem.Character), Item(hasItem.Item)),
        UnaryExpressionNode unary => BindUnary(unary),
        BinaryExpressionNode binary => BindBinary(binary),
        _ => throw new InvalidOperationException($"Unknown expression syntax node '{node.GetType().Name}'."),
    };

    private Expr BindName(NameExpressionNode name)
    {
        var value = Text(name.Name);
        if (_symbols.World.Ruleset.FindTier(value) is { } tier)
        {
            return new TierLiteral(tier);
        }

        return Fail<Expr>("TW2001",
            $"Unknown name '{value}'. References look like 'player:dex', 'faction:{{id}}', 'flag:{{id}}' or 'var:{{id}}'.",
            name.Name.Start);
    }

    private Expr BindPredicate(NamedPredicateExpressionNode predicate)
    {
        var name = Text(predicate.Name);
        if (!_symbols.Predicates.Contains(name))
        {
            Fail("TW2005",
                $"Named predicate '@{name}' is not registered. Named predicates are provided by C# story projects.",
                predicate.Span.Start);
        }

        return new NamedPredicate(name);
    }

    private Expr BindVisited(VisitedExpressionNode visited)
    {
        var name = Text(visited.Scene);
        var scene = new SceneId(name);
        if (!_symbols.Scenes.Contains(scene))
        {
            Fail("TW2002", $"Unknown scene '{name}'.", visited.Scene.Start);
        }

        return new Visited(scene);
    }

    private Expr BindReference(ReferenceExpressionNode reference)
    {
        var ns = Text(reference.Namespace);
        var path = Text(reference.Name);
        var member = reference.Member is { } memberSpan ? Text(memberSpan) : null;
        var subMember = reference.SubMember is { } subMemberSpan ? Text(subMemberSpan) : null;

        switch (ns)
        {
            case "faction":
            {
                var faction = new FactionId(path);
                if (!_symbols.World.Factions.ContainsKey(faction))
                {
                    var dot = path.LastIndexOf('.');
                    var oldMember = dot < 0 ? string.Empty : path[(dot + 1)..];
                    var oldId = dot < 0 ? string.Empty : path[..dot];
                    if (member is null && oldMember is "name" or "tier")
                    {
                        Fail("TW2001",
                            $"Unknown faction '{path}'. Dotted property access is no longer supported; use 'faction:{oldId}:{oldMember}' for its {oldMember} property.",
                            reference.Name.Start + dot);
                    }

                    Fail("TW2001", $"Unknown faction '{path}'.", reference.Name.Start);
                }

                if (member is null) return new StandingRef(faction);
                return member switch
                {
                    "tier" => new StandingTierRef(faction),
                    "name" => new FactionNameRef(faction),
                    _ => Fail<Expr>("TW2001",
                        $"Unknown faction property '{member}'. Use 'faction:{path}:tier' or 'faction:{path}:name'.",
                        reference.Member!.Value.Start),
                };
            }

            case "item":
                if (member is not null)
                {
                    Fail("TW2001", $"Item names are referenced as 'item:{path}'; '.' only groups words inside names.",
                        reference.Member!.Value.Start);
                }

                return new ItemNameRef(Item(reference.Name));
            case "flag":
            {
                if (member is not null)
                {
                    Fail("TW2001", $"Flag references do not have properties; use 'flag:{path}'.",
                        reference.Member!.Value.Start);
                }

                var flag = new FlagId(path);
                if (!_symbols.Flags.Contains(flag))
                {
                    Fail("TW2004", $"Flag '{path}' is never set by any effect and is not declared in story.talewright.yaml.",
                        reference.Name.Start);
                }

                return new FlagRef(flag);
            }

            case "var":
            {
                if (member is not null)
                {
                    Fail("TW2001", $"Variable references do not have properties; use 'var:{path}'.",
                        reference.Member!.Value.Start);
                }

                var variable = new VarId(path);
                if (_symbols.UnresolvedVariables.Contains(variable))
                {
                    Fail("TW2007",
                        $"Cannot infer the type of variable '{path}'. Declare an initial value under 'vars' in story.talewright.yaml.",
                        reference.Name.Start);
                }

                if (!_symbols.Variables.Contains(variable))
                {
                    Fail("TW2004", $"Variable '{path}' is never assigned by any effect and is not declared in story.talewright.yaml.",
                        reference.Name.Start);
                }

                return _symbols.World.TextVariables.ContainsKey(variable)
                    ? new TextVarRef(variable)
                    : new VarRef(variable);
            }
        }

        var character = Character(reference.Namespace);
        if (member is not null)
        {
            if (path != "pronoun")
            {
                Fail("TW2001", $"Unknown character property '{path}:{member}'.", reference.Member!.Value.Start);
            }

            return new PronounRef(character, ParsePronounForm(member, subMember, reference));
        }

        return path switch
        {
            "first-name" => new NameRef(character, NamePart.First),
            "last-name" => new NameRef(character, NamePart.Last),
            "name" => new NameRef(character, NamePart.Full),
            "pronoun" => new PronounRef(character, PronounForm.Subject),
            _ => ResolveStat(character, reference.Name, reference.Namespace),
        };
    }

    private PronounForm ParsePronounForm(string property, string? subMember, ReferenceExpressionNode reference)
    {
        if (property == "possessive-pronoun")
        {
            return subMember switch
            {
                "determiner" => PronounForm.Possessive,
                "independent" => PronounForm.PossessivePronoun,
                _ => Fail<PronounForm>("TW2001",
                    $"Unknown possessive pronoun property '{subMember}'. Use 'determiner' or 'independent'.",
                    reference.SubMember!.Value.Start),
            };
        }

        return property switch
        {
            "subject" => PronounForm.Subject,
            "object" => PronounForm.Object,
            "reflexive" => PronounForm.Reflexive,
            _ => Fail<PronounForm>("TW2001",
                $"Unknown pronoun property '{property}'. Use {string.Join(", ", ExpressionParser.PronounProperties)}.",
                reference.Member!.Value.Start),
        };
    }

    private Expr ResolveStat(EntityId character, ExpressionSpan path, ExpressionSpan ns)
    {
        var stat = new StatId(Text(path));
        return _symbols.World.Ruleset.HasStat(stat)
            ? new StatRef(character, stat)
            : Fail<Expr>("TW2001", $"Unknown property '{Text(path)}' on '{Text(ns)}'.", path.Start);
    }

    private Expr BindUnary(UnaryExpressionNode unary)
    {
        var operand = Bind(unary.Operand);
        if (unary.Operator == TokenKind.Identifier)
        {
            if (operand.Type != ExprType.Bool)
            {
                Fail("TW2003", "'not' needs a true/false condition.", unary.OperatorSpan.Start);
            }

            return new UnaryExpr(UnaryOp.Not, operand);
        }

        if (operand.Type != ExprType.Number)
        {
            Fail("TW2003", "'-' needs a number.", unary.OperatorSpan.Start);
        }

        return operand is NumberLiteral number
            ? new NumberLiteral(number.Value == int.MinValue ? int.MaxValue : -number.Value)
            : new UnaryExpr(UnaryOp.Negate, operand);
    }

    private Expr BindBinary(BinaryExpressionNode binary)
    {
        var chain = new Stack<BinaryExpressionNode>();
        ExpressionNode first = binary;
        while (first is BinaryExpressionNode current)
        {
            chain.Push(current);
            first = current.Left;
        }

        var left = Bind(first);
        while (chain.TryPop(out var current))
        {
            left = BindBinaryOperation(current, left, Bind(current.Right));
        }

        return left;
    }

    private Expr BindBinaryOperation(BinaryExpressionNode binary, Expr left, Expr right)
    {
        var op = binary.Operator switch
        {
            TokenKind.Identifier when Text(binary.OperatorSpan) == "or" => BinaryOp.Or,
            TokenKind.Identifier => BinaryOp.And,
            TokenKind.Equal => BinaryOp.Equal,
            TokenKind.NotEqual => BinaryOp.NotEqual,
            TokenKind.Less => BinaryOp.Less,
            TokenKind.LessOrEqual => BinaryOp.LessOrEqual,
            TokenKind.Greater => BinaryOp.Greater,
            TokenKind.GreaterOrEqual => BinaryOp.GreaterOrEqual,
            TokenKind.Plus => BinaryOp.Add,
            TokenKind.Minus => BinaryOp.Subtract,
            TokenKind.Star => BinaryOp.Multiply,
            TokenKind.Slash => BinaryOp.Divide,
            _ => throw new InvalidOperationException($"Unsupported binary operator '{binary.Operator}'."),
        };

        if (op is BinaryOp.Or or BinaryOp.And)
        {
            if (left.Type != ExprType.Bool || right.Type != ExprType.Bool)
            {
                Fail("TW2003", $"'{op.ToString().ToLowerInvariant()}' needs true/false conditions on both sides.",
                    binary.OperatorSpan.Start);
            }
        }
        else if (op is BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide)
        {
            if (left.Type != ExprType.Number || right.Type != ExprType.Number)
            {
                Fail("TW2003", "Arithmetic needs numbers on both sides.", binary.OperatorSpan.Start);
            }
        }
        else
        {
            if (left.Type == ExprType.Tier && right is StandingRef rightStanding)
            {
                right = new StandingTierRef(rightStanding.Faction);
            }
            else if (right.Type == ExprType.Tier && left is StandingRef leftStanding)
            {
                left = new StandingTierRef(leftStanding.Faction);
            }

            if (left.Type != right.Type)
            {
                Fail("TW2003",
                    $"Cannot compare a {Describe(left.Type)} with a {Describe(right.Type)}.",
                    binary.OperatorSpan.Start);
            }

            if (left.Type is ExprType.Bool or ExprType.Text && op is not (BinaryOp.Equal or BinaryOp.NotEqual))
            {
                Fail("TW2003", $"Only == and != can compare {Describe(left.Type)} values.",
                    binary.OperatorSpan.Start);
            }
        }

        return new BinaryExpr(op, left, right);
    }

    private Effect Bind(EffectSyntax effect) => effect switch
    {
        ErrorEffectSyntax => Fail<Effect>("TW1001", "Expression is incomplete.", effect.Span.Start),
        SetFlagEffectSyntax setFlag => new SetFlag(new FlagId(Text(setFlag.Flag)), setFlag.Set),
        GiveTakeEffectSyntax giveTake => BindGiveTake(giveTake),
        AssignmentEffectSyntax assignment => BindAssignment(assignment),
        _ => throw new InvalidOperationException($"Unknown effect syntax node '{effect.GetType().Name}'."),
    };

    private Effect BindGiveTake(GiveTakeEffectSyntax effect)
    {
        var character = Character(effect.Character);
        var item = Item(effect.Item);
        Expr quantity = new NumberLiteral(1);
        if (effect.Quantity is { } quantitySyntax)
        {
            quantity = Bind(quantitySyntax);
            if (quantity.Type != ExprType.Number)
            {
                Fail("TW2003", "Item quantity must be a number.", quantitySyntax.Span.Start);
            }
        }

        return effect.Give
            ? new GiveItem(character, item, quantity)
            : new TakeItem(character, item, quantity);
    }

    private Effect BindAssignment(AssignmentEffectSyntax effect)
    {
        var targetName = Text(effect.Target);
        var ns = Text(effect.Namespace);
        var variable = new VarId(targetName);
        if (ns == "var" && _symbols.UnresolvedVariables.Contains(variable))
        {
            Fail("TW2007",
                $"Cannot infer the type of variable '{targetName}'. Declare an initial value under 'vars' in story.talewright.yaml.",
                effect.Target.Start);
        }

        var op = effect.Operator switch
        {
            TokenKind.Assign => AssignOp.Set,
            TokenKind.PlusAssign => AssignOp.Add,
            TokenKind.MinusAssign => AssignOp.Subtract,
            _ => throw new InvalidOperationException($"Unsupported assignment operator '{effect.Operator}'."),
        };
        var amount = Bind(effect.Amount);
        if (ns == "var" && _symbols.World.TextVariables.ContainsKey(variable))
        {
            if (op != AssignOp.Set)
            {
                Fail("TW2003", $"Text variable '{targetName}' supports '=' only, not '{AssignmentText(effect.Operator)}'.",
                    effect.Target.Start);
            }

            if (amount.Type != ExprType.Text)
            {
                Fail("TW2003", $"Text variable '{targetName}' needs text, not {Describe(amount.Type)}.",
                    effect.Amount.Span.Start);
            }

            return new SetTextVariable(variable, amount);
        }

        if (amount.Type != ExprType.Number)
        {
            Fail("TW2003", ns == "var"
                ? $"Variable '{targetName}' has a fixed integer type and needs a number, not {Describe(amount.Type)}."
                : "Only numbers can be assigned to stats or faction standings.", effect.Amount.Span.Start);
        }

        return ns switch
        {
            "faction" => _symbols.World.Factions.ContainsKey(new FactionId(targetName))
                ? new ChangeStanding(new FactionId(targetName), op, amount)
                : Fail<Effect>("TW2001", $"Unknown faction '{targetName}'.", effect.Target.Start),
            "var" => new ChangeVariable(variable, op, amount),
            "flag" => Fail<Effect>("TW1003", "Use 'set flag:x' or 'clear flag:x' for flags.", effect.Namespace.Start),
            _ => new ChangeStat(Character(effect.Namespace), Stat(effect.Target), op, amount),
        };
    }

    private EntityId Character(ExpressionSpan span)
    {
        var name = Text(span);
        var id = new EntityId(name);
        if (!_symbols.World.Characters.ContainsKey(id))
        {
            Fail("TW2001", $"Unknown character '{name}'.", span.Start);
        }

        return id;
    }

    private ItemId Item(ExpressionSpan span)
    {
        var name = Text(span);
        var id = new ItemId(name);
        if (!_symbols.World.Items.ContainsKey(id))
        {
            Fail("TW2001", $"Unknown item '{name}'.", span.Start);
        }

        return id;
    }

    private StatId Stat(ExpressionSpan span)
    {
        var name = Text(span);
        var id = new StatId(name);
        if (!_symbols.World.Ruleset.HasStat(id))
        {
            Fail("TW2001", $"Unknown stat '{name}' (not in ruleset '{_symbols.World.Ruleset.Id}').", span.Start);
        }

        return id;
    }

    private string Text(ExpressionSpan span) => _tokens.Source.AsSpan(span.Start, span.Length).ToString();

    private static string AssignmentText(TokenKind kind) => kind switch
    {
        TokenKind.Assign => "=",
        TokenKind.PlusAssign => "+=",
        TokenKind.MinusAssign => "-=",
        _ => throw new InvalidOperationException($"Unsupported assignment operator '{kind}'."),
    };

    private static string Describe(ExprType type) => type switch
    {
        ExprType.Bool => "true/false",
        ExprType.Number => "number",
        ExprType.Text => "text",
        _ => "reputation tier",
    };

    [DoesNotReturn]
    private void Fail(string code, string message, int offset)
    {
        _diagnostics.Error(code, message, _locate(offset));
        throw new BindingException();
    }

    private T Fail<T>(string code, string message, int offset)
    {
        Fail(code, message, offset);
        return default!;
    }
}
