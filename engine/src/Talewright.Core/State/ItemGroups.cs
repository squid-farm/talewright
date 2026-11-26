using System.Collections.ObjectModel;
using Talewright.Core.World;

namespace Talewright.Core.State;

public sealed record HeldItemGroup(
    ExactNumber Quantity,
    IReadOnlyDictionary<string, ItemPropertyValue> Properties);

public enum ItemPropertyValueKind
{
    Number,
    Boolean,
    Text,
}

public sealed record ItemPropertySnapshot(string Key, ItemPropertyValueKind Kind, string Value)
{
    public static ItemPropertySnapshot From(string key, ItemPropertyValue value) => value switch
    {
        ItemPropertyValue.Number number => new(key, ItemPropertyValueKind.Number, number.Value.ToCanonicalString()),
        ItemPropertyValue.Boolean boolean => new(key, ItemPropertyValueKind.Boolean,
            boolean.Value ? "true" : "false"),
        ItemPropertyValue.Text text => new(key, ItemPropertyValueKind.Text, text.Value),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown item property value."),
    };

    public ItemPropertyValue ToValue() => Kind switch
    {
        ItemPropertyValueKind.Number when ExactNumber.TryParse(Value, out var number)
            && number.ToCanonicalString() == Value => new ItemPropertyValue.Number(number),
        ItemPropertyValueKind.Boolean when Value == "true" => new ItemPropertyValue.Boolean(true),
        ItemPropertyValueKind.Boolean when Value == "false" => new ItemPropertyValue.Boolean(false),
        ItemPropertyValueKind.Text => new ItemPropertyValue.Text(Value),
        _ => throw new InvalidOperationException($"Item property '{Key}' has an invalid value."),
    };

    public static ItemPropertyValueKind KindOf(ItemPropertyValue value) => value switch
    {
        ItemPropertyValue.Number => ItemPropertyValueKind.Number,
        ItemPropertyValue.Boolean => ItemPropertyValueKind.Boolean,
        ItemPropertyValue.Text => ItemPropertyValueKind.Text,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown item property value."),
    };
}

public sealed record ItemGroupSnapshot(string Quantity, IReadOnlyList<ItemPropertySnapshot> Properties);

internal static class ItemGroupSnapshotExtensions
{
    public static ItemGroupSnapshot ToSnapshot(this HeldItemGroup group) => new(
        group.Quantity.ToCanonicalString(),
        group.Properties.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => ItemPropertySnapshot.From(property.Key, property.Value)).ToArray());

    public static HeldItemGroup ToHeldGroup(this ItemGroupSnapshot group)
    {
        var properties = group.Properties.ToDictionary(property => property.Key, property => property.ToValue(),
            StringComparer.Ordinal);
        return new(ExactNumber.Parse(group.Quantity),
            new ReadOnlyDictionary<string, ItemPropertyValue>(properties));
    }
}
