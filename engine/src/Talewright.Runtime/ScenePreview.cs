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
                        Walk(ExpressionEvaluator.IsTrue(branch.Condition, context) ? branch.Then : branch.Else);
                        break;
                    case CheckConditionalStep:
                        break;
                    case ProseStep prose:
                        blocks.Add(prose.Block);
                        break;
                    case ChoiceStep choiceStep:
                    {
                        var choice = choiceStep.Choice;
                        Func<IReadOnlyList<RenderedInline>> label;
                        if (choice.Label is { } text)
                        {
                            label = () => renderer.RenderInlines(text, state);
                        }
                        else if (choice.Check is null && choice.Divert?.Scene is { } target &&
                                 story.GetScene(target).ChoiceLabel is { } choiceLabel)
                        {
                            label = () => renderer.RenderInlines(choiceLabel, state);
                        }
                        else
                        {
                            label = static () => ChoiceView.ContinueLabel;
                        }

                        var candidate = new ChoiceCandidate(
                            choice.Key,
                            choice.Requires,
                            label,
                            ChoiceAvailability.ShownLabelSource(choice.Shown, label,
                                text => renderer.RenderInlines(text, state)),
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
            observer: null, eventIndex: 0);
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
            ? () => renderer.RenderInlines(choiceLabel, state)
            : () => [new RenderedText(storylet.Title ?? storylet.Id.Value, TextStyle.None)];
        return new ChoiceCandidate(key, storylet.Requires, label,
            ChoiceAvailability.ShownLabelSource(storylet.Shown, label,
                text => renderer.RenderInlines(text, state)),
            static _ => { },
            story.ReplayIdentities.ForDivert(scene.Id, target));
    }
}
