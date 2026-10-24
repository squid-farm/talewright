using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;

namespace Talewright.Core.State;

/// <summary>
/// The current state of a playthrough. It is only ever changed by <see cref="Apply"/>, which makes the state a
/// pure fold over events: <c>state = events.Aggregate(GameState.CreateInitial(world, seed), Apply)</c>.
/// </summary>
/// <remarks>
/// Use <see cref="EventLog"/> rather than calling <see cref="Apply"/> directly so the log and the state stay in sync.
/// </remarks>
public sealed class GameState : IGameStateView
{
    private readonly Dictionary<EntityId, CharacterState> _characters = [];
    private readonly Dictionary<FactionId, int> _standings = [];
    private readonly HashSet<FlagId> _flags = [];
    private readonly Dictionary<VarId, int> _variables = [];
    private readonly Dictionary<VarId, string> _textVariables = [];
    private readonly Dictionary<SceneId, int> _visits = [];
    private readonly HashSet<string> _firedTriggers = [];

    private GameState(WorldDefinition world, ulong seed)
    {
        World = world;
        Seed = seed;
    }

    public WorldDefinition World { get; }
    public Ruleset Ruleset => World.Ruleset;
    public ulong Seed { get; }
    public int RollCount { get; private set; }
    public SceneId? CurrentScene { get; private set; }
    public bool IsEnded { get; private set; }

    /// <summary>Creates the state before any event, from the world's declarations.</summary>
    public static GameState CreateInitial(WorldDefinition world, ulong seed)
    {
        var state = new GameState(world, seed);
        foreach (var character in world.Characters.Values)
        {
            var cs = new CharacterState();
            foreach (var stat in world.Ruleset.Stats)
            {
                cs.Stats[stat.Id] = stat.Clamp(character.Stats.TryGetValue(stat.Id, out var v) ? v : stat.Default);
            }

            foreach (var (item, qty) in character.Inventory)
            {
                cs.Inventory[item] = qty;
            }

            state._characters[character.Id] = cs;
        }

        foreach (var faction in world.Factions.Values)
        {
            state._standings[faction.Id] = faction.InitialStanding;
        }

        foreach (var (id, value) in world.Variables)
        {
            state._variables[id] = value;
        }

        foreach (var (id, value) in world.TextVariables)
        {
            state._textVariables[id] = value;
        }

        return state;
    }

    /// <summary>Applies one event. This is the only mutation path.</summary>
    public void Apply(GameEvent gameEvent)
    {
        switch (gameEvent)
        {
            case CharacterIdentityChanged e:
                Character(e.Character).Definition = GetCharacterDefinition(e.Character) with
                {
                    FirstName = e.FirstName, LastName = e.LastName, Pronouns = e.Pronouns,
                };
                break;
            case StatChanged e:
                Character(e.Character).Stats[e.Stat] = e.NewValue;
                break;
            case LevelChanged e when World.Ruleset.LevelingRule is { } leveling:
                Character(e.Character).Stats[leveling.LevelStat] = e.NewLevel;
                break;
            case LevelChanged:
                break;
            case ItemsGranted e:
                var inv = Character(e.Character).Inventory;
                inv[e.Item] = inv.GetValueOrDefault(e.Item) + e.Quantity;
                break;
            case ItemsRemoved e:
                var inventory = Character(e.Character).Inventory;
                var left = inventory.GetValueOrDefault(e.Item) - e.Quantity;
                if (left > 0)
                {
                    inventory[e.Item] = left;
                }
                else
                {
                    inventory.Remove(e.Item);
                }

                break;
            case StandingChanged e:
                _standings[e.Faction] = e.NewValue;
                break;
            case FlagChanged e:
                if (e.Value)
                {
                    _flags.Add(e.Flag);
                }
                else
                {
                    _flags.Remove(e.Flag);
                }

                break;
            case VariableChanged e:
                if (World.TextVariables.ContainsKey(e.Variable))
                {
                    throw new InvalidOperationException($"Variable '{e.Variable}' holds text, not an integer.");
                }
                _variables[e.Variable] = e.NewValue;
                break;
            case TextVariableChanged e:
                if (!World.TextVariables.ContainsKey(e.Variable) || e.NewValue is null)
                {
                    throw new InvalidOperationException($"Variable '{e.Variable}' is not a declared text variable or its value is null.");
                }
                _textVariables[e.Variable] = e.NewValue;
                break;
            case SceneEntered e:
                CurrentScene = e.Scene;
                _visits[e.Scene] = _visits.GetValueOrDefault(e.Scene) + 1;
                break;
            case DiceRolled:
                RollCount++;
                break;
            case TriggerFired e:
                _firedTriggers.Add(e.TriggerId);
                break;
            case StoryEnded:
                IsEnded = true;
                break;
            case ChoiceMade or SkillCheckResolved:
                // Pure history; queried through the event log.
                break;
            default:
                throw new NotSupportedException($"Unknown event type {gameEvent.GetType().Name}.");
        }
    }

    public bool HasCharacter(EntityId id) => _characters.ContainsKey(id);

    public CharacterDefinition GetCharacterDefinition(EntityId id) =>
        Character(id).Definition ?? (World.Characters.TryGetValue(id, out var def)
            ? def : throw new KeyNotFoundException($"Unknown character '{id}'."));

    public int GetBaseStat(EntityId character, StatId stat) =>
        Character(character).Stats.TryGetValue(stat, out var value) ? value : 0;

    public int GetStat(EntityId character, StatId stat)
    {
        var value = GetBaseStat(character, stat);
        foreach (var (item, qty) in Character(character).Inventory)
        {
            if (qty > 0 && World.Items.TryGetValue(item, out var def) && def.Modifiers.TryGetValue(stat, out var bonus))
            {
                value += bonus;
            }
        }

        foreach (var modifier in World.Ruleset.Modifiers)
        {
            value += modifier.GetModifier(this, character, stat);
        }

        return World.Ruleset.StatsById.TryGetValue(stat, out var definition) ? definition.Clamp(value) : value;
    }

    public int GetItemCount(EntityId character, ItemId item) => Character(character).Inventory.GetValueOrDefault(item);

    public IEnumerable<KeyValuePair<ItemId, int>> GetInventory(EntityId character) =>
        Character(character).Inventory.Where(kv => kv.Value > 0);

    public int GetStanding(FactionId faction) => _standings.GetValueOrDefault(faction);

    public bool GetFlag(FlagId flag) => _flags.Contains(flag);

    public int GetVariable(VarId variable) => World.TextVariables.ContainsKey(variable)
        ? throw new InvalidOperationException($"Variable '{variable}' holds text, not an integer.")
        : _variables.GetValueOrDefault(variable);

    public string GetTextVariable(VarId variable) => _textVariables.TryGetValue(variable, out var value)
        ? value
        : throw new KeyNotFoundException($"Unknown text variable '{variable}'.");

    public int VisitCount(SceneId scene) => _visits.GetValueOrDefault(scene);

    public bool HasFiredTrigger(string triggerId) => _firedTriggers.Contains(triggerId);

    private CharacterState Character(EntityId id) =>
        _characters.TryGetValue(id, out var cs) ? cs : throw new KeyNotFoundException($"Unknown character '{id}'.");

    private sealed class CharacterState
    {
        public CharacterDefinition? Definition { get; set; }
        public Dictionary<StatId, int> Stats { get; } = [];
        public Dictionary<ItemId, int> Inventory { get; } = [];
    }
}
