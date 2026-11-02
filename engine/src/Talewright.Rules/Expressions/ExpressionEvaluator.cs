using Talewright.Core.Rulesets;
using Talewright.Core.State;

namespace Talewright.Rules.Expressions;

/// <summary>Result of evaluating an <see cref="Expr"/>. Exactly one payload is meaningful, per <see cref="Type"/>.</summary>
public readonly record struct Value(ExprType Type, int Number, bool Bool, string? Text, ReputationTier? Tier)
{
    public static Value Of(int number) => new(ExprType.Number, number, false, null, null);
    public static Value Of(bool value) => new(ExprType.Bool, 0, value, null, null);
    public static Value Of(string text) => new(ExprType.Text, 0, false, text, null);
    public static Value Of(ReputationTier tier) => new(ExprType.Tier, 0, false, null, tier);

    /// <summary>Display form used by the template renderer.</summary>
    public override string ToString() => Type switch
    {
        ExprType.Number => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ExprType.Bool => Bool ? "true" : "false",
        ExprType.Text => Text ?? string.Empty,
        ExprType.Tier => Tier?.Name ?? string.Empty,
        _ => string.Empty,
    };
}

/// <summary>Everything an expression may read.</summary>
public sealed record EvaluationContext(IGameStateView State, NamedPredicateRegistry Predicates)
{
    public Action<ExpressionTrace>? Trace { get; init; }

    public static EvaluationContext For(IGameStateView state) => new(state, NamedPredicateRegistry.Empty);
}

/// <summary>Tree-walking evaluator. Fast enough for interactive fiction and fully trim/AOT safe.</summary>
public static class ExpressionEvaluator
{
    public static bool IsTrue(Expr expr, EvaluationContext context) => Evaluate(expr, context).Bool;

    public static int EvaluateNumber(Expr expr, EvaluationContext context) => Evaluate(expr, context).Number;

    public static Value Evaluate(Expr expr, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(context);

        if (expr is not (UnaryExpr or BinaryExpr))
        {
            var value = EvaluateAtom(expr, context);
            context.Trace?.Invoke(new ExpressionTrace(expr, value, Array.Empty<ExpressionTrace>()));
            return value;
        }

        var frames = new Stack<EvaluationFrame>();
        frames.Push(new EvaluationFrame(expr, context.Trace is not null));
        var returnedValue = default(Value);
        ExpressionTrace? returnedTrace = null;

        while (frames.TryPeek(out var frame))
        {
            switch (frame.Expression)
            {
                case UnaryExpr unary when frame.State == 0:
                    frame.State = 1;
                    frames.Push(new EvaluationFrame(unary.Operand, context.Trace is not null));
                    break;
                case UnaryExpr unary:
                    if (frame.Children is not null) frame.Children.Add(returnedTrace!);
                    returnedValue = unary.Op switch
                    {
                        UnaryOp.Not => Value.Of(!returnedValue.Bool),
                        UnaryOp.Negate => Value.Of(Arithmetic.Saturate(-(long)returnedValue.Number)),
                        _ => throw new NotSupportedException(unary.Op.ToString()),
                    };
                    Complete(frame, returnedValue);
                    break;
                case BinaryExpr binary when frame.State == 0:
                    frame.State = 1;
                    frames.Push(new EvaluationFrame(binary.Left, context.Trace is not null));
                    break;
                case BinaryExpr binary when frame.State == 1:
                    frame.Left = returnedValue;
                    if (frame.Children is not null) frame.Children.Add(returnedTrace!);
                    if (binary.Op == BinaryOp.And && !returnedValue.Bool)
                    {
                        if (frame.Children is not null) frame.Children.Add(ExpressionTrace.NotEvaluated(binary.Right));
                        returnedValue = Value.Of(false);
                        Complete(frame, returnedValue);
                    }
                    else if (binary.Op == BinaryOp.Or && returnedValue.Bool)
                    {
                        if (frame.Children is not null) frame.Children.Add(ExpressionTrace.NotEvaluated(binary.Right));
                        returnedValue = Value.Of(true);
                        Complete(frame, returnedValue);
                    }
                    else
                    {
                        frame.State = 2;
                        frames.Push(new EvaluationFrame(binary.Right, context.Trace is not null));
                    }
                    break;
                case BinaryExpr binary:
                    if (frame.Children is not null) frame.Children.Add(returnedTrace!);
                    returnedValue = ApplyBinary(binary, frame.Left, returnedValue, context);
                    Complete(frame, returnedValue);
                    break;
                default:
                    returnedValue = EvaluateAtom(frame.Expression, context);
                    Complete(frame, returnedValue);
                    break;
            }
        }

        if (context.Trace is { } observe)
        {
            observe(returnedTrace!);
        }
        return returnedValue;

        void Complete(EvaluationFrame completed, Value value)
        {
            returnedTrace = completed.Children is null
                ? null
                : new ExpressionTrace(completed.Expression, value, completed.Children.AsReadOnly());
            frames.Pop();
        }
    }

