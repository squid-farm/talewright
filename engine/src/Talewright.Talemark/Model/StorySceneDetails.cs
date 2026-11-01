using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Talemark.Formatting;

namespace Talewright.Talemark.Model;

/// <summary>Preview. Source-ordered, compiled details for one scene.</summary>
public sealed record StorySceneDetails(
    SceneId Id,
    string? Title,
    IReadOnlyList<string> Tags,
    StoryCondition Requires,
    bool Once,
    int Priority,
    StoryletTags? Hub,
    int? HubLimit,
    IReadOnlyList<StorySceneProse> Prose,
    IReadOnlyList<StorySceneEffect> Effects,
    IReadOnlyList<StorySceneChoice> Choices,
    IReadOnlyList<StorySceneEntryCheck> EntryChecks,
    IReadOnlyList<EntityId> Speakers)
{
    internal static StorySceneDetails From(SceneDefinition scene)
    {
        var proseTemplates = new List<StorySceneProse>();
        var effects = new List<StorySceneEffect>();
        var choices = new List<StorySceneChoice>();
        var entryChecks = new List<StorySceneEntryCheck>();
        var speakers = new List<EntityId>();
        var seenSpeakers = new HashSet<EntityId>();
        var divertsByStep = SceneSteps.Diverts(scene).GroupBy(divert => divert.StepId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var order = 0;
        foreach (var (step, blocks) in SceneSteps.WalkWithBlocks(scene.Steps))
        {
            var stepOrder = order++;
            var blockCondition = StoryCondition.FromExpressions(blocks.Select(BlockExpression));
            switch (step)
            {
                case ProseStep prose:
                    proseTemplates.Add(new StorySceneProse(step.Id, stepOrder, prose.Block, blockCondition));
                    AddSpeakers(prose.Block, speakers, seenSpeakers);
                    break;
                case EffectStep effect:
                    if (effect.Effects.Count > 0)
                        effects.Add(new StorySceneEffect(step.Id, stepOrder, effect.Effects, blockCondition, null, null));
                    break;
                case ChoiceStep choiceStep when choiceStep.Choice.Key == ChoiceDefinition.StructuralPlaceholderKey:
                    break;
                case ChoiceStep choiceStep:
                {
                    var choice = choiceStep.Choice;
                    var conditionParts = blocks.Select(BlockExpression).ToList();
                    if (choice.Requires is { } choiceRequires)
                        conditionParts.Add(choiceRequires);
                    var condition = StoryCondition.FromExpressions(conditionParts);
                    divertsByStep.TryGetValue(step.Id, out var diverts);
                    if (choice.Effects.Count > 0)
                        effects.Add(new StorySceneEffect(step.Id, stepOrder, choice.Effects, condition, null, choice.Key));
                    StorySceneCheckOutcome? success = null;
                    StorySceneCheckOutcome? failure = null;
                    if (choice.Check is { } choiceCheck)
                    {
                        success = CheckOutcome(choiceCheck.Success, SkillCheckOutcome.Success, step.Id, stepOrder,
                            condition, diverts ?? [], effects, choice.Key);
                        failure = CheckOutcome(choiceCheck.Failure, SkillCheckOutcome.Failure, step.Id, stepOrder,
                            condition, diverts ?? [], effects, choice.Key);
                    }
                    choices.Add(new StorySceneChoice(
                        step.Id,
                        stepOrder,
                        choice.Key,
                        choice.Label,
                        condition,
                        choice.Effects,
                        diverts?.Select(divert => divert.Id).ToArray() ?? [],
                        choice.Check?.Check,
                        success,
                        failure));
                    break;
                }
                case CheckStep checkStep:
                {
                    var check = checkStep.Check;
                    var diverts = divertsByStep.GetValueOrDefault(step.Id) ?? [];
                    var success = CheckOutcome(check.Success, SkillCheckOutcome.Success, step.Id, stepOrder,
                        blockCondition, diverts, effects, null);
                    var failure = CheckOutcome(check.Failure, SkillCheckOutcome.Failure, step.Id, stepOrder,
                        blockCondition, diverts, effects, null);
                    entryChecks.Add(new StorySceneEntryCheck(step.Id, stepOrder, check.Check, blockCondition, success, failure));
                    break;
                }
            }
        }

        return new StorySceneDetails(
            scene.Id,
            scene.Title,
            Array.AsReadOnly(scene.Tags.Order(StringComparer.Ordinal).ToArray()),
            StoryCondition.FromExpressions(scene.Requires is { } requires ? [requires] : []),
            scene.Once,
            scene.Priority,
            scene.Hub,
            scene.HubLimit,
            Array.AsReadOnly(proseTemplates.ToArray()),
            Array.AsReadOnly(effects.ToArray()),
            Array.AsReadOnly(choices.ToArray()),
            Array.AsReadOnly(entryChecks.ToArray()),
            Array.AsReadOnly(speakers.ToArray()));
    }

    private static StorySceneCheckOutcome CheckOutcome(
        CheckOutcome outcome,
        SkillCheckOutcome kind,
        string stepId,
        int stepOrder,
        StoryCondition condition,
        IReadOnlyList<StoryDivert> diverts,
        List<StorySceneEffect> effects,
        string? choiceKey)
    {
        var divertId = diverts.FirstOrDefault(divert => divert.Outcome == kind)?.Id;
        if (outcome.Effects.Count > 0)
            effects.Add(new StorySceneEffect(stepId, stepOrder, outcome.Effects, condition, kind, choiceKey));
        return new StorySceneCheckOutcome(kind, outcome.Effects, divertId);
    }

    private static Expr BlockExpression(DivertBlockCondition block) =>
        block.InElse ? new UnaryExpr(UnaryOp.Not, block.Condition) : block.Condition;

    private static void AddSpeakers(TemplateBlock block, List<EntityId> speakers, HashSet<EntityId> seen)
    {
        switch (block)
        {
            case SpeechTemplate speech when seen.Add(speech.Speaker):
                speakers.Add(speech.Speaker);
                break;
            case ConditionalBlockTemplate conditional:
                foreach (var child in conditional.Then.Concat(conditional.Else))
                    AddSpeakers(child, speakers, seen);
                break;
        }
    }
}

/// <summary>Preview. A prose template and its enclosing block conditions, kept compiled and unrendered.</summary>
public sealed record StorySceneProse(string StepId, int Order, TemplateBlock Template, StoryCondition Condition);

/// <summary>Preview. An ordered scene effect batch, choice effect batch or check outcome effect batch.</summary>
/// <param name="StepId">The source step containing the effects.</param>
/// <param name="Order">The zero-based position of that step among the scene's walked steps.</param>
/// <param name="Effects">The compiled effect batch.</param>
/// <param name="Condition">The enclosing block conditions, plus choice <c>requires</c> for choice effects.</param>
/// <param name="Outcome">The skill-check outcome for outcome effects; otherwise null.</param>
/// <param name="ChoiceKey">The choice key for choice effects; otherwise null.</param>
public sealed record StorySceneEffect(
    string StepId,
    int Order,
    IReadOnlyList<Effect> Effects,
    StoryCondition Condition,
    SkillCheckOutcome? Outcome,
    string? ChoiceKey);

/// <summary>Preview. A choice, including its compiled label, conditions, effects, divert ids and optional choice skill check.</summary>
public sealed record StorySceneChoice(
    string StepId,
    int Order,
    string Key,
    IReadOnlyList<TemplateInline>? Label,
    StoryCondition Condition,
    IReadOnlyList<Effect> Effects,
    IReadOnlyList<string> DivertIds,
    SkillCheck? SkillCheck,
    StorySceneCheckOutcome? Success,
    StorySceneCheckOutcome? Failure);

/// <summary>Preview. An entry check, its enclosing conditions and effects and optional diverts for each outcome.</summary>
public sealed record StorySceneEntryCheck(
    string StepId,
    int Order,
    SkillCheck Check,
    StoryCondition Condition,
    StorySceneCheckOutcome Success,
    StorySceneCheckOutcome Failure);

/// <summary>Preview. One entry check or choice skill check outcome, including its effects and optional divert id.</summary>
public sealed record StorySceneCheckOutcome(
    SkillCheckOutcome Outcome,
    IReadOnlyList<Effect> Effects,
    string? DivertId);
