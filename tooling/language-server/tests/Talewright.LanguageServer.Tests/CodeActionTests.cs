using System.Text.Json;
using Talewright.Talemark.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task Missing_flag_action_edits_the_unsaved_manifest_with_its_own_version_and_range()
    {
        await using var client = new Client();
        await client.Initialize(capabilities: new
        {
            workspace = new { workspaceEdit = new { documentChanges = true } }
        });
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\nflags: [ready]\ncharacters:\n  player: {first-name: Ada}\n";
        await client.Open(StorySource.ManifestFileName, manifest, 7);
        await client.Diagnostics(StorySource.ManifestFileName, 7);

        const string text = "=== start\n{if flag:missing}Never.{/if}\n-> END\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2004");
        var actions = await CodeActions(client, text, [diagnostic]);

        var action = Assert.Single(actions);
        Assert.Equal("Declare flag 'missing'", action.GetProperty("title").GetString());
        Assert.Equal("TW2004", action.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        var documentEdit = action.GetProperty("edit").GetProperty("documentChanges")[0];
        Assert.Equal(client.UriOf(StorySource.ManifestFileName),
            documentEdit.GetProperty("textDocument").GetProperty("uri").GetString());
        Assert.Equal(7, documentEdit.GetProperty("textDocument").GetProperty("version").GetInt32());
        var edit = documentEdit.GetProperty("edits")[0];
        Assert.Equal(", missing", edit.GetProperty("newText").GetString());
        Assert.Equal(4, edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(13, edit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task Unsafe_manifest_shape_returns_a_disabled_flag_action_with_a_reason()
    {
        await using var client = new Client();
        await client.Initialize();
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\nflags: not-a-list\ncharacters:\n  player: {first-name: Ada}\n";
        await client.Open(StorySource.ManifestFileName, manifest, 2);
        await client.Diagnostics(StorySource.ManifestFileName, 2);

        const string text = "=== start\n{if flag:missing}Never.{/if}\n-> END\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2004");
        var actions = await CodeActions(client, text, [diagnostic]);

        var action = Assert.Single(actions);
        Assert.Equal("Declare flag 'missing'", action.GetProperty("title").GetString());
        Assert.Contains("not a YAML list", action.GetProperty("disabled").GetProperty("reason").GetString());
        Assert.False(action.TryGetProperty("edit", out _));
    }

    [Fact]
    public async Task Missing_variable_actions_offer_both_types_when_usage_does_not_decide()
    {
        await using var client = new Client();
        await client.Initialize(capabilities: new
        {
            workspace = new { workspaceEdit = new { documentChanges = true } }
        });
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\nvars: {gold: 1}\ncharacters:\n  player: {first-name: Ada}\n";
        await client.Open(StorySource.ManifestFileName, manifest, 3);
        await client.Diagnostics(StorySource.ManifestFileName, 3);

        const string text = "=== start\nThe weather is {var:weather}.\n-> END\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2004");
        var actions = await CodeActions(client, text, [diagnostic]);

        Assert.Equal(
            ["Declare variable 'weather' as a number", "Declare variable 'weather' as text"],
            actions.Select(action => action.GetProperty("title").GetString()));
        Assert.Equal(
            [", weather: 0", ", weather: \"\""],
            actions.Select(action => action.GetProperty("edit").GetProperty("documentChanges")[0]
                .GetProperty("edits")[0].GetProperty("newText").GetString()));
        Assert.All(actions, action => Assert.Equal(3, action.GetProperty("edit").GetProperty("documentChanges")[0]
            .GetProperty("textDocument").GetProperty("version").GetInt32()));
    }

    [Fact]
    public async Task Unsafe_manifest_shape_returns_a_disabled_variable_action_with_a_reason()
    {
        await using var client = new Client();
        await client.Initialize();
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\nvars: [gold]\ncharacters:\n  player: {first-name: Ada}\n";
        await client.Open(StorySource.ManifestFileName, manifest, 2);
        await client.Diagnostics(StorySource.ManifestFileName, 2);

        const string text = "=== start\n{if var:debt > 1}Owed.{/if}\n-> END\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2004");
        var actions = await CodeActions(client, text, [diagnostic]);

        var action = Assert.Single(actions);
        Assert.Equal("Declare variable 'debt'", action.GetProperty("title").GetString());
        Assert.Contains("not a YAML mapping", action.GetProperty("disabled").GetProperty("reason").GetString());
        Assert.False(action.TryGetProperty("edit", out _));
    }

    [Fact]
    public async Task Missing_character_action_edits_the_unsaved_manifest_without_offering_for_properties()
    {
        await using var client = new Client();
        await client.Initialize(capabilities: new
        {
            workspace = new { workspaceEdit = new { documentChanges = true } }
        });
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player: {first-name: Ada}\n";
        var diskManifest = File.ReadAllText(client.PathOf(StorySource.ManifestFileName));
        await client.Open(StorySource.ManifestFileName, manifest, 7);
        await client.Diagnostics(StorySource.ManifestFileName, 7);

        const string text = "=== start\n> old-man: Hello.\n{player:unknown-property}\n-> END\n";
        await client.Open("start.tale", text, 1);
        var diagnostics = await client.Diagnostics("start.tale", 1);
        var diagnostic = Assert.Single(diagnostics, item => item.GetProperty("code").GetString() == "TW2001" &&
            item.GetProperty("message").GetString()!.Contains("character 'old-man'", StringComparison.Ordinal));

        var actions = await CodeActions(client, text, diagnostics);

        var action = Assert.Single(actions);
        Assert.Equal("Declare character 'old-man'", action.GetProperty("title").GetString());
        var documentEdit = action.GetProperty("edit").GetProperty("documentChanges")[0];
        Assert.Equal(client.UriOf(StorySource.ManifestFileName),
            documentEdit.GetProperty("textDocument").GetProperty("uri").GetString());
        Assert.Equal(7, documentEdit.GetProperty("textDocument").GetProperty("version").GetInt32());
        Assert.Equal("  old-man:\n    first-name: Old Man\n",
            documentEdit.GetProperty("edits")[0].GetProperty("newText").GetString());
        Assert.Equal(diskManifest, File.ReadAllText(client.PathOf(StorySource.ManifestFileName)));
    }

    [Fact]
    public async Task Unsupported_manifest_mapping_returns_a_disabled_name_action_with_a_reason()
    {
        await using var client = new Client();
        await client.Initialize();
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters: []\n";
        await client.Open(StorySource.ManifestFileName, manifest, 2);
        await client.Diagnostics(StorySource.ManifestFileName, 2);

        const string text = "=== start\n> old-man: Hello.\n-> END\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2001");

        var action = Assert.Single(await CodeActions(client, text, [diagnostic]));

        Assert.Equal("Declare character 'old-man'", action.GetProperty("title").GetString());
        Assert.Contains("not a YAML mapping", action.GetProperty("disabled").GetProperty("reason").GetString());
        Assert.False(action.TryGetProperty("edit", out _));
    }

    [Fact]
    public async Task Unsafe_manifest_insertion_returns_a_disabled_name_action_with_a_reason()
    {
        await using var client = new Client();
        await client.Initialize();
        const string manifest = "game:\n  id: quick-fix\n  title: Quick Fix\n  start: start\ncharacters:\n  player:\n    first-name: Ada\n    last-name: |\n      Existing text\nitems:\n  rope: Rope\n";
        await client.Open(StorySource.ManifestFileName, manifest, 2);
        await client.Diagnostics(StorySource.ManifestFileName, 2);

        const string text = "=== start\n> old-man: Hello.\n-> END\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2001");

        var action = Assert.Single(await CodeActions(client, text, [diagnostic]));

        Assert.Equal("Declare character 'old-man'", action.GetProperty("title").GetString());
        Assert.Contains("could not be verified to preserve existing manifest data",
            action.GetProperty("disabled").GetProperty("reason").GetString());
        Assert.False(action.TryGetProperty("edit", out _));
    }

    [Fact]
    public async Task Missing_scene_code_actions_append_or_create_a_file_without_overwriting()
    {
        await using var client = new Client();
        var initialized = await client.Initialize(capabilities: new
        {
            workspace = new
            {
                workspaceEdit = new
                {
                    documentChanges = true,
                    resourceOperations = new[] { "create" }
                }
            }
        });
        var capabilities = initialized.GetProperty("result").GetProperty("capabilities");
        Assert.Contains(capabilities.GetProperty("codeActionProvider").GetProperty("codeActionKinds")
            .EnumerateArray(), kind => kind.GetString() == "quickfix");

        const string text = "=== start\n* [Go] -> missing.scene\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2002");

        var actions = await CodeActions(client, text, [diagnostic]);

        Assert.Equal(2, actions.Length);
        var append = Assert.Single(actions, action =>
            action.GetProperty("title").GetString() == "Create scene 'missing.scene' in this file");
        Assert.Equal("quickfix", append.GetProperty("kind").GetString());
        Assert.Equal("TW2002", append.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        var appendChange = append.GetProperty("edit").GetProperty("documentChanges")[0];
        Assert.Equal(1, appendChange.GetProperty("textDocument").GetProperty("version").GetInt32());
        var appendEdit = appendChange.GetProperty("edits")[0];
        Assert.Equal("\n=== missing.scene\n", appendEdit.GetProperty("newText").GetString());
        Assert.Equal(2, appendEdit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, appendEdit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());

        var create = Assert.Single(actions, action =>
            action.GetProperty("title").GetString() == "Create scene 'missing.scene' in a new file");
        var createChanges = create.GetProperty("edit").GetProperty("documentChanges");
        Assert.Equal(2, createChanges.GetArrayLength());
        Assert.Equal("create", createChanges[0].GetProperty("kind").GetString());
        Assert.False(createChanges[0].GetProperty("options").GetProperty("overwrite").GetBoolean());
        Assert.False(createChanges[0].GetProperty("options").GetProperty("ignoreIfExists").GetBoolean());
        Assert.Equal(client.UriOf("missing.scene.tale"), createChanges[0].GetProperty("uri").GetString());
        Assert.Equal("=== missing.scene\n", createChanges[1].GetProperty("edits")[0].GetProperty("newText").GetString());
        Assert.Equal("vscode.open", create.GetProperty("command").GetProperty("command").GetString());
        Assert.Equal(client.UriOf("missing.scene.tale"), create.GetProperty("command").GetProperty("arguments")[0].GetString());
    }

    [Fact]
    public async Task Code_actions_are_limited_to_missing_scene_diagnostics_in_the_requested_range()
    {
        await using var client = new Client();
        await client.Initialize();
        const string text = "=== start\n* -> missing\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2002");

        var response = await client.Request("textDocument/codeAction", new
        {
            textDocument = new { uri = client.UriOf("start.tale") },
            range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } },
            context = new { diagnostics = new[] { diagnostic } }
        });

        Assert.Empty(response.GetProperty("result").EnumerateArray());
    }

    [Fact]
    public async Task Existing_adjacent_scene_file_suppresses_the_new_file_action()
    {
        await using var client = new Client();
        File.WriteAllText(client.PathOf("missing.tale"), "=== another\n-> END\n");
        await client.Initialize();
        const string text = "=== start\n* -> missing\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2002");

        var actions = await CodeActions(client, text, [diagnostic]);

        Assert.Single(actions);
        Assert.Contains("in this file", actions[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Clients_without_resource_operations_get_only_a_text_edit()
    {
        await using var client = new Client();
        await client.Initialize();
        const string text = "=== start\n* -> missing\n";
        await client.Open("start.tale", text, 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1),
            item => item.GetProperty("code").GetString() == "TW2002");

        var actions = await CodeActions(client, text, [diagnostic]);

        var action = Assert.Single(actions);
        Assert.Contains("in this file", action.GetProperty("title").GetString());
        Assert.True(action.GetProperty("edit").TryGetProperty("changes", out _));
    }

    private static async Task<JsonElement[]> CodeActions(Client client, string text, JsonElement[] diagnostics)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var response = await client.Request("textDocument/codeAction", new
        {
            textDocument = new { uri = client.UriOf("start.tale") },
            range = new
            {
                start = new { line = 0, character = 0 },
                end = new { line = lines.Length - 1, character = lines[^1].Length }
            },
            context = new { diagnostics }
        });
        return response.GetProperty("result").EnumerateArray().ToArray();
    }
}
