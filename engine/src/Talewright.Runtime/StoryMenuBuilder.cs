using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Diagnostics;
using Talewright.Rules.Expressions;
using Talewright.Rules.Storylets;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

internal sealed record StoryMenu(
    IReadOnlyList<VisibleChoice> Choices,
    IReadOnlyList<ChoiceView> AvailableChoices,
    IReadOnlyList<ChoiceMenuEntry> Entries);

internal static class StoryMenuBuilder
{
    public static StoryMenu Build(
        StoryDefinition story,
        SceneDefinition scene,
        IReadOnlyList<VisibleChoice> choices,
        EvaluationContext context,
        Func<SceneDefinition, ChoiceCandidate> createHubCandidate,
        Action<RuleDecision>? observer,
        int eventIndex,
        bool includeHubOffers = true,
        bool previewSkillChecks = false)
    {
        var offered = choices.Select((choice, index) =>
            (Choice: choice, Priority: 0, IsGenerated: false, Order: index)).ToList();
        if (includeHubOffers && scene.Hub is { } hub)
        {
            var hubOffers = new Dictionary<SceneId, VisibleChoice>();
            var declarationOrder = story.Scenes
                .Select((storylet, index) => (storylet.Id, index))
                .ToDictionary(entry => entry.Id, entry => entry.index);
            var storylets = StoryletSelector.SelectMenuCandidates(story.Scenes, hub, context, scene.Id,
                scene.HubLimit, observer, eventIndex, storylet =>
                {
                    var candidate = createHubCandidate(storylet);
                    if (previewSkillChecks &&
                        SkillCheckPreview.TryDescribe(storylet.Requires, out var description))
                    {
                        candidate = candidate with
                        {
                            Requirement = new BoolLiteral(false),
                            ShownLabelSource = () => [new RenderedText(description, TextStyle.None)],
                        };
                        var unresolved = ChoiceAvailability.Evaluate(candidate, context,
                            capture: false, out var unresolvedTrace);
                        if (unresolved is not null)
                        {
                            hubOffers.Add(storylet.Id, unresolved);
                        }

                        return (false, unresolved is not null, unresolvedTrace);
                    }

                    var offer = ChoiceAvailability.Evaluate(candidate, context,
                        observer is not null, out var trace);
                    if (offer is not null)
                    {
                        hubOffers.Add(storylet.Id, offer);
                    }

                    return (offer?.IsAvailable ?? false, offer is { IsAvailable: false }, trace);
                });
            foreach (var storylet in storylets)
            {
                offered.Add((hubOffers[storylet.Storylet.Id], storylet.Storylet.Priority, true,
                    declarationOrder[storylet.Storylet.Id]));
            }
        }

        var ordered = offered
            .OrderByDescending(entry => entry.Priority)
            .ThenBy(entry => entry.IsGenerated)
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Choice)
            .ToArray();
        var entries = ordered.Select((choice, index) =>
            new ChoiceMenuEntry(index + 1, choice.View.Key, choice.View.Label, choice.IsAvailable)).ToArray();
        var available = ordered.Where(choice => choice.IsAvailable).Select(choice => choice.View).ToArray();
        return new(
            Array.AsReadOnly(ordered),
            Array.AsReadOnly(available),
            Array.AsReadOnly(entries));
    }
}
