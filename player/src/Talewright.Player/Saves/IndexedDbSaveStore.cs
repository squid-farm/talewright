using Microsoft.JSInterop;
using Talewright.Runtime.Saves;

namespace Talewright.Player.Saves;

public sealed class IndexedDbSaveStore(IJSRuntime js) : ISaveStore, IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/Talewright.Player/Saves/IndexedDbSaveStore.js");

    public async Task<IReadOnlyList<string>> ListAsync(string storyId)
    {
        SaveNames.Validate(storyId);
        var module = await ModuleAsync();
        return await module.InvokeAsync<string[]>("list", storyId);
    }

    public async Task<byte[]> ReadAsync(string storyId, string slot)
    {
        SaveNames.Validate(storyId, slot);
        return await ReadSlotAsync(storyId, slot);
    }

    public async Task<byte[]> ReadQuickAsync(string storyId)
    {
        SaveNames.Validate(storyId);
        return await ReadSlotAsync(storyId, SaveEntry.QuickSlot);
    }

    private async Task<byte[]> ReadSlotAsync(string storyId, string slot)
    {
        var module = await ModuleAsync();
        try
        {
            var data = await module.InvokeAsync<byte[]>("read", storyId, slot);
            SaveNames.ValidateData(data);
            return data;
        }
        catch (JSException ex) when (ex.Message.Contains("does not exist.", StringComparison.Ordinal))
        {
            throw new FileNotFoundException($"Save '{SaveEntry.DisplayNameFor(slot)}' does not exist.", ex);
        }
        catch (JSException ex) when (ex.Message.Contains("invalid or oversized data.", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Save '{SaveEntry.DisplayNameFor(slot)}' contains invalid or oversized data.", ex);
        }
    }

    public async Task WriteAsync(string storyId, string slot, byte[] data, bool overwrite = false)
    {
        SaveNames.Validate(storyId, slot);
        await WriteSlotAsync(storyId, slot, data, overwrite);
    }

    public async Task WriteQuickAsync(string storyId, byte[] data)
    {
        SaveNames.Validate(storyId);
        await WriteSlotAsync(storyId, SaveEntry.QuickSlot, data, overwrite: true);
    }

    public async Task<byte[]> ReadAutosaveAsync(string storyId)
    {
        SaveNames.Validate(storyId);
        return await ReadSlotAsync(storyId, SaveEntry.AutosaveSlot);
    }

    public async Task WriteAutosaveAsync(string storyId, byte[] data)
    {
        SaveNames.Validate(storyId);
        await WriteSlotAsync(storyId, SaveEntry.AutosaveSlot, data, overwrite: true);
    }

    public async Task<TriggerSave> ReadTriggerAsync(string storyId)
    {
        SaveNames.Validate(storyId);
        var module = await ModuleAsync();
        try
        {
            var save = await module.InvokeAsync<TriggerSave>("readTrigger", storyId);
            if (save is null)
            {
                throw new InvalidDataException("The trigger checkpoint contains invalid or oversized data.");
            }
            save.Validate();
            return save;
        }
        catch (JSException ex) when (ex.Message.Contains("does not exist.", StringComparison.Ordinal))
        {
            throw new FileNotFoundException("The trigger checkpoint does not exist.", ex);
        }
        catch (JSException ex) when (ex.Message.Contains("invalid or oversized data.", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The trigger checkpoint contains invalid or oversized data.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("The trigger checkpoint contains invalid or oversized data.", ex);
        }
    }

    public async Task WriteTriggerAsync(string storyId, TriggerSave save)
    {
        SaveNames.Validate(storyId);
        save.Validate();
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("writeTrigger", storyId, save.TriggerId, save.Data);
    }

    private async Task WriteSlotAsync(string storyId, string slot, byte[] data, bool overwrite)
    {
        SaveNames.ValidateData(data);
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("write", storyId, slot, data, overwrite);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
