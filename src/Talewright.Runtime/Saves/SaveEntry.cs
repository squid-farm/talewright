namespace Talewright.Runtime.Saves;

public sealed record SaveSummary(string SceneTitle, DateTimeOffset SavedAt, string? CharacterName);

public sealed record SaveEntry(string Slot, SaveSummary? Summary, string? ProgressKey, string? Warning);

public static class SaveCatalog
{
    /// <summary>Lists bounded payload metadata without restoring or replaying any story.</summary>
    public static async Task<IReadOnlyList<SaveEntry>> ListEntriesAsync(this ISaveStore store, string storyId)
    {
        var entries = new List<SaveEntry>();
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
