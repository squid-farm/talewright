using Talewright.Core.Identity;
using Talewright.Core.World;

namespace Talewright.Core.Events;

/// <summary>
/// Base type for everything that can happen in a game. Game state is a pure fold over these
/// (see <see cref="State.GameState.Apply"/>). Events record <em>results</em> (new values, dice faces),
/// never intentions, so folding an existing log reproduces the same state. Player saves store a seed and
/// choice history, then recreate the event log by replaying the story.
/// </summary>
public abstract record GameEvent;

/// <summary>Created protagonist identity, applied before any scene or trigger is evaluated.</summary>
public sealed record CharacterIdentityChanged(EntityId Character, string FirstName, string LastName, Pronouns Pronouns) : GameEvent;

/// <summary>A character's base stat was set to a new (already clamped) value.</summary>
public sealed record StatChanged(EntityId Character, StatId Stat, int OldValue, int NewValue) : GameEvent;

/// <summary>Items were added to a character's inventory.</summary>
public sealed record ItemsGranted(EntityId Character, ItemId Item, int Quantity) : GameEvent;

/// <summary>Items were removed from a character's inventory (quantity actually removed).</summary>
public sealed record ItemsRemoved(EntityId Character, ItemId Item, int Quantity) : GameEvent;

/// <summary>The player's standing with a faction changed.</summary>
public sealed record StandingChanged(FactionId Faction, int OldValue, int NewValue) : GameEvent;

public sealed record FlagChanged(FlagId Flag, bool Value) : GameEvent;

public sealed record VariableChanged(VarId Variable, int OldValue, int NewValue) : GameEvent;

public sealed record TextVariableChanged(VarId Variable, string OldValue, string NewValue) : GameEvent;

/// <summary>The story moved to a scene. Position is state, not a hard-coded pointer, so any scene can follow any scene.</summary>
public sealed record SceneEntered(SceneId Scene) : GameEvent;

/// <summary>The player picked a choice. <paramref name="ChoiceKey"/> is stable within a story version.</summary>
public sealed record ChoiceMade(SceneId Scene, string ChoiceKey) : GameEvent;

/// <summary>
/// A die was rolled. Advances the RNG cursor. Folding an existing event log reads <paramref name="Result"/>;
/// player-save restore instead replays choices with the saved seed and generates the event again.
/// </summary>
public sealed record DiceRolled(int Sides, int Result) : GameEvent;

public sealed record SkillCheckResolved(
    EntityId Actor,
    StatId Skill,
    int Difficulty,
    int Roll,
    int Modifier,
    int Total,
    bool Success) : GameEvent;

public sealed record LevelChanged(EntityId Character, int OldLevel, int NewLevel) : GameEvent;

/// <summary>A story trigger fired (recorded so <c>once</c> triggers stay fired after replay).</summary>
public sealed record TriggerFired(string TriggerId) : GameEvent;

public sealed record StoryEnded : GameEvent;
