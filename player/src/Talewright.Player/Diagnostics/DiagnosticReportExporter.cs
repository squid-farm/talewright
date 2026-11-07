using Microsoft.JSInterop;
using Talewright.Runtime.Saves;

namespace Talewright.Player.Diagnostics;

public interface IDiagnosticReportExporter
{
    Task<bool> ExportAsync(string suggestedName, byte[] data);
}

public sealed class BrowserDiagnosticReportExporter(IJSRuntime js) : IDiagnosticReportExporter, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task<bool> ExportAsync(string suggestedName, byte[] data)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import",
            "./_content/Talewright.Player/Saves/SaveFileExporter.js");
        await _module.InvokeVoidAsync("download", suggestedName, data);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}

public sealed class LocalDiagnosticReportExporter(Func<string, Task<string?>> choosePath) : IDiagnosticReportExporter
{
    public async Task<bool> ExportAsync(string suggestedName, byte[] data)
    {
        var path = await choosePath(suggestedName);
        if (string.IsNullOrWhiteSpace(path)) return false;
        await AtomicFileWriter.WriteAsync(path, data, overwrite: true);
        return true;
    }
}
