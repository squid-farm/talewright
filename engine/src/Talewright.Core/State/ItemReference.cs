using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Talewright.Core.Identity;

namespace Talewright.Core.State;

/// <summary>
/// What a Talemark 2 item variable holds: an item, or one held group of copies identified by its holder and
/// property values. It is stored as text variable content, so saves need no separate format.
/// </summary>
public sealed record ItemReference
{
    private const string ItemField = "item";
    private const string HolderField = "holder";
    private const string PropertiesField = "properties";

    private ItemReference(ItemId item, EntityId? holder, IReadOnlyList<ItemPropertySnapshot> properties)
    {
        Item = item;
        Holder = holder;
        Properties = properties;
    }

    public ItemId Item { get; }

    /// <summary>The character holding the group, or null for a plain item reference.</summary>
    public EntityId? Holder { get; }

    /// <summary>The group's property values, ordered by key; empty for a plain item reference.</summary>
    public IReadOnlyList<ItemPropertySnapshot> Properties { get; }

    public bool IsGroup => Holder is not null;

    public static ItemReference ForItem(ItemId item)
    {
        ArgumentException.ThrowIfNullOrEmpty(item.Value, nameof(item));
        return new(item, null, []);
    }

    public static ItemReference ForGroup(EntityId holder, ItemId item, HeldItemGroup group)
    {
        ArgumentException.ThrowIfNullOrEmpty(holder.Value, nameof(holder));
        ArgumentException.ThrowIfNullOrEmpty(item.Value, nameof(item));
        ArgumentNullException.ThrowIfNull(group);
        return new(item, holder, group.ToSnapshot().Properties);
    }

    /// <summary>Text stored in the variable. A plain item reference is the item id.</summary>
    public string Encode()
    {
        if (Holder is not { } holder)
            return Item.Value;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(ItemField, Item.Value);
            writer.WriteString(HolderField, holder.Value);
            writer.WriteStartArray(PropertiesField);
            foreach (var property in Properties)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(property.Key);
                writer.WriteStringValue(KindName(property.Kind));
                writer.WriteStringValue(property.Value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads stored variable text. Empty or malformed text holds nothing.</summary>
    public static bool TryDecode(string? text, [NotNullWhen(true)] out ItemReference? reference)
    {
        reference = null;
        if (string.IsNullOrEmpty(text))
            return false;
        if (text[0] != '{')
        {
            reference = ForItem(new ItemId(text));
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(ItemField, out var item) || item.ValueKind != JsonValueKind.String
                || item.GetString() is not { Length: > 0 } itemId
                || !root.TryGetProperty(HolderField, out var holder) || holder.ValueKind != JsonValueKind.String
                || holder.GetString() is not { Length: > 0 } holderId
                || !root.TryGetProperty(PropertiesField, out var properties)
                || properties.ValueKind != JsonValueKind.Array)
                return false;

            var snapshots = new List<ItemPropertySnapshot>(properties.GetArrayLength());
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateArray())
            {
                if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() != 3
                    || property[0].ValueKind != JsonValueKind.String
                    || property[1].ValueKind != JsonValueKind.String
                    || property[2].ValueKind != JsonValueKind.String
                    || property[0].GetString() is not { Length: > 0 } key
                    || !keys.Add(key)
                    || !TryParseKind(property[1].GetString(), out var kind)
                    || property[2].GetString() is not { } value)
                    return false;
                snapshots.Add(new(key, kind, value));
            }

            reference = new(new ItemId(itemId), new EntityId(holderId),
                snapshots.OrderBy(snapshot => snapshot.Key, StringComparer.Ordinal).ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The item this reference names now: the item for a plain reference, or the group's item while its holder still
    /// holds a group with these values. Null means the reference holds nothing.
    /// </summary>
    public ItemId? Resolve(IGameStateView state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.World.Items.ContainsKey(Item))
            return null;
        if (Holder is not { } holder)
            return Item;
        return state.World.Characters.ContainsKey(holder) && FindGroup(state.GetItemGroups(holder, Item)) >= 0
            ? Item
            : null;
    }

    /// <summary>Finds the held group with this reference's values, or -1 when that group is gone.</summary>
    public int FindGroup(IReadOnlyList<HeldItemGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (!IsGroup)
            return -1;
        for (var index = 0; index < groups.Count; index++)
        {
            if (Matches(groups[index]))
                return index;
        }

        return -1;
    }

    private bool Matches(HeldItemGroup group)
    {
        if (group.Properties.Count != Properties.Count)
            return false;
        foreach (var property in Properties)
        {
            if (!group.Properties.TryGetValue(property.Key, out var value)
                || ItemPropertySnapshot.From(property.Key, value) != property)
                return false;
        }

        return true;
    }

    public bool Equals(ItemReference? other) =>
        other is not null && Item == other.Item && Holder == other.Holder && Properties.SequenceEqual(other.Properties);

    public override int GetHashCode() => HashCode.Combine(Item, Holder, Properties.Count);

    private static string KindName(ItemPropertyValueKind kind) => kind switch
    {
        ItemPropertyValueKind.Number => "number",
        ItemPropertyValueKind.Boolean => "boolean",
        ItemPropertyValueKind.Text => "text",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown item property kind."),
    };

    private static bool TryParseKind(string? name, out ItemPropertyValueKind kind)
    {
        (var known, kind) = name switch
        {
            "number" => (true, ItemPropertyValueKind.Number),
            "boolean" => (true, ItemPropertyValueKind.Boolean),
            "text" => (true, ItemPropertyValueKind.Text),
            _ => (false, default),
        };
        return known;
    }
}
