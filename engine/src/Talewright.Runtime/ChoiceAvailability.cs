using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Model;

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

    /// <summary>
    /// The label to show while the requirement is false: the <c>shown</c> text if given, otherwise the normal label.
    /// Null when there is no <c>shown</c> clause, so the candidate stays hidden.
    /// </summary>
    public static Func<IReadOnlyList<RenderedInline>>? ShownLabelSource(ShownDefinition? shown,
        Func<IReadOnlyList<RenderedInline>> labelSource,
        Func<IReadOnlyList<TemplateInline>, IReadOnlyList<RenderedInline>> render)
    {
        if (shown is null)
        {
            return null;
        }

        if (shown.Text is not { } text)
        {
            return labelSource;
        }

        return () => render(text);
    }
}
