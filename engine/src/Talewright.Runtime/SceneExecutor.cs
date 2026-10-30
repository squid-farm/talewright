using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Checks;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

internal sealed record OfferedSceneChoice(ChoiceStep Step, ChoiceView View);
internal sealed record SceneExecution(IReadOnlyList<RenderedBlock> Blocks,
    IReadOnlyList<OfferedSceneChoice> Choices, DivertTarget? Target);

/// <summary>Executes authored steps; StorySession owns entry, transitions, history and presentation.</summary>
internal sealed class SceneExecutor(StoryDefinition story, EventLog log, TemplateRenderer renderer,
    Action afterBatch, Action<RuleDecision>? observer, bool rememberEntry)
{
    private readonly Dictionary<string, IReadOnlyList<RenderedBlock>> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DivertTarget?> _checks = new(StringComparer.Ordinal);

    public SceneExecution Run(SceneDefinition scene, bool refresh = false)
    {
        if (!refresh)
        {
            _notes.Clear();
            _checks.Clear();
        }
        var blocks = new List<RenderedBlock>();
        var choices = new List<OfferedSceneChoice>();
        var target = Execute(scene.Steps, blocks, choices, refresh, out _);
        return new(blocks, choices, target);
    }

    private DivertTarget? Execute(IReadOnlyList<SceneStep> steps, List<RenderedBlock> blocks,
        List<OfferedSceneChoice> choices, bool refresh, out DecisionReason stopReason)
    {
        DivertTarget? target = null;
        stopReason = DecisionReason.EarlierDivert;
        foreach (var step in steps)
        {
            if (target is not null)
            {
                Skip(step, stopReason);
                continue;
            }
            switch (step)
            {
                case ProseStep prose:
                    blocks.AddRange(renderer.Render([prose.Block], log.State).Blocks);
                    break;
                case EffectStep effect:
                    if (!refresh)
                    {
                        var mark = log.Events.Count;
                        ApplyBatch(effect.Effects);
                        Observe(DecisionKind.EntryEffect, step.Id, DecisionOutcome.Applied,
                            DecisionReason.Unconditional, null, mark);
                        AppendNotes(step.Id, mark, blocks);
                    }
                    else
                    {
                        blocks.AddRange(_notes.GetValueOrDefault(step.Id) ?? []);
                    }
                    break;
                case ConditionalStep branch:
                    var applies = Test(branch.Condition, out var trace);
                    Observe(DecisionKind.Branch, step.Id,
                        applies ? DecisionOutcome.Passed : DecisionOutcome.Failed,
                        applies ? DecisionReason.ConditionTrue : DecisionReason.ConditionFalse, trace, log.Events.Count);
                    foreach (var skipped in applies ? branch.Else : branch.Then)
                        Skip(skipped, DecisionReason.ConditionFalse);
                    target = Execute(applies ? branch.Then : branch.Else, blocks, choices, refresh, out stopReason);
                    break;
                case ChoiceStep choice:
                    var available = Test(choice.Choice.Requires, out var requirement);
                    Observe(DecisionKind.Choice, step.Id, available ? DecisionOutcome.Offered : DecisionOutcome.Excluded,
                        choice.Choice.Requires is null ? DecisionReason.Unconditional
                            : available ? DecisionReason.ConditionTrue : DecisionReason.ConditionFalse,
                        requirement, log.Events.Count);
                    if (available)
                    {
                        choices.Add(new(choice, new ChoiceView(choice.Choice.Key,
                            renderer.RenderInlines(choice.Choice.Label, log.State))));
                    }
                    break;
                case DivertStep divert:
                    Observe(DecisionKind.Divert, step.Id, DecisionOutcome.Selected,
                        DecisionReason.Unconditional, null, log.Events.Count);
                    target = divert.Target;
                    stopReason = DecisionReason.EarlierDivert;
                    break;
                case CheckStep check:
                    if (!refresh)
                    {
                        var mark = log.Events.Count;
                        target = ResolveCheck(check.Check, step.Id);
                        if (rememberEntry) _checks[step.Id] = target;
                        AppendNotes(step.Id, mark, blocks);
                    }
                    else
                    {
                        if (!_checks.TryGetValue(step.Id, out target))
                            throw new InvalidOperationException("This edit reaches an unresolved entry check. Start a fresh scene with prerequisite overrides instead.");
                        blocks.AddRange(_notes.GetValueOrDefault(step.Id) ?? []);
                    }
                    stopReason = DecisionReason.EntryCheck;
                    break;
                default:
                    throw new NotSupportedException($"Unknown scene step '{step.GetType().Name}'.");
            }
        }
        return target;
    }

    public void ApplyBatch(IReadOnlyList<Effect> effects)
    {
        EffectExecutor.ExecuteAll(effects, log, story.Predicates);
        afterBatch();
    }

    public DivertTarget? ResolveCheck(CheckBranch check, string stepId)
    {
        var result = SkillCheckResolver.Resolve(check.Check, log, story.Randomness, story.Predicates,
            observer is null ? null : decision => observer(decision with { Subject = stepId }));
        var outcome = result.Success ? check.Success : check.Failure;
        var mark = log.Events.Count;
        ApplyBatch(outcome.Effects);
        Observe(DecisionKind.CheckOutcome, stepId, DecisionOutcome.Applied,
            result.Success ? DecisionReason.CheckSucceeded : DecisionReason.CheckFailed, null, mark);
        return outcome.Divert;
    }

    private bool Test(Expr? expression, out ExpressionTrace? trace) =>
        RuleObservation.Test(expression, new(log.State, story.Predicates), observer is not null, out trace);

    private void Observe(DecisionKind kind, string subject, DecisionOutcome outcome, DecisionReason reason,
        ExpressionTrace? expression, int mark) =>
        observer?.Invoke(new(kind, subject, log.State.CurrentScene, outcome, reason, expression, mark, log.Events.Count));

    private void Skip(SceneStep step, DecisionReason reason)
    {
        if (observer is null) return;
        var kind = step switch
        {
            EffectStep => DecisionKind.EntryEffect,
            ConditionalStep => DecisionKind.Branch,
            ChoiceStep => DecisionKind.Choice,
            DivertStep => DecisionKind.Divert,
            CheckStep => DecisionKind.Check,
            ProseStep => (DecisionKind?)null,
            _ => throw new NotSupportedException($"Unknown scene step '{step.GetType().Name}'."),
        };
        var condition = step switch
        {
            ConditionalStep branch => branch.Condition,
            ChoiceStep choice => choice.Choice.Requires,
            _ => null,
        };
        if (kind is { } value)
            Observe(value, step.Id, DecisionOutcome.NotEvaluated, reason,
                condition is null ? null : ExpressionTrace.NotEvaluated(condition), log.Events.Count);
        if (step is ConditionalStep nested)
            foreach (var child in nested.Then.Concat(nested.Else)) Skip(child, reason);
    }

    private void AppendNotes(string stepId, int mark, List<RenderedBlock> blocks)
    {
        var notes = EventNarrator.Describe(log.Events.Skip(mark).ToList(), log.State).ToArray();
        if (rememberEntry) _notes[stepId] = notes;
        blocks.AddRange(notes);
    }
}
