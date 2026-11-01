using System.Text.Json;
using System.Threading.Channels;
using Talewright.Authoring;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.LanguageServer;

public sealed class LanguageServer
{
    private readonly Protocol _protocol;
    private readonly TextWriter _errors;
    private readonly Func<StorySource, CompilerOptions, CancellationToken, Task<StoryAnalysis>>? _analyze;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private readonly Channel<object> _events = Channel.CreateUnbounded<object>(new() { SingleReader = true });
    private readonly Dictionary<string, Story> _stories = new(PathComparer);
    private readonly Dictionary<string, Document> _documents = new(PathComparer);
    private readonly Dictionary<string, PendingRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<Task<SemanticResult>, PendingRequest> _requestTasks = [];
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

    public LanguageServer(Stream input, Stream output, TextWriter errors)
        : this(input, output, errors, null, null)
    {
    }

    internal LanguageServer(Stream input, Stream output, TextWriter errors,
        Func<StorySource, CompilerOptions, CancellationToken, Task<StoryAnalysis>>? analyze,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _protocol = new(input, output);
        _errors = errors;
        _analyze = analyze;
        _delay = delay;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record Document(string Root, int Version, string Text);
    private sealed record Completed(Story Story, long Generation, AnalysisUpdate? Update, Exception? Error);
    private sealed record RequestFinished(PendingRequest Request, Task<SemanticResult> Task);
    private sealed record SemanticResult(object? Value, StoryAnalysis? Previous = null, SourceRange? Target = null,
        int? ErrorCode = null, string? ErrorMessage = null, Exception? Error = null);
    private abstract record EditorDiagnostic(SourceLocation Start)
    {
        public abstract object Convert(LineMap lines);
    }
    private sealed record CompilerEditorDiagnostic(Diagnostic Diagnostic) : EditorDiagnostic(Diagnostic.Location)
    {
        public override object Convert(LineMap lines) => ToDiagnostic(Diagnostic, lines);
    }
    private sealed record LintEditorDiagnostic(StoryLintDiagnostic Diagnostic) : EditorDiagnostic(Diagnostic.Range.Start)
    {
        public override object Convert(LineMap lines) => ToDiagnostic(Diagnostic, lines);
    }
    private sealed record ConfigurationEditorDiagnostic(string Message) : EditorDiagnostic(new SourceLocation("", 1, 1))
    {
        public override object Convert(LineMap lines) => ToLintConfigurationDiagnostic(Message);
    }
    private readonly record struct DiagnosticPosition(int Line, int Character);
    private sealed record ProtocolError(int Code, string Message);
    private enum Feature { Completion, Hover, Definition, InlayHints }
    private const int DiagnosticLimit = 500;
    private sealed class PendingRequest(JsonElement id, string key, string method, string path, Document document,
        Story story, Feature feature, SourceLocation position, SourceLocation end, bool markdown)
    {
        public JsonElement Id { get; } = id;
        public string Key { get; } = key;
        public string Method { get; } = method;
        public string Path { get; } = path;
        public string Root { get; } = document.Root;
        public string Text { get; } = document.Text;
        public Story Story { get; set; } = story;
        public long Generation { get; set; } = story.Generation;
        public Feature Feature { get; } = feature;
        public SourceLocation Position { get; } = position;
        public SourceLocation End { get; } = end;
        public bool Markdown { get; } = markdown;
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed class Story(
        string root,
        StoryWorkspace workspace,
        LintConfiguration lintConfiguration,
        Exception? lintConfigurationError,
        SemaphoreSlim analysisGate)
    {
        public string Root { get; } = root;
        public StoryWorkspace Workspace { get; } = workspace;
        public LintConfiguration LintConfiguration { get; } = lintConfiguration;
        public Exception? LintConfigurationError { get; } = lintConfigurationError;
        public SemaphoreSlim AnalysisGate { get; } = analysisGate;
        public long Generation { get; set; }
        public SharedAnalysis? Pending { get; set; }
        public StoryAnalysis? PublishedAnalysis { get; set; }
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
                if (item is RequestFinished finished)
                {
                    await CompleteRequestAsync(finished, lifetime.Token);
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
            foreach (var story in _stories.Values) story.Pending?.Supersede();
            foreach (var request in _requestTasks.Values.Distinct()) request.Cancellation.Cancel();
            // Console stdin reads on Unix may not observe cancellation until input arrives.
            // Do not hold the LSP exit response open waiting for the client's next frame.
            if (reader.IsCompleted) await reader;
            try
            {
                foreach (var (task, request) in _requestTasks)
                {
                    var failure = await task.ContinueWith(completed => completed.Exception, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    if (failure is not null)
                        await _errors.WriteLineAsync($"TWLSP002: {request.Method}: {failure.GetBaseException().Message}");
                }
            }
            finally
            {
                foreach (var request in _requestTasks.Values.Distinct()) request.Cancellation.Dispose();
                await Task.WhenAll(_analyses);
            }
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
                await _errors.WriteLineAsync($"TWLSP002: inlay hint refresh: {refreshError}");
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
                case "$/setTrace":
                    break;
                case "$/cancelRequest":
                    await CancelRequestAsync(parameters, cancellationToken);
                    break;
                case "shutdown":
                    if (!hasId) throw new ArgumentException("shutdown requires a request id.");
                    _shutdown = true;
                    foreach (var story in _stories.Values) { story.Generation++; story.Pending?.Supersede(); }
                    await CancelOutstandingRequestsAsync(cancellationToken);
                    await ReplyAsync(id, null, cancellationToken);
                    break;
                case "textDocument/didOpen":
                    Open(parameters);
                    break;
                case "textDocument/completion":
                    if (!hasId) throw new ArgumentException("completion requires a request id.");
                    await StartSemanticAsync(id, parameters, Feature.Completion, cancellationToken);
                    break;
                case "textDocument/hover":
                    if (!hasId) throw new ArgumentException("hover requires a request id.");
                    await StartSemanticAsync(id, parameters, Feature.Hover, cancellationToken);
                    break;
                case "textDocument/definition":
                    if (!hasId) throw new ArgumentException("definition requires a request id.");
                    await StartSemanticAsync(id, parameters, Feature.Definition, cancellationToken);
                    break;
                case "textDocument/inlayHint":
                    if (!hasId) throw new ArgumentException("inlayHint requires a request id.");
                    await StartSemanticAsync(id, parameters, Feature.InlayHints, cancellationToken);
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
            await _errors.WriteLineAsync($"TWLSP002: {method}: {exception.Message}");
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

    private async Task StartSemanticAsync(JsonElement id, JsonElement parameters, Feature kind, CancellationToken cancellationToken)
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

        var key = RequestKey(id);
        if (_requests.ContainsKey(key))
        {
            await ErrorAsync(id, -32600, "Request id is already in use.", cancellationToken);
            return;
        }
        if (story.Pending is null) Schedule(story);
        var request = new PendingRequest(id, key, $"textDocument/{(kind == Feature.InlayHints ? "inlayHint" : feature.ToLowerInvariant())}",
            path, document, story, kind, position, end, _hoverMarkdown);
        _requests.Add(key, request);
        var previous = (kind is Feature.Hover or Feature.Definition) &&
            story.PublishedAnalysis is { } published &&
            published.Source.Files.TryGetValue(position.File, out var publishedText) && publishedText == document.Text
                ? published : null;
        StartRequest(request, previous);
    }

    private void StartRequest(PendingRequest request, StoryAnalysis? previous)
    {
        var shared = previous is null
            ? request.Story.Pending ?? throw new InvalidOperationException("No analysis is scheduled for the request.")
            : null;
        shared?.StartImmediately();
        var task = Task.Run(() => CalculateSemanticAsync(request, shared, previous));
        _requestTasks.Add(task, request);
        _ = task.ContinueWith(completed => _events.Writer.TryWrite(new RequestFinished(request, completed)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task<SemanticResult> CalculateSemanticAsync(PendingRequest request, SharedAnalysis? shared, StoryAnalysis? previous)
    {
        try
        {
            var analysis = previous;
            if (analysis is null)
            {
                if (shared is null) throw new InvalidOperationException("No analysis is available for the request.");
                await Task.WhenAny(shared.Completion, shared.Superseded).WaitAsync(request.Cancellation.Token);
                if (shared.Superseded.IsCompleted)
                    return new(null, ErrorCode: -32801, ErrorMessage: "Document content was modified.");
                var update = await shared.Completion.WaitAsync(request.Cancellation.Token);
                if (!update.Published)
                    return new(null, ErrorCode: -32801, ErrorMessage: "Document content was modified.");
                analysis = update.Analysis;
            }
            request.Cancellation.Token.ThrowIfCancellationRequested();
            SemanticResult result;
            try
            {
                result = CalculateSemantic(request, analysis, previous);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or KeyNotFoundException or JsonException)
            {
                return new(null, ErrorCode: -32602, ErrorMessage: exception.Message, Error: exception);
            }
            request.Cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            return new(null, ErrorCode: -32800, ErrorMessage: "Request cancelled.");
        }
        catch (OperationCanceledException) when (shared?.Superseded.IsCompleted == true)
        {
            return new(null, ErrorCode: -32801, ErrorMessage: "Document content was modified.");
        }
    }

    private static SemanticResult CalculateSemantic(PendingRequest request, StoryAnalysis analysis, StoryAnalysis? previous)
    {
        if (analysis.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Code is "TW5001" or "TW5002") is { } unavailable)
            return new(null, ErrorCode: -32001, ErrorMessage: $"{request.Feature} unavailable: {unavailable.Message}");

        var position = request.Position;
        if (request.Feature == Feature.InlayHints)
        {
            var hints = StoryPreview.Get(analysis, position.File)
                .Where(preview => preview.Value is not null &&
                    Compare(position, preview.Range.End) <= 0 && Compare(preview.Range.End, request.End) <= 0)
                .Select(preview => new
                {
                    position = new { line = preview.Range.End.Line - 1, character = preview.Range.End.Column - 1 },
                    label = StoryPreview.HintLabel(preview.Value!),
                    paddingLeft = true,
                    tooltip = new { kind = "plaintext", value = preview.PlainText }
                }).ToArray();
            return new(hints, previous);
        }
        if (request.Feature == Feature.Definition)
        {
            var target = StoryNavigation.Definition(analysis, position);
            var result = target is null ? null : new
            {
                uri = UriFromPath(Path.GetFullPath(target.Start.File, request.Root)),
                range = new
                {
                    start = new { line = target.Start.Line - 1, character = target.Start.Column - 1 },
                    end = new { line = target.End.Line - 1, character = target.End.Column - 1 }
                }
            };
            return new(result, previous, target);
        }
        if (request.Feature == Feature.Hover)
        {
            var result = StoryHover.Get(analysis, position);
            return new(result is null ? null : new
            {
                contents = new { kind = request.Markdown ? "markdown" : "plaintext", value = request.Markdown ? result.Markdown : result.PlainText },
                range = new
                {
                    start = new { line = result.Range.Start.Line - 1, character = result.Range.Start.Column - 1 },
                    end = new { line = result.Range.End.Line - 1, character = result.Range.End.Column - 1 }
                }
            }, previous);
        }
        var items = StoryCompletion.Complete(analysis, position);
        return new(new
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
        }, previous);
    }

    private async Task CompleteRequestAsync(RequestFinished finished, CancellationToken cancellationToken)
    {
        _requestTasks.Remove(finished.Task);
        var request = finished.Request;
        if (!_requests.TryGetValue(request.Key, out var active) || !ReferenceEquals(active, request))
        {
            if (finished.Task.Exception is { } failure)
                await _errors.WriteLineAsync($"TWLSP002: {request.Method}: {failure.GetBaseException().Message}");
            request.Cancellation.Dispose();
            return;
        }
        if (finished.Task.Exception is { } unexpected)
        {
            var failure = unexpected.GetBaseException();
            await _errors.WriteLineAsync($"TWLSP002: {request.Method}: {failure.Message}");
            await FinishRequestErrorAsync(request, -32603, $"Request failed: {failure.Message}", cancellationToken);
            return;
        }
        if (finished.Task.IsCanceled)
        {
            await _errors.WriteLineAsync($"TWLSP002: {request.Method}: request task cancelled unexpectedly.");
            await FinishRequestErrorAsync(request, -32603, "Request task cancelled unexpectedly.", cancellationToken);
            return;
        }
        var result = await finished.Task;
        if (result.ErrorCode is { } code)
        {
            if (result.Error is { } error) await _errors.WriteLineAsync($"TWLSP002: {request.Method}: {error.Message}");
            await FinishRequestErrorAsync(request, code, result.ErrorMessage!, cancellationToken);
            return;
        }
        if (!_documents.TryGetValue(request.Path, out var document) || document.Text != request.Text ||
            !PathComparer.Equals(document.Root, request.Root) || !_stories.TryGetValue(request.Root, out var story))
        {
            await FinishRequestErrorAsync(request, -32801, "Document content was modified.", cancellationToken);
            return;
        }
        if (story.MetadataError is { } metadataError)
        {
            await FinishRequestErrorAsync(request, -32001, $"{request.Feature} unavailable: {metadataError.Message}", cancellationToken);
            return;
        }
        if (result.Previous is null && (!ReferenceEquals(story, request.Story) || story.Generation != request.Generation))
        {
            await FinishRequestErrorAsync(request, -32801, "Document content was modified.", cancellationToken);
            return;
        }
        if (result.Previous is { } previous && result.Target is { } target &&
            (!previous.Source.Files.TryGetValue(target.Start.File, out var originalText) ||
             target.Start.File != target.End.File || !story.Workspace.HasText(target.Start.File, originalText)))
        {
            request.Story = story;
            if (story.Pending is null) Schedule(story);
            request.Generation = story.Generation;
            StartRequest(request, null);
            return;
        }
        _requests.Remove(request.Key);
        request.Cancellation.Dispose();
        await ReplyAsync(request.Id, result.Value, cancellationToken);
    }

    private async Task CancelRequestAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("id", out var id) ||
            id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null))
            throw new ArgumentException("Cancellation requires a request id.");
        if (!_requests.Remove(RequestKey(id), out var request)) return;
        request.Cancellation.Cancel();
        await ErrorAsync(request.Id, -32800, "Request cancelled.", cancellationToken);
    }

    private async Task CancelOutstandingRequestsAsync(CancellationToken cancellationToken)
    {
        foreach (var request in _requests.Values.ToArray())
        {
            _requests.Remove(request.Key);
            request.Cancellation.Cancel();
            await ErrorAsync(request.Id, -32800, "Request cancelled.", cancellationToken);
        }
    }

    private async Task FinishRequestErrorAsync(PendingRequest request, int code, string message, CancellationToken cancellationToken)
    {
        _requests.Remove(request.Key);
        request.Cancellation.Dispose();
        await ErrorAsync(request.Id, code, message, cancellationToken);
    }

    private static string RequestKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => "string:" + id.GetString(),
        JsonValueKind.Number => "number:" + id.GetRawText(),
        JsonValueKind.Null => "null",
        _ => throw new ArgumentException("Invalid request id.")
    };

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
            story.Pending?.Supersede();
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

    private Story LoadStory(string root, SemaphoreSlim? analysisGate = null)
    {
        analysisGate ??= new SemaphoreSlim(1, 1);
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
                workspace = new StoryWorkspace(source, metadata.CreateOptions(), false, _analyze, _delay, analysisGate);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                workspace = new StoryWorkspace(source, null, true, _analyze, _delay, analysisGate);
                metadataError = new InvalidDataException($"Authoring metadata '{_metadataPath}': {exception.Message}", exception);
            }
        }
        else
        {
            workspace = new StoryWorkspace(source, null,
                _requiresMetadata || (Directory.Exists(root) && Directory.EnumerateFiles(root, "*.csproj").Any()),
                _analyze, _delay, analysisGate);
        }

