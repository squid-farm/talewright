using Talewright.Core.Identity;
using Talewright.Narrative.Rendering;
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
        bool includeHubOffers = true)
    {
        var offered = new List<VisibleChoice>(choices);
        if (includeHubOffers && scene.Hub is { } hub)
        {
            var hubOffers = new Dictionary<SceneId, VisibleChoice>();
            var storylets = StoryletSelector.SelectMenuCandidates(story.Scenes, hub, context, scene.Id,
                scene.HubLimit, observer, eventIndex, storylet =>
                {
                    var offer = ChoiceAvailability.Evaluate(createHubCandidate(storylet), context,
                        observer is not null, out var trace);
                    if (offer is not null)
                    {
                        hubOffers.Add(storylet.Id, offer);
                    }

                    return (offer?.IsAvailable ?? false, offer is { IsAvailable: false }, trace);
                });
            foreach (var storylet in storylets)
            {
                offered.Add(hubOffers[storylet.Storylet.Id]);
            }
        }

        var entries = offered.Select((choice, index) =>
            new ChoiceMenuEntry(index + 1, choice.View.Key, choice.View.Label, choice.IsAvailable)).ToArray();
        var available = offered.Where(choice => choice.IsAvailable).Select(choice => choice.View).ToArray();
        return new(
            Array.AsReadOnly(offered.ToArray()),
            Array.AsReadOnly(available),
            Array.AsReadOnly(entries));
    }
}
