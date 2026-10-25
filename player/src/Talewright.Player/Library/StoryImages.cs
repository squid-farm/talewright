using System.Reflection;
using Microsoft.JSInterop;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.Library;

/// <summary>A source and optional image access; the UI owner disposes it on replacement or shutdown.</summary>
public sealed record LoadedStory(StorySource Source, StoryImages? Images = null) : IDisposable, IAsyncDisposable
{
    public void Dispose() => Images?.Dispose();
    public ValueTask DisposeAsync() => Images?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>Lazy access to one accepted source's local images. Identity also defines a UI source generation.</summary>
public sealed class StoryImages : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Task> _retired = [];
    private Task<IJSObjectReference>? _module;
    private Task? _cleanup;
    private sealed class Entry
    {
        public int Holders;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<string> Url { get; set; } = null!;
    }

    public sealed class Lease : IAsyncDisposable
    {
        private Func<ValueTask>? _release;
        internal Lease(Task<string> url, Func<ValueTask> release) { Url = url; _release = release; }
        public Task<string> Url { get; }
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _release, null)?.Invoke() ?? ValueTask.CompletedTask;
    }

    public Lease Acquire(string path, IJSRuntime js)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_paths.Contains(path)) throw new InvalidDataException($"Image '{path}' is not in this story's safe image catalogue.");
        _module ??= js.InvokeAsync<IJSObjectReference>("import",
            "./_content/Talewright.Player/Components/MediaView.razor.js").AsTask();
        if (!_entries.TryGetValue(path, out var entry))
        {
            entry = new Entry();
            _entries.Add(path, entry);
            entry.Url = TransferAsync(path, entry);
        }
        ++entry.Holders;
        return new(entry.Url, () => ReleaseAsync(path, entry));
    }

    private async Task<string> TransferAsync(string path, Entry entry)
    {
        var module = await _module!;
        await using var stream = await OpenAsync(path, entry.Cancellation.Token);
        using var reference = new DotNetStreamReference(stream, leaveOpen: true);
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => throw new InvalidDataException("Unsupported resolved image format."),
        };
        // Do not abandon this invocation: a URL created after retirement still needs revocation.
        return await module.InvokeAsync<string>("createUrl", reference, mime);
    }

    private ValueTask ReleaseAsync(string path, Entry entry)
    {
        if (--entry.Holders != 0 || !_entries.TryGetValue(path, out var current) || !ReferenceEquals(entry, current))
            return ValueTask.CompletedTask;
        _entries.Remove(path);
        var task = RetireAsync(entry);
        _retired.RemoveAll(retired => retired.IsCompletedSuccessfully);
        _retired.Add(task);
        return new(task);
    }

    private async Task RetireAsync(Entry entry)
    {
        entry.Cancellation.Cancel();
        try
        {
            string url;
            try { url = await entry.Url; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException
                or InvalidDataException or ObjectDisposedException or JSException)
            {
                Console.Error.WriteLine($"Retired image transfer: {ex.Message}");
                return;
            }
            await (await _module!).InvokeVoidAsync("revokeUrl", url);
        }
        finally { entry.Cancellation.Dispose(); }
    }
    private readonly HashSet<string> _paths;
    private readonly Func<string, Stream> _open;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    private StoryImages(StorySource source, Func<string, Stream> open)
    {
        _paths = StoryImageResolver.CompletionPaths(source).Select(path => "images/" + path)
            .ToHashSet(StringComparer.Ordinal);
        _open = open;
        Lifetime = _lifetime.Token;
    }

    public CancellationToken Lifetime { get; }

    public async ValueTask<Stream> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_paths.Contains(path))
            throw new InvalidDataException($"Image '{path}' is not in this story's safe image catalogue.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
        var stream = await Task.Run(() =>
        {
            cancellation.Token.ThrowIfCancellationRequested();
            return _open(path);
        }, cancellation.Token);
        if (cancellation.IsCancellationRequested)
        {
            await stream.DisposeAsync();
            cancellation.Token.ThrowIfCancellationRequested();
        }
        return stream;
    }

    public static StoryImages FromDirectory(StorySource source, string root)
    {
        var directory = Path.GetFullPath(root);
        return new(source, path =>
        {
            var current = directory;
            for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
                RejectLink(ancestor.FullName);
            foreach (var part in path.Split('/'))
            {
                current = Path.Combine(current, part);
                RejectLink(current);
            }
            return new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        });

        static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Story images cannot contain symbolic links: '{path}'.");
        }
    }

    public static StoryImages FromResources(StorySource source, Assembly assembly, string prefix)
    {
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(name => name.Replace('\\', '/'), StringComparer.Ordinal);
        return new(source, path => resources.TryGetValue(prefix + path, out var name)
            ? assembly.GetManifestResourceStream(name)
                ?? throw new FileNotFoundException($"Image resource '{path}' is unavailable.", path)
            : throw new FileNotFoundException($"Image resource '{path}' is unavailable.", path));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        foreach (var entry in _entries.Values) _retired.Add(RetireAsync(entry));
        _entries.Clear();
        _cleanup = CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        try
        {
            await Task.WhenAll(_retired);
            if (_module is not null) await (await _module).DisposeAsync();
        }
        catch (JSDisconnectedException) { /* The document and its URLs are already gone. */ }
        finally { _lifetime.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _cleanup!;
    }
}
