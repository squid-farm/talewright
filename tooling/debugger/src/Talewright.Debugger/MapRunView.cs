using Talewright.Core.Events;
using Talewright.Rules.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Runtime;

namespace Talewright.Debugger;

/// <summary>Preview of one scene and selected map item in the current playtest route.</summary>
public sealed record MapRunStep(
    int Order,
    string Scene,
    string? TakenItemId,
    string? TakenItemName,
    string? LineId);

/// <summary>Preview of the current playtest route using visited scenes and selected map items.</summary>
public sealed record MapRunView(
    string StartScene,
    bool StartedMidStory,
    bool IsEnded,
    string? CurrentScene,
    IReadOnlyList<MapRunStep> Steps)
{
    public static MapRunView Build(StoryDefinition story, MapView map, AuthorSession session)
    {
        ArgumentNullException.ThrowIfNull(story);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(session);

        var storyMap = StoryMap.Build(story);
        var items = map.Nodes.SelectMany(node => node.Items).ToDictionary(item => item.Id, StringComparer.Ordinal);
        var divertItems = storyMap.Diverts
            .Where(divert => items.ContainsKey(divert.Id))
            .ToDictionary(divert => divert.Id, divert => items[divert.Id], StringComparer.Ordinal);
        var steps = new List<MapRunStep>();
        MapItem? pendingItem = null;

        MapItem? ItemFor(StoryDivert? divert) =>
            divert is not null && divertItems.TryGetValue(divert.Id, out var item) ? item : null;

        void Observe(RuleDecision decision)
        {
            if (decision.Scene is not { } scene)
                return;

            if (decision.Kind == DecisionKind.Divert && decision.Outcome == DecisionOutcome.Selected)
            {
                pendingItem = ItemFor(storyMap.Diverts.FirstOrDefault(divert =>
                    divert.From == scene && divert.StepId == decision.Subject && divert.Outcome is null));
            }
            else if (decision.Kind == DecisionKind.CheckOutcome)
            {
                var outcome = decision.Reason switch
                {
                    DecisionReason.CheckSucceeded => SkillCheckOutcome.Success,
                    DecisionReason.CheckFailed => SkillCheckOutcome.Failure,
                    _ => (SkillCheckOutcome?)null,
                };
                pendingItem = outcome is { } selected
                    ? ItemFor(storyMap.Diverts.FirstOrDefault(divert =>
                        divert.From == scene && divert.StepId == decision.Subject && divert.Outcome == selected))
                    : null;
            }
        }

        void AddScene(string scene)
        {
            steps.Add(new(steps.Count + 1, scene, pendingItem?.Id, pendingItem?.Name, pendingItem?.Line));
            pendingItem = null;
        }

        var decisions = session.Decisions.Where(decision => decision.EventStart is not null)
            .GroupBy(decision => decision.EventStart!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var events = session.Events;
        for (var index = 0; index <= events.Count; index++)
        {
            if (decisions.TryGetValue(index, out var observed))
                foreach (var decision in observed)
                    Observe(decision);
            if (index == events.Count)
                break;

            switch (events[index])
            {
                case ChoiceMade choice when choice.ChoiceKey == StorySession.ContinueKey:
                    break;
                case ChoiceMade choice when choice.ChoiceKey.StartsWith(StorySession.StoryletKeyPrefix, StringComparison.Ordinal):
                {
                    var target = choice.ChoiceKey[StorySession.StoryletKeyPrefix.Length..];
                    var offer = storyMap.HubOffers.FirstOrDefault(candidate =>
                        candidate.Hub == choice.Scene && candidate.Scene.Value == target);
                    pendingItem = offer is null ? null : items.GetValueOrDefault(offer.Id);
                    break;
                }
                case ChoiceMade choice:
                {
                    var selected = storyMap.Diverts.Where(divert =>
                        divert.From == choice.Scene && divert.ChoiceKey == choice.ChoiceKey).ToArray();
                    pendingItem = selected.Length == 1 && selected[0].Outcome is null
                        ? ItemFor(selected[0])
                        : null;
                    break;
                }
                case SceneEntered entered:
                    AddScene(entered.Scene.Value);
                    break;
                case StoryEnded when pendingItem?.Target == "END":
                    AddScene("END");
                    break;
                case StoryEnded:
                    pendingItem = null;
                    break;
            }
        }

        return new MapRunView(
            session.StartScene.Value,
            session.StartScene != story.Start,
            session.State.IsEnded,
            steps.LastOrDefault()?.Scene,
            Array.AsReadOnly(steps.ToArray()));
    }
}
