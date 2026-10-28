using Talewright.Core.Identity;
using Talewright.Core.Rulesets;

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
}

/// <summary>An item that can be held. Items held by a character apply their <see cref="Modifiers"/> to that character's stats.</summary>
public sealed record ItemDefinition(ItemId Id, string Name, IReadOnlyDictionary<StatId, int> Modifiers);

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
        IReadOnlyDictionary<VarId, string>? textVariables = null)
    {
        Ruleset = ruleset;
        Characters = characters.ToDictionary(c => c.Id);
        Items = items.ToDictionary(i => i.Id);
        Factions = factions.ToDictionary(f => f.Id);
        Flags = flags.ToHashSet();
        Variables = variables;
        TextVariables = textVariables ?? new Dictionary<VarId, string>();
        if (Variables.Keys.Any(TextVariables.ContainsKey))
        {
            throw new ArgumentException("A variable cannot be both integer and text.", nameof(textVariables));
        }
        if (TextVariables.Values.Any(value => value is null))
        {
            throw new ArgumentException("Text variable values cannot be null.", nameof(textVariables));
        }
    }

    public Ruleset Ruleset { get; }
    public IReadOnlyDictionary<EntityId, CharacterDefinition> Characters { get; }
    public IReadOnlyDictionary<ItemId, ItemDefinition> Items { get; }
    public IReadOnlyDictionary<FactionId, FactionDefinition> Factions { get; }
    public IReadOnlySet<FlagId> Flags { get; }

    /// <summary>Declared variables with their initial values.</summary>
    public IReadOnlyDictionary<VarId, int> Variables { get; }

    public IReadOnlyDictionary<VarId, string> TextVariables { get; }
}
