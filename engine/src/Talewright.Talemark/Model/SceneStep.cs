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
    public static IEnumerable<SceneStep> Walk(IEnumerable<SceneStep> steps) =>
        WalkWithBlocks(steps).Select(entry => entry.Step);

    internal static IEnumerable<(SceneStep Step, IReadOnlyList<DivertBlockCondition> Blocks)> WalkWithBlocks(
        IEnumerable<SceneStep> steps, IReadOnlyList<DivertBlockCondition>? blocks = null)
    {
        blocks ??= [];
        foreach (var step in steps)
        {
            yield return (step, blocks);
            if (step is ConditionalStep branch)
            {
                foreach (var child in WalkWithBlocks(branch.Then, [.. blocks, new(branch.Condition, false)]))
                    yield return child;
                foreach (var child in WalkWithBlocks(branch.Else, [.. blocks, new(branch.Condition, true)]))
                    yield return child;
            }
        }
    }

    /// <summary>Potentially executable steps, without evaluating state-dependent conditions.</summary>
    public static IEnumerable<SceneStep> Reachable(IEnumerable<SceneStep> steps) =>
        Reachable(steps, null);

    internal static IEnumerable<SceneStep> Reachable(IEnumerable<SceneStep> steps, CancellationCheckpoint? checkpoint) =>
        ReachableWithBlocks(steps, [], checkpoint).Select(entry => entry.Step);

    private static IEnumerable<(SceneStep Step, IReadOnlyList<DivertBlockCondition> Blocks)> ReachableWithBlocks(
        IEnumerable<SceneStep> steps, IReadOnlyList<DivertBlockCondition> blocks, CancellationCheckpoint? checkpoint)
    {
        foreach (var step in steps)
        {
            checkpoint?.Check();
            yield return (step, blocks);
            if (step is ConditionalStep branch)
            {
                if (branch.Condition is not BoolLiteral { Value: false })
                    foreach (var child in ReachableWithBlocks(branch.Then, [.. blocks, new(branch.Condition, false)], checkpoint))
                        yield return child;
                if (branch.Condition is not BoolLiteral { Value: true })
                    foreach (var child in ReachableWithBlocks(branch.Else, [.. blocks, new(branch.Condition, true)], checkpoint))
                        yield return child;
            }
            if (!CanContinue(step, checkpoint))
                yield break;
        }
    }

    public static bool CanContinue(IEnumerable<SceneStep> steps) => CanContinue(steps, null);

    internal static bool CanContinue(IEnumerable<SceneStep> steps, CancellationCheckpoint? checkpoint)
    {
        foreach (var step in steps)
            if (!CanContinue(step, checkpoint)) return false;
        return true;
    }

    public static bool CanContinue(SceneStep step) => CanContinue(step, null);

    internal static bool CanContinue(SceneStep step, CancellationCheckpoint? checkpoint)
    {
        checkpoint?.Check();
        return step switch
        {
            DivertStep => false,
            CheckStep check => check.Check.Success.Divert is null || check.Check.Failure.Divert is null,
            ConditionalStep { Condition: BoolLiteral literal } branch =>
                CanContinue(literal.Value ? branch.Then : branch.Else, checkpoint),
            ConditionalStep branch => CanContinue(branch.Then, checkpoint) || CanContinue(branch.Else, checkpoint),
            ProseStep or EffectStep or ChoiceStep => true,
            _ => throw new NotSupportedException($"Unknown scene step '{step.GetType().Name}'."),
        };
    }

    public static IEnumerable<DivertTarget> Targets(IEnumerable<SceneStep> steps) =>
        Targets(steps, null);

    internal static IEnumerable<DivertTarget> Targets(IEnumerable<SceneStep> steps, CancellationCheckpoint? checkpoint) =>
        DivertLines(steps, checkpoint).Select(line => line.Target);

    /// <summary>
    /// Preview. Every divert on a line of <paramref name="scene"/> that can run, in source order. Conditional diverts
    /// are listed whatever their condition.
    /// </summary>
    public static IEnumerable<StoryDivert> Diverts(SceneDefinition scene) => Diverts(scene, null);

    internal static IEnumerable<StoryDivert> Diverts(SceneDefinition scene, CancellationCheckpoint? checkpoint)
    {
        var conditionsByStep = new Dictionary<string, StoryCondition>(StringComparer.Ordinal);
        return DivertLines(scene.Steps, checkpoint).Select((line, order) =>
        {
            checkpoint?.Check();
            var choice = (line.Step as ChoiceStep)?.Choice;
            var id = $"{scene.Id.Value}/{line.Step.Id}" + line.Outcome switch
            {
                SkillCheckOutcome.Success => "/success",
                SkillCheckOutcome.Failure => "/failure",
                _ => "",
            };
            if (!conditionsByStep.TryGetValue(line.Step.Id, out var condition))
            {
                var conditions = new List<Expr>();
                foreach (var block in line.Blocks)
                {
                    checkpoint?.Check();
                    conditions.Add(block.InElse ? new UnaryExpr(UnaryOp.Not, block.Condition) : block.Condition);
                }
                if (choice?.Requires is { } requires)
                    conditions.Add(requires);
                condition = StoryCondition.FromExpressions(conditions);
                conditionsByStep.Add(line.Step.Id, condition);
            }
            return new StoryDivert(id, scene.Id, line.Target, line.Kind, line.Outcome, line.Step.Id, line.Step.Source,
                order, choice?.Key, choice?.Label, choice?.Requires, line.Blocks, condition);
        });
    }

    private static IEnumerable<(SceneStep Step, IReadOnlyList<DivertBlockCondition> Blocks, DivertKind Kind,
        SkillCheckOutcome? Outcome, DivertTarget Target)> DivertLines(IEnumerable<SceneStep> steps,
        CancellationCheckpoint? checkpoint)
    {
        foreach (var (step, blocks) in ReachableWithBlocks(steps, [], checkpoint))
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
