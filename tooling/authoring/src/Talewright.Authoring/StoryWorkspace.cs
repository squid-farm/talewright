using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Parsing;
using Talewright.Talemark.Vocabulary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Talewright.Authoring;

public sealed record AnalysisUpdate(long Revision, StoryAnalysis Analysis, bool Published);

internal sealed record SceneNavigationSymbol(string File, string Id, string? Title, SourceRange Range,
    SourceRange SelectionRange);

/// <summary>One cancellable analysis shared by callers interested in the same workspace revision.</summary>
public sealed class SharedAnalysis
{
    private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _superseded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();
    private Task<AnalysisUpdate>? _completion;
    private long _startedAt;

    internal SharedAnalysis(long revision) => Revision = revision;

    public long Revision { get; }
    public Task<AnalysisUpdate> Completion => _completion ?? throw new InvalidOperationException("Analysis has not been scheduled.");
    public Task Superseded => _superseded.Task;
    internal CancellationToken CancellationToken => _cancellation.Token;
    internal long ScheduledAt { get; } = Stopwatch.GetTimestamp();
    internal long ReadyAt { get; set; }
    internal long StartedAt
    {
        get => Volatile.Read(ref _startedAt);
        set => Volatile.Write(ref _startedAt, value);
    }
    internal long FinishedAt { get; set; }
    internal int ParsedFiles { get; set; }
    internal int CachedFiles { get; set; }

    /// <summary>Ends the debounce without creating another analysis.</summary>
    public void StartImmediately() => _start.TrySetResult();

    internal Task StartRequested => _start.Task;
    internal void Supersede()
    {
        if (_superseded.TrySetResult()) _cancellation.Cancel();
    }
    internal void ThrowIfCancellationRequested()
    {
        // Supersession can wake a worker before token cancellation has run.
        if (_superseded.Task.IsCompleted) throw new OperationCanceledException(_cancellation.Token);
        _cancellation.Token.ThrowIfCancellationRequested();
    }
    internal void SetCompletion(Task<AnalysisUpdate> completion) => _completion = completion;
}

/// <summary>
/// One story's disk snapshot and versioned unsaved buffers. Hosts forward filesystem notifications;
/// this class does not start watchers, execute projects, or own an editor protocol.
/// </summary>
public sealed class StoryWorkspace
{
    private readonly object _gate = new();
    private readonly object _currentFileGate = new();
    private readonly Dictionary<string, string> _disk;
    private readonly HashSet<string> _imagePaths;
    private readonly Dictionary<string, (int Version, string Text)> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaleFileSyntax> _syntax = new(StringComparer.Ordinal);
    private readonly CompilerOptions _options;
    private readonly bool _missingMetadata;
    private readonly Func<StorySource, CompilerOptions, CancellationToken, Task<StoryAnalysis>> _analyze;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string, CancellationToken, TaleFileSyntax> _parseSyntax;
    private readonly SemaphoreSlim _analysisGate;
    private long _revision;
    private StoryAnalysis? _current;
    private SharedAnalysis? _shared;
    private CurrentFileAnalysis? _currentFileAnalysis;
    private int _syntaxParseCount;

    private sealed record CurrentFileAnalysis(string File, string Text, StoryAnalysis? Snapshot, StoryAnalysis Analysis);

    public StoryWorkspace(StorySource source, CompilerOptions? options = null, bool requiresProjectMetadata = false)
        : this(source, options, requiresProjectMetadata, null, null)
    {
    }

    internal StoryWorkspace(StorySource source, CompilerOptions? options, bool requiresProjectMetadata,
        Func<StorySource, CompilerOptions, CancellationToken, Task<StoryAnalysis>>? analyze,
        Func<TimeSpan, CancellationToken, Task>? delay, SemaphoreSlim? analysisGate = null,
        Func<string, CancellationToken, TaleFileSyntax>? parseSyntax = null)
    {
        _disk = source.Files.ToDictionary(StringComparer.Ordinal);
        _imagePaths = source.ImagePaths.ToHashSet(StringComparer.Ordinal);
        _options = options ?? CompilerOptions.Default;
        _missingMetadata = requiresProjectMetadata && options is null;
        _analyze = analyze ?? ((snapshot, compilerOptions, token) =>
            Task.Run(() => AnalyzeSnapshot(snapshot, compilerOptions, token), token));
        _delay = delay ?? Task.Delay;
        _parseSyntax = parseSyntax ?? TaleSyntaxParser.Parse;
        _analysisGate = analysisGate ?? new SemaphoreSlim(1, 1);
    }

    public static StoryWorkspace FromMetadata(StorySource source, AuthoringProjectMetadata metadata) =>
        new(source, metadata.CreateOptions());

    public StoryAnalysis? Current
    {
        get { lock (_gate) return _current; }
    }

    internal int SyntaxParseCount => Volatile.Read(ref _syntaxParseCount);

