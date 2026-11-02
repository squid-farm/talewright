using System.Text;
using Microsoft.JSInterop;
using Talewright.Runtime.Saves;

namespace Talewright.Player.Saves;

public interface ISaveFileExporter
{
    /// <returns>False if the player canceled the file dialog; true if export was requested.</returns>
    Task<bool> ExportAsync(string suggestedName, byte[] data);
}

public static class SaveFileName
{
    public static string Create(string storyTitle, string saveName) =>
        $"{SafePart(storyTitle, "Story")} - {SafePart(saveName, "Save")}.json";

    private static string SafePart(string value, string fallback)
    {
        var result = new StringBuilder();
        var byteCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var part = Rune.IsControl(rune) || (rune.IsAscii && "<>:\"/\\|?*".Contains((char)rune.Value))
                ? "_"
                : rune.ToString();
            var size = Encoding.UTF8.GetByteCount(part);
            if (byteCount + size > 90)
            {
                break;
            }
            result.Append(part);
            byteCount += size;
        }

        var safe = result.ToString().Trim().TrimEnd(' ', '.');
        return safe.Length == 0 ? fallback : safe;
    }
}

public sealed class BrowserSaveFileExporter(IJSRuntime js) : ISaveFileExporter, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task<bool> ExportAsync(string suggestedName, byte[] data)
    {
        SaveNames.ValidateData(data);
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./_content/Talewright.Player/Saves/SaveFileExporter.js");
        await _module.InvokeVoidAsync("download", suggestedName, data);
        return true;
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

public sealed class LocalSaveFileExporter(Func<string, Task<string?>> choosePath) : ISaveFileExporter
{
    public async Task<bool> ExportAsync(string suggestedName, byte[] data)
    {
        SaveNames.ValidateData(data);
        var path = await choosePath(suggestedName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        await AtomicFileWriter.WriteAsync(path, data, overwrite: true);
        return true;
    }
}
