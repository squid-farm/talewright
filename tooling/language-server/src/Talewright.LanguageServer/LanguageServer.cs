using System.Text.Json;
using System.Threading.Channels;
using Talewright.Authoring;
using Talewright.Tale.Analysis;
using Talewright.Tale.Compilation;
using Talewright.Tale.Diagnostics;

namespace Talewright.LanguageServer;

public sealed class LanguageServer(Stream input, Stream output, TextWriter errors)
{
    private readonly Protocol _protocol = new(input, output);
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(new() { SingleReader = true });
    private readonly Dictionary<string, Story> _stories = new(PathComparer);
    private readonly Dictionary<string, Document> _documents = new(PathComparer);
    private readonly List<string> _folders = [];
    private readonly List<Task> _analyses = [];
    private bool _initialized;
    private bool _shutdown;
    private string? _metadataPath;
    private bool _requiresMetadata;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record Document(string Root, int Version, string Text);
    private sealed record Completed(Story Story, long Generation, AnalysisUpdate? Update, Exception? Error);
    private sealed record ProtocolError(int Code, string Message);
    private sealed class Story(string root, StoryWorkspace workspace)
    {
        public string Root { get; } = root;
        public StoryWorkspace Workspace { get; } = workspace;
        public long Generation { get; set; }
        public CancellationTokenSource? Pending { get; set; }
        public HashSet<string> Published { get; } = new(PathComparer);
        public Exception? MetadataError { get; init; }
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = ReadAsync(lifetime.Token);
        var exitCode = 1;
        try
        {
            await foreach (var item in _events.Reader.ReadAllAsync(lifetime.Token))
            {
                if (item is Completed completed)
                {
                    await PublishAsync(completed, lifetime.Token);
                    continue;
                }
                if (item is ProtocolError protocolError)
                {
                    await ErrorAsync(null, protocolError.Code, protocolError.Message, lifetime.Token);
                    continue;
                }
                if (item is Exception exception) throw new IOException("LSP input failed.", exception);
                if (item is not JsonElement message) break;
                if (message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0" &&
                    message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "exit")
                {
                    exitCode = _shutdown ? 0 : 1;
                    break;
                }
                await HandleAsync(message, lifetime.Token);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            foreach (var story in _stories.Values) story.Pending?.Cancel();
            // Console stdin reads on Unix may not observe cancellation until input arrives.
            // Do not hold the LSP exit response open waiting for the client's next frame.
            if (reader.IsCompleted) await reader;
            await Task.WhenAll(_analyses);
            foreach (var story in _stories.Values) story.Pending?.Dispose();
        }
        return exitCode;
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    if (await _protocol.ReadAsync(cancellationToken) is not { } message) break;
                    await _events.Writer.WriteAsync(message, cancellationToken);
                }
                catch (JsonException exception)
                {
                    _events.Writer.TryWrite(new ProtocolError(-32700, exception.Message));
                }
            }
            _events.Writer.TryWrite(new object());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            _events.Writer.TryWrite(exception);
        }
    }

    private async Task HandleAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            await ErrorAsync(null, -32600, "Expected a JSON-RPC request object.", cancellationToken);
            return;
        }
        var hasId = message.TryGetProperty("id", out var id);
        if (!message.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String ||
            !message.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
            (hasId && id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null)))
        {
            await ErrorAsync(null, -32600, "Invalid JSON-RPC request.", cancellationToken);
            return;
        }
        var method = methodElement.GetString();
        var parameters = message.TryGetProperty("params", out var value) ? value : default;
        try
        {
            if (method == "initialize")
            {
                if (!hasId) throw new ArgumentException("initialize requires a request id.");
                if (_initialized) throw new InvalidOperationException("Already initialized.");
                Configure(parameters);
                _initialized = true;
                await ReplyAsync(id, new
                {
                    capabilities = new
                    {
                        positionEncoding = "utf-16",
                        textDocumentSync = new { openClose = true, change = 1, save = new { includeText = false } },
                        workspace = new { workspaceFolders = new { supported = true, changeNotifications = false } }
                    },
                    serverInfo = new { name = "Talewright", version = typeof(LanguageServer).Assembly.GetName().Version?.ToString() }
                }, cancellationToken);
                return;
            }
            if (!_initialized || _shutdown)
            {
                if (hasId) await ErrorAsync(id, _shutdown ? -32600 : -32002, _shutdown ? "Server has shut down." : "Server not initialized.", cancellationToken);
                return;
            }
            switch (method)
            {
                case "initialized":
                case "$/cancelRequest":
                case "$/setTrace":
                    break;
                case "shutdown":
                    if (!hasId) throw new ArgumentException("shutdown requires a request id.");
                    _shutdown = true;
                    foreach (var story in _stories.Values) { story.Generation++; story.Pending?.Cancel(); }
                    await ReplyAsync(id, null, cancellationToken);
                    break;
                case "textDocument/didOpen":
                    Open(parameters);
                    break;
                case "textDocument/didChange":
                    Change(parameters);
                    break;
                case "textDocument/didClose":
                    Close(parameters);
                    break;
                case "textDocument/didSave":
                    Refresh(PathFromUri(parameters.GetProperty("textDocument").GetProperty("uri").GetString()!));
                    break;
                case "workspace/didChangeWatchedFiles":
                    Refresh(parameters.GetProperty("changes").EnumerateArray()
                        .Select(change => PathFromUri(change.GetProperty("uri").GetString()!)).ToArray());
                    break;
                default:
                    if (hasId) await ErrorAsync(id, -32601, $"Method not supported: {method}", cancellationToken);
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            await errors.WriteLineAsync($"TWLSP002: {method}: {exception.Message}");
            if (hasId) await ErrorAsync(id, -32602, exception.Message, cancellationToken);
            else await NotifyAsync("window/showMessage", new { type = 1, message = $"Talewright: {exception.Message}" }, cancellationToken);
        }
    }

    private void Configure(JsonElement parameters)
    {
        if (parameters.TryGetProperty("workspaceFolders", out var folders) && folders.ValueKind == JsonValueKind.Array)
            foreach (var folder in folders.EnumerateArray()) _folders.Add(PathFromUri(folder.GetProperty("uri").GetString()!));
        else if (parameters.TryGetProperty("rootUri", out var root) && root.ValueKind == JsonValueKind.String)
            _folders.Add(PathFromUri(root.GetString()!));
        if (parameters.TryGetProperty("initializationOptions", out var options) && options.ValueKind == JsonValueKind.Object)
        {
            if (options.TryGetProperty("metadataPath", out var path) && path.ValueKind == JsonValueKind.String)
                _metadataPath = string.IsNullOrWhiteSpace(path.GetString()) ? null : path.GetString();
            if (options.TryGetProperty("requiresProjectMetadata", out var required)) _requiresMetadata = required.GetBoolean();
        }
    }

    private void Open(JsonElement parameters)
    {
        var document = parameters.GetProperty("textDocument");
        var path = PathFromUri(document.GetProperty("uri").GetString()!);
        if (!IsStoryFile(path)) return;
        if (_documents.ContainsKey(path)) throw new InvalidOperationException($"Document already open: {path}");
        var story = GetStory(path);
        var version = document.GetProperty("version").GetInt32();
        var text = document.GetProperty("text").GetString()!;
        story.Workspace.Open(Relative(story.Root, path), version, text);
        _documents.Add(path, new(story.Root, version, text));
        Schedule(story);
    }

    private void Change(JsonElement parameters)
    {
        var document = parameters.GetProperty("textDocument");
        var path = PathFromUri(document.GetProperty("uri").GetString()!);
        if (!IsStoryFile(path)) return;
        if (!_documents.TryGetValue(path, out var current)) throw new InvalidOperationException($"Document is not open: {path}");
        var version = document.GetProperty("version").GetInt32();
        var changes = parameters.GetProperty("contentChanges");
        if (changes.GetArrayLength() != 1 || changes[0].TryGetProperty("range", out _))
            throw new ArgumentException("Expected one full-text content change.");
        var text = changes[0].GetProperty("text").GetString()!;
        var story = _stories[current.Root];
        if (!story.Workspace.Change(Relative(story.Root, path), version, text)) return;
        _documents[path] = current with { Version = version, Text = text };
        Schedule(story);
    }

    private void Close(JsonElement parameters)
    {
        var path = PathFromUri(parameters.GetProperty("textDocument").GetProperty("uri").GetString()!);
        if (!IsStoryFile(path)) return;
        if (!_documents.TryGetValue(path, out var document)) throw new InvalidOperationException($"Document is not open: {path}");
        var story = _stories[document.Root];
        // Disk may have changed without a watcher event while the overlay was open.
        story = Reload(story);
        story.Workspace.Close(Relative(story.Root, path));
        _documents.Remove(path);
        Schedule(story);
    }

    private void Refresh(params string[] paths)
    {
        foreach (var story in _stories.Values.Where(story => paths.Any(path => IsWithin(story.Root, path) ||
                     (_metadataPath is not null && PathComparer.Equals(path, Path.GetFullPath(_metadataPath, story.Root))))).ToArray())
        {
            Reload(story);
            Schedule(story);
        }
    }

    private Story GetStory(string path)
    {
        var root = StoryWorkspace.FindStoryRoot(path)
            ?? _stories.Keys.Where(root => IsWithin(root, path)).OrderByDescending(root => root.Length).FirstOrDefault()
            ?? (Path.GetFileName(path) == StorySource.ManifestFileName ? Path.GetDirectoryName(path) : null)
            ?? _folders.Where(root => IsWithin(root, path)).OrderByDescending(root => root.Length).FirstOrDefault()
            ?? throw new ArgumentException($"No story.yaml or workspace root found for {path}.");
        if (_stories.TryGetValue(root, out var existing)) return existing;
        var story = LoadStory(root);
        _stories.Add(root, story);
        return story;
    }

    private Story LoadStory(string root)
    {
        var source = StorySource.FromDirectory(root);
        if (_metadataPath is not null)
        {
            try
            {
                var metadata = AuthoringProjectMetadata.FromJson(File.ReadAllText(Path.GetFullPath(_metadataPath, root)));
                return new(root, StoryWorkspace.FromMetadata(source, metadata));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                return new(root, new StoryWorkspace(source, requiresProjectMetadata: true))
                {
                    MetadataError = new InvalidDataException($"Authoring metadata '{_metadataPath}': {exception.Message}", exception)
                };
            }
        }
        return new(root, new StoryWorkspace(source,
            requiresProjectMetadata: _requiresMetadata || Directory.EnumerateFiles(root, "*.csproj").Any()));
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool IsWithin(string root, string path) => !Path.IsPathRooted(Path.GetRelativePath(root, path)) &&
        Path.GetRelativePath(root, path) is var relative && relative != ".." &&
        !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    private static bool IsStoryFile(string path) => Path.GetFileName(path) == StorySource.ManifestFileName ||
        path.EndsWith(StorySource.TaleExtension, StringComparison.OrdinalIgnoreCase);
    private static string PathFromUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile)
            throw new ArgumentException("Only absolute file: document URIs are supported.");
        return Path.GetFullPath(uri.LocalPath);
    }
    private static string UriFromPath(string path) => new Uri(path).AbsoluteUri;

    private Story Reload(Story story)
    {
        story.Generation++;
        story.Pending?.Cancel();
        var replacement = LoadStory(story.Root);
        replacement.Generation = story.Generation;
        foreach (var path in story.Published) replacement.Published.Add(path);
        foreach (var (path, document) in _documents.Where(pair => PathComparer.Equals(pair.Value.Root, story.Root)))
            replacement.Workspace.Open(Relative(story.Root, path), document.Version, document.Text);
        story.Pending?.Dispose();
        _stories[story.Root] = replacement;
        return replacement;
    }

    private void Schedule(Story story)
    {
        // Reload can replace a workspace; always schedule its current instance.
        story = _stories[story.Root];
        story.Pending?.Cancel();
        story.Pending?.Dispose();
        story.Pending = new();
        var generation = ++story.Generation;
        if (story.MetadataError is { } error)
        {
            _events.Writer.TryWrite(new Completed(story, generation, null, error));
            return;
        }
        var task = AnalyzeAsync(story, generation, story.Pending.Token);
        _analyses.RemoveAll(task => task.IsCompletedSuccessfully);
        _analyses.Add(task);
    }

    private async Task AnalyzeAsync(Story story, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var update = await story.Workspace.AnalyzeAsync(TimeSpan.FromMilliseconds(150), cancellationToken);
            _events.Writer.TryWrite(new Completed(story, generation, update, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            _events.Writer.TryWrite(new Completed(story, generation, null, exception));
        }
    }

    private async Task PublishAsync(Completed completed, CancellationToken cancellationToken)
    {
        var story = completed.Story;
        if (_shutdown || !_stories.TryGetValue(story.Root, out var current) || !ReferenceEquals(story, current) ||
            story.Generation != completed.Generation) return;
        if (completed.Error is { } error)
        {
            await errors.WriteLineAsync($"TWLSP003: {error.Message}");
            await NotifyAsync("window/showMessage", new { type = 1, message = $"Talewright analysis failed: {error.Message}" }, cancellationToken);
            var manifest = Path.Combine(story.Root, StorySource.ManifestFileName);
            foreach (var path in story.Published.Append(manifest).Distinct(PathComparer).ToArray())
            {
                var diagnostics = PathComparer.Equals(path, manifest)
                    ? new[] { ToDiagnostic(new Diagnostic(DiagnosticSeverity.Error, "TW5002",
                        $"Cannot analyze story: {error.Message}", new(StorySource.ManifestFileName, 1, 1)), "") }
                    : [];
                await NotifyAsync("textDocument/publishDiagnostics", new
                {
                    uri = UriFromPath(path),
                    version = _documents.TryGetValue(path, out var document) ? (int?)document.Version : null,
                    diagnostics
                }, cancellationToken);
            }
            story.Published.Clear();
            story.Published.Add(manifest);
            return;
        }
        if (completed.Update is not { Published: true } update) return;
        var groups = update.Analysis.Diagnostics.GroupBy(diagnostic =>
                string.IsNullOrEmpty(diagnostic.Location.File) ? StorySource.ManifestFileName : diagnostic.Location.File)
            .ToDictionary(group => Path.GetFullPath(group.Key, story.Root), group => group.ToArray(), PathComparer);
        var paths = groups.Keys.Concat(story.Published)
            .Concat(_documents.Where(pair => PathComparer.Equals(pair.Value.Root, story.Root)).Select(pair => pair.Key))
            .Distinct(PathComparer).Order(PathComparer).ToArray();
        foreach (var path in paths)
        {
            var diagnostics = groups.TryGetValue(path, out var found) ? found : [];
            var text = update.Analysis.Source.Files.GetValueOrDefault(Relative(story.Root, path), "");
            await NotifyAsync("textDocument/publishDiagnostics", new
            {
                uri = UriFromPath(path),
                version = _documents.TryGetValue(path, out var document) ? (int?)document.Version : null,
                diagnostics = diagnostics.Select(diagnostic => ToDiagnostic(diagnostic, text)).ToArray()
            }, cancellationToken);
        }
        story.Published.Clear();
        foreach (var path in groups.Keys) story.Published.Add(path);
    }

    private static object ToDiagnostic(Diagnostic diagnostic, string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var line = Math.Clamp(diagnostic.Location.Line - 1, 0, lines.Length - 1);
        var character = Math.Clamp(diagnostic.Location.Column - 1, 0, lines[line].Length);
        var point = new { line, character };
        return new
        {
            range = new { start = point, end = point },
            severity = diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 2,
            code = diagnostic.Code,
            source = "talewright",
            message = diagnostic.Message
        };
    }

    private Task ReplyAsync(JsonElement id, object? result, CancellationToken cancellationToken) =>
        _protocol.WriteAsync(new { jsonrpc = "2.0", id, result }, cancellationToken);
    private Task ErrorAsync(object? id, int code, string message, CancellationToken cancellationToken) =>
        _protocol.WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, cancellationToken);
    private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        _protocol.WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);
}
