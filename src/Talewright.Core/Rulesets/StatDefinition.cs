using Talewright.Core.Identity;

namespace Talewright.Core.Rulesets;

/// <summary>Broad grouping for a stat. Used by the UI (character sheet sections) and by rulesets.</summary>
public enum StatCategory
{
    Attribute,
    Skill,
    /// <summary>A depletable pool such as hit points.</summary>
    Resource,
    /// <summary>Level, experience and similar.</summary>
    Progression,
    Custom,
}

/// <summary>
/// Declares one stat. Values are integers clamped to <see cref="Min"/>..<see cref="Max"/>.
/// </summary>
/// <param name="Id">Author-facing id, e.g. <c>dex</c>.</param>
/// <param name="Name">Display name, e.g. <c>Dexterity</c>.</param>
/// <param name="Category">Grouping.</param>
/// <param name="Min">Inclusive lower bound.</param>
/// <param name="Max">Inclusive upper bound.</param>
/// <param name="Default">Value a character starts with if the story does not set one.</param>
/// <param name="GoverningAttribute">For skills: the attribute whose modifier applies to checks.</param>
public sealed record StatDefinition(
    StatId Id,
    string Name,
    StatCategory Category,
    int Min = 0,
    int Max = int.MaxValue,
    int Default = 0,
    StatId? GoverningAttribute = null)
{
    public int Clamp(int value) => Math.Clamp(value, Min, Max);
}

/// <summary>
/// A named band of faction standing. A standing belongs to the highest tier whose
/// <see cref="Minimum"/> it reaches.
/// </summary>
public sealed record ReputationTier(string Id, string Name, int Minimum);
