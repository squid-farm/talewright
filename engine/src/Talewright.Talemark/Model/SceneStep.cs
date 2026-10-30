using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;

namespace Talewright.Talemark.Model;

/// <summary>An executable scene line or block. Id is stable for the same source position within its scene.</summary>
public abstract record SceneStep(string Id, SourceRange Source);

public sealed record ProseStep(string Id, SourceRange Source, TemplateBlock Block) : SceneStep(Id, Source);
public sealed record EffectStep(string Id, SourceRange Source, IReadOnlyList<Effect> Effects) : SceneStep(Id, Source);
public sealed record ConditionalStep(string Id, SourceRange Source, Expr Condition,
    IReadOnlyList<SceneStep> Then, IReadOnlyList<SceneStep> Else) : SceneStep(Id, Source);
public sealed record ChoiceStep(string Id, SourceRange Source, ChoiceDefinition Choice) : SceneStep(Id, Source);
public sealed record DivertStep(string Id, SourceRange Source, DivertTarget Target) : SceneStep(Id, Source);
public sealed record CheckStep(string Id, SourceRange Source, CheckBranch Check) : SceneStep(Id, Source);

/// <summary>Shared source-order and structural-flow walks for validation, inspection and story maps.</summary>
public static class SceneSteps
{
    public static IEnumerable<SceneStep> Walk(IEnumerable<SceneStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step is ConditionalStep branch)
            {
                foreach (var child in Walk(branch.Then).Concat(Walk(branch.Else)))
                    yield return child;
            }
        }
    }

    /// <summary>Potentially executable steps, without evaluating state-dependent conditions.</summary>
    public static IEnumerable<SceneStep> Reachable(IEnumerable<SceneStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step is ConditionalStep branch)
            {
                if (branch.Condition is not BoolLiteral { Value: false })
                    foreach (var child in Reachable(branch.Then)) yield return child;
                if (branch.Condition is not BoolLiteral { Value: true })
                    foreach (var child in Reachable(branch.Else)) yield return child;
            }
            if (!CanContinue(step))
                yield break;
        }
    }

    public static bool CanContinue(IEnumerable<SceneStep> steps) => steps.All(CanContinue);

    public static bool CanContinue(SceneStep step) => step switch
    {
        DivertStep => false,
        CheckStep check => check.Check.Success.Divert is null || check.Check.Failure.Divert is null,
        ConditionalStep { Condition: BoolLiteral literal } branch =>
            CanContinue(literal.Value ? branch.Then : branch.Else),
        ConditionalStep branch => CanContinue(branch.Then) || CanContinue(branch.Else),
        ProseStep or EffectStep or ChoiceStep => true,
        _ => throw new NotSupportedException($"Unknown scene step '{step.GetType().Name}'."),
    };

    public static IEnumerable<DivertTarget> Targets(IEnumerable<SceneStep> steps)
    {
        foreach (var step in Reachable(steps))
        {
            switch (step)
            {
                case DivertStep divert:
                    yield return divert.Target;
                    break;
                case ChoiceStep choice:
                    if (choice.Choice.Divert is { } target) yield return target;
                    if (choice.Choice.Check is { } choiceCheck)
                    {
                        if (choiceCheck.Success.Divert is { } success) yield return success;
                        if (choiceCheck.Failure.Divert is { } failure) yield return failure;
                    }
                    break;
                case CheckStep check:
                    if (check.Check.Success.Divert is { } passed) yield return passed;
                    if (check.Check.Failure.Divert is { } failed) yield return failed;
                    break;
            }
        }
    }
}
