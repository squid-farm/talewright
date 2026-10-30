using Talewright.Core.Identity;
using Talewright.Rules.Expressions;

namespace Talewright.Rules.Diagnostics;

public enum DecisionKind
{
    Branch,
    EntryEffect,
    Choice,
    Storylet,
    Divert,
    Check,
    CheckOutcome,
    Trigger,
}

public enum DecisionOutcome
{
    Applied,
    Offered,
    Selected,
    Excluded,
    NotEvaluated,
    Passed,
    Failed,
    Primed,
    Fired,
    Suppressed,
}

public enum DecisionReason
{
    Unconditional,
    ConditionTrue,
    ConditionFalse,
    SelfExcluded,
    TagMismatch,
    AlreadyVisited,
    Duplicate,
    PriorityOrder,
    HubLimit,
    EarlierDivert,
    ChoicesAvailable,
    EntryCheck,
    ExplicitTarget,
    CheckSucceeded,
    CheckFailed,
    InitialCondition,
    NotRising,
    AlreadyFired,
    RisingEdge,
    CascadeLimit,
}

/// <summary>
/// An actual execution decision. Event offsets refer to the observing run, not a later state.
/// Null offsets mean the caller did not supply an event-log position.
/// Effects/results occupy [EventStart, EventEnd); a condition-only decision has an empty range.
/// </summary>
public sealed record RuleDecision(
    DecisionKind Kind,
    string Subject,
    SceneId? Scene,
    DecisionOutcome Outcome,
    DecisionReason Reason,
    ExpressionTrace? Expression,
    int? EventStart,
    int? EventEnd,
    int? Pass = null,
    int? Priority = null,
    int? Rank = null,
    int? Limit = null);

public static class RuleObservation
{
    /// <summary>Evaluates once, optionally retaining the evaluator's actual expression tree results.</summary>
    public static bool Test(Expr? expression, EvaluationContext context, bool capture, out ExpressionTrace? trace)
    {
        if (expression is null)
        {
            trace = null;
            return true;
        }
        return Evaluate(expression, context, capture, out trace).Bool;
    }

    public static Value Evaluate(Expr expression, EvaluationContext context, bool capture, out ExpressionTrace? trace)
    {
        if (!capture)
        {
            trace = null;
            return ExpressionEvaluator.Evaluate(expression, context);
        }
        return Capture(expression, context, out trace);
    }

    private static Value Capture(Expr expression, EvaluationContext context, out ExpressionTrace? trace)
    {
        ExpressionTrace? result = null;
        var value = ExpressionEvaluator.Evaluate(expression, context with { Trace = observed => result = observed });
        trace = result;
        return value;
    }
}
