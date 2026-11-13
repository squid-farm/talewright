using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Grammar;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

/// <summary>Builds a read-only, effect-free preview of a scene as a normal story frame.</summary>
public static class ScenePreview
{
    /// <summary>
    /// Renders the scene's prose and builds its menu using the supplied state, without changing that state,
    /// running effects or skill checks, or following diverts.
    /// </summary>
    /// <param name="story">The compiled story containing the scene.</param>
    /// <param name="scene">The scene to preview.</param>
    /// <param name="state">The read-only state against which prose and conditions are evaluated.</param>
    public static StoryFrame Build(StoryDefinition story, SceneId scene, IGameStateView state)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(state);

        var definition = story.GetScene(scene);
        var renderer = new TemplateRenderer(new EnglishGrammar(), story.Predicates);
        var context = new EvaluationContext(state, story.Predicates);
        var blocks = new List<TemplateBlock>();
        var choices = new List<VisibleChoice>();

        void Walk(IEnumerable<SceneStep> steps)
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case ConditionalStep branch:
                        if (SkillCheckPreview.TryDescribe(branch.Condition, out var description))
                        {
                            blocks.Add(SkillCheckPreview.Marker(description));
                        }
                        else
                        {
                            Walk(ExpressionEvaluator.IsTrue(branch.Condition, context) ? branch.Then : branch.Else);
                        }
                        break;
                    case ProseStep prose:
                        blocks.Add(SkillCheckPreview.Sanitize(prose.Block));
                        break;
                    case ChoiceStep choiceStep:
                    {
                        var choice = choiceStep.Choice;
                        var requiresCheck = SkillCheckPreview.TryDescribe(choice.Requires, out var choiceDescription);
                        Func<IReadOnlyList<RenderedInline>> label;
                        if (choice.Label is { } text)
                        {
                            label = () => renderer.RenderInlines(SkillCheckPreview.Sanitize(text), state);
                        }
                        else if (choice.Check is null && choice.Divert?.Scene is { } target &&
                                 story.GetScene(target).ChoiceLabel is { } choiceLabel)
                        {
                            label = () => renderer.RenderInlines(SkillCheckPreview.Sanitize(choiceLabel), state);
                        }
                        else
                        {
                            label = static () => ChoiceView.ContinueLabel;
                        }

                        var candidate = new ChoiceCandidate(
                            choice.Key,
                            requiresCheck ? new BoolLiteral(false) : choice.Requires,
                            label,
                            requiresCheck
                                ? () => [new RenderedText(choiceDescription, TextStyle.None)]
                                : ChoiceAvailability.ShownLabelSource(choice.Shown, label,
                                    text => renderer.RenderInlines(SkillCheckPreview.Sanitize(text), state)),
                            static _ => { },
                            story.ReplayIdentities.For(choice));
                        var offered = ChoiceAvailability.Evaluate(candidate, context, capture: false, out _);
                        if (offered is not null)
                        {
                            choices.Add(offered);
                        }
                        break;
                    }
                }
            }
        }

        Walk(definition.Steps);
        var menu = StoryMenuBuilder.Build(story, definition, choices, context,
            storylet => CreateHubCandidate(story, definition, storylet, renderer, state),
            observer: null, eventIndex: 0, previewSkillChecks: true);
        return new StoryFrame(definition.Id, definition.Title, renderer.Render(blocks, state),
            menu.AvailableChoices, IsEnded: false)
        {
            ContinuesPrevious = definition.ContinuesPrevious,
            ClearsPrevious = definition.ClearsPrevious,
            Menu = menu.Entries,
        };
    }

    private static ChoiceCandidate CreateHubCandidate(
        StoryDefinition story,
        SceneDefinition scene,
        SceneDefinition storylet,
        TemplateRenderer renderer,
        IGameStateView state)
    {
        var key = StorySession.StoryletKeyPrefix + storylet.Id.Value;
        var target = new DivertTarget(storylet.Id);
        Func<IReadOnlyList<RenderedInline>> label = storylet.ChoiceLabel is { } choiceLabel
            ? () => renderer.RenderInlines(SkillCheckPreview.Sanitize(choiceLabel), state)
            : () => [new RenderedText(storylet.Title ?? storylet.Id.Value, TextStyle.None)];
        var requiresCheck = SkillCheckPreview.TryDescribe(storylet.Requires, out var description);
        return new ChoiceCandidate(key, requiresCheck ? new BoolLiteral(false) : storylet.Requires, label,
            ChoiceAvailability.ShownLabelSource(storylet.Shown, label,
                text => renderer.RenderInlines(SkillCheckPreview.Sanitize(text), state)),
            static _ => { },
            story.ReplayIdentities.ForDivert(scene.Id, target));
    }
}

internal static class SkillCheckPreview
{
    public static bool TryDescribe(Expr? expression, out string description)
    {
        var skills = new SortedSet<string>(StringComparer.Ordinal);
        if (expression is not null)
        {
            var pending = new Stack<Expr>();
            pending.Push(expression);
            while (pending.TryPop(out var current))
            {
                switch (current)
                {
                    case SkillCheckExpr check:
                        skills.Add(check.Skill.Value);
                        if (check.Difficulty is { } difficulty)
                            pending.Push(difficulty);
                        break;
                    case UnaryExpr unary:
                        pending.Push(unary.Operand);
                        break;
                    case BinaryExpr binary:
                        pending.Push(binary.Right);
                        pending.Push(binary.Left);
                        break;
                }
            }
        }

        if (skills.Count == 0)
        {
            description = string.Empty;
            return false;
        }

        var names = string.Join(" and ", skills);
        description = skills.Count == 1
            ? $"Preview depends on a {names} check."
            : $"Preview depends on {names} checks.";
        return true;
    }

    public static ParagraphTemplate Marker(string description) =>
        new([new TextTemplate(description)]);

    public static TemplateBlock Sanitize(TemplateBlock block) => block switch
    {
        ParagraphTemplate paragraph => paragraph with { Inlines = Sanitize(paragraph.Inlines) },
        SpeechTemplate speech => speech with { Inlines = Sanitize(speech.Inlines) },
        ConditionalBlockTemplate conditional when TryDescribe(conditional.Condition, out var description) =>
            Marker(description),
        ConditionalBlockTemplate conditional => conditional with
        {
            Then = conditional.Then.Select(Sanitize).ToArray(),
            Else = conditional.Else.Select(Sanitize).ToArray(),
        },
        _ => throw new NotSupportedException($"Unknown template block '{block.GetType().Name}'."),
    };

    public static IReadOnlyList<TemplateInline> Sanitize(IReadOnlyList<TemplateInline> inlines)
    {
        var result = new List<TemplateInline>(inlines.Count);
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case SubstitutionTemplate substitution:
                    result.Add(TryDescribe(substitution.Value, out var valueDescription)
                        ? new TextTemplate(valueDescription)
                        : substitution);
                    break;
                case StyledTemplate styled:
                    result.Add(styled with { Children = Sanitize(styled.Children) });
                    break;
                case MediaTemplate { AltTemplate: { } alt } media:
                    result.Add(media with { AltTemplate = Sanitize(alt) });
                    break;
                case ConditionalInlineTemplate conditional:
                    result.Add(TryDescribe(conditional.Condition, out var conditionDescription)
                        ? new TextTemplate(conditionDescription)
                        : conditional with
                        {
                            Then = Sanitize(conditional.Then),
                            Else = Sanitize(conditional.Else),
                        });
                    break;
                default:
                    result.Add(inline);
                    break;
            }
        }

        return result.AsReadOnly();
    }
}
