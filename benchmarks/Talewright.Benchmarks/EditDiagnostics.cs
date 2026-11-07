using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Talewright.Authoring;
using Talewright.LanguageServer;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Parsing;
using Talewright.Testing;

internal static class EditDiagnostics
{
    private const int Iterations = 7;

    public static async Task<int> RunCurrentTextAsync()
    {
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; processors: {Environment.ProcessorCount}");
        Console.WriteLine("Each request follows a current-buffer edit in a warm workspace; three warmups and seven samples. p95 is the maximum.");
        Console.WriteLine("Operation\tScenes\tFiles\tUTF-8 bytes\tp50 ms\tp95 ms\tManaged bytes p50/max");
        foreach (var count in new[] { 100, 1_000, 10_000 })
        {
            var source = CreateSceneCorpus(count);
            var path = "current.tale";
            var target = $"scene.{count - 1}";
            await using var client = new Client(source);
            await client.Initialize();
            await client.Open(path, CurrentText(target, 1), 1);
            await client.Published(path, 1);

            var version = 1;
            version = await MeasureCurrentRequests(client, path, target, "textDocument/completion", version, count,
                source.Files.Count, PerformanceSource.Utf8ByteCount(source));
            await MeasureCurrentRequests(client, path, target, "textDocument/hover", version, count,
                source.Files.Count, PerformanceSource.Utf8ByteCount(source));
        }
        return 0;
    }

