using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Talewright.Cli;
using Talewright.Authoring;
using Talewright.Tale.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed class ServerTests
{
    private const string Manifest = "id: lsp-test\ntitle: LSP test\nstart: start\ncharacters:\n  player: {first-name: Ada}\n";
    private const string Valid = "=== start\nHello.\n-> END\n";
    private const string Invalid = "=== start\nHello.\n* [Missing] -> missing\n";

    [Fact]
    public async Task Lifecycle_and_unknown_requests_follow_json_rpc()
    {
        await using var client = new Client();
        var uninitialized = await client.Request("unknown", new { });
        Assert.Equal(-32002, uninitialized.GetProperty("error").GetProperty("code").GetInt32());
        var initialized = await client.Initialize();
        Assert.Equal("utf-16", initialized.GetProperty("result").GetProperty("capabilities").GetProperty("positionEncoding").GetString());
        Assert.Equal(1, initialized.GetProperty("result").GetProperty("capabilities").GetProperty("textDocumentSync").GetProperty("change").GetInt32());
        var unknown = await client.Request("textDocument/hover", new { });
        Assert.Equal(-32601, unknown.GetProperty("error").GetProperty("code").GetInt32());
        var shutdown = await client.Request("shutdown", new { });
        Assert.Equal(JsonValueKind.Null, shutdown.GetProperty("result").ValueKind);
        await client.Send("exit", new { });
        await client.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, client.Process.ExitCode);
    }

    [Fact]
    public async Task Exit_without_shutdown_is_unsuccessful()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Send("exit", new { });
        await client.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.Process.ExitCode);
    }

    [Theory]
    [InlineData("{", -32700)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":3}", -32600)]
    [InlineData("[]", -32600)]
    public async Task Malformed_requests_report_protocol_errors_and_keep_the_connection_usable(string body, int code)
    {
        await using var client = new Client();
        await client.Raw(body);
        var error = await client.Next(message => message.TryGetProperty("error", out _));
        Assert.Equal(code, error.GetProperty("error").GetProperty("code").GetInt32());
        await client.Initialize();
    }

    [Theory]
    [InlineData("Content-Length: 16777217\r\n\r\n")]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n")]
    public async Task Invalid_protocol_lengths_fail_explicitly(string header)
    {
        await using var client = new Client();
        await client.Process.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await client.Process.StandardInput.BaseStream.FlushAsync();
        await client.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.Process.ExitCode);
    }

    [Fact]
    public async Task Unsaved_changes_clear_diagnostics_and_ignore_superseded_versions()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Invalid, 1);
        var invalid = await client.Diagnostics("start.tale", 1);
        Assert.NotEmpty(invalid);
        await client.Change("start.tale", Invalid, 2);
        await client.Change("start.tale", Valid, 3);
        await client.Change("start.tale", Invalid, 2);
        Assert.Empty(await client.Diagnostics("start.tale", 3));
        Assert.Equal(Valid, File.ReadAllText(client.PathOf("start.tale")));
    }

    [Fact]
    public async Task Close_restores_latest_disk_not_the_unsaved_overlay()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Invalid, 1);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("start.tale") } });
        Assert.Empty(await client.Diagnostics("start.tale", null));
    }

    [Fact]
    public async Task Close_discards_overlay_and_registration_even_when_disk_reload_fails()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Invalid, 1);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        File.WriteAllText(client.PathOf("oversized.tale"), new string('x', 5 * 1024 * 1024 + 1));
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("start.tale") } });
        var error = await client.Next(message => message.TryGetProperty("method", out var method) &&
            method.GetString() == "window/showMessage");
        Assert.Contains("5 MiB", error.GetProperty("params").GetProperty("message").GetString());
        File.Delete(client.PathOf("oversized.tale"));
        await client.Watch("oversized.tale", 3);
        Assert.Empty(await client.Diagnostics("start.tale", null));
        await client.Open("start.tale", Valid, 1);
        Assert.Empty(await client.Diagnostics("start.tale", 1));
        await client.Change("start.tale", Invalid, 2);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unsaved_nested_manifest_rehomes_existing_tale_overlays_and_diagnostics(bool parentManifest)
    {
        await using var client = new Client();
        if (!parentManifest) File.Delete(client.PathOf("story.yaml"));
        Directory.CreateDirectory(client.PathOf("child"));
        const string childValid = "=== child\nHello.\n-> END\n";
        const string childInvalid = "=== child\n* [Go] -> absent\n";
        File.WriteAllText(client.PathOf("child/main.tale"), childValid);
        await client.Initialize();
        await client.Open("child/main.tale", childInvalid, 1);
        var previous = await client.Diagnostics("child/main.tale", 1);
        if (parentManifest) Assert.NotEmpty(previous);
        await client.Open("child/story.yaml", Manifest.Replace("start: start", "start: child"), 1);
        Assert.Contains(await client.Diagnostics("child/main.tale", 1),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW2002");
        await client.Change("child/main.tale", childValid, 2);
        Assert.Empty(await client.Diagnostics("child/main.tale", 2));
        // An ancestor reload must neither reclaim the overlay nor republish its diagnostics.
        await client.Watch("start.tale", 2);
        await client.Open("start.tale", Valid, 1);
        await client.Diagnostics("start.tale", 1);
        Assert.DoesNotContain(client.PendingDiagnostics, message =>
            message.GetProperty("params").GetProperty("uri").GetString() == client.UriOf("child/main.tale") &&
            message.GetProperty("params").GetProperty("diagnostics").GetArrayLength() != 0);
        await client.Change("child/main.tale", childInvalid, 3);
        Assert.Contains(await client.Diagnostics("child/main.tale", 3),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW2002");
        await client.Change("child/main.tale", childValid, 4);
        Assert.Empty(await client.Diagnostics("child/main.tale", 4));
        Assert.False(File.Exists(client.PathOf("child/story.yaml")));
        Assert.Equal(childValid, File.ReadAllText(client.PathOf("child/main.tale")));
    }

    [Fact]
    public async Task Opening_unsaved_nested_manifest_before_tale_uses_its_own_directory()
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("child"));
        await client.Initialize();
        await client.Open("child/story.yaml", Manifest.Replace("start: start", "start: child"), 1);
        Assert.NotEmpty(await client.Diagnostics("child/story.yaml", 1));
        await client.Open("child/main.tale", "=== child\nHello.\n-> END\n", 1);
        Assert.Empty(await client.Diagnostics("child/story.yaml", 1));
        Assert.Empty(await client.Diagnostics("child/main.tale", 1));
    }

    [Fact]
    public async Task Opening_tale_discovers_disk_child_and_transfers_diagnostic_ownership()
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("child"));
        File.WriteAllText(client.PathOf("child/story.yaml"), Manifest.Replace("start: start", "start: child"));
        const string childValid = "=== child\nHello.\n-> END\n";
        File.WriteAllText(client.PathOf("child/main.tale"), "=== child\n* [Go] -> absent\n");
        await client.Initialize();
        await client.Open("start.tale", Invalid, 1);
        Assert.NotEmpty(await client.Diagnostics("child/main.tale", null));
        await client.Diagnostics("start.tale", 1);
        await client.Open("child/main.tale", childValid, 1);
        Assert.Empty(await client.Diagnostics("child/main.tale", 1));
        await client.Diagnostics("start.tale", 1);
        await client.Change("start.tale", Valid, 2);
        await client.Diagnostics("start.tale", 2);
        Assert.DoesNotContain(client.PendingDiagnostics, message =>
            message.GetProperty("params").GetProperty("uri").GetString() == client.UriOf("child/main.tale"));
        await client.Change("child/main.tale", "=== child\n* [Go] -> absent\n", 2);
        Assert.NotEmpty(await client.Diagnostics("child/main.tale", 2));
        await client.Watch("start.tale", 2);
        await client.Diagnostics("start.tale", 2);
        Assert.DoesNotContain(client.PendingDiagnostics, message =>
            message.GetProperty("params").GetProperty("uri").GetString() == client.UriOf("child/main.tale"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Discarding_unsaved_child_manifest_retires_root_and_rehomes_overlays(bool parentManifest)
    {
        await using var client = new Client();
        if (!parentManifest) File.Delete(client.PathOf("story.yaml"));
        Directory.CreateDirectory(client.PathOf("child"));
        File.WriteAllText(client.PathOf("child/main.tale"), "=== child\nHello.\n-> END\n");
        await client.Initialize();
        await client.Open("start.tale", Valid, 1);
        await client.Diagnostics("start.tale", 1);
        await client.Open("child/story.yaml", Manifest.Replace("start: start", "start: absent"), 1);
        Assert.NotEmpty(await client.Diagnostics("child/story.yaml", 1));
        await client.Open("child/main.tale", "=== child\n* [Go] -> absent\n", 1);
        Assert.NotEmpty(await client.Diagnostics("child/main.tale", 1));
        Assert.NotEmpty(await client.Diagnostics("child/story.yaml", 1));
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("child/story.yaml") } });
        Assert.Empty(await client.Diagnostics("child/story.yaml", null));
        await client.Diagnostics("start.tale", 1);
        await client.Diagnostics("child/main.tale", 1); // Retirement clears the old publication.
        await client.Diagnostics("child/main.tale", 1); // Ancestor reanalysis owns the overlay.
        await client.Change("child/main.tale", "=== child\nHello.\n-> END\n", 2);
        var diagnostics = await client.Diagnostics("child/main.tale", 2);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.GetProperty("code").GetString() == "TW2002");
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("child/main.tale") } });
        await client.Request("unknown", new { });
        await client.Open("child/main.tale", "=== child\nHello.\n-> END\n", 1);
        await client.Diagnostics("child/main.tale", 1);
        await client.Diagnostics("start.tale", 1);
        Assert.DoesNotContain(client.PendingDiagnostics, message =>
            message.GetProperty("params").GetProperty("uri").GetString() == client.UriOf("child/story.yaml") &&
            message.GetProperty("params").GetProperty("diagnostics").GetArrayLength() != 0);
    }

    [Fact]
    public async Task Cross_file_add_change_delete_and_diagnostic_clearing()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Invalid, 1);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        File.WriteAllText(client.PathOf("other.tale"), "=== missing\nArrived.\n-> END\n");
        await client.Watch("other.tale", 1);
        Assert.Empty(await client.Diagnostics("start.tale", 1));
        File.WriteAllText(client.PathOf("other.tale"), "=== wrong\nArrived.\n-> END\n");
        await client.Watch("other.tale", 2);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        File.WriteAllText(client.PathOf("other.tale"), "=== missing\nArrived.\n* [Bad] -> absent\n");
        await client.Watch("other.tale", 2);
        Assert.NotEmpty(await client.Diagnostics("other.tale", null));
        Assert.Empty(await client.Diagnostics("start.tale", 1));
        File.Delete(client.PathOf("other.tale"));
        await client.Watch("other.tale", 3);
        Assert.Empty(await client.Diagnostics("other.tale", null));
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
    }

    [Fact]
    public async Task Invalid_and_incomplete_yaml_and_tale_use_the_shared_compiler()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("story.yaml", "id: [\n", 1);
        Assert.NotEmpty(await client.Diagnostics("story.yaml", 1));
        await client.Change("story.yaml", Manifest, 2);
        Assert.Empty(await client.Diagnostics("story.yaml", 2));
        await client.Open("start.tale", "=== start\n::: skill-check athletics dc\n", 1);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        await client.Change("start.tale", Valid, 2);
        Assert.Empty(await client.Diagnostics("start.tale", 2));
    }

    [Fact]
    public async Task Saved_diagnostics_match_cli_codes_severities_messages_and_utf16_positions()
    {
        await using var client = new Client();
        File.WriteAllText(client.PathOf("start.tale"), "=== start\r\n😀 Hello.\r\n* [Missing] -> missing\r\n");
        var output = new StringWriter();
        Assert.Equal(1, CommandLine.Run(["validate", client.Root, "--format", "json"], output, new StringWriter()));
        using var cli = JsonDocument.Parse(output.ToString());
        await client.Initialize();
        await client.Open("start.tale", File.ReadAllText(client.PathOf("start.tale")), 1);
        var diagnostics = await client.Diagnostics("start.tale", 1);
        var expected = cli.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(diagnostic => diagnostic.GetProperty("file").GetString() == "start.tale").ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Length, diagnostics.Length);
        foreach (var diagnostic in expected)
        {
            var actual = Assert.Single(diagnostics, item => item.GetProperty("code").GetString() == diagnostic.GetProperty("code").GetString());
            Assert.Equal(diagnostic.GetProperty("message").GetString(), actual.GetProperty("message").GetString());
            Assert.Equal(diagnostic.GetProperty("severity").GetString() == "error" ? 1 : 2, actual.GetProperty("severity").GetInt32());
            var start = actual.GetProperty("range").GetProperty("start");
            Assert.Equal(diagnostic.GetProperty("line").GetInt32() - 1, start.GetProperty("line").GetInt32());
            Assert.Equal(diagnostic.GetProperty("column").GetInt32() - 1, start.GetProperty("character").GetInt32());
        }
    }

    [Fact]
    public async Task Csharp_project_is_never_executed_and_missing_metadata_is_explicit()
    {
        await using var client = new Client();
        File.WriteAllText(client.PathOf("Untrusted.csproj"), "<Project><Target Name=\"Exploit\" BeforeTargets=\"Build\"><Error Text=\"Must never run\" /></Target></Project>");
        await client.Initialize();
        await client.Open("story.yaml", Manifest, 1);
        Assert.Contains(await client.Diagnostics("story.yaml", 1), item => item.GetProperty("code").GetString() == "TW5001");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("{\"FormatVersion\":2,\"Predicates\":[],\"Modifiers\":[],\"Rulesets\":[]}")]
    public async Task Bad_explicit_metadata_reports_an_error_instead_of_builtin_success(string? contents)
    {
        await using var client = new Client();
        if (contents is not null) File.WriteAllText(client.PathOf("missing.authoring.json"), contents);
        await client.Initialize(new { metadataPath = "missing.authoring.json" });
        await client.Open("start.tale", Valid, 1);
        var error = await client.Next(message => message.TryGetProperty("method", out var method) && method.GetString() == "window/showMessage");
        Assert.Equal(1, error.GetProperty("params").GetProperty("type").GetInt32());
        Assert.Contains("missing.authoring.json", error.GetProperty("params").GetProperty("message").GetString());
        Assert.Contains(await client.Diagnostics("story.yaml", null), item => item.GetProperty("code").GetString() == "TW5002");
        File.WriteAllText(client.PathOf("missing.authoring.json"), AuthoringProjectMetadata.Export(CompilerOptions.Default).ToJson());
        await client.Watch("missing.authoring.json", 1);
        Assert.Empty(await client.Diagnostics("story.yaml", null));
        Assert.Empty(await client.Diagnostics("start.tale", 1));
    }

    [Fact]
    public async Task Unsaved_manifest_invalidates_other_files_and_superseded_results_never_publish()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Valid, 1);
        Assert.Empty(await client.Diagnostics("start.tale", 1));
        await client.Open("story.yaml", Manifest.Replace("start: start", "start: absent"), 1);
        Assert.NotEmpty(await client.Diagnostics("story.yaml", 1));
        await client.Change("story.yaml", "id: [", 2);
        await client.Change("story.yaml", Manifest, 3);
        Assert.Empty(await client.Diagnostics("story.yaml", 3));
        Assert.Equal(Manifest, File.ReadAllText(client.PathOf("story.yaml")));
        // A request/response is an event-loop barrier; no old version may arrive after it.
        await client.Request("unknown", new { });
        Assert.DoesNotContain(client.PendingDiagnostics, message =>
            message.GetProperty("params").GetProperty("uri").GetString() == client.UriOf("story.yaml") &&
            message.GetProperty("params").GetProperty("version").ToString() == "2");
    }

    private sealed class Client : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"talewright-lsp-{Guid.NewGuid():N}");
        public Process Process { get; }
        private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
        private readonly List<JsonElement> _backlog = [];
        private readonly Task _reader;
        private readonly Task<string> _stderr;
        private int _id;
        public IEnumerable<JsonElement> PendingDiagnostics => _backlog.Where(message =>
            message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics");

        public Client()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(PathOf("story.yaml"), Manifest);
            File.WriteAllText(PathOf("start.tale"), Valid);
            var installed = Environment.GetEnvironmentVariable("TALEWRIGHT_LSP_COMMAND");
            var start = new ProcessStartInfo(installed ?? "dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (installed is null)
            {
                start.ArgumentList.Add("exec");
                start.ArgumentList.Add("--runtimeconfig");
                start.ArgumentList.Add(typeof(ServerTests).Assembly.Location.Replace(".dll", ".runtimeconfig.json"));
                start.ArgumentList.Add(typeof(LanguageServer).Assembly.Location);
            }
            start.ArgumentList.Add("--stdio");
            Process = Process.Start(start)!;
            _stderr = Process.StandardError.ReadToEndAsync();
            _reader = ReadMessages();
        }

        public string PathOf(string name) => Path.Combine(Root, name);
        public string UriOf(string name) => new Uri(PathOf(name)).AbsoluteUri;
        public Task<JsonElement> Initialize(object? options = null) => Request("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri = new Uri(Root).AbsoluteUri,
            capabilities = new { },
            initializationOptions = options
        });
        public Task Open(string name, string text, int version) => Send("textDocument/didOpen",
            new { textDocument = new { uri = UriOf(name), languageId = name.EndsWith(".tale") ? "tale" : "yaml", version, text } });
        public Task Change(string name, string text, int version) => Send("textDocument/didChange",
            new { textDocument = new { uri = UriOf(name), version }, contentChanges = new[] { new { text } } });
        public Task Watch(string name, int type) => Send("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = UriOf(name), type } } });
        public async Task<JsonElement[]> Diagnostics(string name, int? version)
        {
            var message = await Next(message => message.TryGetProperty("method", out var method) &&
                method.GetString() == "textDocument/publishDiagnostics" &&
                message.GetProperty("params").GetProperty("uri").GetString() == UriOf(name) &&
                message.GetProperty("params").GetProperty("version").ToString() == (version?.ToString() ?? ""));
            return message.GetProperty("params").GetProperty("diagnostics").EnumerateArray().ToArray();
        }
        public async Task<JsonElement> Request(string method, object parameters)
        {
            var id = ++_id;
            await Write(new { jsonrpc = "2.0", id, method, @params = parameters });
            return await Next(message => message.TryGetProperty("id", out var value) && value.GetInt32() == id);
        }
        public Task Send(string method, object parameters) => Write(new { jsonrpc = "2.0", method, @params = parameters });
        private Task Write(object message) => Raw(JsonSerializer.Serialize(message));
        public async Task Raw(string message)
        {
            var body = Encoding.UTF8.GetBytes(message);
            await Process.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            await Process.StandardInput.BaseStream.WriteAsync(body);
            await Process.StandardInput.BaseStream.FlushAsync();
        }
        public async Task<JsonElement> Next(Func<JsonElement, bool> predicate)
        {
            var index = _backlog.FindIndex(message => predicate(message));
            if (index >= 0)
            {
                var result = _backlog[index];
                _backlog.RemoveAt(index);
                return result;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var message = await _messages.Reader.ReadAsync(timeout.Token);
                if (predicate(message)) return message;
                _backlog.Add(message);
            }
        }
        private async Task ReadMessages()
        {
            try
            {
                var stream = Process.StandardOutput.BaseStream;
                while (true)
                {
                    var header = new List<byte>();
                    var buffer = new byte[1];
                    while (header.Count < 4 || !header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 }))
                    {
                        if (await stream.ReadAsync(buffer) == 0) { _messages.Writer.TryComplete(); return; }
                        header.Add(buffer[0]);
                    }
                    var length = int.Parse(Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0].Split(':')[1]);
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body);
                    using var document = JsonDocument.Parse(body);
                    await _messages.Writer.WriteAsync(document.RootElement.Clone());
                }
            }
            catch (IOException exception) { _messages.Writer.TryComplete(exception); }
        }
        public async ValueTask DisposeAsync()
        {
            Process.StandardInput.Close();
            if (!Process.HasExited)
            {
                try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); }
            }
            await _reader;
            var stderr = await _stderr;
            Process.Dispose();
            Directory.Delete(Root, recursive: true);
            Assert.DoesNotContain("Unhandled exception", stderr);
        }
    }
}
