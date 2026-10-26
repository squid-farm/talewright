using System.Text.Json;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    private const string GuideFragment = "characters/guide.talewright.yaml";
    private const string GuideDeclaration = "characters:\n  guide: {first-name: Bea}\n";
    private const string GuideTale = "=== start\n> guide: Hi.\n-> END\n";

    [Fact]
    public async Task Unsaved_supporting_configuration_drives_diagnostics_and_definition()
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("characters"));
        var initialized = await client.Initialize();
        Assert.True(initialized.GetProperty("result").GetProperty("capabilities").GetProperty("definitionProvider").GetBoolean());
        await client.Open("start.tale", GuideTale, 1);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));

        await client.Open(GuideFragment, GuideDeclaration, 1);
        Assert.Empty(await client.Diagnostics("start.tale", 1));
        Assert.Empty(await client.Diagnostics(GuideFragment, 1));
        var definition = (await Definition(client, "start.tale", 1, 3)).GetProperty("result");
        Assert.Equal(client.UriOf(GuideFragment), definition.GetProperty("uri").GetString());
        Assert.Equal(1, definition.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(2, definition.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        var self = (await Definition(client, GuideFragment, 1, 4)).GetProperty("result");
        Assert.Equal(client.UriOf(GuideFragment), self.GetProperty("uri").GetString());

        await client.Change(GuideFragment, "game:\n  id: nested\n" + GuideDeclaration, 2);
        var placement = Assert.Single(await client.Diagnostics(GuideFragment, 2));
        Assert.Equal("TW4002", placement.GetProperty("code").GetString());
        Assert.Empty(await client.Diagnostics("start.tale", 1));
        await client.Change(GuideFragment, GuideDeclaration, 3);
        Assert.Empty(await client.Diagnostics(GuideFragment, 3));
        Assert.Empty(await client.Diagnostics("start.tale", 1));

        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf(GuideFragment) } });
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        Assert.Equal(JsonValueKind.Null, (await Definition(client, "start.tale", 1, 3)).GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Watched_supporting_configuration_add_rename_delete_clears_stale_diagnostics()
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("characters"));
        await client.Initialize();
        await client.Open("start.tale", GuideTale, 1);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));

        File.WriteAllText(client.PathOf(GuideFragment), GuideDeclaration + "  player: {first-name: Twin}\n");
        await client.Watch(GuideFragment, 1);
        Assert.Contains(await client.Diagnostics(GuideFragment, null),
            diagnostic => diagnostic.GetProperty("code").GetString() == "TW4007");
        Assert.Empty(await client.Diagnostics("start.tale", 1));

        const string renamed = "characters/mentor.talewright.yaml";
        File.Move(client.PathOf(GuideFragment), client.PathOf(renamed));
        await client.Send("workspace/didChangeWatchedFiles", new
        {
            changes = new[] { new { uri = client.UriOf(GuideFragment), type = 3 }, new { uri = client.UriOf(renamed), type = 1 } }
        });
        Assert.Empty(await client.Diagnostics(GuideFragment, null));
        Assert.NotEmpty(await client.Diagnostics(renamed, null));
        Assert.Empty(await client.Diagnostics("start.tale", 1));

        File.Delete(client.PathOf(renamed));
        await client.Watch(renamed, 3);
        Assert.Empty(await client.Diagnostics(renamed, null));
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
    }

    [Fact]
    public async Task Reserved_manifest_overlay_is_rejected_and_cannot_supply_declarations()
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("characters"));
        await client.Initialize();
        await client.Open("start.tale", GuideTale, 1);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        const string reserved = "characters/story.talewright.yaml";
        await client.Open(reserved, GuideDeclaration, 1);
        var error = Assert.Single(await client.Diagnostics(reserved, 1));
        Assert.Equal("TW4002", error.GetProperty("code").GetString());
        Assert.Contains("reserved", error.GetProperty("message").GetString());
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        Assert.Equal(JsonValueKind.Null, (await Definition(client, "start.tale", 1, 3)).GetProperty("result").ValueKind);

        await client.Change(reserved, "game: { id: invalid }\n" + GuideDeclaration, 2);
        Assert.Single(await client.Diagnostics(reserved, 2));
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        await client.Send("textDocument/didClose", new { textDocument = new { uri = client.UriOf(reserved) } });
        Assert.Empty(await client.Diagnostics(reserved, null));
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        await client.Open(GuideFragment, GuideDeclaration, 1);
        Assert.Empty(await client.Diagnostics("start.tale", 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Introducing_parent_manifest_reclaims_existing_child_scope(bool saved)
    {
        await using var client = new Client();
        File.Delete(client.PathOf("story.talewright.yaml"));
        Directory.CreateDirectory(client.PathOf("characters"));
        const string childManifest = "characters/story.talewright.yaml";
        File.WriteAllText(client.PathOf(childManifest), Manifest.Replace("start: start", "start: child"));
        await client.Initialize();
        await client.Open(childManifest, Manifest.Replace("start: start", "start: child"), 1);
        await client.Diagnostics(childManifest, 1);
        await client.Open("characters/main.tale", "=== child\n-> END\n", 1);
        Assert.Empty(await client.Diagnostics("characters/main.tale", 1));
        Assert.Empty(await client.Diagnostics(childManifest, 1));

        if (saved)
        {
            File.WriteAllText(client.PathOf("story.talewright.yaml"), Manifest);
            await client.Watch("story.talewright.yaml", 1);
        }
        else await client.Open("story.talewright.yaml", Manifest, 1);
        var error = Assert.Single(await client.Diagnostics(childManifest, 1));
        Assert.Equal("TW4002", error.GetProperty("code").GetString());
        Assert.Contains("reserved", error.GetProperty("message").GetString());
        Assert.Empty(await client.Diagnostics("characters/main.tale", 1));
        await client.Change("characters/main.tale", "=== child\n-> start\n", 2);
        Assert.Empty(await client.Diagnostics("characters/main.tale", 2));
    }

    [Fact]
    public async Task Unrelated_yaml_is_not_a_story_document()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("notes.yaml", "characters:\n  guide: {first-name: Bea}\n", 1);
        await client.Open("start.tale", GuideTale, 1);
        Assert.NotEmpty(await client.Diagnostics("start.tale", 1));
        var response = await Definition(client, "notes.yaml", 1, 3);
        Assert.Equal(-32602, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    private static Task<JsonElement> Definition(Client client, string file, int line, int character) =>
        client.Request("textDocument/definition", new
        {
            textDocument = new { uri = client.UriOf(file) },
            position = new { line, character }
        });
}
