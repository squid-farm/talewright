using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core;

namespace Talewright.Core.World;

/// <summary>Static declaration of a character (player or NPC) as authored in <c>story.talewright.yaml</c>.</summary>
/// <param name="Id">Character id; <see cref="EntityId.Player"/> for the protagonist.</param>
/// <param name="FirstName">Given name.</param>
/// <param name="LastName">Family name, may be empty.</param>
/// <param name="Pronouns">Pronoun set used by the template renderer.</param>
/// <param name="Stats">Starting base values; stats not listed use <see cref="StatDefinition.Default"/>.</param>
/// <param name="Inventory">Starting items and quantities.</param>
/// <param name="NameAgreement">Displayed-name grammar, not population: <c>the pack</c> is singular, <c>the goblins</c> plural. Controls name possessives.</param>
public sealed record CharacterDefinition(
    EntityId Id,
    string FirstName,
    string LastName,
    Pronouns Pronouns,
    IReadOnlyDictionary<StatId, int> Stats,
    IReadOnlyDictionary<ItemId, int> Inventory,
    GrammaticalNumber NameAgreement = GrammaticalNumber.Singular)
{
    public string FullName => string.IsNullOrEmpty(LastName) ? FirstName : $"{FirstName} {LastName}";
    /// <summary>Exact starting values override matching entries in <see cref="Stats"/>.</summary>
    public IReadOnlyDictionary<StatId, ExactNumber>? ExactStats { get; init; }
    /// <summary>Exact starting quantities override matching entries in <see cref="Inventory"/>.</summary>
    public IReadOnlyDictionary<ItemId, ExactNumber>? ExactInventory { get; init; }
}

/// <summary>An item that can be held. Items held by a character apply their <see cref="Modifiers"/> to that character's stats.</summary>
/// <remarks>Name agreement and uncountability are independent of divisibility and inventory quantity.
/// Uncountable names take some instead of a/an, without implying plural agreement.</remarks>
public sealed record ItemDefinition(
    ItemId Id,
    string Name,
    IReadOnlyDictionary<StatId, int> Modifiers,
    GrammaticalNumber NameAgreement = GrammaticalNumber.Singular,
    bool Uncountable = false,
    bool Divisible = false)
{
    /// <summary>Exact modifiers override matching entries in <see cref="Modifiers"/>.</summary>
    public IReadOnlyDictionary<StatId, ExactNumber>? ExactModifiers { get; init; }
}

/// <summary>A faction the player can stand well or badly with.</summary>
public sealed record FactionDefinition(FactionId Id, string Name, int InitialStanding);

/// <summary>
/// Everything static about a game world: the ruleset plus all declared characters, items, factions, flags and variables.
/// Combined with a seed it fully determines the initial <see cref="State.GameState"/>.
/// </summary>
public sealed class WorldDefinition
{
    public WorldDefinition(
        Ruleset ruleset,
        IEnumerable<CharacterDefinition> characters,
        IEnumerable<ItemDefinition> items,
        IEnumerable<FactionDefinition> factions,
        IEnumerable<FlagId> flags,
        IReadOnlyDictionary<VarId, int> variables,
        IReadOnlyDictionary<VarId, string>? textVariables = null,
        IReadOnlyDictionary<FlagId, bool>? initialFlags = null,
        int talemarkVersion = 1)
        : this(ruleset, characters, items, factions, flags,
            variables.ToDictionary(pair => pair.Key, pair => ExactNumber.FromInteger(pair.Value)),
            textVariables, initialFlags, talemarkVersion)
    {
    }

    public WorldDefinition(
        Ruleset ruleset,
        IEnumerable<CharacterDefinition> characters,
        IEnumerable<ItemDefinition> items,
        IEnumerable<FactionDefinition> factions,
        IEnumerable<FlagId> flags,
        IReadOnlyDictionary<VarId, ExactNumber> variables,
        IReadOnlyDictionary<VarId, string>? textVariables = null,
        IReadOnlyDictionary<FlagId, bool>? initialFlags = null,
        int talemarkVersion = 1)
    {
        if (talemarkVersion is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(talemarkVersion), talemarkVersion, "Unsupported Talemark version.");
        TalemarkVersion = talemarkVersion;
        Ruleset = ruleset;
        Characters = characters.ToDictionary(c => c.Id);
        Items = items.ToDictionary(i => i.Id);
        Factions = factions.ToDictionary(f => f.Id);
        Flags = flags.ToHashSet();
        InitialFlags = Flags.ToDictionary(id => id, id => initialFlags?.GetValueOrDefault(id) ?? false);
        ExactVariables = variables;
        Variables = variables.ToDictionary(pair => pair.Key, pair => pair.Value.ToInt32RoundedAwayFromZero());
        TextVariables = textVariables ?? new Dictionary<VarId, string>();
        if (ExactVariables.Keys.Any(TextVariables.ContainsKey))
        {
            throw new ArgumentException("A variable cannot be both integer and text.", nameof(textVariables));
        }
        if (TextVariables.Values.Any(value => value is null))
        {
            throw new ArgumentException("Text variable values cannot be null.", nameof(textVariables));
        }
    }

    public Ruleset Ruleset { get; }
    public int TalemarkVersion { get; }
    public IReadOnlyDictionary<EntityId, CharacterDefinition> Characters { get; }
    public IReadOnlyDictionary<ItemId, ItemDefinition> Items { get; }
    public IReadOnlyDictionary<FactionId, FactionDefinition> Factions { get; }
    public IReadOnlySet<FlagId> Flags { get; }

    /// <summary>Declared flags and their values before any events are applied.</summary>
    public IReadOnlyDictionary<FlagId, bool> InitialFlags { get; }

    /// <summary>Declared variables with their legacy int32-projected initial values.</summary>
    public IReadOnlyDictionary<VarId, int> Variables { get; }

    /// <summary>Declared numeric variables with their authoritative exact initial values.</summary>
    public IReadOnlyDictionary<VarId, ExactNumber> ExactVariables { get; }

    public IReadOnlyDictionary<VarId, string> TextVariables { get; }
}
