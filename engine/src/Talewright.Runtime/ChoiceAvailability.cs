using Talewright.Narrative.Rendering;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;

namespace Talewright.Runtime;

internal sealed record ChoiceCandidate(
    string Key,
    Expr? Requirement,
    Func<IReadOnlyList<RenderedInline>> LabelSource,
    Func<IReadOnlyList<RenderedInline>>? ShownLabelSource,
    Action<List<RenderedBlock>> Destination,
    ReadOnlyMemory<byte>? ReplayIdentity);

internal sealed record VisibleChoice(ChoiceCandidate Candidate, ChoiceView View, bool IsAvailable);

internal static class ChoiceAvailability
{
    public static VisibleChoice? Evaluate(ChoiceCandidate candidate, EvaluationContext context, bool capture,
        out ExpressionTrace? trace)
    {
        if (RuleObservation.Test(candidate.Requirement, context, capture, out trace))
        {
            return new(candidate, CreateView(candidate), IsAvailable: true);
        }

        if (candidate.ShownLabelSource is not { } shownLabelSource)
        {
            return null;
        }

        return new(candidate, CreateView(candidate.Key, shownLabelSource, candidate.ReplayIdentity), IsAvailable: false);
    }

    public static ChoiceView CreateView(ChoiceCandidate candidate) =>
        CreateView(candidate.Key, candidate.LabelSource, candidate.ReplayIdentity);

    public static ChoiceView CreateView(string key, Func<IReadOnlyList<RenderedInline>> labelSource,
        ReadOnlyMemory<byte>? replayIdentity) =>
        new(key, labelSource()) { ReplayIdentity = replayIdentity };
}
