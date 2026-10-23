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
        var module = await ModuleAsync();
        try
        {
            var data = await module.InvokeAsync<byte[]>("read", storyId, slot);
            SaveNames.ValidateData(data);
            return data;
        }
        catch (JSException ex) when (ex.Message.Contains("does not exist.", StringComparison.Ordinal))
        {
            throw new FileNotFoundException(ex.Message, ex);
        }
        catch (JSException ex) when (ex.Message.Contains("invalid or oversized data.", StringComparison.Ordinal))
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    public async Task WriteAsync(string storyId, string slot, byte[] data, bool overwrite = false)
    {
        SaveNames.Validate(storyId, slot);
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
