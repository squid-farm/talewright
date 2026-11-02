namespace Talewright.Rules.Expressions;

/// <summary>
/// Values captured during evaluation, never a second evaluation. A null Result means not evaluated.
/// Named predicates expose only their opaque result, not invented implementation details.
/// </summary>
public sealed record ExpressionTrace(Expr Expression, Value? Result, IReadOnlyList<ExpressionTrace> Children)
{
    public bool IsOpaque => Expression is NamedPredicate;

    public static ExpressionTrace NotEvaluated(Expr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var frames = new Stack<Frame>();
        frames.Push(new Frame(expression));
        ExpressionTrace? result = null;

        while (frames.TryPeek(out var frame))
        {
            var child = frame.NextChild switch
            {
                0 when frame.Expression is UnaryExpr unary => unary.Operand,
                0 when frame.Expression is BinaryExpr binary => binary.Left,
                1 when frame.Expression is BinaryExpr binary => binary.Right,
                _ => null,
            };
            if (child is not null)
            {
                frame.NextChild++;
                frames.Push(new Frame(child));
                continue;
            }

            var completed = new ExpressionTrace(frame.Expression, null, frame.Children.AsReadOnly());
            frames.Pop();
            if (frames.TryPeek(out var parent))
            {
                parent.Children.Add(completed);
            }
            else
            {
                result = completed;
            }
        }

        return result!;
    }

    private sealed class Frame(Expr expression)
    {
        public Expr Expression { get; } = expression;
        public int NextChild { get; set; }
        public List<ExpressionTrace> Children { get; } = [];
    }
}