    public static async Task<int> RunReferencesAsync()
    {
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; processors: {Environment.ProcessorCount}");
        Console.WriteLine("Find All References on a 20-chapter RealisticStory; three warmups and seven samples. p95 is the maximum.");
        Console.WriteLine("Cold requests follow a published edit, so they include building the analysis's name index; warm requests repeat on the same analysis.");
        Console.WriteLine("Operation\tTarget\tReferences\tScenes\tFiles\tUTF-8 bytes\tp50 ms\tp95 ms\tManaged bytes p50/max");
        var story = RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 });
        var source = story.ToSource();
        const string path = "chapters/c000.tale";
        var original = source.Files[path];
        await using var client = new Client(source);
        await client.Initialize();
        await client.Open(path, original, 1);
        await client.Published(path, 1);
        var version = 1;
        foreach (var (label, needle) in new[] { ("scene", "-> c1.open"), ("flag", "flag:alarm") })
        {
            var lines = original.Split('\n');
            var line = Array.FindIndex(lines, text => text.Contains(needle, StringComparison.Ordinal));
            var character = lines[line].IndexOf(needle, StringComparison.Ordinal) + needle.Length - 1;
            var parameters = new
            {
                textDocument = new { uri = client.UriOf(path) },
                position = new { line, character },
                context = new { includeDeclaration = true }
            };
            var cold = new List<(double Time, long Bytes)>(Iterations);
            var warm = new List<(double Time, long Bytes)>(Iterations);
            var count = 0;
            for (var iteration = 0; iteration < Iterations + 3; iteration++)
            {
                version++;
                await client.Change(path, $"{original}// edit {version}\n", version);
                await client.Published(path, version);
                var first = await MeasureReferences(client, parameters, line);
                var second = await MeasureReferences(client, parameters, line);
                count = second.Count;
                if (iteration < 3) continue;
                cold.Add((first.Time, first.Bytes));
                warm.Add((second.Time, second.Bytes));
            }
            ReportReferences("Cold", label, count, story, source, cold);
            ReportReferences("Warm", label, count, story, source, warm);
        }
        return 0;
    }

    private static async Task<(double Time, long Bytes, int Count)> MeasureReferences(Client client, object parameters, int line)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var response = await client.Request("textDocument/references", parameters);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"References failed: {error}");
        var locations = response.GetProperty("result").EnumerateArray().ToArray();
        if (locations.Length < 2 || !locations.Any(location =>
                location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == line))
            throw new InvalidOperationException("References did not include the requested use and another occurrence.");
        return (elapsed, allocated, locations.Length);
    }

    private static void ReportReferences(string operation, string label, int count, RealisticStory story, StorySource source,
        List<(double Time, long Bytes)> samples)
    {
        var times = samples.Select(sample => sample.Time).Order().ToArray();
        var bytes = samples.Select(sample => sample.Bytes).Order().ToArray();
        Console.WriteLine(FormattableString.Invariant(
            $"{operation}\t{label}\t{count}\t{story.SceneCount}\t{source.Files.Count}\t{PerformanceSource.Utf8ByteCount(source)}\t{times[Iterations / 2]:F2}\t{times[^1]:F2}\t{bytes[Iterations / 2]:N0}/{bytes[^1]:N0}"));
        if (operation == "Warm")
            Console.WriteLine($"  20 ms warm p95 target: {(times[^1] <= 20 ? "MET" : "MISSED")} (report-only).");
    }

    private static async Task<int> MeasureCurrentRequests(Client client, string path, string target, string method,
        int version, int scenes, int files, long sourceBytes)
    {
        var times = new List<double>(Iterations);
        var allocations = new List<long>(Iterations);
        var uri = client.UriOf(path);
        var line = 2;
        var character = 3 + target.Length - (method == "textDocument/hover" ? 1 : 0);
        for (var iteration = 0; iteration < Iterations + 3; iteration++)
        {
            version++;
            var text = CurrentText(target, version);
            await client.Change(path, text, version);
            var parameters = new
            {
                textDocument = new { uri },
                position = new { line, character }
            };
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var response = await client.Request(method, parameters);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            ValidateCurrentResponse(method, response, target, line);
            if (iteration >= 3)
            {
                times.Add(elapsed);
                allocations.Add(allocated);
            }
        }

        times.Sort();
        allocations.Sort();
        var label = method == "textDocument/completion" ? "Completion" : "Hover";
        var p50 = times[Iterations / 2];
        var p95 = times[^1];
        Console.WriteLine(FormattableString.Invariant(
            $"{label}\t{scenes}\t{files}\t{sourceBytes}\t{p50:F2}\t{p95:F2}\t{allocations[Iterations / 2]:N0}/{allocations[^1]:N0}"));
        Console.WriteLine($"  20 ms p95 target: {(p95 <= 20 ? "MET" : "MISSED")} (report-only).");
        return version;
    }

    private static void ValidateCurrentResponse(string method, JsonElement response, string target, int line)
    {
        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"{method} failed: {error}");
        if (method == "textDocument/completion")
        {
            var matching = response.GetProperty("result").GetProperty("items").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("label").GetString() == target);
            if (matching.ValueKind == JsonValueKind.Undefined ||
                matching.GetProperty("textEdit").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() != line)
                throw new InvalidOperationException($"Current-text completion did not return '{target}' with a current-line edit.");
        }
        else if (!response.GetProperty("result").GetProperty("contents").GetProperty("value").GetString()!.Contains(target, StringComparison.Ordinal) ||
                 response.GetProperty("result").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() != line)
            throw new InvalidOperationException($"Current-text hover did not describe '{target}' at the current line.");
    }

    private static string CurrentText(string target, int version) =>
        $"=== current\n// edit {version}\n-> {target}\n";

    private static StorySource CreateSceneCorpus(int count)
    {
        var generated = BenchmarkStories.CreateSceneLoop(count);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = generated.Files[StorySource.ManifestFileName],
            ["current.tale"] = CurrentText($"scene.{count - 1}", 1),
        };
        var fileIndex = 0;
        var scenesInFile = 0;
        var text = new System.Text.StringBuilder();
        foreach (var line in generated.Files[BenchmarkStories.TaleFileName].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("=== scene.", StringComparison.Ordinal) && scenesInFile == 100)
            {
                files[$"catalog-{fileIndex++:D3}.tale"] = text.ToString();
                text.Clear();
                scenesInFile = 0;
            }
            if (line.StartsWith("=== scene.", StringComparison.Ordinal)) scenesInFile++;
            text.Append(line).Append('\n');
        }
        if (text.Length > 0) files[$"catalog-{fileIndex:D3}.tale"] = text.ToString();
        return new StorySource(files, generated.ImagePaths);
    }

    public static async Task<int> RunAsync()
    {
        var source = RealisticStory.Create(new RealisticStoryProfile { Chapters = 20 }).ToSource();
        var path = source.TaleFiles.First().Key;
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}; processors: {Environment.ProcessorCount}");
        Console.WriteLine($"Corpus: {source.Files.Count} text files, {source.TaleFiles.Count()} .tale files, {PerformanceSource.Utf8ByteCount(source)} UTF-8 source bytes.");
        Console.WriteLine("Seven samples after three warmups; p95 is the maximum. Allocation samples are process-wide managed bytes, including worker threads.");
        await MeasureAllocations(source, path);
        Console.WriteLine($"Approximate retained syntax, excluding shared source text: {RetainedSyntax(source):N0} bytes.");

        await using var client = new Client(source);
        await client.Initialize();
        await client.Open(path, source.Files[path], 1);
        await client.Published(path, 1);
        var samples = new List<(double Total, LanguageServer.DiagnosticPublication Stages)>();
        for (var version = 2; version < Iterations + 5; version++)
        {
            var text = source.Files[path] + $"\n// edit {version}";
            var started = Stopwatch.GetTimestamp();
            await client.Change(path, text, version);
            var publication = await client.Published(path, version);
            var total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (publication.ParsedFiles != 1 || publication.CachedFiles != source.TaleFiles.Count())
                throw new InvalidOperationException("A one-file edit did not reuse the remaining file syntax.");
            if (version >= 5) samples.Add((total, publication));
        }

        Console.WriteLine("Stage\tp50 ms\tp95 ms");
        Report("Notification to flushed/received diagnostics", samples.Select(sample => sample.Total));
        Report("Debounce", samples.Select(sample => sample.Stages.Debounce.TotalMilliseconds));
        Report("Analysis-gate queue", samples.Select(sample => sample.Stages.Queue.TotalMilliseconds));
        Report("Whole-story analysis (one file parsed)", samples.Select(sample => sample.Stages.Analysis.TotalMilliseconds));
        Report("Completion-event dispatch", samples.Select(sample => sample.Stages.Dispatch.TotalMilliseconds));
        Report("Lint and diagnostic conversion", samples.Select(sample => sample.Stages.Conversion.TotalMilliseconds));
        Report("Protocol serialization and publication", samples.Select(sample => sample.Stages.Publication.TotalMilliseconds));
        Report("Scheduling/transport/client-receipt residual", samples.Select(sample => sample.Total -
            (sample.Stages.Debounce + sample.Stages.Queue + sample.Stages.Analysis + sample.Stages.Dispatch +
             sample.Stages.Conversion + sample.Stages.Publication).TotalMilliseconds));
        var p95 = samples.Max(sample => sample.Total);
        Console.WriteLine($"50 ms edit-to-diagnostics target: {(p95 <= 50 ? "MET" : "MISSED")} (report-only; #411 is not automatically enabled).");
        return 0;
    }

    private static async Task MeasureAllocations(StorySource source, string path)
    {
        var full = new List<long>();
        var edited = new List<long>();
        var workspace = new StoryWorkspace(source);
        workspace.Open(path, 1, source.Files[path]);
        await workspace.AnalyzeAsync(TimeSpan.Zero);
        var parsed = workspace.SyntaxParseCount;
        for (var index = 0; index < Iterations + 3; index++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var fresh = new StoryWorkspace(source);
            var fullResult = await fresh.AnalyzeAsync(TimeSpan.Zero);
            var fullBytes = GC.GetTotalAllocatedBytes(precise: true) - before;
            GC.KeepAlive(fullResult);

            workspace.Change(path, index + 2, source.Files[path] + $"\n// allocation edit {index}");
            before = GC.GetTotalAllocatedBytes(precise: true);
            var update = await workspace.AnalyzeAsync(TimeSpan.Zero);
            var editBytes = GC.GetTotalAllocatedBytes(precise: true) - before;
            GC.KeepAlive(update);
            if (workspace.SyntaxParseCount != ++parsed)
                throw new InvalidOperationException("Expected exactly one parser invocation per edited file.");
            if (index >= 3)
            {
                full.Add(fullBytes);
                edited.Add(editBytes);
            }
        }
        var bytes = PerformanceSource.Utf8ByteCount(source);
        PrintAllocation("Fresh full workspace analysis", full, bytes);
        PrintAllocation("One-file edit workspace analysis", edited, bytes);
        var beforeUnchanged = workspace.SyntaxParseCount;
        await workspace.AnalyzeAsync(TimeSpan.Zero);
        if (workspace.SyntaxParseCount != beforeUnchanged)
            throw new InvalidOperationException("An unchanged revision parsed source again.");
    }

    private static void PrintAllocation(string name, List<long> values, long sourceBytes)
    {
        values.Sort();
        var median = values[values.Count / 2];
        Console.WriteLine(FormattableString.Invariant($"{name}: median {median:N0} bytes ({median / (double)sourceBytes:F2} B/source byte), max {values[^1]:N0} bytes."));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RetainedSyntax(StorySource source)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var syntax = source.TaleFiles.ToDictionary(file => file.Key, file => TaleSyntaxParser.Parse(file.Value));
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(syntax);
        return retained;
    }

    private static void Report(string name, IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        Console.WriteLine(FormattableString.Invariant($"{name}\t{ordered[ordered.Length / 2]:F2}\t{ordered[^1]:F2}"));
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-edit-diagnostics-{Guid.NewGuid():N}");
        private readonly AnonymousPipeServerStream _toServer = new(PipeDirection.Out, HandleInheritability.None);
        private readonly AnonymousPipeServerStream _fromServer = new(PipeDirection.In, HandleInheritability.None);
        private readonly AnonymousPipeClientStream _serverInput;
        private readonly AnonymousPipeClientStream _serverOutput;
        private readonly Protocol _protocol;
        private readonly StringWriter _errors = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<LanguageServer.DiagnosticPublication> _publications =
            Channel.CreateUnbounded<LanguageServer.DiagnosticPublication>();
        private readonly Task<int> _server;
        private readonly Task _reader;

        public Client(StorySource source)
        {
            Directory.CreateDirectory(_root);
            foreach (var (path, text) in source.Files)
            {
                var file = Path.Combine(_root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, text);
            }
            foreach (var path in source.ImagePaths)
            {
                var file = Path.Combine(_root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, []);
            }
            _serverInput = new(PipeDirection.In, _toServer.ClientSafePipeHandle);
            _serverOutput = new(PipeDirection.Out, _fromServer.ClientSafePipeHandle);
            _protocol = new(_fromServer, _toServer);
            _server = new LanguageServer(_serverInput, _serverOutput, _errors, null, null,
                publication => _publications.Writer.TryWrite(publication)).RunAsync(_lifetime.Token);
            _reader = Read();
        }

        public string UriOf(string path) => new Uri(Path.Combine(_root, path)).AbsoluteUri;

        public async Task Initialize()
        {
            await Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { rootUri = new Uri(_root).AbsoluteUri, capabilities = new { } } });
            await Next(message => message.TryGetProperty("id", out var id) && id.GetInt32() == 1);
        }

        public Task Open(string path, string text, int version) => Send(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new { textDocument = new { uri = UriOf(path), languageId = "tale", version, text } }
        });

        public Task Change(string path, string text, int version) => Send(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didChange",
            @params = new { textDocument = new { uri = UriOf(path), version }, contentChanges = new[] { new { text } } }
        });

        public async Task<LanguageServer.DiagnosticPublication> Published(string path, int version)
        {
            var publication = await _publications.Reader.ReadAsync(_lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            await Next(message => message.TryGetProperty("method", out var method) &&
                method.GetString() == "textDocument/publishDiagnostics" &&
                message.GetProperty("params").GetProperty("uri").GetString() == UriOf(path) &&
                message.GetProperty("params").GetProperty("version").GetInt32() == version);
            return publication;
        }

        private Task Send(object message) => _protocol.WriteAsync(message, _lifetime.Token);

        public async Task<JsonElement> Request(string method, object parameters)
        {
            var id = Interlocked.Increment(ref _requestId);
            await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
            return await Next(message => message.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id);
        }

        private int _requestId = 1;

        private async Task<JsonElement> Next(Func<JsonElement, bool> matches)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                var message = await _messages.Reader.ReadAsync(timeout.Token);
                if (matches(message)) return message;
            }
        }

        private async Task Read()
        {
            try
            {
                while (await _protocol.ReadAsync(_lifetime.Token) is { } message)
                    await _messages.Writer.WriteAsync(message, _lifetime.Token);
                _messages.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                _messages.Writer.TryComplete();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Send(new { jsonrpc = "2.0", id = 999, method = "shutdown", @params = new { } });
                await Next(message => message.TryGetProperty("id", out var id) && id.GetInt32() == 999);
                await Send(new { jsonrpc = "2.0", method = "exit", @params = new { } });
                if (await _server.WaitAsync(TimeSpan.FromSeconds(10)) != 0 || _errors.ToString().Length != 0)
                    throw new InvalidOperationException($"Language server failed: {_errors}");
            }
            finally
            {
                _lifetime.Cancel();
                try { await _reader; }
                finally
                {
                    _serverInput.Dispose();
                    _serverOutput.Dispose();
                    _toServer.Dispose();
                    _fromServer.Dispose();
                    _errors.Dispose();
                    _lifetime.Dispose();
                    Directory.Delete(_root, recursive: true);
                }
            }
        }
    }
}
