using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Talewright.Cli;
using Talewright.Authoring;
using Talewright.Talemark.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    private const string Manifest = "game:\n  id: lsp-test\n  title: LSP test\n  start: start\ncharacters:\n  player: {first-name: Ada}\n";
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
        var unknown = await client.Request("textDocument/signatureHelp", new { });
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
    public async Task One_many_diagnostics_clear_when_the_two_string_pronoun_selector_is_fixed()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", "=== start\n{player:pronoun#one-many(\"has\")}\n-> END", 1);
        var error = Assert.Single(await client.Diagnostics("start.tale", 1));
        Assert.Equal("TW2006", error.GetProperty("code").GetString());
        Assert.Contains("exactly two", error.GetProperty("message").GetString());
        await client.Change("start.tale", "=== start\n{player:pronoun#one-many(\"has\", \"have\")}\n-> END", 2);
        Assert.Empty(await client.Diagnostics("start.tale", 2));
    }

    [Fact]
    public async Task Image_syntax_diagnostics_clear_and_valid_calls_receive_asset_lookup()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", "=== start\n![Camp](camp)\n-> END", 1);
        var diagnostics = await client.Diagnostics("start.tale", 1);
        var legacy = Assert.Single(diagnostics, diagnostic => diagnostic.GetProperty("code").GetString() == "TW1015");
        Assert.Contains("{image(\"camp\", \"Camp\")}", legacy.GetProperty("message").GetString());
        await client.Change("start.tale", "=== start\n{image(\"camp\", \"Camp\")}\n-> END", 2);
        var corrected = Assert.Single(await client.Diagnostics("start.tale", 2));
        Assert.Equal("TW2012", corrected.GetProperty("code").GetString());
        await client.Change("start.tale", "=== start\n{image(camp)}\n-> END", 3);
        Assert.Contains(await client.Diagnostics("start.tale", 3),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW1014");
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

    [Theory]
    [InlineData("textDocument/didOpen", "1.5")]
    [InlineData("textDocument/didOpen", "2147483648")]
    [InlineData("textDocument/didChange", "1.5")]
    [InlineData("textDocument/didChange", "2147483648")]
    public async Task Invalid_document_versions_report_errors_without_disconnect(string method, string version)
    {
        await using var client = new Client();
        await client.Initialize();
        if (method.EndsWith("didChange", StringComparison.Ordinal))
        {
            await client.Open("start.tale", Valid, 1);
            await client.Diagnostics("start.tale", 1);
        }
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", method,
            @params = new
            {
                textDocument = new { uri = client.UriOf("start.tale"), version = "INVALID_VERSION", text = Invalid },
                contentChanges = new[] { new { text = Invalid } }
            }
        }).Replace("\"INVALID_VERSION\"", version);
        await client.Raw(body);
        var error = await client.Next(message => message.TryGetProperty("method", out var name) && name.GetString() == "window/showMessage");
        Assert.Contains("32-bit integer", error.GetProperty("params").GetProperty("message").GetString());
        var response = await client.Request("unknown", new { });
        Assert.Equal(-32601, response.GetProperty("error").GetProperty("code").GetInt32());
        if (method.EndsWith("didOpen", StringComparison.Ordinal)) await client.Open("start.tale", Invalid, 2);
        else await client.Change("start.tale", Invalid, 2);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 2));
    }

    [Fact]
    public async Task Watched_additional_manifest_is_rejected_without_rehoming_open_tale()
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("child"));
        File.WriteAllText(client.PathOf("child/main.tale"), "=== child\n-> END\n");
        await client.Initialize();
        await client.Open("child/main.tale", "=== child\n* [Go] -> start\n", 1);
        await client.Diagnostics("child/main.tale", 1);
        File.WriteAllText(client.PathOf("child/story.talewright.yaml"), Manifest.Replace("start: start", "start: child"));
        await client.Watch("child/story.talewright.yaml", 1);
        Assert.Empty(await client.Diagnostics("child/main.tale", 1));
        var reserved = Assert.Single(await client.Diagnostics("child/story.talewright.yaml", null));
        Assert.Equal("TW4002", reserved.GetProperty("code").GetString());
        Assert.Contains("reserved", reserved.GetProperty("message").GetString());
        File.Delete(client.PathOf("child/story.talewright.yaml"));
        await client.Watch("child/story.talewright.yaml", 3);
        Assert.Empty(await client.Diagnostics("child/story.talewright.yaml", null));
        Assert.DoesNotContain(await client.Diagnostics("child/main.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2002");
        await client.Change("child/main.tale", "=== child\n* [Go] -> absent\n", 2);
        Assert.Contains(await client.Diagnostics("child/main.tale", 2), item => item.GetProperty("code").GetString() == "TW2002");
    }

    [Fact]
    public async Task Deleting_disk_manifest_preserves_its_open_overlay()
    {
        await using var client = new Client();
        File.Delete(client.PathOf("story.talewright.yaml"));
        Directory.CreateDirectory(client.PathOf("child"));
        var manifest = Manifest.Replace("start: start", "start: child");
        File.WriteAllText(client.PathOf("child/story.talewright.yaml"), manifest);
        await client.Initialize();
        await client.Open("child/story.talewright.yaml", manifest, 1);
        await client.Diagnostics("child/story.talewright.yaml", 1);
        File.Delete(client.PathOf("child/story.talewright.yaml"));
        await client.Watch("child/story.talewright.yaml", 3);
        await client.Diagnostics("child/story.talewright.yaml", 1);
        await client.Open("child/main.tale", "=== child\n-> END\n", 1);
        Assert.Empty(await client.Diagnostics("child/story.talewright.yaml", 1));
        Assert.Empty(await client.Diagnostics("child/main.tale", 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_story_directory_clears_closed_files_and_preserves_open_tale(bool openTale)
    {
        await using var client = new Client();
        File.Delete(client.PathOf("story.talewright.yaml"));
        Directory.CreateDirectory(client.PathOf("child"));
        File.WriteAllText(client.PathOf("child/story.talewright.yaml"), Manifest.Replace("start: start", "start: absent"));
        await client.Initialize();
        await client.Watch("child/story.talewright.yaml", 1);
        Assert.NotEmpty(await client.Diagnostics("child/story.talewright.yaml", null));
        if (openTale)
        {
            await client.Open("child/main.tale", "=== child\n* [Go] -> start\n", 1);
            Assert.NotEmpty(await client.Diagnostics("child/main.tale", 1));
            await client.Diagnostics("child/story.talewright.yaml", null);
        }
        File.Delete(client.PathOf("child/story.talewright.yaml"));
        Directory.Delete(client.PathOf("child"));
        await client.Watch("child", 3);
        Assert.Empty(await client.Diagnostics("child/story.talewright.yaml", null));
        if (openTale)
        {
            Assert.Empty(await client.Diagnostics("child/main.tale", 1));
            File.WriteAllText(client.PathOf("story.talewright.yaml"), Manifest);
            await client.Watch("story.talewright.yaml", 1);
            await client.Diagnostics("child/main.tale", 1);
            await client.Change("child/main.tale", "=== child\n* [Go] -> absent\n", 2);
            Assert.NotEmpty(await client.Diagnostics("child/main.tale", 2));
        }
        Assert.Equal(-32601, (await client.Request("unknown", new { })).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Additional_manifest_does_not_reintroduce_authoring_size_ceilings(bool fileCount)
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("child"));
        File.WriteAllText(client.PathOf("child/story.talewright.yaml"), Manifest.Replace("start: start", "start: child"));
        File.WriteAllText(client.PathOf("child/main.tale"), "=== child\n-> END\n");
        await client.Initialize();
        await client.Open("child/main.tale", "=== child\n-> END\n", 1);
        await client.Diagnostics("child/main.tale", 1);
        if (fileCount)
        {
            for (var index = 0; index < 998; index++)
                File.WriteAllText(client.PathOf($"child/scene{index}.tale"), $"=== scene{index}\n-> END\n");
        }
        else File.WriteAllText(client.PathOf("child/oversized.tale"), "// " + new string('x', 5 * 1024 * 1024 + 1));
        await client.Watch("child/main.tale", 2);
        Assert.Empty(await client.Diagnostics("child/main.tale", 1));
        Assert.Contains(await client.Diagnostics("child/story.talewright.yaml", null),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW4002");
        await client.Change("child/main.tale", "=== child\n* [Go] -> absent\n", 2);
        Assert.Contains(await client.Diagnostics("child/main.tale", 2),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW2002");
    }

    [Fact]
    public async Task Deleted_standalone_story_keeps_open_buffer_usable_without_workspace_folders()
    {
        await using var client = new Client();
        File.Delete(client.PathOf("story.talewright.yaml"));
        Directory.CreateDirectory(client.PathOf("child"));
        File.WriteAllText(client.PathOf("child/story.talewright.yaml"), Manifest.Replace("start: start", "start: child"));
        await client.Initialize(includeRoot: false);
        await client.Open("child/main.tale", "=== child\n* [Go] -> absent\n", 1);
        Assert.NotEmpty(await client.Diagnostics("child/main.tale", 1));
        File.Delete(client.PathOf("child/story.talewright.yaml"));
        Directory.Delete(client.PathOf("child"));
        await client.Watch("child", 3);
        Assert.Empty(await client.Diagnostics("child/main.tale", 1));
        Assert.Contains(await client.Diagnostics("child/story.talewright.yaml", null), item => item.GetProperty("code").GetString() == "TW4002");
        await client.Change("child/main.tale", "=== child\n-> END\n", 2);
        Assert.Empty(await client.Diagnostics("child/main.tale", 2));
    }

    [Fact]
    public async Task Close_discards_overlay_and_registration_even_when_disk_reload_fails()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Invalid, 1);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        var nested = client.PathOf("deep");
        for (var depth = 0; depth < 65; depth++)
            nested = Path.Combine(nested, "nested");
        Directory.CreateDirectory(nested);
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("start.tale") } });
        var error = await client.Next(message => message.TryGetProperty("method", out var method) &&
            method.GetString() == "window/showMessage");
        Assert.Contains("64 levels", error.GetProperty("params").GetProperty("message").GetString());
        Directory.Delete(client.PathOf("deep"), recursive: true);
        await client.Watch("deep", 3);
        Assert.Empty(await client.Diagnostics("start.tale", null));
        await client.Open("start.tale", Valid, 1);
        Assert.Empty(await client.Diagnostics("start.tale", 1));
        await client.Change("start.tale", Invalid, 2);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Opening_manifest_preserves_existing_overlays_under_single_root_ownership(bool parentManifest)
    {
        await using var client = new Client();
        if (!parentManifest) File.Delete(client.PathOf("story.talewright.yaml"));
        Directory.CreateDirectory(client.PathOf("child"));
        const string childValid = "=== child\nHello.\n-> END\n";
        const string childInvalid = "=== child\n* [Go] -> absent\n";
        File.WriteAllText(client.PathOf("child/main.tale"), childValid);
        await client.Initialize();
        await client.Open("child/main.tale", childInvalid, 1);
        var previous = await client.Diagnostics("child/main.tale", 1);
        if (parentManifest) Assert.NotEmpty(previous);
        await client.Open("child/story.talewright.yaml", Manifest.Replace("start: start", "start: child"), 1);
        Assert.Contains(await client.Diagnostics("child/main.tale", 1),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW2002");
        await client.Change("child/main.tale", childValid, 2);
        Assert.Empty(await client.Diagnostics("child/main.tale", 2));
        // Refreshing disk must preserve the corrected unsaved overlay.
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
        Assert.False(File.Exists(client.PathOf("child/story.talewright.yaml")));
        Assert.Equal(childValid, File.ReadAllText(client.PathOf("child/main.tale")));
    }

    [Fact]
    public async Task Opening_unsaved_additional_manifest_before_tale_keeps_parent_ownership()
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("child"));
        await client.Initialize();
        await client.Open("child/story.talewright.yaml", Manifest.Replace("start: start", "start: child"), 1);
        var error = Assert.Single(await client.Diagnostics("child/story.talewright.yaml", 1));
        Assert.Equal("TW4002", error.GetProperty("code").GetString());
        Assert.Contains("reserved", error.GetProperty("message").GetString());
        await client.Open("child/main.tale", "=== child\nHello.\n-> END\n", 1);
        Assert.Single(await client.Diagnostics("child/story.talewright.yaml", 1));
        Assert.Empty(await client.Diagnostics("child/main.tale", 1));
    }

    [Fact]
    public async Task Opening_tale_below_additional_disk_manifest_keeps_shared_diagnostic_ownership()
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("child"));
        File.WriteAllText(client.PathOf("child/story.talewright.yaml"), Manifest.Replace("start: start", "start: child"));
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
        Assert.Empty(await client.Diagnostics("child/main.tale", 1));
        await client.Change("child/main.tale", "=== child\n* [Go] -> absent\n", 2);
        Assert.NotEmpty(await client.Diagnostics("child/main.tale", 2));
        await client.Watch("start.tale", 2);
        await client.Diagnostics("start.tale", 2);
        Assert.NotEmpty(await client.Diagnostics("child/main.tale", 2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Discarding_manifest_overlay_recomputes_ownership_and_clears_stale_diagnostics(bool parentManifest)
    {
        await using var client = new Client();
        if (!parentManifest) File.Delete(client.PathOf("story.talewright.yaml"));
        Directory.CreateDirectory(client.PathOf("child"));
        File.WriteAllText(client.PathOf("child/main.tale"), "=== child\nHello.\n-> END\n");
        await client.Initialize();
        await client.Open("start.tale", Valid, 1);
        await client.Diagnostics("start.tale", 1);
        await client.Open("child/story.talewright.yaml", Manifest.Replace("start: start", "start: absent"), 1);
        Assert.NotEmpty(await client.Diagnostics("child/story.talewright.yaml", 1));
        await client.Open("child/main.tale", "=== child\n* [Go] -> absent\n", 1);
        Assert.NotEmpty(await client.Diagnostics("child/main.tale", 1));
        Assert.NotEmpty(await client.Diagnostics("child/story.talewright.yaml", 1));
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("child/story.talewright.yaml") } });
        Assert.Empty(await client.Diagnostics("child/story.talewright.yaml", null));
        await client.Diagnostics("start.tale", 1);
        await client.Diagnostics("child/main.tale", 1);
        await client.Change("child/main.tale", "=== child\nHello.\n-> END\n", 2);
        var diagnostics = await client.Diagnostics("child/main.tale", 2);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.GetProperty("code").GetString() == "TW2002");
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("child/main.tale") } });
        await client.Request("unknown", new { });
        await client.Open("child/main.tale", "=== child\nHello.\n-> END\n", 1);
        await client.Diagnostics("child/main.tale", 1);
        await client.Diagnostics("start.tale", 1);
        Assert.DoesNotContain(client.PendingDiagnostics, message =>
            message.GetProperty("params").GetProperty("uri").GetString() == client.UriOf("child/story.talewright.yaml") &&
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
        await client.Open("story.talewright.yaml", "id: [\n", 1);
        Assert.NotEmpty(await client.Diagnostics("story.talewright.yaml", 1));
        await client.Change("story.talewright.yaml", Manifest, 2);
        Assert.Empty(await client.Diagnostics("story.talewright.yaml", 2));
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
    public async Task Lint_diagnostics_match_cli_rules_suppressions_and_compiler_results()
    {
        await using var client = new Client();
        var manifest = Manifest.Replace("id: lsp-test", "id: Lsp_Test", StringComparison.Ordinal)
            .Replace("start: start", "start: Start_Scene", StringComparison.Ordinal);
        const string tale = "=== Start_Scene\n* [Missing] -> absent\n";
        File.WriteAllText(client.PathOf("story.talewright.yaml"), manifest);
        File.WriteAllText(client.PathOf("start.tale"), tale);
        File.WriteAllText(client.PathOf(LintConfiguration.FileName),
            """{"rules":{"TWL001":"error"},"suppressions":{"story.talewright.yaml":["TWL002"]}}""");

        var output = new StringWriter();
        Assert.Equal(1, CommandLine.Run(["lint", client.Root, "--format", "json"], output, new StringWriter()));
        using var cli = JsonDocument.Parse(output.ToString());
        var expected = cli.RootElement.GetProperty("diagnostics").EnumerateArray().ToArray();

        await client.Initialize();
        await client.Open("story.talewright.yaml", manifest, 1);
        await client.Open("start.tale", tale, 1);
        var actual = (await client.Diagnostics("story.talewright.yaml", 1))
            .Select(diagnostic => (File: "story.talewright.yaml", Diagnostic: diagnostic))
            .Concat((await client.Diagnostics("start.tale", 1))
                .Select(diagnostic => (File: "start.tale", Diagnostic: diagnostic)))
            .ToArray();

        Assert.Equal(expected.Length, actual.Length);
        foreach (var expectedDiagnostic in expected)
        {
            var file = expectedDiagnostic.GetProperty("file").GetString()!;
            var match = Assert.Single(actual, item =>
                item.File == file &&
                item.Diagnostic.GetProperty("code").GetString() == expectedDiagnostic.GetProperty("code").GetString());
            var diagnostic = match.Diagnostic;
            Assert.Equal(expectedDiagnostic.GetProperty("severity").GetString() == "error" ? 1 : 2,
                diagnostic.GetProperty("severity").GetInt32());
            Assert.Equal(expectedDiagnostic.GetProperty("message").GetString(),
                diagnostic.GetProperty("message").GetString());
            Assert.Equal(expectedDiagnostic.GetProperty("line").GetInt32() - 1,
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
            Assert.Equal(expectedDiagnostic.GetProperty("column").GetInt32() - 1,
                diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
            if (expectedDiagnostic.TryGetProperty("endLine", out var endLine))
            {
                Assert.Equal(endLine.GetInt32() - 1,
                    diagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
                Assert.Equal(expectedDiagnostic.GetProperty("endColumn").GetInt32() - 1,
                    diagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
            }
        }
        Assert.Contains(actual, item => item.Diagnostic.GetProperty("code").GetString() == "TWL001" &&
            item.Diagnostic.GetProperty("severity").GetInt32() == 1);
        Assert.DoesNotContain(actual, item => item.Diagnostic.GetProperty("code").GetString() == "TWL002");
    }

    [Fact]
    public async Task Invalid_lint_configuration_is_diagnosed_and_recovers_after_watched_change()
    {
        await using var client = new Client();
        const string tale = "=== Start_Scene\nHello.\n-> END\n";
        File.WriteAllText(client.PathOf(LintConfiguration.FileName), """{"rules":{"TWL001":"invalid"}}""");
        await client.Initialize();
        await client.Open("start.tale", tale, 1);

        var configurationDiagnostics = await client.Diagnostics(LintConfiguration.FileName, null);
        Assert.Collection(configurationDiagnostics, diagnostic =>
        {
            Assert.Equal("TWLSP004", diagnostic.GetProperty("code").GetString());
            Assert.Equal(1, diagnostic.GetProperty("severity").GetInt32());
            Assert.Contains("Invalid lint configuration", diagnostic.GetProperty("message").GetString());
        });
        Assert.Contains(await client.Diagnostics("start.tale", 1),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TWL001");

        File.WriteAllText(client.PathOf(LintConfiguration.FileName), """{"rules":{"TWL001":"off"}}""");
        await client.Watch(LintConfiguration.FileName, 2);

        Assert.Empty(await client.Diagnostics(LintConfiguration.FileName, null));
        Assert.DoesNotContain(await client.Diagnostics("start.tale", 1),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TWL001");
        var unknown = await client.Request("unknown", new { });
        Assert.Equal(-32601, unknown.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Invalid_lint_configuration_is_published_and_cleared_when_metadata_is_invalid()
    {
        await using var client = new Client();
        File.WriteAllText(client.PathOf(LintConfiguration.FileName), """{"rules":{"TWL001":"invalid"}}""");
        await client.Initialize(new { metadataPath = "missing.authoring.json" });
        await client.Open("start.tale", "=== Start_Scene\nHello.\n-> END\n", 1);

        Assert.Contains(await client.Diagnostics("story.talewright.yaml", null),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW5002");
        Assert.Contains(await client.Diagnostics(LintConfiguration.FileName, null),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TWLSP004");

        File.WriteAllText(client.PathOf(LintConfiguration.FileName), """{"rules":{"TWL001":"off"}}""");
        await client.Watch(LintConfiguration.FileName, 2);

        Assert.Empty(await client.Diagnostics(LintConfiguration.FileName, null));
        Assert.Contains(await client.Diagnostics("story.talewright.yaml", null),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW5002");
        var unknown = await client.Request("unknown", new { });
        Assert.Equal(-32601, unknown.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Csharp_project_is_never_executed_and_missing_metadata_is_explicit()
    {
        await using var client = new Client();
        File.WriteAllText(client.PathOf("Untrusted.csproj"), "<Project><Target Name=\"Exploit\" BeforeTargets=\"Build\"><Error Text=\"Must never run\" /></Target></Project>");
        await client.Initialize();
        await client.Open("story.talewright.yaml", Manifest, 1);
        Assert.Contains(await client.Diagnostics("story.talewright.yaml", 1), item => item.GetProperty("code").GetString() == "TW5001");
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
        Assert.Contains(await client.Diagnostics("story.talewright.yaml", null), item => item.GetProperty("code").GetString() == "TW5002");
        File.WriteAllText(client.PathOf("missing.authoring.json"), AuthoringProjectMetadata.Export(CompilerOptions.Default).ToJson());
        await client.Watch("missing.authoring.json", 1);
        Assert.Empty(await client.Diagnostics("story.talewright.yaml", null));
        Assert.Empty(await client.Diagnostics("start.tale", 1));
    }

    [Fact]
    public async Task Unsaved_manifest_invalidates_other_files_and_superseded_results_never_publish()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Valid, 1);
        Assert.Empty(await client.Diagnostics("start.tale", 1));
        await client.Open("story.talewright.yaml", Manifest.Replace("start: start", "start: absent"), 1);
        Assert.NotEmpty(await client.Diagnostics("story.talewright.yaml", 1));
        await client.Change("story.talewright.yaml", "id: [", 2);
        await client.Change("story.talewright.yaml", Manifest, 3);
        Assert.Empty(await client.Diagnostics("story.talewright.yaml", 3));
        Assert.Equal(Manifest, File.ReadAllText(client.PathOf("story.talewright.yaml")));
        // A request/response is an event-loop barrier; no old version may arrive after it.
        await client.Request("unknown", new { });
        Assert.DoesNotContain(client.PendingDiagnostics, message =>
            message.GetProperty("params").GetProperty("uri").GetString() == client.UriOf("story.talewright.yaml") &&
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
            File.WriteAllText(PathOf("story.talewright.yaml"), Manifest);
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
        public Task<JsonElement> Initialize(object? options = null, bool includeRoot = true) => Request("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri = includeRoot ? new Uri(Root).AbsoluteUri : null,
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
