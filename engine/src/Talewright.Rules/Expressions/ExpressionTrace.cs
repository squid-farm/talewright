namespace Talewright.Rules.Expressions;

/// <summary>
/// Values captured during evaluation, never a second evaluation. A null Result means not evaluated.
/// Named predicates expose only their opaque result, not invented implementation details.
/// </summary>
public sealed record ExpressionTrace(Expr Expression, Value? Result, IReadOnlyList<ExpressionTrace> Children)
{
    public bool IsOpaque => Expression is NamedPredicate;

    public static ExpressionTrace NotEvaluated(Expr expression) => new(expression, null, expression switch
    {
        BinaryExpr e => Array.AsReadOnly(new[] { NotEvaluated(e.Left), NotEvaluated(e.Right) }),
        UnaryExpr e => Array.AsReadOnly(new[] { NotEvaluated(e.Operand) }),
        _ => Array.Empty<ExpressionTrace>(),
    });
}
