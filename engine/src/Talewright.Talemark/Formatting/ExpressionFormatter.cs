using System.Globalization;
using Talewright.Core.Identity;
using Talewright.Rules.Expressions;

namespace Talewright.Talemark.Formatting;

/// <summary>Formats compiled expressions using Talemark's canonical spelling and grouping.</summary>
/// <remarks>
/// Reparse against the same story symbols, ruleset and predicate registrations. Parsed expressions
/// round-trip structurally; C#-constructed numeric negations may normalize to literals.
/// Double quotes within text cannot be represented until Talemark supports expression string escapes.
/// This API does not format substitutions' modifier chains or reproduce original source text.
/// </remarks>
public static class ExpressionFormatter
{
    /// <summary>Returns Talemark text without surrounding template braces.</summary>
    /// <exception cref="ArgumentNullException">The expression is null.</exception>
    /// <exception cref="ArgumentException">The tree has invalid operands, names or enum values.</exception>
    /// <exception cref="NotSupportedException">The expression kind or a quoted text value cannot be represented.</exception>
    public static string Format(Expr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return Write(expression);
    }

    private static string Write(Expr expression, int parentPrecedence = 0, bool groupEqual = false)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var precedence = Precedence(expression);
        var text = expression switch
        {
            NumberLiteral number => number.Value.ToString(CultureInfo.InvariantCulture),
            BoolLiteral boolean => boolean.Value ? "true" : "false",
            TextLiteral literal => Quote(literal.Value),
            TierLiteral tier => TierName(tier.Tier.Id),
            StatRef stat => $"{Character(stat.Character)}:{StatName(stat.Stat.Value)}",
            StandingRef standing => $"faction:{Name(standing.Faction.Value)}",
            StandingTierRef standing => $"faction:{Name(standing.Faction.Value)}:tier",
            FlagRef flag => $"flag:{Name(flag.Flag.Value)}",
            VarRef variable => $"var:{Name(variable.Variable.Value)}",
            TextVarRef variable => $"var:{Name(variable.Variable.Value)}",
            NameRef name => $"{Character(name.Character)}:{name.Part switch
            {
                NamePart.First => "first-name",
                NamePart.Last => "last-name",
                NamePart.Full => "name",
                _ => throw Invalid("Unknown name part."),
            }}",
            PronounRef pronoun => $"{Character(pronoun.Character)}:pronoun{pronoun.Form switch
            {
                PronounForm.Subject => "",
                PronounForm.Object => ":object",
                PronounForm.Possessive => ":possessive-pronoun:determiner",
                PronounForm.PossessivePronoun => ":possessive-pronoun:independent",
                PronounForm.Reflexive => ":reflexive",
                _ => throw Invalid("Unknown pronoun form."),
            }}",
            FactionNameRef faction => $"faction:{Name(faction.Faction.Value)}:name",
            ItemNameRef item => $"item:{Name(item.Item.Value)}",
            HasItem item => $"{Character(item.Character)} has {Name(item.Item.Value)}",
            Visited visited => $"visited({Name(visited.Scene.Value)})",
            NamedPredicate predicate => $"@{Name(predicate.Name)}",
            UnaryExpr unary => WriteUnary(unary, precedence),
            BinaryExpr binary => WriteBinary(binary, precedence),
            _ => throw new NotSupportedException($"Expression kind '{expression.GetType().Name}' has no Talemark spelling."),
        };
        return precedence < parentPrecedence || groupEqual && precedence == parentPrecedence ? $"({text})" : text;
    }

    private static string WriteUnary(UnaryExpr expression, int precedence)
    {
        var expected = expression.Op == UnaryOp.Not ? ExprType.Bool : ExprType.Number;
        if (expression.Operand.Type != expected)
        {
            throw Invalid($"Unary '{expression.Op}' requires a {expected} operand.");
        }

        return (expression.Op == UnaryOp.Not ? "not " : "-") + Write(expression.Operand, precedence);
    }

    private static string WriteBinary(BinaryExpr expression, int precedence)
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

        var op = expression.Op switch
        {
            BinaryOp.Add => "+",
            BinaryOp.Subtract => "-",
            BinaryOp.Multiply => "*",
            BinaryOp.Divide => "/",
            BinaryOp.Equal => "==",
            BinaryOp.NotEqual => "!=",
            BinaryOp.Less => "<",
            BinaryOp.LessOrEqual => "<=",
            BinaryOp.Greater => ">",
            BinaryOp.GreaterOrEqual => ">=",
            BinaryOp.And => "and",
            BinaryOp.Or => "or",
            _ => throw Invalid("Unknown binary operator."),
        };
        // Comparisons do not chain; other binary levels parse left-associatively.
        return $"{Write(expression.Left, precedence, precedence == 4)} {op} {Write(expression.Right, precedence, true)}";
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
        if (value.Contains('"'))
        {
            throw new NotSupportedException("Talemark expression strings cannot contain double quotes: string escape syntax is not supported.");
        }

        return $"\"{value}\"";
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
        if (name is "faction" or "item" or "flag" or "var" or "true" or "false" or "visited" or "not")
        {
            throw Invalid($"Character name '{name}' is reserved in Talemark expressions.");
        }

        return name;
    }

    private static string StatName(string value)
    {
        var name = Name(value);
        if (name is "first-name" or "last-name" or "name" or "pronoun")
        {
            throw Invalid($"Stat name '{name}' resolves to a text property in Talemark.");
        }

        return name;
    }

    private static string TierName(string value)
    {
        var name = Name(value);
        if (name is "true" or "false" or "visited" or "not")
        {
            throw Invalid($"Tier name '{name}' is reserved in Talemark expressions.");
        }

        return name;
    }

    private static ArgumentException Invalid(string message) => new(message, "expression");
}
