using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Grammar;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Runtime;
using Talewright.Talemark.Model;

namespace Talewright.Debugger;

/// <summary>Preview of a scene's prose and available choices rendered against a read-only game state.</summary>
public sealed record MapScenePreview(
    string Scene,
    string? Title,
    RenderedDocument Document,
    IReadOnlyList<ChoiceView> Choices,
    bool UsesCurrentRunState);

/// <summary>Builds a scene preview without executing entry effects or skill checks.</summary>
public static class MapScenePreviewBuilder
{
    public static MapScenePreview Build(
        StoryDefinition story,
        SceneId sceneId,
        IGameStateView state,
        bool usesCurrentRunState)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(state);

        var scene = story.GetScene(sceneId);
        var details = StoryMap.SceneDetails(scene);
        var prose = details.Prose.ToDictionary(item => item.StepId, StringComparer.Ordinal);
        var choices = details.Choices.ToDictionary(item => item.StepId, StringComparer.Ordinal);
        var renderer = new TemplateRenderer(new EnglishGrammar(), story.Predicates);
        var context = new EvaluationContext(state, story.Predicates);
        var blocks = new List<TemplateBlock>();
        var renderedChoices = new List<ChoiceView>();

        void Walk(IEnumerable<SceneStep> steps)
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case ConditionalStep branch:
                        Walk(ExpressionEvaluator.IsTrue(branch.Condition, context) ? branch.Then : branch.Else);
                        break;
                    case ProseStep:
                        blocks.Add(prose[step.Id].Template);
                        break;
                    case ChoiceStep choice when choice.Choice.Requires is null
                        || ExpressionEvaluator.IsTrue(choice.Choice.Requires, context):
                    {
                        var detail = choices[choice.Id];
                        var label = detail.Label is { } template
                            ? renderer.RenderInlines(template, state)
                            : Array.AsReadOnly<RenderedInline>(
                                [new RenderedText(ChoiceDefinition.ContinueLabelText, TextStyle.None)]);
                        renderedChoices.Add(new ChoiceView(detail.Key, label));
                        break;
                    }
                }
            }
        }

        Walk(scene.Steps);
        return new MapScenePreview(
            sceneId.Value,
            details.Title,
            renderer.Render(blocks, state),
            Array.AsReadOnly(renderedChoices.ToArray()),
            usesCurrentRunState);
    }
}
