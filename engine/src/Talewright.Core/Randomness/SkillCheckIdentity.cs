using System.Globalization;
using Talewright.Core.Identity;

namespace Talewright.Core.Randomness;

/// <summary>A skill check's stable source identity, independent of prose, difficulty and source location.</summary>
public readonly record struct SkillCheckIdentity(SceneId Scene, EntityId Actor, StatId Skill, int Occurrence)
{
    /// <summary>A length-prefixed key so identifier contents cannot make distinct checks collide.</summary>
    public string ToKey() =>
        $"skill-check:{Scene.Value.Length}:{Scene.Value}{Actor.Value.Length}:{Actor.Value}{Skill.Value.Length}:{Skill.Value}{Occurrence.ToString(CultureInfo.InvariantCulture)}";
}
