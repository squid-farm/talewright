using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.State;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

internal static class StatDisclosureVisibility
{
    internal static StatId? StatFor(GameEvent gameEvent, Ruleset ruleset) => gameEvent switch
    {
        StatChanged changed => changed.Stat,
        SkillCheckResolved check => check.Skill,
        LevelChanged when ruleset.LevelingRule is { } leveling => leveling.LevelStat,
        _ => null,
    };

    internal static bool IsVisible(StoryDefinition story, GameEvent gameEvent, IGameStateView state) =>
        StatFor(gameEvent, story.World.Ruleset) is not { } stat || story.IsStatVisible(stat, state);
}
