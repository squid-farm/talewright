using Talewright.Core.Events;
using Talewright.Core;
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
    private readonly Dictionary<VarId, ExactNumber> _variables = [];
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
                var resource = world.TalemarkVersion == 2 && world.Ruleset.IsFractionalResource(stat.Id);
                if (resource)
                {
                    var initial = character.ExactStats is not null && character.ExactStats.TryGetValue(stat.Id, out var exactValue)
                        ? exactValue : character.Stats.TryGetValue(stat.Id, out var startingValue) ? startingValue : stat.ExactDefault ?? stat.Default;
                    cs.ExactStats[stat.Id] = stat.ClampExact(initial);
                }
                else
                    cs.Stats[stat.Id] = stat.Clamp(character.Stats.TryGetValue(stat.Id, out var value) ? value : stat.Default);
            }

            foreach (var (item, qty) in character.Inventory)
            {
                cs.Inventory[item] = qty;
            }
            if (character.ExactInventory is { } exactInventory)
                foreach (var (item, qty) in exactInventory)
                    cs.Inventory[item] = world.TalemarkVersion == 2 &&
                        world.Items.TryGetValue(item, out var definition) && definition.Divisible
                            ? qty
                            : qty.ToInt32RoundedAwayFromZero();

            state._characters[character.Id] = cs;
        }

        foreach (var faction in world.Factions.Values)
        {
            state._standings[faction.Id] = faction.InitialStanding;
        }

        foreach (var (id, value) in world.InitialFlags)
        {
            if (value) state._flags.Add(id);
        }

        foreach (var (id, value) in world.ExactVariables)
        {
            state._variables[id] = value;
        }

        foreach (var (id, value) in world.TextVariables)
        {
            state._textVariables[id] = value;
        }

        return state;
    }

    internal GameState Fork()
    {
        var fork = new GameState(World, Seed)
        {
            RollCount = RollCount,
            CurrentScene = CurrentScene,
            IsEnded = IsEnded,
        };
        foreach (var (id, character) in _characters)
        {
            var copy = new CharacterState { Definition = character.Definition };
            foreach (var (stat, value) in character.Stats)
                copy.Stats.Add(stat, value);
            foreach (var (stat, value) in character.ExactStats)
                copy.ExactStats.Add(stat, value);
            foreach (var (item, quantity) in character.Inventory)
                copy.Inventory.Add(item, quantity);
            fork._characters.Add(id, copy);
        }
        foreach (var (id, value) in _standings)
            fork._standings.Add(id, value);
        fork._flags.UnionWith(_flags);
        foreach (var (id, value) in _variables)
            fork._variables.Add(id, value);
        foreach (var (id, value) in _textVariables)
            fork._textVariables.Add(id, value);
        foreach (var (id, value) in _visits)
            fork._visits.Add(id, value);
        fork._firedTriggers.UnionWith(_firedTriggers);
        return fork;
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
                Character(e.Character).ExactStats.Remove(e.Stat);
                break;
            case ExactStatChanged e:
                Character(e.Character).ExactStats[e.Stat] = ExactNumber.Parse(e.NewValue);
                break;
            case LevelChanged e when World.Ruleset.LevelingRule is { } leveling:
                Character(e.Character).Stats[leveling.LevelStat] = e.NewLevel;
                Character(e.Character).ExactStats.Remove(leveling.LevelStat);
                break;
            case LevelChanged:
                break;
            case ItemsGranted e:
                var inv = Character(e.Character).Inventory;
                inv[e.Item] = World.TalemarkVersion == 1
                    ? unchecked(inv.GetValueOrDefault(e.Item).ToInt32RoundedAwayFromZero() + e.Quantity)
                    : inv.GetValueOrDefault(e.Item) + e.Quantity;
                break;
            case ItemsRemoved e:
                var inventory = Character(e.Character).Inventory;
                var left = World.TalemarkVersion == 1
                    ? unchecked(inventory.GetValueOrDefault(e.Item).ToInt32RoundedAwayFromZero() - e.Quantity)
                    : inventory.GetValueOrDefault(e.Item) - e.Quantity;
                if (left > 0)
                {
                    inventory[e.Item] = left;
                }
                else
                {
                    inventory.Remove(e.Item);
                }

                break;
            case ExactItemsGranted e:
                var exactInventory = Character(e.Character).Inventory;
                exactInventory[e.Item] = exactInventory.GetValueOrDefault(e.Item) + ExactNumber.Parse(e.Quantity);
                break;
            case ExactItemsRemoved e:
                var exactItems = Character(e.Character).Inventory;
                var exactLeft = exactItems.GetValueOrDefault(e.Item) - ExactNumber.Parse(e.Quantity);
                if (exactLeft > 0)
                    exactItems[e.Item] = exactLeft;
                else
                    exactItems.Remove(e.Item);
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
                _variables[e.Variable] = ExactNumber.FromInteger(e.NewValue);
                break;
            case ExactVariableChanged e:
                if (World.TextVariables.ContainsKey(e.Variable))
                {
                    throw new InvalidOperationException($"Variable '{e.Variable}' holds text, not a number.");
                }
                _variables[e.Variable] = ExactNumber.Parse(e.NewValue);
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
            case ChoiceMade or SkillCheckResolved or TriggerDiverted:
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

    public int GetBaseStat(EntityId character, StatId stat)
    {
        var state = Character(character);
        return World.TalemarkVersion == 2 && state.ExactStats.TryGetValue(stat, out var exact)
            ? exact.ToInt32RoundedAwayFromZero() : state.Stats.GetValueOrDefault(stat);
    }

    public ExactNumber GetExactBaseStat(EntityId character, StatId stat) =>
        Character(character).ExactStats.TryGetValue(stat, out var exact)
            ? exact : Character(character).Stats.GetValueOrDefault(stat);

    public int GetStat(EntityId character, StatId stat)
    {
        if (World.TalemarkVersion == 2)
            return GetExactStat(character, stat).ToInt32RoundedAwayFromZero();
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

    public ExactNumber GetExactStat(EntityId character, StatId stat)
    {
        if (World.TalemarkVersion == 1)
            return GetStat(character, stat);
        var value = GetExactBaseStat(character, stat);
        Ruleset.StatsById.TryGetValue(stat, out var definition);
        var resource = Ruleset.IsFractionalResource(stat);
        foreach (var (item, qty) in Character(character).Inventory)
        {
            if (qty <= 0 || !World.Items.TryGetValue(item, out var def)) continue;
            var bonus = def.ExactModifiers is not null && def.ExactModifiers.TryGetValue(stat, out var exactBonus)
                ? exactBonus : def.Modifiers.TryGetValue(stat, out var integerBonus) ? integerBonus : ExactNumber.Zero;
            value += resource ? bonus : bonus.ToInt32RoundedAwayFromZero();
        }
        foreach (var modifier in Ruleset.Modifiers)
        {
            var bonus = modifier.GetExactModifier(this, character, stat);
            value += resource ? bonus : bonus.ToInt32RoundedAwayFromZero();
        }
        return definition is null ? value : resource ? definition.ClampExact(value)
            : definition.Clamp(value.ToInt32RoundedAwayFromZero());
    }

    public int GetItemCount(EntityId character, ItemId item) =>
        GetExactItemCount(character, item).ToInt32RoundedAwayFromZero();

    public ExactNumber GetExactItemCount(EntityId character, ItemId item) =>
        Character(character).Inventory.GetValueOrDefault(item);

    public IEnumerable<KeyValuePair<ItemId, int>> GetInventory(EntityId character) =>
        GetExactInventory(character).Select(kv => KeyValuePair.Create(kv.Key, kv.Value.ToInt32RoundedAwayFromZero()))
            .Where(kv => kv.Value > 0);

    public IEnumerable<KeyValuePair<ItemId, ExactNumber>> GetExactInventory(EntityId character) =>
        Character(character).Inventory.Where(kv => kv.Value > 0);

    public int GetStanding(FactionId faction) => _standings.GetValueOrDefault(faction);

    public bool GetFlag(FlagId flag) => _flags.Contains(flag);

    public int GetVariable(VarId variable) => GetExactVariable(variable).ToInt32RoundedAwayFromZero();

    public ExactNumber GetExactVariable(VarId variable) => World.TextVariables.ContainsKey(variable)
        ? throw new InvalidOperationException($"Variable '{variable}' holds text, not an integer.")
        : _variables.GetValueOrDefault(variable, ExactNumber.Zero);

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
        public Dictionary<StatId, ExactNumber> ExactStats { get; } = [];
        public Dictionary<ItemId, ExactNumber> Inventory { get; } = [];
    }
}
