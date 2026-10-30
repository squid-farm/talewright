using Talewright.Core.State;
using Talewright.Core.Identity;

namespace Talewright.Rules.Expressions;

/// <summary>
/// Named predicates are the C# escape hatch for conditions too complex for inline expressions
/// (<c>@CanPickLock</c> in a story). Register them before compiling a story so references can be validated.
/// </summary>
public sealed class NamedPredicateRegistry
{
    private readonly Dictionary<string, Func<IGameStateView, bool>> _predicates = new(StringComparer.Ordinal);

    public static NamedPredicateRegistry Empty => new();

    public NamedPredicateRegistry Register(string name, Func<IGameStateView, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IdentifierRules.IsValid(name))
        {
            throw new ArgumentException("Predicate names must use ASCII letters, digits or underscores, start with a letter or underscore, and use single dots or hyphens only between name segments.", nameof(name));
        }

        _predicates[name] = predicate;
        return this;
    }

    public bool Contains(string name) => _predicates.ContainsKey(name);

    public IEnumerable<string> Names => _predicates.Keys;

    public bool Evaluate(string name, IGameStateView state) =>
        _predicates.TryGetValue(name, out var predicate)
            ? predicate(state)
            : throw new KeyNotFoundException($"Named predicate '@{name}' is not registered.");
}