        return new(root, workspace, lintConfiguration, lintConfigurationError, analysisGate) { MetadataError = metadataError };
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
        story.Pending?.Supersede();
        var replacement = LoadStory(story.Root, story.AnalysisGate);
        replacement.Generation = story.Generation;
        replacement.PublishedAnalysis = story.PublishedAnalysis;
        foreach (var path in story.Published) replacement.Published.Add(path);
        foreach (var (path, document) in _documents.Where(pair => PathComparer.Equals(pair.Value.Root, story.Root)))
            replacement.Workspace.Open(Relative(story.Root, path), document.Version, document.Text);
        _stories[story.Root] = replacement;
        return replacement;
    }

    private void Schedule(Story story)
    {
        // Reload can replace a workspace; always schedule its current instance.
        story = _stories[story.Root];
        var generation = ++story.Generation;
        if (story.MetadataError is { } error)
        {
            story.Pending = null;
            _events.Writer.TryWrite(new Completed(story, generation, null, error));
            return;
        }
        var shared = story.Workspace.ScheduleAnalysis(TimeSpan.FromMilliseconds(150));
        story.Pending = shared;
        var task = ObserveAnalysisAsync(story, generation, shared);
        _analyses.RemoveAll(task => task.IsCompletedSuccessfully);
        _analyses.Add(task);
    }

    private Task ObserveAnalysisAsync(Story story, long generation, SharedAnalysis shared) =>
        shared.Completion.ContinueWith(completed =>
        {
            if (completed.IsCanceled)
            {
                if (!shared.Superseded.IsCompleted)
                    _events.Writer.TryWrite(new Completed(story, generation, null,
                        new OperationCanceledException("Analysis was cancelled unexpectedly.")));
            }
            else if (completed.Exception is { } failure)
            {
                _events.Writer.TryWrite(new Completed(story, generation, null, failure.GetBaseException()));
            }
            else
            {
                _events.Writer.TryWrite(new Completed(story, generation, completed.Result, null));
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task PublishAsync(Completed completed, CancellationToken cancellationToken)
    {
        var story = completed.Story;
        if (_shutdown || !_stories.TryGetValue(story.Root, out var current) || !ReferenceEquals(story, current) ||
            story.Generation != completed.Generation) return;
        if (completed.Error is { } error)
        {
            story.PublishedAnalysis = null;
            await _errors.WriteLineAsync($"TWLSP003: {error.Message}");
            await NotifyAsync("window/showMessage", new { type = 1, message = $"Talewright analysis failed: {error.Message}" }, cancellationToken);
            var manifest = Path.Combine(story.Root, StorySource.ManifestFileName);
            var lintConfiguration = Path.Combine(story.Root, LintConfiguration.FileName);
            var errorPaths = story.Published.Append(manifest);
            if (story.LintConfigurationError is not null) errorPaths = errorPaths.Append(lintConfiguration);
            foreach (var path in errorPaths.Distinct(PathComparer).Where(Owns).ToArray())
            {
                var diagnostics = PathComparer.Equals(path, manifest)
                    ? new[] { ToDiagnostic(new Diagnostic(DiagnosticSeverity.Error, "TW5002",
                        $"Cannot analyze story: {error.Message}", new(StorySource.ManifestFileName, 1, 1)), new LineMap("")) }
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
        var groups = new Dictionary<string, List<EditorDiagnostic>>(PathComparer);
        var lineMaps = new Dictionary<string, LineMap>(StringComparer.Ordinal);
        foreach (var diagnostic in lintResult.CompilerDiagnostics)
        {
            var file = string.IsNullOrEmpty(diagnostic.Location.File)
                ? StorySource.ManifestFileName
                : diagnostic.Location.File;
            Add(file, new CompilerEditorDiagnostic(diagnostic));
        }
        foreach (var diagnostic in lintResult.RuleDiagnostics)
        {
            var file = diagnostic.Range.Start.File;
            Add(file, new LintEditorDiagnostic(diagnostic));
        }
        if (story.LintConfigurationError is { } lintError)
        {
            Add(LintConfiguration.FileName, new ConfigurationEditorDiagnostic(lintError.Message));
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
                diagnostics = diagnostics.Count == 0 ? [] : LimitDiagnostics(diagnostics, Lines(Relative(story.Root, path)))
            }, cancellationToken);
        }
        story.Published.Clear();
        foreach (var path in groups.Keys) story.Published.Add(path);
        story.PublishedAnalysis = update.Analysis;
        if (_inlayRefreshSupported && !_inlayRefreshPending)
        {
            _inlayRefreshPending = true;
            await _protocol.WriteAsync(new { jsonrpc = "2.0", id = InlayRefreshId, method = "workspace/inlayHint/refresh" },
                cancellationToken);
        }

        LineMap Lines(string relativeFile)
        {
            if (!lineMaps.TryGetValue(relativeFile, out var map))
            {
                map = new LineMap(update.Analysis.Source.Files.GetValueOrDefault(relativeFile, ""));
                lineMaps.Add(relativeFile, map);
            }
            return map;
        }

        void Add(string relativeFile, EditorDiagnostic diagnostic)
        {
            var path = Path.GetFullPath(relativeFile, story.Root);
            if (!Owns(path)) return;
            if (!groups.TryGetValue(path, out var diagnostics)) groups.Add(path, diagnostics = []);
            diagnostics.Add(diagnostic);
        }

        bool Owns(string path) => PathComparer.Equals(FindRoot(path, story.Root), story.Root);
    }

    private static object[] LimitDiagnostics(List<EditorDiagnostic> diagnostics, LineMap lines)
    {
        if (diagnostics.Count <= DiagnosticLimit) return diagnostics.Select(diagnostic => diagnostic.Convert(lines)).ToArray();
        var ordered = diagnostics.OrderBy(diagnostic => diagnostic.Start.Line).ThenBy(diagnostic => diagnostic.Start.Column).ToArray();
        var firstHidden = ordered[DiagnosticLimit];
        var hiddenPosition = lines.Position(firstHidden.Start);
        var point = new { line = hiddenPosition.Line, character = hiddenPosition.Character };
        var notice = new
        {
            range = new { start = point, end = point },
            severity = 3,
            code = "TWLSP005",
            source = "talewright",
            message = $"Showing the first {DiagnosticLimit} problems in this file; {ordered.Length - DiagnosticLimit} more are hidden. Fix these to see the rest."
        };
        return ordered.Take(DiagnosticLimit).Select(diagnostic => diagnostic.Convert(lines)).Append(notice).ToArray();
    }

    private static object ToDiagnostic(Diagnostic diagnostic, LineMap lines)
    {
        var position = lines.Position(diagnostic.Location);
        var point = new { line = position.Line, character = position.Character };
        return new
        {
            range = new { start = point, end = point },
            severity = diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 2,
            code = diagnostic.Code,
            source = "talewright",
            message = diagnostic.Message
        };
    }

    private static object ToDiagnostic(StoryLintDiagnostic diagnostic, LineMap lines)
    {
        var first = lines.Position(diagnostic.Range.Start);
        var last = lines.Position(diagnostic.Range.End);
        var start = new { line = first.Line, character = first.Character };
        var end = new { line = last.Line, character = last.Character };
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

    private sealed class LineMap
    {
        private readonly int[] _starts;
        private readonly int[] _ends;

        public LineMap(string text)
        {
            var starts = new List<int> { 0 };
            var ends = new List<int>();
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] is not ('\r' or '\n')) continue;
                ends.Add(index);
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                starts.Add(index + 1);
            }
            ends.Add(text.Length);
            _starts = starts.ToArray();
            _ends = ends.ToArray();
        }

        public DiagnosticPosition Position(SourceLocation location)
        {
            var line = Math.Clamp(location.Line - 1, 0, _starts.Length - 1);
            return new(line, Math.Clamp(location.Column - 1, 0, _ends[line] - _starts[line]));
        }
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