    private sealed class EvaluationFrame(Expr expression, bool trace)
    {
        public Expr Expression { get; } = expression;
        public int State { get; set; }
        public Value Left { get; set; }
        public List<ExpressionTrace>? Children { get; } = trace ? [] : null;
    }

    private static Value EvaluateAtom(Expr expr, EvaluationContext context)
    {
        var state = context.State;
        return expr switch
        {
            NumberLiteral e => Value.Of(e.Value),
            BoolLiteral e => Value.Of(e.Value),
            TextLiteral e => Value.Of(e.Value),
            TierLiteral e => Value.Of(e.Tier),
            StatRef e => Value.Of(state.GetStat(e.Character, e.Stat)),
            StandingRef e => Value.Of(state.GetStanding(e.Faction)),
            StandingTierRef e => state.Ruleset.TierFor(state.GetStanding(e.Faction)) is { } tier
                ? Value.Of(tier)
                : throw new InvalidOperationException("The ruleset defines no reputation tiers."),
            FlagRef e => Value.Of(state.GetFlag(e.Flag)),
            VarRef e => Value.Of(state.GetVariable(e.Variable)),
            TextVarRef e => Value.Of(state.GetTextVariable(e.Variable)),
            NameRef e => Value.Of(Name(state, e)),
            PronounRef e => Value.Of(Pronoun(state, e)),
            FactionNameRef e => Value.Of(state.World.Factions[e.Faction].Name),
            ItemNameRef e => Value.Of(state.World.Items[e.Item].Name),
            HasItem e => Value.Of(state.GetItemCount(e.Character, e.Item) > 0),
            Visited e => Value.Of(state.VisitCount(e.Scene) > 0),
            NamedPredicate e => Value.Of(context.Predicates.Evaluate(e.Name, state)),
            _ => throw new NotSupportedException($"Unknown expression {expr.GetType().Name}."),
        };
    }

    private static Value ApplyBinary(BinaryExpr e, Value left, Value right, EvaluationContext context)
    {
        return e.Op switch
        {
            BinaryOp.Add => Value.Of(Arithmetic.Saturate((long)left.Number + right.Number)),
            BinaryOp.Subtract => Value.Of(Arithmetic.Saturate((long)left.Number - right.Number)),
            BinaryOp.Multiply => Value.Of(Arithmetic.Saturate((long)left.Number * right.Number)),
            BinaryOp.Divide => Value.Of(right.Number == 0 ? 0 : Arithmetic.Saturate((long)left.Number / right.Number)),
            BinaryOp.Equal => Value.Of(Compare(left, right, context) == 0),
            BinaryOp.NotEqual => Value.Of(Compare(left, right, context) != 0),
            BinaryOp.Less => Value.Of(Compare(left, right, context) < 0),
            BinaryOp.LessOrEqual => Value.Of(Compare(left, right, context) <= 0),
            BinaryOp.Greater => Value.Of(Compare(left, right, context) > 0),
            BinaryOp.GreaterOrEqual => Value.Of(Compare(left, right, context) >= 0),
            BinaryOp.And => Value.Of(left.Bool && right.Bool),
            BinaryOp.Or => Value.Of(left.Bool || right.Bool),
            _ => throw new NotSupportedException(e.Op.ToString()),
        };
    }

    private static int Compare(Value left, Value right, EvaluationContext context) => left.Type switch
    {
        ExprType.Number => left.Number.CompareTo(right.Number),
        ExprType.Bool => left.Bool.CompareTo(right.Bool),
        ExprType.Text => string.CompareOrdinal(left.Text, right.Text),
        ExprType.Tier => context.State.Ruleset.TierIndex(left.Tier!).CompareTo(context.State.Ruleset.TierIndex(right.Tier!)),
        _ => 0,
    };

    private static string Name(IGameStateView state, NameRef e)
    {
        var character = state.GetCharacterDefinition(e.Character);
        return e.Part switch
        {
            NamePart.First => character.FirstName,
            NamePart.Last => character.LastName,
            _ => character.FullName,
        };
    }

    private static string Pronoun(IGameStateView state, PronounRef e)
    {
        var p = state.GetCharacterDefinition(e.Character).Pronouns;
        return e.Form switch
        {
            PronounForm.Subject => p.Subject,
            PronounForm.Object => p.Object,
            PronounForm.Possessive => p.Possessive,
            PronounForm.PossessivePronoun => p.PossessivePronoun,
            _ => p.Reflexive,
        };
    }
}
