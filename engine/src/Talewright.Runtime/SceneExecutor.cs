using Talewright.Core.Events;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

internal sealed record SceneExecution(IReadOnlyList<RenderedBlock> Blocks,
    IReadOnlyList<VisibleChoice> Choices, DivertTarget? Target, bool AutomaticallyContinue);
internal sealed record CheckExecution(DivertTarget? Target, bool TriggerDiverted);

/// <summary>Executes authored steps; StorySession owns entry, transitions, history and presentation.</summary>
internal sealed class SceneExecutor(StoryDefinition story, EventLog log, TemplateRenderer renderer,
    Func<DivertTarget?> afterBatch, Action<RuleDecision>? observer, bool rememberEntry,
    Action<SceneDefinition, ChoiceStep, List<RenderedBlock>> makeChoice,
    Func<int, IEnumerable<RenderedBlock>> notesSince)
{
    private readonly ReplayIdentityTable _replayIdentities = story.ReplayIdentities;
    private readonly Dictionary<string, IReadOnlyList<RenderedBlock>> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckExecution> _checks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DivertTarget?> _effectDiverts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<RenderedBlock>> _prose = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _branches = new(StringComparer.Ordinal);
    private readonly List<string> _visits = [];
    private readonly HashSet<string> _settled = new(StringComparer.Ordinal);
    private readonly List<ChoiceStep> _replayChoices = [];
    private readonly Dictionary<ChoiceStep, ChoiceCandidate> _choiceCandidates =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<AuthorEntryReplayStep> _entryReplay = [];
    private readonly Func<int, IEnumerable<RenderedBlock>> _notesSince = notesSince;
    private Func<SkillCheckExpr, Value>? _resolveExpressionCheck;
    private Func<SkillCheckExpr, Value> ExpressionCheckResolver => _resolveExpressionCheck ??= ResolveExpressionCheck;
    internal EvaluationContext Context => new(log.State, story.Predicates)
    {
        ResolveSkillCheck = ExpressionCheckResolver,
    };
    public IReadOnlyList<ChoiceStep> ReplayChoices => _replayChoices;
    public IReadOnlyList<AuthorEntryReplayStep> EntryReplay => _entryReplay;

    /// <summary>
    /// Runs a scene. A refresh (author live edit) keeps every step up to the scene's last executed effect batch or
    /// check as recorded, and reevaluates only the steps after it, so an edit cannot rewrite text or branch decisions
    /// that ran before that state existed. Reaching an effect or check that never ran is rejected.
    /// </summary>
    public SceneExecution Run(SceneDefinition scene, bool refresh = false)
    {
        if (!refresh)
        {
            ResetEntry();
        }
        var blocks = new List<RenderedBlock>();
        var choices = new List<VisibleChoice>();
        _replayChoices.Clear();
        var target = Execute(scene, scene.Steps, blocks, choices, _replayChoices, refresh, out var stopReason);
        if (!refresh && rememberEntry)
        {
            var lastBatch = _visits.FindLastIndex(id => _notes.ContainsKey(id));
            _settled.UnionWith(_visits.Take(lastBatch + 1));
        }
        return new(new RenderedDocument(blocks).TrimUnboundedBlankLines().Blocks, choices, target,
            target is not null && stopReason == DecisionReason.EarlierDivert);
    }

    public void ResetEntry()
    {
        _notes.Clear();
        _checks.Clear();
        _effectDiverts.Clear();
        _prose.Clear();
        _branches.Clear();
        _visits.Clear();
        _settled.Clear();
        _replayChoices.Clear();
        _entryReplay.Clear();
    }

    private DivertTarget? Execute(SceneDefinition scene, IReadOnlyList<SceneStep> steps, List<RenderedBlock> blocks,
        List<VisibleChoice> choices, List<ChoiceStep> replayChoices, bool refresh,
        out DecisionReason stopReason)
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
            var settled = refresh && _settled.Contains(step.Id);
            if (!refresh && rememberEntry)
            {
                _visits.Add(step.Id);
            }
            switch (step)
            {
                case ProseStep prose:
                    if (settled)
                    {
                        if (_notes.TryGetValue(step.Id, out var savedNotes))
                            blocks.AddRange(savedNotes);
                        blocks.AddRange(_prose[step.Id]);
                        break;
                    }
                    var renderMark = log.Events.Count;
                    var rendered = renderer.Render([prose.Block], Context).Blocks;
                    AppendSkillCheckNotes(step.Id, renderMark, blocks);
                    if (!refresh && rememberEntry) _prose[step.Id] = rendered;
                    blocks.AddRange(rendered);
                    break;
                case EffectStep effect:
                    if (!refresh)
                    {
                        if (rememberEntry) _entryReplay.Add(new("effect", effect.Effects));
                        var mark = log.Events.Count;
                        target = ApplyBatch(effect.Effects);
                        if (rememberEntry) _effectDiverts[step.Id] = target;
                        Observe(DecisionKind.EntryEffect, step.Id, DecisionOutcome.Applied,
                            DecisionReason.Unconditional, null, mark);
                        AppendNotes(step.Id, mark, blocks);
                        if (target is not null)
                            stopReason = DecisionReason.EarlierDivert;
                    }
                    else
                    {
                        RequireSettled(settled);
                        blocks.AddRange(_notes[step.Id]);
                        target = _effectDiverts.GetValueOrDefault(step.Id);
                        if (target is not null)
                            stopReason = DecisionReason.EarlierDivert;
                    }
                    break;
                case ConditionalStep branch:
                    bool applies;
                    if (settled)
                    {
                        applies = _branches[step.Id];
                        if (_notes.TryGetValue(step.Id, out var savedNotes))
                            blocks.AddRange(savedNotes);
                    }
                    else
                    {
                        var branchMark = log.Events.Count;
                        applies = Test(branch.Condition, out var trace);
                        AppendSkillCheckNotes(step.Id, branchMark, blocks);
                        if (!refresh && rememberEntry) _branches[step.Id] = applies;
                        Observe(DecisionKind.Branch, step.Id,
                            applies ? DecisionOutcome.Passed : DecisionOutcome.Failed,
                            applies ? DecisionReason.ConditionTrue : DecisionReason.ConditionFalse, trace, branchMark);
                        foreach (var skipped in applies ? branch.Else : branch.Then)
                            Skip(skipped, DecisionReason.ConditionFalse);
                    }
                    var replayMark = _entryReplay.Count;
                    target = Execute(scene, applies ? branch.Then : branch.Else, blocks, choices, replayChoices,
                        refresh, out stopReason);
                    if (!refresh && rememberEntry && _entryReplay.Count > replayMark)
                    {
                        _entryReplay.Insert(replayMark, new("condition", branch.Condition));
                        _entryReplay.Insert(replayMark + 1, new("branch", applies));
                    }
                    break;
                case ChoiceStep choice:
                    replayChoices.Add(choice);
                    var candidate = CreateCandidate(scene, choice);
                    var choiceMark = log.Events.Count;
                    if (refresh && _notes.TryGetValue(step.Id, out var priorNotes))
                        blocks.AddRange(priorNotes);
                    var offered = ChoiceAvailability.Evaluate(candidate,
                        Context, observer is not null, out var requirement);
                    AppendSkillCheckNotes(step.Id, choiceMark, blocks);
                    DecisionOutcome choiceOutcome;
                    if (offered is not null)
                    {
                        choiceOutcome = DecisionOutcome.Offered;
                    }
                    else
                    {
                        choiceOutcome = DecisionOutcome.Excluded;
                    }

                    DecisionReason choiceReason;
                    if (choice.Choice.Requires is null)
                    {
                        choiceReason = DecisionReason.Unconditional;
                    }
                    else if (offered?.IsAvailable == true)
                    {
                        choiceReason = DecisionReason.ConditionTrue;
                    }
                    else
                    {
                        choiceReason = DecisionReason.ConditionFalse;
                    }

                    Observe(DecisionKind.Choice, step.Id, choiceOutcome, choiceReason, requirement, choiceMark);
                    if (offered is not null)
                    {
                        choices.Add(offered);
                    }
                    break;
                case DivertStep divert:
                    if (!refresh && rememberEntry) _entryReplay.Add(new("divert", divert.Target));
                    Observe(DecisionKind.Divert, step.Id, DecisionOutcome.Selected,
                        DecisionReason.Unconditional, null, log.Events.Count);
                    target = divert.Target;
                    stopReason = DecisionReason.EarlierDivert;
                    break;
                case CheckStep check:
                    if (!refresh)
                    {
                        if (rememberEntry) _entryReplay.Add(new("check", check.Check));
                        var mark = log.Events.Count;
                        var result = ResolveCheck(check.Check, step.Id);
                        target = result.Target;
                        if (rememberEntry) _checks[step.Id] = result;
                        AppendNotes(step.Id, mark, blocks);
                        stopReason = result.TriggerDiverted
                            ? DecisionReason.EarlierDivert
                            : DecisionReason.EntryCheck;
                    }
                    else
                    {
                        RequireSettled(settled);
                        var result = _checks[step.Id];
                        target = result.Target;
                        blocks.AddRange(_notes[step.Id]);
                        stopReason = result.TriggerDiverted
                            ? DecisionReason.EarlierDivert
                            : DecisionReason.EntryCheck;
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unknown scene step '{step.GetType().Name}'.");
            }
        }
        return target;
    }

    public DivertTarget? ApplyBatch(IReadOnlyList<Effect> effects)
    {
        EffectExecutor.ExecuteAll(effects, log, story.Predicates, ExpressionCheckResolver);
        return afterBatch();
    }

    public CheckExecution ResolveCheck(CheckBranch check, string stepId)
    {
        var result = SkillCheckResolver.Resolve(check.Check, check.Identity, log, story.Randomness, story.Predicates,
            observer is null ? null : decision => observer(decision with { Subject = stepId }));
        var success = result.Success
            ?? throw new InvalidOperationException("A check with no difficulty cannot select a success or failure outcome.");
        var outcome = success ? check.Success : check.Failure;
        var mark = log.Events.Count;
        var triggerDivert = ApplyBatch(outcome.Effects);
        Observe(DecisionKind.CheckOutcome, stepId, DecisionOutcome.Applied,
            success ? DecisionReason.CheckSucceeded : DecisionReason.CheckFailed, null, mark);
        return new CheckExecution(triggerDivert ?? outcome.Divert, triggerDivert is not null);
    }

    public ChoiceView View(ChoiceDefinition choice) =>
        ChoiceAvailability.CreateView(choice.Key, LabelSource(choice), _replayIdentities.For(choice));

    public IReadOnlyList<RenderedInline> Render(IReadOnlyList<TemplateInline> text) =>
        renderer.RenderInlines(text, Context);

    private ChoiceCandidate CreateCandidate(SceneDefinition scene, ChoiceStep step)
    {
        if (_choiceCandidates.TryGetValue(step, out var cached))
        {
            return cached;
        }

        var label = LabelSource(step.Choice);
        var candidate = new ChoiceCandidate(step.Choice.Key, step.Choice.Requires, label,
            ChoiceAvailability.ShownLabelSource(step.Choice.Shown, label, Render),
            notes => makeChoice(scene, step, notes),
            _replayIdentities.For(step.Choice), new ChoiceStepExecution(scene, step));
        _choiceCandidates.Add(step, candidate);
        return candidate;
    }

    private Func<IReadOnlyList<RenderedInline>> LabelSource(ChoiceDefinition choice)
    {
        if (choice.Label is { } label)
        {
            return () => renderer.RenderInlines(label, Context);
        }

        if (choice.Check is null && choice.Divert?.Scene is { } target &&
            story.GetScene(target).ChoiceLabel is { } choiceLabel)
        {
            return () => renderer.RenderInlines(choiceLabel, Context);
        }

        return static () => ChoiceView.ContinueLabel;
    }

    private static void RequireSettled(bool settled)
    {
        if (!settled)
        {
            throw new InvalidOperationException("This edit reaches an effect or check that did not run in this passage. Start a fresh scene with prerequisite overrides instead.");
        }
    }

    private bool Test(Expr? expression, out ExpressionTrace? trace) =>
        RuleObservation.Test(expression, Context, observer is not null, out trace);

    internal Value ResolveExpressionCheck(SkillCheckExpr expression)
    {
        var check = new SkillCheck(expression.Actor, expression.Skill, expression.Difficulty, expression.IsHidden);
        var result = SkillCheckResolver.Resolve(check, expression.Identity, log, story.Randomness, story.Predicates,
            observer);
        return expression.Type == ExprType.Number
            ? Value.Of(result.Total)
            : Value.Of(result.Success
                ?? throw new InvalidOperationException("A check with a difficulty must produce a success result."));
    }

    private void AppendSkillCheckNotes(string stepId, int mark, List<RenderedBlock> blocks)
    {
        for (var index = mark; index < log.Events.Count; index++)
        {
            if (log.Events[index] is SkillCheckResolved)
            {
                AppendNotes(stepId, mark, blocks);
                return;
            }
        }
    }

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
        var notes = _notesSince(mark).ToArray();
        if (rememberEntry) _notes[stepId] = notes;
        blocks.AddRange(notes);
    }
}
