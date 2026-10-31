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
    public static IEnumerable<SceneStep> Reachable(IEnumerable<SceneStep> steps) =>
        ReachableWithBlocks(steps, []).Select(entry => entry.Step);

    private static IEnumerable<(SceneStep Step, IReadOnlyList<DivertBlockCondition> Blocks)> ReachableWithBlocks(
        IEnumerable<SceneStep> steps, IReadOnlyList<DivertBlockCondition> blocks)
    {
        foreach (var step in steps)
        {
            yield return (step, blocks);
            if (step is ConditionalStep branch)
            {
                if (branch.Condition is not BoolLiteral { Value: false })
                    foreach (var child in ReachableWithBlocks(branch.Then, [.. blocks, new(branch.Condition, false)]))
                        yield return child;
                if (branch.Condition is not BoolLiteral { Value: true })
                    foreach (var child in ReachableWithBlocks(branch.Else, [.. blocks, new(branch.Condition, true)]))
                        yield return child;
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

    public static IEnumerable<DivertTarget> Targets(IEnumerable<SceneStep> steps) =>
        DivertLines(steps).Select(line => line.Target);

    /// <summary>
    /// Preview. Every divert on a line of <paramref name="scene"/> that can run, in source order. Conditional diverts
    /// are listed whatever their condition.
    /// </summary>
    public static IEnumerable<StoryDivert> Diverts(SceneDefinition scene) =>
        DivertLines(scene.Steps).Select((line, order) =>
        {
            var choice = (line.Step as ChoiceStep)?.Choice;
            var id = $"{scene.Id.Value}/{line.Step.Id}" + line.Outcome switch
            {
                SkillCheckOutcome.Success => "/success",
                SkillCheckOutcome.Failure => "/failure",
                _ => "",
            };
            return new StoryDivert(id, scene.Id, line.Target, line.Kind, line.Outcome, line.Step.Id, line.Step.Source,
                order, choice?.Key, choice?.Label, choice?.Requires, line.Blocks);
        });

    private static IEnumerable<(SceneStep Step, IReadOnlyList<DivertBlockCondition> Blocks, DivertKind Kind,
        SkillCheckOutcome? Outcome, DivertTarget Target)> DivertLines(IEnumerable<SceneStep> steps)
    {
        foreach (var (step, blocks) in ReachableWithBlocks(steps, []))
        {
            switch (step)
            {
                case DivertStep divert:
                    yield return (step, blocks, DivertKind.Scene, null, divert.Target);
                    break;
                case ChoiceStep choice:
                    if (choice.Choice.Divert is { } target)
                        yield return (step, blocks, DivertKind.Choice, null, target);
                    if (choice.Choice.Check is { } choiceCheck)
                    {
                        if (choiceCheck.Success.Divert is { } success)
                            yield return (step, blocks, DivertKind.ChoiceSkillCheck, SkillCheckOutcome.Success, success);
                        if (choiceCheck.Failure.Divert is { } failure)
                            yield return (step, blocks, DivertKind.ChoiceSkillCheck, SkillCheckOutcome.Failure, failure);
                    }
                    break;
                case CheckStep check:
                    if (check.Check.Success.Divert is { } passed)
                        yield return (step, blocks, DivertKind.EntryCheck, SkillCheckOutcome.Success, passed);
                    if (check.Check.Failure.Divert is { } failed)
                        yield return (step, blocks, DivertKind.EntryCheck, SkillCheckOutcome.Failure, failed);
                    break;
            }
        }
    }
}
