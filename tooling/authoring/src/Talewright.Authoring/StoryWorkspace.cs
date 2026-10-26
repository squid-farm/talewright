using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Analysis;

namespace Talewright.Authoring;

public sealed record AnalysisUpdate(long Revision, StoryAnalysis Analysis, bool Published);

/// <summary>
/// One story's disk snapshot and versioned unsaved buffers. Hosts forward filesystem notifications;
/// this class does not start watchers, execute projects, or own an editor protocol.
/// </summary>
public sealed class StoryWorkspace
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _disk;
    private readonly HashSet<string> _imagePaths;
    private readonly Dictionary<string, (int Version, string Text)> _open = new(StringComparer.Ordinal);
    private readonly CompilerOptions _options;
    private readonly bool _missingMetadata;
    private long _revision;
    private StoryAnalysis? _current;

    public StoryWorkspace(StorySource source, CompilerOptions? options = null, bool requiresProjectMetadata = false)
    {
        _disk = source.Files.ToDictionary(StringComparer.Ordinal);
        _imagePaths = source.ImagePaths.ToHashSet(StringComparer.Ordinal);
        _options = options ?? CompilerOptions.Default;
        _missingMetadata = requiresProjectMetadata && options is null;
    }

    public static StoryWorkspace FromMetadata(StorySource source, AuthoringProjectMetadata metadata) =>
        new(source, metadata.CreateOptions());

    public StoryAnalysis? Current
    {
        get { lock (_gate) return _current; }
    }

    public static StoryWorkspace FromDirectory(string directory, CompilerOptions? options = null,
        bool requiresProjectMetadata = false) => new(StorySource.FromDirectory(directory), options,
            requiresProjectMetadata || Directory.EnumerateFiles(directory, "*.csproj").Any());

    /// <summary>Finds the nearest manifest ancestor, without evaluating project files.</summary>
    public static string? FindStoryRoot(string fileOrDirectory)
    {
        var full = Path.GetFullPath(fileOrDirectory);
        var directory = Directory.Exists(full) ? new DirectoryInfo(full) : Directory.GetParent(full);
        for (; directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, StorySource.ManifestFileName))) return directory.FullName;
        return null;
    }

    /// <summary>Replaces a disk file, or removes it when text is null. Open buffers continue to win.</summary>
    public void UpdateFile(string path, string? text)
    {
        path = Normalize(path);
        lock (_gate)
        {
            if (text is null) _disk.Remove(path);
            else _disk[path] = text;
            Invalidate();
        }
    }

    public void Open(string path, int version, string text)
    {
        path = Normalize(path);
        lock (_gate)
        {
            if (_open.ContainsKey(path)) throw new InvalidOperationException($"'{path}' is already open.");
            _open.Add(path, (version, text));
            Invalidate();
        }
    }

    /// <summary>Updates the image catalogue without reading image content. Hosts forward create/delete events.</summary>
    public void UpdateImage(string path, bool exists)
    {
        if (!path.StartsWith("images/", StringComparison.Ordinal) || path.Contains('\\') ||
            path.Split('/').Any(segment => segment is "" or "." or "..") || path.Contains(':'))
            throw new ArgumentException("Expected a relative images/ path without traversal.", nameof(path));
        lock (_gate)
        {
            if (exists) _imagePaths.Add(path);
            else _imagePaths.Remove(path);
            Invalidate();
        }
    }

    /// <summary>Returns false for out-of-order document changes; they never invalidate newer analysis.</summary>
    public bool Change(string path, int version, string text)
    {
        path = Normalize(path);
        lock (_gate)
        {
            if (!_open.TryGetValue(path, out var current)) throw new InvalidOperationException($"'{path}' is not open.");
            if (version <= current.Version) return false;
            _open[path] = (version, text);
            Invalidate();
            return true;
        }
    }

    /// <summary>Discards an overlay and returns to the latest supplied disk content.</summary>
    public void Close(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            if (!_open.Remove(path)) throw new InvalidOperationException($"'{path}' is not open.");
            Invalidate();
        }
    }

    /// <summary>
    /// Debounces then analyzes an immutable whole-story snapshot. Superseded results are returned with
    /// Published=false and never replace Current. Cancellation propagates to the caller.
    /// </summary>
    public async Task<AnalysisUpdate> AnalyzeAsync(TimeSpan debounce, CancellationToken cancellationToken = default)
    {
        StorySource snapshot;
        long revision;
        lock (_gate)
        {
            revision = _revision;
            var files = new Dictionary<string, string>(_disk, StringComparer.Ordinal);
            foreach (var (path, document) in _open) files[path] = document.Text;
            snapshot = new StorySource(files, _imagePaths);
        }
        await Task.Delay(debounce, cancellationToken).ConfigureAwait(false);
        var analysis = await Task.Run(() => StoryAnalyzer.Analyze(snapshot, _options), cancellationToken).ConfigureAwait(false);
        if (_missingMetadata)
        {
            analysis = analysis.WithDiagnostic(new Diagnostic(DiagnosticSeverity.Error, "TW5001",
                "Project authoring metadata is missing. Explicitly supply the project's CompilerOptions or exported authoring metadata; editing never loads project assemblies.",
                new SourceLocation(StorySource.ManifestFileName, 1, 1)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var published = revision == _revision;
            if (published) _current = analysis;
            return new(revision, analysis, published);
        }
    }

    private void Invalidate()
    {
        _revision++;
        _current = null;
    }

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        if (Path.IsPathRooted(path) || path.Split('/').Any(segment => segment is "" or "." or "..") ||
            path.Contains(':') || !StorySource.IsStoryFile(path))
            throw new ArgumentException("Expected a relative .talewright.yaml or .tale path without traversal.", nameof(path));
        return path;
    }
}
