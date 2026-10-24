using System.Text.Json;
using Talewright.Authoring;
using Talewright.Tale.Analysis;
using Talewright.Tale.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task Multiline_yaml_identifiers_are_not_returned_as_primary_completion_edits()
    {
        await using var client = new Client();
        await client.Initialize();
        var marked = Manifest.Replace("start: start", "start: \"sta\\\n  |rt\"");
        var text = marked.Replace("|", "");
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = text, ["start.tale"] = Valid
        });
        Assert.Contains(StoryAnalyzer.Analyze(source).CompletionSites, site =>
            site.Text == "start" && site.Range.Start.Line != site.Range.End.Line);
        await client.Open(StorySource.ManifestFileName, text, 1);
        Assert.Empty(Items(await Completion(client, StorySource.ManifestFileName, marked)));
        // Single-line scalar edits remain available after the same unsaved buffer changes.
        marked = Manifest.Replace("start: start", "start: \"sta|rt\"");
        await client.Change(StorySource.ManifestFileName, marked.Replace("|", ""), 2);
        var item = Assert.Single(Items(await Completion(client, StorySource.ManifestFileName, marked)), HasLabel("start"));
        var range = item.GetProperty("textEdit").GetProperty("range");
        Assert.Equal(range.GetProperty("start").GetProperty("line").GetInt32(),
            range.GetProperty("end").GetProperty("line").GetInt32());
    }

    [Theory]
    [InlineData("-> sta|", "start")]
    [InlineData("> pla|", "player")]
    [InlineData("@req|", "requires")]
    [InlineData("@requires player:h|", "hp")]
    [InlineData("@requires flag:re|", "ready")]
    [InlineData("@requires var:co|", "coins")]
    [InlineData("@requires faction:gu|", "guild")]
    [InlineData("@requires player has ro|", "rope")]
    [InlineData("{item:ro|}", "rope")]
    [InlineData("@requires item:ro| == \"Rope\"", "rope")]
    [InlineData("~ give player rope |", "var:")]
    [InlineData("~ take player rope |", "player")]
    [InlineData("@requires @IsH|", "IsHero")]
    [InlineData("@requires flag:door-|", "door-open")]
    [InlineData("{player:pronoun#|}", "reflexive")]
    [InlineData("{player:pronoun#possessive(|)}", "\"independent\"")]
    [InlineData("{player:first-name#cus|", "custom")]
    public async Task Completion_categories_use_the_same_stdio_contract(string line, string expected)
    {
        await using var client = new Client();
        var registrations = AuthoringProjectMetadata.Export(CompilerOptions.Default) with
        {
            Predicates = ["IsHero"],
            Modifiers = ["custom"]
        };
        File.WriteAllText(client.PathOf("project.authoring.json"), registrations.ToJson());
        File.AppendAllText(client.PathOf("story.talewright.yaml"),
            "flags: [ready, door-open]\nvars: {coins: 1}\nitems:\n  rope: {name: Rope}\nfactions:\n  guild: {name: Guild}\n");
        var initialized = await client.Initialize(new { metadataPath = "project.authoring.json" });
        Assert.False(initialized.GetProperty("result").GetProperty("capabilities").GetProperty("completionProvider")
            .GetProperty("resolveProvider").GetBoolean());
        var marked = "=== start\n" + line;
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        var response = await Completion(client, "start.tale", marked);
        Assert.False(response.GetProperty("result").GetProperty("isIncomplete").GetBoolean());
        var item = Assert.Single(Items(response), item => item.GetProperty("label").GetString() == expected);
        Assert.Equal(expected, item.GetProperty("textEdit").GetProperty("newText").GetString());
        Assert.Equal(1, item.GetProperty("textEdit").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Possessive_pronoun_values_mark_the_default()
    {
        await using var client = new Client();
        await client.Initialize();
        const string marked = "=== start\n{player:pronoun#possessive(|)}";
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        var items = Items(await Completion(client, "start.tale", marked)).ToArray();
        Assert.Contains("(default)", Assert.Single(items, HasLabel("\"determiner\"")).GetProperty("detail").GetString());
        Assert.DoesNotContain("default", Assert.Single(items, HasLabel("\"independent\"")).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Implicit_text_types_follow_unsaved_assignments_and_report_compiler_conflicts()
    {
        await using var client = new Client();
        await client.Initialize();
        const string marked = "=== start\n{var:wea|ther}\n-> END";
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        await client.Open("other.tale", "=== other\n~ var:weather = \"stormy\"\n-> END", 1);
        var text = Assert.Single(Items(await Completion(client, "start.tale", marked)), HasLabel("weather"));
        Assert.Equal("Variable: text", text.GetProperty("detail").GetString());
        Assert.Contains("(text)", Text(await Hover(client, "start.tale", marked)));
        await client.Change("other.tale", "=== other\n~ var:weather = 1\n-> END", 2);
        var number = Assert.Single(Items(await Completion(client, "start.tale", marked)), HasLabel("weather"));
        Assert.Equal("Variable: integer", number.GetProperty("detail").GetString());
        await client.Change("other.tale", "=== other\n~ var:weather = 1\n~ var:weather = \"wrong\"\n-> END", 3);
        Assert.Contains(await client.Diagnostics("other.tale", 3),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW2003");
        await client.Change("other.tale", "=== other\n~ var:weather =", 4);
        var partial = Assert.Single(Items(await Completion(client, "start.tale", marked)), HasLabel("weather"));
        Assert.Equal("Variable: unresolved", partial.GetProperty("detail").GetString());
        await client.Change("other.tale", "=== other\n~ var:weather = \"recovered\"\n-> END", 5);
        Assert.Contains("(text)", Text(await Hover(client, "start.tale", marked)));
    }

    [Theory]
    [InlineData("~ player:h|p-=3", "hp", "~ player:hp-=3")]
    [InlineData("{player:pronoun#|}", "object", "{player:pronoun#object}")]
    [InlineData("{player:pronoun#possessive(|)}", "\"determiner\"", "{player:pronoun#possessive(\"determiner\")}")]
    [InlineData("~ player:hp|-=3", "hp", "~ player:hp-=3")]
    [InlineData("~ player:hp| - 3", "hp", "~ player:hp - 3")]
    [InlineData("~ player:hp -= player:h|p-", "hp", "~ player:hp -= player:hp-")]
    [InlineData("{faction:gu|ild:name}", "guild", "{faction:guild:name}")]
    [InlineData("{faction:gu|ild:tier}", "guild", "{faction:guild:tier}")]
    [InlineData("{faction:guild:na|me}", "name", "{faction:guild:name}")]
    [InlineData("{faction:guild:|}", "name", "{faction:guild:name}")]
    [InlineData("@requires va|r:coins > 0", "var:", "@requires var:coins > 0")]
    [InlineData("@requires vis|ited(start)", "visited(", "@requires visited(start)")]
    [InlineData("~ give player rope va|", "var:", "~ give player rope var:")]
    [InlineData("~ take player rope pla|", "player", "~ take player rope player")]
    public async Task Applying_protocol_text_edit_preserves_surrounding_syntax(string line, string label, string expected)
    {
        await using var client = new Client();
        File.AppendAllText(client.PathOf(StorySource.ManifestFileName), "factions: {guild: {name: Guild}}\n");
        await client.Initialize();
        var marked = "=== start\n" + line;
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        var item = Assert.Single(Items(await Completion(client, "start.tale", marked)), HasLabel(label));
        var edit = item.GetProperty("textEdit");
        var range = edit.GetProperty("range");
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");
        Assert.Equal(1, start.GetProperty("line").GetInt32());
        Assert.Equal(1, end.GetProperty("line").GetInt32());
        var original = line.Replace("|", "");
        Assert.Equal(expected, original[..start.GetProperty("character").GetInt32()] +
            edit.GetProperty("newText").GetString() + original[end.GetProperty("character").GetInt32()..]);
    }

    [Theory]
    [InlineData("inventory: [|]", "rope", "[rope]")]
    [InlineData("inventory: [ | ]", "rope", "[ rope ]")]
    [InlineData("disable: [|]", "hp", "[hp]")]
    [InlineData("disable: [  | ]", "hp", "[  hp ]")]
    public async Task Empty_manifest_lists_return_zero_width_edits_inside_brackets(string list, string label, string expected)
    {
        await using var client = new Client();
        await client.Initialize();
        var marked = list.StartsWith("inventory", StringComparison.Ordinal)
            ? Manifest.Replace("player: {first-name: Ada}", "player: {first-name: Ada, " + list + "}") + "items: {rope: Rope}\n"
            : Manifest + "ruleset: {" + list + "}\n";
        await client.Open(StorySource.ManifestFileName, marked.Replace("|", ""), 1);
        var item = Assert.Single(Items(await Completion(client, StorySource.ManifestFileName, marked)), HasLabel(label));
        var range = item.GetProperty("textEdit").GetProperty("range");
        Assert.Equal(range.GetProperty("start").ToString(), range.GetProperty("end").ToString());
        var prefix = marked[..marked.IndexOf('|')].Split('\n');
        var start = range.GetProperty("start");
        Assert.Equal(prefix.Length - 1, start.GetProperty("line").GetInt32());
        Assert.Equal(prefix[^1].Length, start.GetProperty("character").GetInt32());
        var line = marked.Replace("|", "").Split('\n')[prefix.Length - 1];
        var column = start.GetProperty("character").GetInt32();
        Assert.Contains(expected, line[..column] + item.GetProperty("textEdit").GetProperty("newText").GetString() + line[column..]);
    }

    [Fact]
    public async Task Completion_uses_latest_overlays_and_cross_file_add_change_delete()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", "=== start\n-> ", 1);
        const string marked = "=== start\n-> |";
        File.WriteAllText(client.PathOf("other.tale"), "=== second\n-> END");
        await client.Watch("other.tale", 1);
        Assert.Contains(Items(await Completion(client, "start.tale", marked)), HasLabel("second"));
        await client.Open("other.tale", "=== unsaved\n-> END", 1);
        var items = Items(await Completion(client, "start.tale", marked));
        Assert.Contains(items, HasLabel("unsaved"));
        Assert.DoesNotContain(items, HasLabel("second"));
        await client.Change("other.tale", "=== latest\n-> END", 3);
        await client.Change("other.tale", "=== stale\n-> END", 2);
        items = Items(await Completion(client, "start.tale", marked));
        Assert.Contains(items, HasLabel("latest"));
        Assert.DoesNotContain(items, HasLabel("stale"));
        File.WriteAllText(client.PathOf("other.tale"), "=== disk\n-> END");
        await client.Watch("other.tale", 2);
        Assert.Contains(Items(await Completion(client, "start.tale", marked)), HasLabel("latest"));
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf("other.tale") } });
        items = Items(await Completion(client, "start.tale", marked));
        Assert.Contains(items, HasLabel("disk"));
        Assert.DoesNotContain(items, HasLabel("latest"));
        File.Delete(client.PathOf("other.tale"));
        await client.Watch("other.tale", 3);
        Assert.DoesNotContain(Items(await Completion(client, "start.tale", marked)), HasLabel("disk"));
    }

    [Fact]
    public async Task Canonical_manifest_overlay_supplies_story_values_and_quoted_text_edits()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", "=== start\n-> END", 1);
        var marked = Manifest.Replace("start: start", "start: \"\\u0073ta|le\"");
        await client.Open("story.talewright.yaml", marked.Replace("|", ""), 1);
        var item = Assert.Single(Items(await Completion(client, "story.talewright.yaml", marked)), HasLabel("start"));
        var range = item.GetProperty("textEdit").GetProperty("range");
        Assert.Equal(8, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(18, range.GetProperty("end").GetProperty("character").GetInt32());
        await client.Change("story.talewright.yaml", Manifest + "flags: [overlay]\n", 2);
        await client.Change("start.tale", "=== start\n@requires flag:ove", 2);
        Assert.Contains(Items(await Completion(client, "start.tale", "=== start\n@requires flag:ove|")), HasLabel("overlay"));
        Assert.DoesNotContain("overlay", File.ReadAllText(client.PathOf("story.talewright.yaml")));
        await client.Open("story.yaml", "start: sta", 1);
        var legacy = await Completion(client, "story.yaml", "start: sta|");
        Assert.True(legacy.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    public async Task Missing_or_invalid_metadata_makes_completion_explicitly_unavailable_then_recovers(string? json)
    {
        await using var client = new Client();
        if (json is not null) File.WriteAllText(client.PathOf("project.authoring.json"), json);
        await client.Initialize(new { metadataPath = "project.authoring.json", requiresProjectMetadata = true });
        await client.Open("start.tale", "=== start\n-> sta", 1);
        var response = await Completion(client, "start.tale", "=== start\n-> sta|");
        Assert.Equal(-32001, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("unavailable", response.GetProperty("error").GetProperty("message").GetString());
        File.WriteAllText(client.PathOf("project.authoring.json"), AuthoringProjectMetadata.Export(CompilerOptions.Default).ToJson());
        await client.Watch("project.authoring.json", 1);
        Assert.Contains(Items(await Completion(client, "start.tale", "=== start\n-> sta|")), HasLabel("start"));
    }

    [Fact]
    public async Task Required_but_unconfigured_metadata_does_not_offer_builtins()
    {
        await using var client = new Client();
        await client.Initialize(new { requiresProjectMetadata = true });
        await client.Open("start.tale", "=== start\n@requires player:h", 1);
        var response = await Completion(client, "start.tale", "=== start\n@requires player:h|");
        Assert.Equal(-32001, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("Prose sta|")]
    [InlineData("// -> sta|")]
    [InlineData("@title sta|")]
    [InlineData("@requires \"sta|\"")]
    [InlineData("{unknown:h|")]
    public async Task Completion_in_irrelevant_contexts_is_empty(string marked)
    {
        await using var client = new Client();
        await client.Initialize();
        marked = "=== start\n" + marked;
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        Assert.Empty(Items(await Completion(client, "start.tale", marked)));
    }

    [Fact]
    public async Task Completion_replaces_full_token_after_utf16_prose_and_crlf()
    {
        await using var client = new Client();
        await client.Initialize();
        const string marked = "=== start\r\nA 🦑 {player:first-name#cap|italize}";
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        var item = Assert.Single(Items(await Completion(client, "start.tale", marked)), HasLabel("capitalize"));
        var range = item.GetProperty("textEdit").GetProperty("range");
        Assert.Equal(24, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(34, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(50, 0)]
    [InlineData(0, 50)]
    public async Task Completion_rejects_invalid_positions_without_disconnect(int line, int character)
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", Valid, 1);
        var response = await client.Request("textDocument/completion", new
        {
            textDocument = new { uri = client.UriOf("start.tale") }, position = new { line, character }
        });
        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(Items(await Completion(client, "start.tale", "=== start\nHello.|\n-> END\n")));
    }

    private static Predicate<JsonElement> HasLabel(string label) => item => item.GetProperty("label").GetString() == label;
    private static JsonElement[] Items(JsonElement response) => response.GetProperty("result").GetProperty("items").EnumerateArray().ToArray();
    private static Task<JsonElement> Completion(Client client, string file, string marked)
    {
        var prefix = marked[..marked.IndexOf('|')].ReplaceLineEndings("\n").Split('\n');
        return client.Request("textDocument/completion", new
        {
            textDocument = new { uri = client.UriOf(file) },
            position = new { line = prefix.Length - 1, character = prefix[^1].Length }
        });
    }
}
