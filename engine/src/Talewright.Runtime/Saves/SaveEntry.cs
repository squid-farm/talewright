namespace Talewright.Runtime.Saves;

public sealed record SaveSummary(string SceneTitle, DateTimeOffset SavedAt, string? CharacterName);

public sealed record SaveEntry(string Slot, SaveSummary? Summary, string? ProgressKey, string? Warning)
{
    // Reserved identities cannot pass named-slot validation and are never user-entered names.
    public const string QuickSlot = "\0quick";
    public const string AutosaveSlot = "\0autosave";
    public bool IsQuick => Slot == QuickSlot;
    public bool IsAutosave => Slot == AutosaveSlot;
    public string DisplayName => DisplayNameFor(Slot);
    public static string DisplayNameFor(string slot) => slot switch
    {
        QuickSlot => "Quick Save checkpoint",
        AutosaveSlot => "Autosave",
        _ => slot,
    };
}

public static class SaveCatalog
{
    /// <summary>Lists bounded payload metadata without restoring or replaying any story.</summary>
    public static async Task<IReadOnlyList<SaveEntry>> ListEntriesAsync(this ISaveStore store, string storyId)
    {
        var entries = new List<SaveEntry>();
        try
        {
            entries.Add(StoryHistory.DescribeSave(SaveEntry.QuickSlot, await store.ReadQuickAsync(storyId)));
        }
        catch (FileNotFoundException)
        {
            // No checkpoint has been created for this story.
        }
        catch (InvalidDataException ex)
        {
            entries.Add(new(SaveEntry.QuickSlot, null, null, $"Save details unavailable: {ex.Message}"));
        }
        foreach (var slot in await store.ListAsync(storyId))
        {
            try
            {
                entries.Add(StoryHistory.DescribeSave(slot, await store.ReadAsync(storyId, slot)));
            }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
            {
                entries.Add(new(slot, null, null, $"Save details unavailable: {ex.Message}"));
            }
        }
        return entries;
    }
}
