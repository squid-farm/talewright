using System.Reflection;
using Talewright.Talemark.Compilation;

namespace Talewright.Player.Library;

/// <summary>A source and optional image access; the UI owner disposes it on replacement or shutdown.</summary>
public sealed record LoadedStory(StorySource Source, StoryImages? Images = null) : IDisposable
{
    public void Dispose() => Images?.Dispose();
}

/// <summary>Lazy access to one accepted source's local images. Identity also defines a UI source generation.</summary>
public sealed class StoryImages : IDisposable
{
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
        _lifetime.Dispose();
    }
}
