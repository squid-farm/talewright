namespace Talewright.Talemark.Model;

/// <summary>
/// Character sheet sections the author allows the player to show. A shown section still appears only when the
/// story declares something for it. Presentation only: hidden sections keep their gameplay.
/// </summary>
public sealed record CharacterSheetVisibility(bool Stats = true, bool Inventory = true, bool Standing = true)
{
    /// <summary>Every section the story declares is shown; the default.</summary>
    public static CharacterSheetVisibility Shown { get; } = new();

    /// <summary>The author turned the whole sheet off.</summary>
    public static CharacterSheetVisibility Hidden { get; } = new(false, false, false);
}
