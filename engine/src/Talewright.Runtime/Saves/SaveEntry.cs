namespace Talewright.Runtime.Saves;

public sealed record SaveSummary(string SceneTitle, DateTimeOffset SavedAt, string? CharacterName);

public sealed record SaveEntry(string Slot, SaveSummary? Summary, string? ProgressKey, string? Warning)
{
    // Reserved identities cannot pass named-slot validation and are never user-entered names.
    public const string QuickSlot = "\0quick";
    public const string AutosaveSlot = "\0autosave";
    public const string TriggerSlot = "\0trigger";
    public bool IsQuick => Slot == QuickSlot;
    public bool IsAutosave => Slot == AutosaveSlot;
    public bool IsTriggerSave => Slot == TriggerSlot;
    public string? TriggerLabel { get; init; }
    public string DisplayName => DisplayNameFor(Slot);
    public static string DisplayNameFor(string slot) => slot switch
    {
        QuickSlot => "Quick Save checkpoint",
        AutosaveSlot => "Autosave",
        TriggerSlot => "Trigger checkpoint",
        _ => slot,
    };
}

public static class SaveCatalog
{
    /// <summary>Lists bounded payload metadata without restoring or replaying any story.</summary>
    public static async Task<IReadOnlyList<SaveEntry>> ListEntriesAsync(this ISaveStore store, string storyId)
    {
        var entries = new List<SaveEntry>();
        await AddReservedAsync(entries, SaveEntry.QuickSlot, () => store.ReadQuickAsync(storyId));
        await AddReservedAsync(entries, SaveEntry.AutosaveSlot, () => store.ReadAutosaveAsync(storyId));
        await AddTriggerAsync(entries, storyId, store);
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

    private static async Task AddTriggerAsync(List<SaveEntry> entries, string storyId, ISaveStore store)
    {
        try
        {
            var save = await store.ReadTriggerAsync(storyId);
            entries.Add(StoryHistory.DescribeSave(SaveEntry.TriggerSlot, save.Data) with { TriggerLabel = save.TriggerId });
        }
        catch (FileNotFoundException)
        {
        }
        catch (InvalidDataException ex)
        {
            entries.Add(new(SaveEntry.TriggerSlot, null, null, $"Save details unavailable: {ex.Message}"));
        }
    }

    private static async Task AddReservedAsync(List<SaveEntry> entries, string slot, Func<Task<byte[]>> read)
    {
        try
        {
            entries.Add(StoryHistory.DescribeSave(slot, await read()));
        }
        catch (FileNotFoundException)
        {
            // This reserved save has not been created for this story.
        }
        catch (InvalidDataException ex)
        {
            entries.Add(new(slot, null, null, $"Save details unavailable: {ex.Message}"));
        }
    }
}
