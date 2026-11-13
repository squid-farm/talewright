using System.Collections.ObjectModel;
using Talewright.Core.Identity;
using Talewright.Rules.Expressions;

namespace Talewright.Talemark.Model;

/// <summary>
/// Character sheet sections and stats the author allows the player to see. Shown sections still appear only when
/// the story declares something for them; hidden stats remain available to story rules.
/// </summary>
public sealed record CharacterSheetVisibility(bool Stats = true, bool Inventory = true, bool Standing = true)
{
    private static readonly IReadOnlyDictionary<StatId, Expr> EmptyStatRules =
        new ReadOnlyDictionary<StatId, Expr>(new Dictionary<StatId, Expr>());

    /// <summary>Per-stat conditions; stats without a rule are visible whenever the stats section is shown.</summary>
    public IReadOnlyDictionary<StatId, Expr> StatRules { get; init; } = EmptyStatRules;

    /// <summary>Every section the story declares is shown; the default.</summary>
    public static CharacterSheetVisibility Shown { get; } = new();

    /// <summary>The author turned the whole sheet off.</summary>
    public static CharacterSheetVisibility Hidden { get; } = new(false, false, false);
}
