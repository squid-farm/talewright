using System.Globalization;
using System.Text;
using Talewright.Core.Identity;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Parsing;
using Talewright.Talemark.Vocabulary;
using static Talewright.Talemark.Vocabulary.ExpressionVocabulary;

namespace Talewright.Talemark.Formatting;

/// <summary>Formats compiled expressions using Talemark's canonical spelling and grouping.</summary>
/// <remarks>
/// Reparse against the same story symbols, ruleset and predicate registrations. Parsed expressions
/// round-trip structurally; C#-constructed numeric negations may normalize to literals.
/// Text literals escape double quotes and backslashes using Talemark string syntax.
/// This API does not format substitutions' modifier chains or reproduce original source text.
/// </remarks>
public static class ExpressionFormatter
{
    /// <summary>Returns Talemark text without surrounding template braces.</summary>
    /// <exception cref="ArgumentNullException">The expression is null.</exception>
    /// <exception cref="ArgumentException">The tree has invalid operands, names or enum values.</exception>
    /// <exception cref="NotSupportedException">The expression kind cannot be represented.</exception>
    public static string Format(Expr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return Write(expression);
    }

    private static string Write(Expr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var output = new StringBuilder();
        var pending = new Stack<FormatAction>();
        pending.Push(FormatAction.ForExpression(expression, 0, false));

        while (pending.TryPop(out var action))
        {
            if (action.Expression is not { } current)
            {
                output.Append(action.Text);
                continue;
            }

            var precedence = Precedence(current);
            var wrap = precedence < action.ParentPrecedence
                || action.GroupEqual && precedence == action.ParentPrecedence;
            if (wrap) pending.Push(FormatAction.ForText(CloseParen.ToString()));

            switch (current)
            {
                case UnaryExpr unary:
                    pending.Push(FormatAction.ForExpression(unary.Operand, precedence, false));
                    pending.Push(FormatAction.ForText(UnaryPrefix(unary)));
                    break;
                case BinaryExpr binary:
                    var op = BinaryOperator(binary);
                    pending.Push(FormatAction.ForExpression(binary.Right, precedence, true));
                    pending.Push(FormatAction.ForText($" {op} "));
                    pending.Push(FormatAction.ForExpression(binary.Left, precedence, precedence == 4));
                    break;
                default:
                    pending.Push(FormatAction.ForText(Atom(current)));
                    break;
            }

            if (wrap) pending.Push(FormatAction.ForText(OpenParen.ToString()));
        }

        return output.ToString();
    }

    private static string Atom(Expr expression) => expression switch
    {
        NumberLiteral number => number.Value.ToString(CultureInfo.InvariantCulture),
        BoolLiteral boolean => boolean.Value ? True : False,
        TextLiteral literal => Quote(literal.Value),
        TierLiteral tier => TierName(tier.Tier.Id),
        StatRef stat => Reference(Character(stat.Character), StatName(stat.Stat.Value)),
        StandingRef standing => Reference(Faction, Name(standing.Faction.Value), Standing),
        StandingTierRef standing => Reference(Faction, Name(standing.Faction.Value), Tier),
        FlagRef flag => Reference(Flag, Name(flag.Flag.Value)),
        VarRef variable => Reference(Var, Name(variable.Variable.Value)),
        TextVarRef variable => Reference(Var, Name(variable.Variable.Value)),
        NameRef name => Reference(Character(name.Character),
            TryGetSpelling(name.Part, out var part) ? part : throw Invalid("Unknown name part.")),
        PronounRef pronoun => PronounReference(Character(pronoun.Character), pronoun.Form),
        FactionNameRef faction => Reference(Faction, Name(faction.Faction.Value)),
        ItemNameRef item => Reference(Item, Name(item.Item.Value)),
        HasItem item => $"{Character(item.Character)} {Has} {Name(item.Item.Value)}",
        Visited visited => $"{VisitedCall}{Name(visited.Scene.Value)}{CloseParen}",
        NamedPredicate predicate => $"{PredicateMarker}{Name(predicate.Name)}",
        UnaryExpr or BinaryExpr => throw new InvalidOperationException("Composite expressions are formatted separately."),
        _ => throw new NotSupportedException($"Expression kind '{expression.GetType().Name}' has no Talemark spelling."),
    };

