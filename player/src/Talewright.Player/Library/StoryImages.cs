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
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<ImageDimensions>> _dimensions = new(StringComparer.Ordinal);
    private readonly HashSet<Entry> _detached = [];
    private readonly List<Task> _retired = [];
    private readonly IStoryImageSource _source;
    private Task<IJSObjectReference>? _module;
    private Task? _cleanup;
    private sealed class Entry
    {
        public int Holders;
        public bool Retired;
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
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_paths.Contains(path)) throw new InvalidDataException($"Image '{path}' is not in this story's safe image catalogue.");
            if (_module is { IsCompleted: true, IsCompletedSuccessfully: false })
                _module = null;
            _module ??= js.InvokeAsync<IJSObjectReference>("import",
                "./_content/Talewright.Player/Components/MediaView.razor.js").AsTask();
            if (_entries.TryGetValue(path, out var failed) && !failed.Url.IsCompletedSuccessfully && failed.Url.IsCompleted)
            {
                _entries.Remove(path);
                RetireLocked(failed);
            }
            if (!_entries.TryGetValue(path, out var entry))
            {
                entry = new Entry();
                _entries.Add(path, entry);
                entry.Url = TransferAsync(path, entry);
            }
            ++entry.Holders;
            return new(entry.Url, () => ReleaseAsync(path, entry));
        }
    }

    internal Lease? TryAcquireExisting(string path)
    {
        lock (_sync)
        {
            if (_disposed || !_paths.Contains(path) || !_entries.TryGetValue(path, out var entry) ||
                entry.Url.IsCompleted && !entry.Url.IsCompletedSuccessfully) return null;
            ++entry.Holders;
            return new(entry.Url, () => ReleaseAsync(path, entry));
        }
    }

    internal Task<ImageDimensions> GetImageDimensionsAsync(string path)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_paths.Contains(path))
                throw new InvalidDataException($"Image '{path}' is not in this story's safe image catalogue.");
            if (_dimensions.TryGetValue(path, out var existing))
            {
                if (!existing.IsFaulted && !existing.IsCanceled) return existing;
                _dimensions.Remove(path);
            }

            var read = ReadImageDimensionsAsync(path);
            _dimensions.Add(path, read);
            return read;
        }
    }

    private async Task<ImageDimensions> ReadImageDimensionsAsync(string path)
    {
        await using var stream = await OpenAsync(path, Lifetime);
        return await ImageDimensions.ReadAsync(stream, path, Lifetime);
    }

    internal void BeginGeneration()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var entry in _entries.Values)
                _detached.Add(entry);
            _entries.Clear();
        }
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
        lock (_sync)
        {
            if (--entry.Holders != 0) return ValueTask.CompletedTask;
            if (_entries.TryGetValue(path, out var current) && ReferenceEquals(entry, current))
                _entries.Remove(path);
            _detached.Remove(entry);
            return new(RetireLocked(entry));
        }
    }

    private Task RetireLocked(Entry entry)
    {
        if (entry.Retired) return Task.CompletedTask;
        entry.Retired = true;
        var task = RetireAsync(entry);
        _retired.RemoveAll(retired => retired.IsCompletedSuccessfully);
        _retired.Add(task);
        return task;
    }

    private async Task RetireAsync(Entry entry)
    {
        try
        {
            string url;
            // Blazor may still be reading the stream after its final view unmounts.
            try { url = await entry.Url; }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException
                or InvalidDataException or ObjectDisposedException or JSException)
            {
                Console.Error.WriteLine($"Retired image transfer: {ex.Message}");
                return;
            }
            await (await _module!).InvokeVoidAsync("revokeUrl", url);
        }
        finally
        {
            entry.Cancellation.Cancel();
            entry.Cancellation.Dispose();
        }
    }
    private readonly HashSet<string> _paths;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    private StoryImages(StorySource source, IStoryImageSource imageSource)
    {
        _paths = StoryImageResolver.CompletionPaths(source).Select(path => "images/" + path)
            .ToHashSet(StringComparer.Ordinal);
        _source = imageSource;
        Lifetime = _lifetime.Token;
    }

    public CancellationToken Lifetime { get; }

    public async ValueTask<Stream> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_paths.Contains(path))
                throw new InvalidDataException($"Image '{path}' is not in this story's safe image catalogue.");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime);
        }
        try
        {
            var stream = await _source.OpenAsync(path, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                await stream.DisposeAsync();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return new ImageReadStream(stream, cancellation);
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
    }

    private sealed class ImageReadStream(Stream input, CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => input.CanSeek;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            return input.Read(buffer);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
            return await input.ReadAsync(buffer, linked.Token);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { input.Dispose(); }
                finally { cancellation.Dispose(); }
            }
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static StoryImages FromDirectory(StorySource source, string root)
    {
        var directory = Path.GetFullPath(root);
        return new(source, new DirectoryImageSource(directory));
    }

    /// <summary>Creates lazy image access over a story-owned backing store; this instance takes ownership.</summary>
    public static StoryImages FromSource(StorySource source, IStoryImageSource imageSource) =>
        new(source, imageSource);

    private sealed class DirectoryImageSource(string directory) : IStoryImageSource
    {
        public async ValueTask<Stream> OpenAsync(string path, CancellationToken cancellationToken)
        {
            return await Task.Run<Stream>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            }, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static void RejectLink(string path)
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
        return new(source, new ResourceImageSource(assembly, prefix, resources));
    }

    private sealed class ResourceImageSource(Assembly assembly, string prefix, IReadOnlyDictionary<string, string> resources)
        : IStoryImageSource
    {
        public ValueTask<Stream> OpenAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stream stream = resources.TryGetValue(prefix + path, out var name)
                ? assembly.GetManifestResourceStream(name)
                    ?? throw new FileNotFoundException($"Image resource '{path}' is unavailable.", path)
                : throw new FileNotFoundException($"Image resource '{path}' is unavailable.", path);
            return ValueTask.FromResult(stream);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            foreach (var entry in _entries.Values.Concat(_detached)) RetireLocked(entry);
            _entries.Clear();
            _detached.Clear();
            var dimensions = _dimensions.Values.ToArray();
            _dimensions.Clear();
            _cleanup = CleanupAsync(dimensions);
        }
    }

    private async Task CleanupAsync(Task<ImageDimensions>[] dimensions)
    {
        try
        {
            await Task.WhenAll(_retired);
            await Task.WhenAll(dimensions.Select(DrainDimensionReadAsync));
            if (_module is not null) await (await _module).DisposeAsync();
        }
        catch (JSDisconnectedException) { /* The document and its URLs are already gone. */ }
        finally
        {
            try { await _source.DisposeAsync(); }
            finally { _lifetime.Dispose(); }
        }
    }

    private static async Task DrainDimensionReadAsync(Task<ImageDimensions> read)
    {
        try
        {
            await read;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException
            or InvalidDataException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _cleanup!;
    }
}
