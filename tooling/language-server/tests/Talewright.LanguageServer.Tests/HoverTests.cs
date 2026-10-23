using System.Text.Json;
using Talewright.Authoring;
using Talewright.Tale.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    [Theory]
    [InlineData("-> sta|rt", "Scene", "start")]
    [InlineData("> pla|yer: Hello", "Character", "player")]
    [InlineData("@requires player:h|p > 0", "Stat", "hp")]
    [InlineData("@requires flag:rea|dy", "Flag", "ready")]
    [InlineData("{var:co|ins}", "Variable", "coins")]
    [InlineData("{item:ro|pe}", "Item", "rope")]
    [InlineData("{faction:gu|ild.name}", "Faction", "guild")]
    [InlineData("@requires @IsH|ero", "Predicate", "IsHero")]
    [InlineData("{player:first-name#cus|tom}", "Modifier", "custom")]
    [InlineData("@req|uires player:hp > 0", "Boolean condition", "requires")]
    [InlineData("@requires player h|as rope", "holds an item", "has")]
    [InlineData("~ player:hp -|= 1", "Subtracts a numeric", "-=")]
    public async Task Hover_categories_share_the_stdio_contract(string marked, string expected, string token)
    {
        await using var client = new Client();
        var metadata = AuthoringProjectMetadata.Export(CompilerOptions.Default) with { Predicates = ["IsHero"], Modifiers = ["custom"] };
        File.WriteAllText(client.PathOf("project.authoring.json"), metadata.ToJson());
        File.AppendAllText(client.PathOf(StorySource.ManifestFileName),
            "flags: [ready]\nvars: {coins: 1}\nitems: {rope: Rope}\nfactions: {guild: {name: Guild}}\n");
        var initialized = await client.Initialize(new { metadataPath = "project.authoring.json" });
        Assert.True(initialized.GetProperty("result").GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
        marked = "=== start\n" + marked;
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        var response = await Hover(client, "start.tale", marked);
        var result = response.GetProperty("result");
        Assert.Equal("plaintext", result.GetProperty("contents").GetProperty("kind").GetString());
        Assert.Contains(expected, result.GetProperty("contents").GetProperty("value").GetString());
        var range = result.GetProperty("range");
        Assert.Equal(1, range.GetProperty("start").GetProperty("line").GetInt32());
        var line = marked.Replace("|", "").Split('\n')[1];
        Assert.Equal(token, line[range.GetProperty("start").GetProperty("character").GetInt32()..
            range.GetProperty("end").GetProperty("character").GetInt32()]);
    }

    [Fact]
    public async Task Hover_negotiates_markdown_and_keeps_source_inside_code()
    {
        await using var client = new Client();
        File.WriteAllText(client.PathOf(StorySource.ManifestFileName),
            Manifest.Replace("first-name: Ada", "first-name: '[click](https://example.invalid) `x` <b>'"));
        await client.Request("initialize", new
        {
            rootUri = new Uri(client.Root).AbsoluteUri,
            capabilities = new { textDocument = new { hover = new { contentFormat = new[] { "markdown", "plaintext" } } } }
        });
        const string marked = "=== start\n> pla|yer: Hello";
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        var contents = (await Hover(client, "start.tale", marked)).GetProperty("result").GetProperty("contents");
        Assert.Equal("markdown", contents.GetProperty("kind").GetString());
        Assert.Contains("**Character**", contents.GetProperty("value").GetString());
        Assert.Contains("`` ", contents.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("Prose sta|rt")]
    [InlineData("// -> sta|rt")]
    [InlineData("-> ab|sent")]
    [InlineData("-> sta|")]
    [InlineData("@requires \"pla|yer\" == \"player\"")]
    [InlineData("{unknown:h|p}")]
    public async Task Hover_returns_null_for_unknown_and_irrelevant_input(string marked)
    {
        await using var client = new Client();
        await client.Initialize();
        marked = "=== start\n" + marked;
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        Assert.Equal(JsonValueKind.Null, (await Hover(client, "start.tale", marked)).GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Hover_tracks_cross_file_overlays_versions_watched_changes_and_deletion()
    {
        await using var client = new Client();
        await client.Initialize();
        const string marked = "=== start\n-> sec|ond";
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        Assert.Equal(JsonValueKind.Null, (await Hover(client, "start.tale", marked)).GetProperty("result").ValueKind);
        File.WriteAllText(client.PathOf("other.tale"), "=== second\n-> END");
        await client.Watch("other.tale", 1);
        Assert.Contains("other.tale:1:5", Text(await Hover(client, "start.tale", marked)));
        await client.Open("other.tale", "// unsaved\n=== second\n-> END", 3);
        Assert.Contains("other.tale:2:5", Text(await Hover(client, "start.tale", marked)));
        await client.Change("other.tale", "=== stale\n-> END", 2);
        Assert.Contains("other.tale:2:5", Text(await Hover(client, "start.tale", marked)));
        await client.Change("other.tale", "=== renamed\n-> END", 4);
        Assert.Equal(JsonValueKind.Null, (await Hover(client, "start.tale", marked)).GetProperty("result").ValueKind);
        File.WriteAllText(client.PathOf("other.tale"), "// disk\n// next\n=== second\n-> END");
        await client.Watch("other.tale", 2);
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("other.tale") } });
        Assert.Contains("other.tale:3:5", Text(await Hover(client, "start.tale", marked)));
        File.Delete(client.PathOf("other.tale"));
        await client.Watch("other.tale", 3);
        Assert.Equal(JsonValueKind.Null, (await Hover(client, "start.tale", marked)).GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Hover_handles_unsaved_manifest_schema_and_escaped_reference_ranges()
    {
        await using var client = new Client();
        await client.Initialize();
        var marked = Manifest.Replace("start: start", "sta|rt: \"start\"");
        await client.Open(StorySource.ManifestFileName, marked.Replace("|", ""), 1);
        Assert.Contains("scene where play begins", Text(await Hover(client, StorySource.ManifestFileName, marked)));
        marked = Manifest.Replace("start: start", "start: \"\\u0073ta|rt\"");
        await client.Change(StorySource.ManifestFileName, marked.Replace("|", ""), 2);
        var result = (await Hover(client, StorySource.ManifestFileName, marked)).GetProperty("result");
        Assert.Contains("Scene", result.GetProperty("contents").GetProperty("value").GetString());
        Assert.Equal(8, result.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(18, result.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
        await client.Change(StorySource.ManifestFileName, Manifest + "flags: [overlay]\n", 3);
        const string tale = "=== start\n@requires flag:over|lay";
        await client.Open("start.tale", tale.Replace("|", ""), 1);
        Assert.Contains("Flag", Text(await Hover(client, "start.tale", tale)));
        await client.Change(StorySource.ManifestFileName, Manifest, 4);
        Assert.Equal(JsonValueKind.Null, (await Hover(client, "start.tale", tale)).GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Hover_uses_utf16_in_an_incomplete_crlf_substitution()
    {
        await using var client = new Client();
        await client.Initialize();
        const string marked = "=== start\r\nA 🦑 {player:h|p";
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        var result = (await Hover(client, "start.tale", marked)).GetProperty("result");
        Assert.Equal(13, result.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(15, result.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    public async Task Hover_metadata_unavailability_is_explicit_and_recovers(string? json)
    {
        await using var client = new Client();
        if (json is not null) File.WriteAllText(client.PathOf("project.authoring.json"), json);
        await client.Initialize(new { requiresProjectMetadata = true, metadataPath = "project.authoring.json" });
        const string marked = "=== start\n-> sta|rt";
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        var response = await Hover(client, "start.tale", marked);
        Assert.Equal(-32001, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("unavailable", response.GetProperty("error").GetProperty("message").GetString());
        File.WriteAllText(client.PathOf("project.authoring.json"), AuthoringProjectMetadata.Export(CompilerOptions.Default).ToJson());
        await client.Watch("project.authoring.json", 1);
        Assert.Contains("Scene", Text(await Hover(client, "start.tale", marked)));
    }

    [Fact]
    public async Task Hover_rejects_invalid_positions_without_disconnecting()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Valid, 1);
        var response = await client.Request("textDocument/hover", new
        {
            textDocument = new { uri = client.UriOf("start.tale") }, position = new { line = -1, character = 0 }
        });
        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("Scene", Text(await Hover(client, "start.tale", "=== sta|rt\nHello.\n-> END\n")));
    }

    private static string Text(JsonElement response) => response.GetProperty("result").GetProperty("contents").GetProperty("value").GetString()!;
    private static Task<JsonElement> Hover(Client client, string file, string marked)
    {
        var lines = marked[..marked.IndexOf('|')].ReplaceLineEndings("\n").Split('\n');
        return client.Request("textDocument/hover", new
        {
            textDocument = new { uri = client.UriOf(file) },
            position = new { line = lines.Length - 1, character = lines[^1].Length }
        });
    }
}
