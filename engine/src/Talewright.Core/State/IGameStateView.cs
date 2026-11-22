using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;
using Talewright.Core;

namespace Talewright.Core.State;

/// <summary>
/// Read-only view of the current game state. Rules, templates and the UI only ever see this interface.
/// </summary>
public interface IGameStateView
{
    WorldDefinition World { get; }

    Ruleset Ruleset => World.Ruleset;

    /// <summary>Seed for keyed deterministic dice rolls.</summary>
    ulong Seed { get; }

    /// <summary>How many dice have been rolled so far; informational only, not a random cursor.</summary>
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

    /// <summary>Returns the legacy int32 projection of a numeric variable.</summary>
    int GetVariable(VarId variable);

    /// <summary>Returns the exact numeric value. Version-2 state views must implement this member.</summary>
    ExactNumber GetExactVariable(VarId variable) => World.TalemarkVersion == 1
        ? ExactNumber.FromInteger(GetVariable(variable))
        : throw new NotSupportedException("This state view does not expose exact numeric variables.");

    string GetTextVariable(VarId variable);

    /// <summary>Number of times a scene has been entered.</summary>
    int VisitCount(SceneId scene);

    bool HasFiredTrigger(string triggerId);
}
