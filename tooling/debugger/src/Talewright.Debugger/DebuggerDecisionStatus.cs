using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Rules.Diagnostics;
using Talewright.Runtime;

namespace Talewright.Debugger;

internal static class DebuggerDecisionStatus
{
    public const string Available = "Available";
    public const string ShownUnavailable = "Shown, unavailable";
    public const string Hidden = "Hidden";
    public const string DroppedByHubLimit = "Dropped by hub limit";
    public const string NotReached = "Not reached";
    public const string ShownItemSuffix = ", shown when unavailable";

    public static string? ForDecision(RuleDecision? decision)
    {
        if (decision is null)
            return NotReached;
        if (decision.Kind is not (DecisionKind.Choice or DecisionKind.Storylet))
            return null;

        return decision switch
        {
            { Outcome: DecisionOutcome.Offered, Reason: DecisionReason.ConditionTrue or DecisionReason.Unconditional } => Available,
            { Kind: DecisionKind.Storylet, Outcome: DecisionOutcome.Selected } => Available,
            { Outcome: DecisionOutcome.Offered, Reason: DecisionReason.ConditionFalse } => ShownUnavailable,
            { Outcome: DecisionOutcome.Excluded, Reason: DecisionReason.ConditionFalse } => Hidden,
            { Outcome: DecisionOutcome.Excluded, Reason: DecisionReason.HubLimit } => DroppedByHubLimit,
            { Outcome: DecisionOutcome.NotEvaluated } => NotReached,
            _ => null,
        };
    }

    public static IReadOnlyList<RuleDecision> ForLatestSceneEntry(AuthorInspection? inspection, SceneId scene)
    {
        if (inspection is null)
            return [];

        var entryEvent = -1;
        for (var index = inspection.Events.Count - 1; index >= 0; index--)
        {
            if (inspection.Events[index] is SceneEntered entered && entered.Scene == scene)
            {
                entryEvent = index;
                break;
            }
        }

        if (entryEvent < 0)
            return [];

        return Array.AsReadOnly(inspection.Decisions
            .Where(decision => decision.Scene == scene && decision.EventStart is { } start && start >= entryEvent)
            .ToArray());
    }
}
