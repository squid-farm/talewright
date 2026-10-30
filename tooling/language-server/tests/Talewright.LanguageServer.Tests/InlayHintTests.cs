using System.Text.Json;
using Talewright.Authoring;
using Talewright.Talemark.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task Inlay_hints_are_final_transformed_defaults_after_braces_without_edits()
    {
        await using var client = new Client();
        File.WriteAllText(client.PathOf(StorySource.ManifestFileName),
            Manifest.Replace("first-name: Ada", "first-name: Mara, last-name: Burns, pronouns: she"));
        var initialized = await client.Initialize();
        Assert.True(initialized.GetProperty("result").GetProperty("capabilities").GetProperty("inlayHintProvider").GetBoolean());
        const string text = "=== start\r\n🦑 **{player:name#upper}** {player:pronoun:object#capitalize}\r\n-> END\r\n";
        await client.Open("start.tale", text, 1);
        var hints = (await Hints(client)).GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal(["\"MARA BURNS\"", "\"Her\""], hints.Select(hint => hint.GetProperty("label").GetString()));
        foreach (var hint in hints)
        {
            var position = hint.GetProperty("position");
            Assert.Equal(1, position.GetProperty("line").GetInt32());
            Assert.Equal('}', text.Split('\n')[1][position.GetProperty("character").GetInt32() - 1]);
            Assert.True(hint.GetProperty("paddingLeft").GetBoolean());
            Assert.False(hint.TryGetProperty("textEdits", out _));
            Assert.Contains("(default)", hint.GetProperty("tooltip").GetProperty("value").GetString());
        }
        var rangeHints = (await client.Request("textDocument/inlayHint", new
        {
            textDocument = new { uri = client.UriOf("start.tale") },
            range = new { start = new { line = 1, character = 25 },
                end = new { line = 1, character = text.Split('\n')[1].TrimEnd('\r').Length } },
        })).GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal("\"Her\"", Assert.Single(rangeHints).GetProperty("label").GetString());
        Assert.Equal(Valid, File.ReadAllText(client.PathOf("start.tale")));
    }

    [Fact]
    public async Task Unsaved_cross_file_defaults_refresh_and_recover_without_a_restart()
    {
        await using var client = new Client();
        await client.Request("initialize", new
        {
            rootUri = new Uri(client.Root).AbsoluteUri,
            capabilities = new { workspace = new { inlayHint = new { refreshSupport = true } } },
        });
        await client.Open("start.tale", "=== start\n{player:pronoun:object}\n-> END\n", 1);
        var refresh = await client.Next(message => message.TryGetProperty("method", out var method) &&
            method.GetString() == "workspace/inlayHint/refresh");
        Assert.Equal("\"them\"", (await Hints(client)).GetProperty("result")[0].GetProperty("label").GetString());
        await client.Raw(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = refresh.GetProperty("id"), result = (object?)null }));
        await client.Open(StorySource.ManifestFileName, Manifest.Replace("first-name: Ada", "first-name: Ada, pronouns: she"), 1);
        await client.Next(message => message.TryGetProperty("method", out var method) &&
            method.GetString() == "workspace/inlayHint/refresh");
        Assert.Equal("\"her\"", (await Hints(client)).GetProperty("result")[0].GetProperty("label").GetString());
        await client.Change(StorySource.ManifestFileName, "game: [", 2);
        Assert.Empty((await Hints(client)).GetProperty("result").EnumerateArray());
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf(StorySource.ManifestFileName) } });
        Assert.Equal("\"them\"", (await Hints(client)).GetProperty("result")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Unknown_and_gameplay_values_are_omitted_but_builtin_metadata_previews_work()
    {
        await using var client = new Client();
        var metadata = AuthoringProjectMetadata.Export(CompilerOptions.Default) with
        {
            Modifiers = [.. CompilerOptions.Default.Modifiers.Names, "custom"],
        };
        File.WriteAllText(client.PathOf("project.authoring.json"), metadata.ToJson());
        await client.Initialize(new { metadataPath = "project.authoring.json" });
        await client.Open("start.tale",
            "=== start\n{player:name#upper} {player:name#custom} {player:hp} {missing:name}\n-> END\n", 1);
        var hint = Assert.Single((await Hints(client)).GetProperty("result").EnumerateArray());
        Assert.Equal("\"ADA\"", hint.GetProperty("label").GetString());
        var hover = await Hover(client, "start.tale",
            "=== start\n{player:name#upper} {player:name#cus|tom} {player:hp} {missing:name}\n-> END\n");
        Assert.Contains("not executed while editing", Text(hover));
    }

    [Theory]
    [InlineData(-1, 0, 1, 0)]
    [InlineData(0, -1, 1, 0)]
    [InlineData(0, 99, 1, 0)]
    [InlineData(2, 0, 1, 0)]
    [InlineData(0, 0, 20, 0)]
    public async Task Invalid_inlay_ranges_report_errors(int startLine, int startCharacter, int endLine, int endCharacter)
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Valid, 1);
        var response = await client.Request("textDocument/inlayHint", new
        {
            textDocument = new { uri = client.UriOf("start.tale") },
            range = new { start = new { line = startLine, character = startCharacter }, end = new { line = endLine, character = endCharacter } },
        });
        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Inlay_hints_require_open_documents_and_valid_project_metadata()
    {
        await using var client = new Client();
        await client.Initialize(new { requiresProjectMetadata = true });
        Assert.Equal(-32602, (await Hints(client)).GetProperty("error").GetProperty("code").GetInt32());
        await client.Open("start.tale", Valid, 1);
        Assert.Equal(-32001, (await Hints(client)).GetProperty("error").GetProperty("code").GetInt32());
    }

    private static Task<JsonElement> Hints(Client client) => client.Request("textDocument/inlayHint", new
    {
        textDocument = new { uri = client.UriOf("start.tale") },
        range = new { start = new { line = 0, character = 0 }, end = new { line = 3, character = 0 } },
    });
}
