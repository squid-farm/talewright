using Talewright.Narrative.Rendering;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;

namespace Talewright.Runtime;

internal sealed record ChoiceCandidate(
    string Key,
    Expr? Requirement,
    Func<IReadOnlyList<RenderedInline>> LabelSource,
    Action<List<RenderedBlock>> Destination,
    ReadOnlyMemory<byte>? ReplayIdentity);

internal sealed record OfferedChoice(ChoiceCandidate Candidate, ChoiceView View);

internal static class ChoiceAvailability
{
    public static OfferedChoice? Evaluate(ChoiceCandidate candidate, EvaluationContext context, bool capture,
        out ExpressionTrace? trace)
    {
        if (!RuleObservation.Test(candidate.Requirement, context, capture, out trace))
        {
            return null;
        }

        return new(candidate, CreateView(candidate));
    }

    public static ChoiceView CreateView(ChoiceCandidate candidate) =>
        CreateView(candidate.Key, candidate.LabelSource, candidate.ReplayIdentity);

    public static ChoiceView CreateView(string key, Func<IReadOnlyList<RenderedInline>> labelSource,
        ReadOnlyMemory<byte>? replayIdentity) =>
        new(key, labelSource()) { ReplayIdentity = replayIdentity };
}
