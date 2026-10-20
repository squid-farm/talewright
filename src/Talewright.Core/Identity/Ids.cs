namespace Talewright.Core.Identity;

/// <summary>
/// Strongly typed identifiers. Authors write plain kebab-case strings (<c>explorers-guild</c>);
/// the compiler wraps them in these types so a stat id can never be passed where an item id is expected.
/// </summary>
/// <remarks>All ids compare ordinally and print as their raw value.</remarks>
public readonly record struct EntityId(string Value)
{
    /// <summary>The protagonist. Every story must declare a character with this id.</summary>
    public static readonly EntityId Player = new("player");
    public override string ToString() => Value;
}

/// <summary>Identifies a stat (attribute, skill, resource, …) declared by the ruleset.</summary>
public readonly record struct StatId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies an item definition.</summary>
public readonly record struct ItemId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies a faction.</summary>
public readonly record struct FactionId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies a scene / storylet (dotted kebab-case, e.g. <c>temple.entrance</c>).</summary>
public readonly record struct SceneId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies a boolean story flag.</summary>
public readonly record struct FlagId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Identifies an integer story variable.</summary>
public readonly record struct VarId(string Value)
{
    public override string ToString() => Value;
}