    internal StoryAnalysis AnalyzeSnapshot(StorySource snapshot, CompilerOptions options, CancellationToken token) =>
        StoryAnalyzer.Analyze(snapshot, options, token, GetSyntax);

    /// <summary>Analyzes an edited copy of the story with this workspace's options, reusing unchanged files' syntax.</summary>
    internal StoryAnalysis AnalyzeEdited(StorySource edited, CancellationToken token) =>
        AnalyzeSnapshot(edited, _options, token);

    internal IReadOnlyDictionary<string, TaleFileSyntax> Syntax
    {
        get { lock (_gate) return new Dictionary<string, TaleFileSyntax>(_syntax, StringComparer.Ordinal); }
    }

    internal void ReuseSyntaxFrom(StoryWorkspace previous)
    {
        var entries = previous.Syntax;
        lock (_gate)
        {
            foreach (var (path, syntax) in entries)
                if (TryGetText(path, out var text) && string.Equals(text, syntax.Text, StringComparison.Ordinal))
                    _syntax[path] = syntax;
        }
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
            Invalidate(path);
        }
    }

    public void Open(string path, int version, string text)
    {
        path = Normalize(path);
        lock (_gate)
        {
            if (_open.ContainsKey(path)) throw new InvalidOperationException($"'{path}' is already open.");
            _open.Add(path, (version, text));
            Invalidate(path);
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
            Invalidate(path);
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
            Invalidate(path);
        }
    }

    /// <summary>
    /// Debounces then analyzes an immutable whole-story snapshot. Superseded analysis is cancelled;
    /// the caller's token cancels only its wait and never the work shared with other callers.
    /// </summary>
    public Task<AnalysisUpdate> AnalyzeAsync(TimeSpan debounce, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<AnalysisUpdate>(cancellationToken);
        return GetSharedAnalysis(debounce).Completion.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the single scheduled analysis for the latest revision. Superseded work that has not
    /// started may be skipped; a caller can stop waiting without cancelling the shared analysis.
    /// </summary>
    public SharedAnalysis ScheduleAnalysis(TimeSpan debounce) => GetSharedAnalysis(debounce);

    internal bool HasText(string path, string text)
    {
        path = Normalize(path);
        lock (_gate)
        {
            return TryGetText(path, out var current) && string.Equals(current, text, StringComparison.Ordinal);
        }
    }

    internal StoryAnalysis AnalyzeCurrentFile(string path, string text, StoryAnalysis? snapshot,
        CancellationToken cancellationToken)
    {
        path = Normalize(path);
        lock (_currentFileGate)
        {
            lock (_gate)
            {
                if (_currentFileAnalysis is { } cached &&
                    cached.File == path && cached.Text == text && ReferenceEquals(cached.Snapshot, snapshot))
                    return cached.Analysis;
            }

            Dictionary<string, string> files;
            string[] images;
            lock (_gate)
            {
                files = snapshot?.Source.Files.ToDictionary(StringComparer.Ordinal) ?? new(_disk, StringComparer.Ordinal);
                if (snapshot is null)
                    foreach (var (openPath, document) in _open)
                        files[openPath] = document.Text;
                images = _imagePaths.ToArray();
            }
            files[path] = text;
            var source = new StorySource(files, images);
            var syntax = path.EndsWith(StorySource.TaleExtension, StringComparison.OrdinalIgnoreCase)
                ? GetSyntax(path, text, cancellationToken)
                : null;
            var analysis = StoryAnalyzer.AnalyzeCurrentFile(source, path, syntax, snapshot, _options, cancellationToken);
            if (_missingMetadata && !analysis.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == AuthoringDiagnosticCodes.CustomFeaturesUnavailable))
                analysis = analysis.WithDiagnostic(new Diagnostic(DiagnosticSeverity.Error,
                    AuthoringDiagnosticCodes.CustomFeaturesUnavailable,
                    "Project authoring metadata is missing. Explicitly supply the project's CompilerOptions or exported authoring metadata; editing never loads project assemblies.",
                    new SourceLocation(StorySource.ManifestFileName, 1, 1)));

            lock (_gate)
            {
                if (TryGetText(path, out var current) && string.Equals(current, text, StringComparison.Ordinal))
                    _currentFileAnalysis = new(path, text, snapshot, analysis);
            }
            return analysis;
        }
    }

    internal IReadOnlyList<SceneNavigationSymbol> GetSceneSymbols(string? file = null)
    {
        var files = new List<(string Path, string Text)>();
        lock (_gate)
        {
            var paths = file is null
                ? _disk.Keys.Concat(_open.Keys).Distinct(StringComparer.Ordinal).ToArray()
                : [Normalize(file)];
            foreach (var path in paths)
                if (path.EndsWith(StorySource.TaleExtension, StringComparison.OrdinalIgnoreCase) &&
                    TryGetText(path, out var text))
                    files.Add((path, text));
        }

        var symbols = new List<SceneNavigationSymbol>();
        foreach (var (path, text) in files)
        {
            var syntax = GetSyntax(path, text, CancellationToken.None);
            for (var index = 0; index < syntax.Scenes.Count; index++)
            {
                var scene = syntax.Scenes[index];
                // A header still being typed (`===` with no id) is not navigable; editors reject empty symbol names.
                if (string.IsNullOrWhiteSpace(scene.Id))
                    continue;
                var title = scene.Lines
                    .Where(line => line.MetadataKey == SceneMetadataKey.Title)
                    .Select(line => syntax.Value(scene, line).ToString())
                    .LastOrDefault();
                var start = new SourceLocation(path, scene.Line + 1, scene.HeaderIndent + 1);
                var end = index + 1 < syntax.Scenes.Count
                    ? new SourceLocation(path, syntax.Scenes[index + 1].Line + 1,
                        syntax.Scenes[index + 1].HeaderIndent + 1)
                    : EndLocation(path, syntax);
                var selectionStart = new SourceLocation(path, scene.Line + 1, scene.HeaderIdColumn + 1);
                var selectionEnd = new SourceLocation(path, scene.Line + 1,
                    scene.HeaderIdColumn + scene.Id.Length + 1);
                symbols.Add(new(path, scene.Id, title, new(start, end),
                    new(selectionStart, selectionEnd)));
            }
        }
        return symbols;
    }

    private SharedAnalysis GetSharedAnalysis(TimeSpan debounce)
    {
        lock (_gate)
        {
            if (_shared is { } existing)
            {
                if (debounce == TimeSpan.Zero) existing.StartImmediately();
                return existing;
            }
            var files = new Dictionary<string, string>(_disk, StringComparer.Ordinal);
            foreach (var (path, document) in _open) files[path] = document.Text;
            var snapshot = new StorySource(files, _imagePaths);
            var shared = new SharedAnalysis(_revision);
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
        var ready = await Task.WhenAny(delay, shared.StartRequested, shared.Superseded).ConfigureAwait(false);
        if (ready != delay)
        {
            delayCancellation.Cancel();
            try { await delay.ConfigureAwait(false); }
            catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested) { }
        }
        else await delay.ConfigureAwait(false);
        shared.ReadyAt = Stopwatch.GetTimestamp();
        shared.ThrowIfCancellationRequested();
        await _analysisGate.WaitAsync(shared.CancellationToken).ConfigureAwait(false);
        try
        {
            shared.ThrowIfCancellationRequested();
            shared.StartedAt = Stopwatch.GetTimestamp();
            var parsedBefore = SyntaxParseCount;
            var analysis = await _analyze(snapshot, _options, shared.CancellationToken).ConfigureAwait(false);
            shared.ThrowIfCancellationRequested();
            if (_missingMetadata)
            {
                analysis = analysis.WithDiagnostic(new Diagnostic(DiagnosticSeverity.Error, AuthoringDiagnosticCodes.CustomFeaturesUnavailable,
                    "Project authoring metadata is missing. Explicitly supply the project's CompilerOptions or exported authoring metadata; editing never loads project assemblies.",
                    new SourceLocation(StorySource.ManifestFileName, 1, 1)));
            }
            lock (_gate)
            {
                var published = shared.Revision == _revision;
                if (published) _current = analysis;
                shared.ParsedFiles = SyntaxParseCount - parsedBefore;
                shared.CachedFiles = _syntax.Count;
                shared.FinishedAt = Stopwatch.GetTimestamp();
                return new(shared.Revision, analysis, published);
            }
        }
        finally { _analysisGate.Release(); }
    }

    private TaleFileSyntax GetSyntax(string path, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            if (_syntax.TryGetValue(path, out var cached) && string.Equals(cached.Text, text, StringComparison.Ordinal))
                return cached;

        Interlocked.Increment(ref _syntaxParseCount);
        var syntax = _parseSyntax(text, cancellationToken);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetText(path, out var current) && string.Equals(current, text, StringComparison.Ordinal))
                _syntax[path] = syntax;
        }
        return syntax;
    }

    private static SourceLocation EndLocation(string path, TaleFileSyntax syntax)
    {
        var lastLineStart = syntax.Text.AsSpan().LastIndexOfAny("\r\n\u0085\u2028\u2029") + 1;
        return new SourceLocation(path, syntax.LineCount, syntax.Text.Length - lastLineStart + 1);
    }

    private bool TryGetText(string path, [NotNullWhen(true)] out string? text)
    {
        if (_open.TryGetValue(path, out var document))
        {
            text = document.Text;
            return true;
        }
        return _disk.TryGetValue(path, out text);
    }

    private void Invalidate(string? path = null)
    {
        if (path is null || _currentFileAnalysis?.File == path)
            _currentFileAnalysis = null;
        if (path is not null && _syntax.TryGetValue(path, out var syntax) &&
            (!TryGetText(path, out var text) || !string.Equals(text, syntax.Text, StringComparison.Ordinal)))
            _syntax.Remove(path);
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
