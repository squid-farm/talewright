using System.Text.Json;
using System.Threading.Channels;
using Talewright.Authoring;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

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
    private bool _hoverMarkdown;
    private bool _inlayRefreshSupported;
    private bool _inlayRefreshPending;
    private const string InlayRefreshId = "talewright/inlayHintRefresh";
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record Document(string Root, int Version, string Text);
    private sealed record Completed(Story Story, long Generation, AnalysisUpdate? Update, Exception? Error);
    private sealed record ProtocolError(int Code, string Message);
    private enum Feature { Completion, Hover, Definition, InlayHints }
    private sealed class Story(
        string root,
        StoryWorkspace workspace,
        LintConfiguration lintConfiguration,
        Exception? lintConfigurationError)
    {
        public string Root { get; } = root;
        public StoryWorkspace Workspace { get; } = workspace;
        public LintConfiguration LintConfiguration { get; } = lintConfiguration;
        public Exception? LintConfigurationError { get; } = lintConfigurationError;
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
        if (hasId && id.ValueKind == JsonValueKind.String && id.GetString() == InlayRefreshId &&
            !message.TryGetProperty("method", out _) &&
            message.TryGetProperty("jsonrpc", out var responseVersion) &&
            responseVersion.ValueKind == JsonValueKind.String && responseVersion.GetString() == "2.0")
        {
            _inlayRefreshPending = false;
            if (message.TryGetProperty("error", out var refreshError))
                await errors.WriteLineAsync($"TWLSP002: inlay hint refresh: {refreshError}");
            return;
        }
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
                        hoverProvider = true,
                        definitionProvider = true,
                        inlayHintProvider = true,
                        completionProvider = new { resolveProvider = false, triggerCharacters = new[] { ":", "@", "#", ">", "(", "\"", "/" } },
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
                case "textDocument/completion":
                    if (!hasId) throw new ArgumentException("completion requires a request id.");
                    await SemanticAsync(id, parameters, Feature.Completion, cancellationToken);
                    break;
                case "textDocument/hover":
                    if (!hasId) throw new ArgumentException("hover requires a request id.");
                    await SemanticAsync(id, parameters, Feature.Hover, cancellationToken);
                    break;
                case "textDocument/definition":
                    if (!hasId) throw new ArgumentException("definition requires a request id.");
                    await SemanticAsync(id, parameters, Feature.Definition, cancellationToken);
                    break;
                case "textDocument/inlayHint":
                    if (!hasId) throw new ArgumentException("inlayHint requires a request id.");
                    await SemanticAsync(id, parameters, Feature.InlayHints, cancellationToken);
                    break;
                case "textDocument/didChange":
                    Change(parameters);
                    break;
                case "textDocument/didClose":
                    Close(parameters);
                    break;
                case "textDocument/didSave":
                    Refresh([PathFromUri(parameters.GetProperty("textDocument").GetProperty("uri").GetString()!)]);
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
        if (parameters.TryGetProperty("capabilities", out var clientCapabilities) &&
            clientCapabilities.TryGetProperty("workspace", out var workspaceCapabilities) &&
            workspaceCapabilities.TryGetProperty("inlayHint", out var inlayHint) &&
            inlayHint.TryGetProperty("refreshSupport", out var refreshSupport))
            _inlayRefreshSupported = refreshSupport.GetBoolean();
        if (parameters.TryGetProperty("capabilities", out var capabilities) &&
            capabilities.TryGetProperty("textDocument", out var textDocument) &&
            textDocument.TryGetProperty("hover", out var hover) &&
            hover.TryGetProperty("contentFormat", out var formats) && formats.ValueKind == JsonValueKind.Array)
            _hoverMarkdown = formats.EnumerateArray()
                .Select(format => format.ValueKind == JsonValueKind.String ? format.GetString() : null)
                .FirstOrDefault(format => format is "markdown" or "plaintext") == "markdown";
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
        var version = DocumentVersion(document);
        var text = document.GetProperty("text").GetString()!;
        var story = GetStory(path, opening: true);
        story.Workspace.Open(Relative(story.Root, path), version, text);
        _documents.Add(path, new(story.Root, version, text));
        Refresh([path]);
    }

    private async Task SemanticAsync(JsonElement id, JsonElement parameters, Feature kind, CancellationToken cancellationToken)
    {
        var feature = kind.ToString();
        var path = PathFromUri(parameters.GetProperty("textDocument").GetProperty("uri").GetString()!);
        if (!_documents.TryGetValue(path, out var document))
            throw new ArgumentException($"{feature} requires an open story document.");
        var lines = document.Text.ReplaceLineEndings("\n").Split('\n');
        var story = _stories[document.Root];
        SourceLocation Position(JsonElement point)
        {
            if (!point.GetProperty("line").TryGetInt32(out var line) || !point.GetProperty("character").TryGetInt32(out var character))
                throw new ArgumentException($"{feature} positions must be 32-bit integers.");
            if (line < 0 || line >= lines.Length || character < 0 || character > lines[line].Length)
                throw new ArgumentException($"{feature} position is outside the current document.");
            return new(Relative(story.Root, path), line + 1, character + 1);
        }
        var position = Position(kind == Feature.InlayHints
            ? parameters.GetProperty("range").GetProperty("start") : parameters.GetProperty("position"));
        var end = kind == Feature.InlayHints ? Position(parameters.GetProperty("range").GetProperty("end")) : position;
        if (Compare(position, end) > 0) throw new ArgumentException($"{feature} range is reversed.");
        if (story.MetadataError is { } error)
        {
            await ErrorAsync(id, -32001, $"{feature} unavailable: {error.Message}", cancellationToken);
            return;
        }
        var analysis = story.Workspace.Current;
        if (analysis is null)
        {
            var update = await story.Workspace.AnalyzeAsync(TimeSpan.Zero, cancellationToken);
            if (!update.Published) throw new InvalidOperationException($"{feature} snapshot was superseded; request again.");
            analysis = update.Analysis;
        }
        if (analysis.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Code is "TW5001" or "TW5002") is { } unavailable)
        {
            await ErrorAsync(id, -32001, $"{feature} unavailable: {unavailable.Message}", cancellationToken);
            return;
        }
        if (kind == Feature.InlayHints)
        {
            var hints = StoryPreview.Get(analysis, position.File)
                .Where(preview => preview.Value is not null &&
                    Compare(position, preview.Range.End) <= 0 && Compare(preview.Range.End, end) <= 0)
                .Select(preview => new
                {
                    position = new { line = preview.Range.End.Line - 1, character = preview.Range.End.Column - 1 },
                    label = StoryPreview.HintLabel(preview.Value!),
                    paddingLeft = true,
                    tooltip = new { kind = "plaintext", value = preview.PlainText }
                }).ToArray();
            await ReplyAsync(id, hints, cancellationToken);
            return;
        }
        if (kind == Feature.Definition)
        {
            var target = StoryNavigation.Definition(analysis, position);
            await ReplyAsync(id, target is null ? null : new
            {
                uri = UriFromPath(Path.GetFullPath(target.Start.File, story.Root)),
                range = new
                {
                    start = new { line = target.Start.Line - 1, character = target.Start.Column - 1 },
                    end = new { line = target.End.Line - 1, character = target.End.Column - 1 }
                }
            }, cancellationToken);
            return;
        }
        if (kind == Feature.Hover)
        {
            var result = StoryHover.Get(analysis, position);
            await ReplyAsync(id, result is null ? null : new
            {
                contents = new { kind = _hoverMarkdown ? "markdown" : "plaintext", value = _hoverMarkdown ? result.Markdown : result.PlainText },
                range = new
                {
                    start = new { line = result.Range.Start.Line - 1, character = result.Range.Start.Column - 1 },
                    end = new { line = result.Range.End.Line - 1, character = result.Range.End.Column - 1 }
                }
            }, cancellationToken);
            return;
        }
        var items = StoryCompletion.Complete(analysis, position);
        await ReplyAsync(id, new
        {
            isIncomplete = false,
            items = items.Where(item => item.Range.Start.Line == item.Range.End.Line).Select(item => new
            {
                label = item.Label,
                detail = item.Detail,
                kind = item.Detail.StartsWith("Image:", StringComparison.Ordinal) ? 17 : 6,
                textEdit = new
                {
                    range = new
                    {
                        start = new { line = item.Range.Start.Line - 1, character = item.Range.Start.Column - 1 },
                        end = new { line = item.Range.End.Line - 1, character = item.Range.End.Column - 1 }
                    },
                    newText = item.NewText
                }
            }).ToArray()
        }, cancellationToken);
    }

    private void Change(JsonElement parameters)
    {
        var document = parameters.GetProperty("textDocument");
        var path = PathFromUri(document.GetProperty("uri").GetString()!);
        if (!IsStoryFile(path)) return;
        if (!_documents.TryGetValue(path, out var current)) throw new InvalidOperationException($"Document is not open: {path}");
        var version = DocumentVersion(document);
        var changes = parameters.GetProperty("contentChanges");
        if (changes.GetArrayLength() != 1 || changes[0].TryGetProperty("range", out _))
            throw new ArgumentException("Expected one full-text content change.");
        var text = changes[0].GetProperty("text").GetString()!;
        var story = _stories[current.Root];
        if (!story.Workspace.Change(Relative(story.Root, path), version, text)) return;
        _documents[path] = current with { Version = version, Text = text };
        Schedule(story);
    }

    private static int DocumentVersion(JsonElement document)
    {
        var version = document.GetProperty("version");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value))
            throw new ArgumentException("Document version must be a 32-bit integer.");
        return value;
    }

    private void Close(JsonElement parameters)
    {
        var path = PathFromUri(parameters.GetProperty("textDocument").GetProperty("uri").GetString()!);
        if (!IsStoryFile(path)) return;
        if (!_documents.TryGetValue(path, out var document)) throw new InvalidOperationException($"Document is not open: {path}");
        var story = _stories[document.Root];
        story.Workspace.Close(Relative(story.Root, path));
        _documents.Remove(path);
        // Closing must discard the overlay even when reloading disk fails.
        // Disk may have changed without a watcher event while the overlay was open.
        Refresh([path]);
    }

    private HashSet<string> ReconcileOwnership()
    {
        var affected = new HashSet<string>(PathComparer);
        foreach (var (path, document) in _documents.ToArray())
        {
            var destination = GetStory(path, document.Root);
            if (PathComparer.Equals(destination.Root, document.Root)) continue;
            affected.Add(document.Root);
            affected.Add(destination.Root);
            _documents[path] = document with { Root = destination.Root };
        }
        foreach (var story in _stories.Values.ToArray())
        {
            foreach (var path in story.Published.ToArray())
            {
                var destination = GetStory(path, story.Root);
                if (PathComparer.Equals(destination.Root, story.Root)) continue;
                story.Published.Remove(path);
                destination.Published.Add(path);
                affected.Add(story.Root);
                affected.Add(destination.Root);
            }
            if (PathComparer.Equals(FindRoot(Path.Combine(story.Root, StorySource.ManifestFileName), story.Root), story.Root))
                continue;
            story.Generation++;
            story.Pending?.Cancel();
            story.Pending?.Dispose();
            _stories.Remove(story.Root);
        }
        return affected;
    }

    private void Refresh(string[] paths)
    {
        foreach (var path in paths.Where(path => Path.GetFileName(path) == StorySource.ManifestFileName && File.Exists(path)))
            GetStory(path);
        var affected = ReconcileOwnership();
        foreach (var story in _stories.Values.Where(story => affected.Contains(story.Root) ||
                     paths.Any(path => IsWithin(story.Root, path) ||
                     IsWithin(path, story.Root) ||
                     (_metadataPath is not null && PathComparer.Equals(path, Path.GetFullPath(_metadataPath, story.Root))))).ToArray())
        {
            Reload(story);
            Schedule(story);
        }
    }

    private Story GetStory(string path, string? fallbackRoot = null, bool opening = false)
    {
        var root = FindRoot(path, fallbackRoot,
            opening && Path.GetFileName(path) == StorySource.ManifestFileName ? Path.GetDirectoryName(path) : null);
        if (_stories.TryGetValue(root, out var existing)) return existing;
        var story = LoadStory(root);
        _stories.Add(root, story);
        return story;
    }

    private string FindRoot(string path, string? fallbackRoot = null, string? openingManifestRoot = null) =>
        _documents.Keys.Where(path => Path.GetFileName(path) == StorySource.ManifestFileName)
                .Select(Path.GetDirectoryName)
                .Append(StoryWorkspace.FindStoryRoot(path))
                .Append(openingManifestRoot)
                .OfType<string>().Where(root => IsWithin(root, path)).OrderBy(root => root.Length).FirstOrDefault()
            ?? _folders.Where(root => IsWithin(root, path)).OrderByDescending(root => root.Length).FirstOrDefault()
            ?? fallbackRoot
            ?? throw new ArgumentException($"No story.talewright.yaml or workspace root found for {path}.");

    private Story LoadStory(string root)
    {
        var source = Directory.Exists(root) ? StorySource.FromDirectory(root) : new StorySource(new Dictionary<string, string>());
        var lintConfiguration = LintConfiguration.Defaults;
        Exception? lintConfigurationError = null;
        try
        {
            lintConfiguration = LintConfiguration.LoadFromDirectory(root);
        }
        catch (Exception exception) when (exception is LintConfigurationException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            lintConfigurationError = exception;
        }

        StoryWorkspace workspace;
        Exception? metadataError = null;
        if (_metadataPath is not null)
        {
            try
            {
                var metadata = AuthoringProjectMetadata.FromJson(File.ReadAllText(Path.GetFullPath(_metadataPath, root)));
                workspace = StoryWorkspace.FromMetadata(source, metadata);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                workspace = new StoryWorkspace(source, requiresProjectMetadata: true);
                metadataError = new InvalidDataException($"Authoring metadata '{_metadataPath}': {exception.Message}", exception);
            }
        }
        else
        {
            workspace = new StoryWorkspace(source,
                requiresProjectMetadata: _requiresMetadata ||
                    (Directory.Exists(root) && Directory.EnumerateFiles(root, "*.csproj").Any()));
        }

        return new(root, workspace, lintConfiguration, lintConfigurationError) { MetadataError = metadataError };
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool IsWithin(string root, string path) => !Path.IsPathRooted(Path.GetRelativePath(root, path)) &&
        Path.GetRelativePath(root, path) is var relative && relative != ".." &&
        !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    private static bool IsStoryFile(string path) => StorySource.IsStoryFile(Path.GetFileName(path));
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
            var lintConfiguration = Path.Combine(story.Root, LintConfiguration.FileName);
            var errorPaths = story.Published.Append(manifest);
            if (story.LintConfigurationError is not null) errorPaths = errorPaths.Append(lintConfiguration);
            foreach (var path in errorPaths.Distinct(PathComparer).Where(Owns).ToArray())
            {
                var diagnostics = PathComparer.Equals(path, manifest)
                    ? new[] { ToDiagnostic(new Diagnostic(DiagnosticSeverity.Error, "TW5002",
                        $"Cannot analyze story: {error.Message}", new(StorySource.ManifestFileName, 1, 1)), "") }
                    : PathComparer.Equals(path, lintConfiguration) && story.LintConfigurationError is { } configurationError
                        ? new[] { ToLintConfigurationDiagnostic(configurationError.Message) }
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
            if (story.LintConfigurationError is not null) story.Published.Add(lintConfiguration);
            return;
        }
        if (completed.Update is not { Published: true } update) return;
        var lintResult = StoryLintAnalyzer.Analyze(update.Analysis, story.LintConfiguration);
        var groups = new Dictionary<string, List<object>>(PathComparer);
        foreach (var diagnostic in lintResult.CompilerDiagnostics)
        {
            var file = string.IsNullOrEmpty(diagnostic.Location.File)
                ? StorySource.ManifestFileName
                : diagnostic.Location.File;
            Add(file, ToDiagnostic(diagnostic, update.Analysis.Source.Files.GetValueOrDefault(file, "")));
        }
        foreach (var diagnostic in lintResult.RuleDiagnostics)
        {
            var file = diagnostic.Range.Start.File;
            Add(file, ToDiagnostic(diagnostic, update.Analysis.Source.Files.GetValueOrDefault(file, "")));
        }
        if (story.LintConfigurationError is { } lintError)
        {
            Add(LintConfiguration.FileName, ToLintConfigurationDiagnostic(lintError.Message));
        }

        var paths = groups.Keys.Concat(story.Published)
            .Concat(_documents.Where(pair => PathComparer.Equals(pair.Value.Root, story.Root)).Select(pair => pair.Key))
            .Distinct(PathComparer).Where(Owns).Order(PathComparer).ToArray();
        foreach (var path in paths)
        {
            var diagnostics = groups.TryGetValue(path, out var found) ? found : [];
            await NotifyAsync("textDocument/publishDiagnostics", new
            {
                uri = UriFromPath(path),
                version = _documents.TryGetValue(path, out var document) ? (int?)document.Version : null,
                diagnostics = diagnostics.ToArray()
            }, cancellationToken);
        }
        story.Published.Clear();
        foreach (var path in groups.Keys) story.Published.Add(path);
        if (_inlayRefreshSupported && !_inlayRefreshPending)
        {
            _inlayRefreshPending = true;
            await _protocol.WriteAsync(new { jsonrpc = "2.0", id = InlayRefreshId, method = "workspace/inlayHint/refresh" },
                cancellationToken);
        }

        void Add(string relativeFile, object diagnostic)
        {
            var path = Path.GetFullPath(relativeFile, story.Root);
            if (!Owns(path)) return;
            if (!groups.TryGetValue(path, out var diagnostics)) groups.Add(path, diagnostics = []);
            diagnostics.Add(diagnostic);
        }

        bool Owns(string path) => PathComparer.Equals(FindRoot(path, story.Root), story.Root);
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

    private static object ToDiagnostic(StoryLintDiagnostic diagnostic, string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = ToPosition(diagnostic.Range.Start, lines);
        var end = ToPosition(diagnostic.Range.End, lines);
        return new
        {
            range = new { start, end },
            severity = diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 2,
            code = diagnostic.Code,
            source = "talewright",
            message = diagnostic.Message
        };
    }

    private static object ToLintConfigurationDiagnostic(string message) => new
    {
        range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } },
        severity = 1,
        code = "TWLSP004",
        source = "talewright",
        message = $"Invalid lint configuration: {message}"
    };

    private static object ToPosition(SourceLocation location, string[] lines)
    {
        var line = Math.Clamp(location.Line - 1, 0, lines.Length - 1);
        return new { line, character = Math.Clamp(location.Column - 1, 0, lines[line].Length) };
    }

    private static int Compare(SourceLocation left, SourceLocation right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);

    private Task ReplyAsync(JsonElement id, object? result, CancellationToken cancellationToken) =>
        _protocol.WriteAsync(new { jsonrpc = "2.0", id, result }, cancellationToken);
    private Task ErrorAsync(object? id, int code, string message, CancellationToken cancellationToken) =>
        _protocol.WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, cancellationToken);
    private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        _protocol.WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);
}
