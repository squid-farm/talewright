using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Model;

namespace Talewright.Player.Saves;

internal sealed class ValidatedSaveImport
{
    private readonly byte[] _data;

    private ValidatedSaveImport(string storyId, string slot, byte[] data)
    {
        StoryId = storyId;
        Slot = slot;
        _data = data;
    }

    public string StoryId { get; }
    public string Slot { get; }

    public static ValidatedSaveImport Prepare(StoryDefinition story, string slot, byte[] data)
    {
        SaveNames.Validate(story.Id, slot);
        SaveNames.ValidateData(data);
        var owned = data.ToArray();
        _ = StoryHistory.Restore(story, owned);
        return new(story.Id, slot, owned);
    }

    public Task StoreAsync(ISaveStore store, bool overwrite) =>
        store.WriteAsync(StoryId, Slot, _data, overwrite);

    public SaveEntry Describe() => StoryHistory.DescribeSave(Slot, _data);
}
