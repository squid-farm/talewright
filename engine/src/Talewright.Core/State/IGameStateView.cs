using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;

namespace Talewright.Core.State;

/// <summary>
/// Read-only view of the current game state. Rules, templates and the UI only ever see this interface.
/// </summary>
public interface IGameStateView
{
    WorldDefinition World { get; }

    Ruleset Ruleset => World.Ruleset;

    /// <summary>Seed for the deterministic dice stream.</summary>
    ulong Seed { get; }

    /// <summary>How many dice have been rolled so far (the RNG cursor).</summary>
    int RollCount { get; }

    SceneId? CurrentScene { get; }

    bool IsEnded { get; }

    bool HasCharacter(EntityId id);

    CharacterDefinition GetCharacterDefinition(EntityId id);

    /// <summary>Raw stored value, without modifiers.</summary>
    int GetBaseStat(EntityId character, StatId stat);

    /// <summary>Effective value: base + held item modifiers + ruleset modifiers, clamped to the stat's bounds.</summary>
    int GetStat(EntityId character, StatId stat);

    int GetItemCount(EntityId character, ItemId item);

    /// <summary>Items with a positive count, in insertion order.</summary>
    IEnumerable<KeyValuePair<ItemId, int>> GetInventory(EntityId character);

    int GetStanding(FactionId faction);

    bool GetFlag(FlagId flag);

    int GetVariable(VarId variable);

    string GetTextVariable(VarId variable);

    /// <summary>Number of times a scene has been entered.</summary>
    int VisitCount(SceneId scene);

    bool HasFiredTrigger(string triggerId);
}