    private static string Reference(params string[] parts) => string.Join(Separator, parts);

    private static string PronounReference(string character, PronounForm form)
    {
        if (form == PronounForm.Subject) return Reference(character, Pronoun);
        if (TryGetPronounSpelling(form, out var property)) return Reference(character, Pronoun, property);
        return TryGetPossessiveSpelling(form, out var possessive)
            ? Reference(character, Pronoun, PossessivePronoun, possessive)
            : throw Invalid("Unknown pronoun form.");
    }

    private static string UnaryPrefix(UnaryExpr expression)
    {
        var expected = expression.Op == UnaryOp.Not ? ExprType.Bool : ExprType.Number;
        if (expression.Operand.Type != expected)
        {
            throw Invalid($"Unary '{expression.Op}' requires a {expected} operand.");
        }

        if (!TryGetSpelling(expression.Op, out var spelling)) throw Invalid("Unknown unary operator.");
        return expression.Op == UnaryOp.Not ? spelling + " " : spelling;
    }

    private static string BinaryOperator(BinaryExpr expression)
    {
        var left = expression.Left.Type;
        var right = expression.Right.Type;
        var valid = expression.Op switch
        {
            BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide =>
                left == ExprType.Number && right == ExprType.Number,
            BinaryOp.And or BinaryOp.Or => left == ExprType.Bool && right == ExprType.Bool,
            BinaryOp.Equal or BinaryOp.NotEqual => left == right,
            _ => left == right && left is ExprType.Number or ExprType.Tier,
        };
        if (!valid)
        {
            throw Invalid($"Binary '{expression.Op}' cannot combine {left} and {right} operands.");
        }

        return TryGetSpelling(expression.Op, out var spelling) ? spelling : throw Invalid("Unknown binary operator.");
    }

    private readonly record struct FormatAction(Expr? Expression, string? Text, int ParentPrecedence, bool GroupEqual)
    {
        public static FormatAction ForExpression(Expr expression, int parentPrecedence, bool groupEqual) =>
            new(expression, null, parentPrecedence, groupEqual);

        public static FormatAction ForText(string text) => new(null, text, 0, false);
    }

    private static int Precedence(Expr expression) => expression switch
    {
        BinaryExpr { Op: BinaryOp.Or } => 1,
        BinaryExpr { Op: BinaryOp.And } => 2,
        UnaryExpr { Op: UnaryOp.Not } => 3,
        BinaryExpr { Op: BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.LessOrEqual
            or BinaryOp.Greater or BinaryOp.GreaterOrEqual } => 4,
        BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract } => 5,
        BinaryExpr { Op: BinaryOp.Multiply or BinaryOp.Divide } => 6,
        UnaryExpr { Op: UnaryOp.Negate } or NumberLiteral { Value: < 0 } => 7,
        UnaryExpr => throw Invalid("Unknown unary operator."),
        BinaryExpr => throw Invalid("Unknown binary operator."),
        _ => 8,
    };

    private static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return QuotedStringSyntax.Quote(value);
    }

    private static string Name(string value)
    {
        if (value is null || !IdentifierRules.IsValid(value))
        {
            throw Invalid($"'{value}' is not a valid Talemark identifier.");
        }

        return value;
    }

    private static string Character(EntityId character)
    {
        var name = Name(character.Value);
        if (name is Faction or Item or Flag or Var or True or False or ExpressionVocabulary.Visited or Not)
        {
            throw Invalid($"Character name '{name}' is reserved in Talemark expressions.");
        }

        return name;
    }

    private static string StatName(string value)
    {
        var name = Name(value);
        if (CharacterProperties.Contains(name))
        {
            throw Invalid($"Stat name '{name}' resolves to a text property in Talemark.");
        }

        return name;
    }

    private static string TierName(string value)
    {
        var name = Name(value);
        if (name is True or False or ExpressionVocabulary.Visited or Not)
        {
            throw Invalid($"Tier name '{name}' is reserved in Talemark expressions.");
        }

        return name;
    }

    private static ArgumentException Invalid(string message) => new(message, "expression");
}
