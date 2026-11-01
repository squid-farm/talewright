using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Analysis;

namespace Talewright.Authoring;

public sealed record AnalysisUpdate(long Revision, StoryAnalysis Analysis, bool Published);

/// <summary>One analysis shared by callers interested in the same workspace revision.</summary>
public sealed class SharedAnalysis
{
    private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _superseded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<AnalysisUpdate>? _completion;
    private int _retainSupersededResult;

    internal SharedAnalysis(long revision, bool retainSupersededResult)
    {
        Revision = revision;
        if (retainSupersededResult) _retainSupersededResult = 1;
    }

    public long Revision { get; }
    public Task<AnalysisUpdate> Completion => _completion ?? throw new InvalidOperationException("Analysis has not been scheduled.");
    public Task Superseded => _superseded.Task;

    /// <summary>Ends the debounce without creating another analysis.</summary>
    public void StartImmediately() => _start.TrySetResult();

    internal Task StartRequested => _start.Task;
    internal bool RetainSupersededResult => Volatile.Read(ref _retainSupersededResult) != 0;
    internal void RetainResult() => Interlocked.Exchange(ref _retainSupersededResult, 1);
    internal void Supersede() => _superseded.TrySetResult();
    internal void SetCompletion(Task<AnalysisUpdate> completion) => _completion = completion;
}

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
    private readonly Func<StorySource, CompilerOptions, CancellationToken, Task<StoryAnalysis>> _analyze;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _analysisGate;
    private long _revision;
    private StoryAnalysis? _current;
    private SharedAnalysis? _shared;

    public StoryWorkspace(StorySource source, CompilerOptions? options = null, bool requiresProjectMetadata = false)
        : this(source, options, requiresProjectMetadata, null, null)
    {
    }

    internal StoryWorkspace(StorySource source, CompilerOptions? options, bool requiresProjectMetadata,
        Func<StorySource, CompilerOptions, CancellationToken, Task<StoryAnalysis>>? analyze,
        Func<TimeSpan, CancellationToken, Task>? delay, SemaphoreSlim? analysisGate = null)
    {
        _disk = source.Files.ToDictionary(StringComparer.Ordinal);
        _imagePaths = source.ImagePaths.ToHashSet(StringComparer.Ordinal);
        _options = options ?? CompilerOptions.Default;
        _missingMetadata = requiresProjectMetadata && options is null;
        _analyze = analyze ?? ((snapshot, compilerOptions, token) =>
            Task.Run(() => StoryAnalyzer.Analyze(snapshot, compilerOptions), token));
        _delay = delay ?? Task.Delay;
        _analysisGate = analysisGate ?? new SemaphoreSlim(1, 1);
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

    /// <summary>Finds the outermost manifest ancestor, without evaluating project files.</summary>
    public static string? FindStoryRoot(string fileOrDirectory)
    {
        var full = Path.GetFullPath(fileOrDirectory);
        var directory = Directory.Exists(full) ? new DirectoryInfo(full) : Directory.GetParent(full);
        string? root = null;
        for (; directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, StorySource.ManifestFileName))) root = directory.FullName;
        return root;
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
    public Task<AnalysisUpdate> AnalyzeAsync(TimeSpan debounce, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<AnalysisUpdate>(cancellationToken);
        return GetSharedAnalysis(debounce, retainSupersededResult: true).Completion.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the single scheduled analysis for the latest revision. Superseded work that has not
    /// started may be skipped; a caller can stop waiting without cancelling the shared analysis.
    /// </summary>
    public SharedAnalysis ScheduleAnalysis(TimeSpan debounce) => GetSharedAnalysis(debounce, retainSupersededResult: false);

    internal bool HasText(string path, string text)
    {
        path = Normalize(path);
        lock (_gate)
        {
            return _open.TryGetValue(path, out var document)
                ? document.Text == text
                : _disk.TryGetValue(path, out var diskText) && diskText == text;
        }
    }

    private SharedAnalysis GetSharedAnalysis(TimeSpan debounce, bool retainSupersededResult)
    {
        lock (_gate)
        {
            if (_shared is { } existing)
            {
                if (retainSupersededResult) existing.RetainResult();
                if (debounce == TimeSpan.Zero) existing.StartImmediately();
                return existing;
            }
            var files = new Dictionary<string, string>(_disk, StringComparer.Ordinal);
            foreach (var (path, document) in _open) files[path] = document.Text;
            var snapshot = new StorySource(files, _imagePaths);
            var shared = new SharedAnalysis(_revision, retainSupersededResult);
            shared.SetCompletion(_current is { } current
                ? Task.FromResult(new AnalysisUpdate(_revision, current, true))
                : RunSharedAnalysisAsync(shared, snapshot, debounce));
            _shared = shared;
            return shared;
        }
    }

    private async Task<AnalysisUpdate> RunSharedAnalysisAsync(SharedAnalysis shared, StorySource snapshot, TimeSpan debounce)
    {
        await Task.Yield();
        using var delayCancellation = new CancellationTokenSource();
        var delay = _delay(debounce, delayCancellation.Token);
        var ready = shared.RetainSupersededResult
            ? await Task.WhenAny(delay, shared.StartRequested).ConfigureAwait(false)
            : await Task.WhenAny(delay, shared.StartRequested, shared.Superseded).ConfigureAwait(false);
        if (ready != delay)
        {
            delayCancellation.Cancel();
            try { await delay.ConfigureAwait(false); }
            catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested) { }
        }
        else await delay.ConfigureAwait(false);
        if (shared.Superseded.IsCompleted && !shared.RetainSupersededResult)
            throw new OperationCanceledException("Analysis revision was superseded before it started.");

        using var gateCancellation = new CancellationTokenSource();
        var enter = _analysisGate.WaitAsync(gateCancellation.Token);
        if (!shared.RetainSupersededResult &&
            await Task.WhenAny(enter, shared.Superseded).ConfigureAwait(false) == shared.Superseded &&
            !enter.IsCompletedSuccessfully)
        {
            gateCancellation.Cancel();
            try
            {
                await enter.ConfigureAwait(false);
                _analysisGate.Release();
            }
            catch (OperationCanceledException) when (gateCancellation.IsCancellationRequested) { }
            throw new OperationCanceledException("Analysis revision was superseded before it started.");
        }
        await enter.ConfigureAwait(false);
        try
        {
            if (shared.Superseded.IsCompleted && !shared.RetainSupersededResult)
                throw new OperationCanceledException("Analysis revision was superseded before it started.");
            var analysis = await _analyze(snapshot, _options, CancellationToken.None).ConfigureAwait(false);
            if (_missingMetadata)
            {
                analysis = analysis.WithDiagnostic(new Diagnostic(DiagnosticSeverity.Error, "TW5001",
                    "Project authoring metadata is missing. Explicitly supply the project's CompilerOptions or exported authoring metadata; editing never loads project assemblies.",
                    new SourceLocation(StorySource.ManifestFileName, 1, 1)));
            }
            lock (_gate)
            {
                var published = shared.Revision == _revision;
                if (published) _current = analysis;
                return new(shared.Revision, analysis, published);
            }
        }
        finally { _analysisGate.Release(); }
    }

    private void Invalidate()
    {
        _revision++;
        _current = null;
        _shared?.Supersede();
        _shared = null;
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
