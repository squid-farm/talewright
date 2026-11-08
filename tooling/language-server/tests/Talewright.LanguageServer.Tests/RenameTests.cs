using System.Text.Json;
using Talewright.Talemark.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    private const string RenameStart = "=== start\n{if flag:lit}Lit.{/if} The next room waits.\n-> next\n";
    private const string RenameNext = "=== next\n{if player visited next}Again.{/if}\n-> END\n";

    private static readonly object RenameCapabilities = new
    {
        textDocument = new { rename = new { prepareSupport = true } },
        workspace = new { workspaceEdit = new { documentChanges = true, changeAnnotationSupport = new { groupsOnLabel = true } } }
    };

    private static Task<JsonElement> PrepareRename(Client client, string name, int line, int character) =>
        client.Request("textDocument/prepareRename", new
        {
            textDocument = new { uri = client.UriOf(name) },
            position = new { line, character }
        });

    private static Task<JsonElement> RenameAt(Client client, string name, int line, int character, string newName) =>
        client.Request("textDocument/rename", new
        {
            textDocument = new { uri = client.UriOf(name) },
            position = new { line, character },
            newName
        });

    private static int LineOf(string text, string prefix) =>
        Array.FindIndex(text.ReplaceLineEndings("\n").Split('\n'), line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static (string Uri, int? Version, (int Line, int Start, int End, string Text, string? Annotation)[] Edits)[]
        DocumentChanges(JsonElement response) =>
        response.GetProperty("result").GetProperty("documentChanges").EnumerateArray().Select(change =>
        {
            var document = change.GetProperty("textDocument");
            var version = document.GetProperty("version");
            return (document.GetProperty("uri").GetString()!,
                version.ValueKind == JsonValueKind.Null ? (int?)null : version.GetInt32(),
                change.GetProperty("edits").EnumerateArray().Select(edit =>
                {
                    var range = edit.GetProperty("range");
                    Assert.Equal(range.GetProperty("start").GetProperty("line").GetInt32(), range.GetProperty("end").GetProperty("line").GetInt32());
                    return (range.GetProperty("start").GetProperty("line").GetInt32(),
                        range.GetProperty("start").GetProperty("character").GetInt32(),
                        range.GetProperty("end").GetProperty("character").GetInt32(),
                        edit.GetProperty("newText").GetString()!,
                        edit.TryGetProperty("annotationId", out var annotation) ? annotation.GetString() : null);
                }).ToArray());
        }).ToArray();

    [Fact]
    public async Task Rename_capability_advertises_prepare_only_for_clients_that_support_it()
    {
        await using (var client = new Client())
        {
            var initialized = await client.Initialize(capabilities: RenameCapabilities);
            Assert.True(initialized.GetProperty("result").GetProperty("capabilities").GetProperty("renameProvider")
                .GetProperty("prepareProvider").GetBoolean());
        }
        await using (var client = new Client())
        {
            var initialized = await client.Initialize();
            Assert.True(initialized.GetProperty("result").GetProperty("capabilities").GetProperty("renameProvider").GetBoolean());
        }
    }

    [Fact]
    public async Task Cross_file_scene_rename_edits_unsaved_tale_and_manifest_documents_with_their_versions()
    {
        await using var client = new Client();
        File.AppendAllText(client.PathOf(StorySource.ManifestFileName), "flags: [lit]\n");
        File.WriteAllText(client.PathOf("start.tale"), RenameStart);
        File.WriteAllText(client.PathOf("next.tale"), RenameNext);
        await client.Initialize(capabilities: RenameCapabilities);
        var manifest = File.ReadAllText(client.PathOf(StorySource.ManifestFileName));
        await client.Open(StorySource.ManifestFileName, manifest, 4);
        await client.Open("start.tale", RenameStart, 1);
        var unsaved = "=== next\n\n{if player visited next}Again.{/if}\n-> END\n";
        await client.Open("next.tale", RenameNext, 1);
        await client.Change("next.tale", unsaved, 7);

        var prepared = (await PrepareRename(client, "start.tale", 2, 4)).GetProperty("result");
        Assert.Equal("next", prepared.GetProperty("placeholder").GetString());
        Assert.Equal(3, prepared.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(7, prepared.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        var response = await RenameAt(client, "start.tale", 2, 4, "hall");
        Assert.Equal(new[]
            {
                (client.UriOf("next.tale"), (int?)7, new[] { (0, 4, 8, "hall", (string?)"talewright.rename"), (2, 19, 23, "hall", "talewright.rename") }),
                (client.UriOf("start.tale"), 1, new[] { (2, 3, 7, "hall", (string?)"talewright.rename") }),
            },
            DocumentChanges(response).Select(change => (change.Uri, change.Version, change.Edits)).ToArray(),
            new RenameChangeComparer());
        var annotation = response.GetProperty("result").GetProperty("changeAnnotations").GetProperty("talewright.rename");
        Assert.False(annotation.GetProperty("needsConfirmation").GetBoolean());
        Assert.Contains("Saves are not migrated", annotation.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(RenameNext, File.ReadAllText(client.PathOf("next.tale")));

        var startLine = LineOf(manifest, "  start: ");
        Assert.True(startLine >= 0);
        var startScene = await RenameAt(client, StorySource.ManifestFileName, startLine, manifest.Split('\n')[startLine].Length - 1, "entry");
        var changes = DocumentChanges(startScene);
        var manifestChange = Assert.Single(changes, change => change.Uri == client.UriOf(StorySource.ManifestFileName));
        Assert.Equal(4, manifestChange.Version);
        Assert.Equal(startLine, Assert.Single(manifestChange.Edits).Line);
        Assert.Equal(0, Assert.Single(Assert.Single(changes, change => change.Uri == client.UriOf("start.tale")).Edits).Line);
    }

    private sealed class RenameChangeComparer : IEqualityComparer<(string, int?, (int, int, int, string, string?)[])>
    {
        public bool Equals((string, int?, (int, int, int, string, string?)[]) left, (string, int?, (int, int, int, string, string?)[]) right) =>
            left.Item1 == right.Item1 && left.Item2 == right.Item2 && left.Item3.SequenceEqual(right.Item3);

        public int GetHashCode((string, int?, (int, int, int, string, string?)[]) value) => value.Item1.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_uses_null_versions_for_closed_files_and_plain_changes_without_document_changes()
    {
        await using var client = new Client();
        File.AppendAllText(client.PathOf(StorySource.ManifestFileName), "flags: [lit]\n");
        File.WriteAllText(client.PathOf("start.tale"), RenameStart);
        File.WriteAllText(client.PathOf("next.tale"), RenameNext);
        await client.Initialize(capabilities: new { workspace = new { workspaceEdit = new { documentChanges = true } } });
        await client.Open("start.tale", RenameStart, 3);

        var response = await RenameAt(client, "start.tale", 1, 10, "glowing");
        var changes = DocumentChanges(response);
        Assert.Equal(new[] { (client.UriOf("start.tale"), (int?)3), (client.UriOf(StorySource.ManifestFileName), null) },
            changes.Select(change => (change.Uri, change.Version)).ToArray());
        Assert.All(changes.SelectMany(change => change.Edits), edit => Assert.Null(edit.Annotation));
        Assert.False(response.GetProperty("result").TryGetProperty("changeAnnotations", out _));

        await using var plain = new Client();
        File.AppendAllText(plain.PathOf(StorySource.ManifestFileName), "flags: [lit]\n");
        File.WriteAllText(plain.PathOf("start.tale"), RenameStart);
        File.WriteAllText(plain.PathOf("next.tale"), RenameNext);
        await plain.Initialize();
        await plain.Open("start.tale", RenameStart, 1);
        var plainResponse = await RenameAt(plain, "start.tale", 2, 4, "hall");
        Assert.True(plainResponse.TryGetProperty("result", out var plainResult), plainResponse.ToString());
        Assert.False(plainResult.TryGetProperty("documentChanges", out _));
        Assert.Equal(new[] { plain.UriOf("next.tale"), plain.UriOf("start.tale") },
            plainResult.GetProperty("changes").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, plainResult.GetProperty("changes").GetProperty(plain.UriOf("next.tale")).GetArrayLength());
    }

    [Fact]
    public async Task Prepare_rename_returns_null_off_names_and_explains_refusals()
    {
        await using var client = new Client();
        File.AppendAllText(client.PathOf(StorySource.ManifestFileName), "flags: [lit]\n");
        File.WriteAllText(client.PathOf("start.tale"), RenameStart);
        File.WriteAllText(client.PathOf("next.tale"), RenameNext);
        await client.Initialize(capabilities: RenameCapabilities);
        await client.Open("start.tale", RenameStart, 1);
        await client.Open("next.tale", RenameNext, 1);

        Assert.Equal(JsonValueKind.Null, (await PrepareRename(client, "start.tale", 1, 30)).GetProperty("result").ValueKind);
        var end = (await PrepareRename(client, "next.tale", 2, 4)).GetProperty("error");
        Assert.Equal(-32803, end.GetProperty("code").GetInt32());
        Assert.Contains("ends the story", end.GetProperty("message").GetString(), StringComparison.Ordinal);
        var player = (await PrepareRename(client, "next.tale", 1, 5)).GetProperty("error");
        Assert.Contains("player character", player.GetProperty("message").GetString(), StringComparison.Ordinal);

        var collision = (await RenameAt(client, "start.tale", 2, 4, "start")).GetProperty("error");
        Assert.Equal(-32803, collision.GetProperty("code").GetInt32());
        Assert.Contains("Scene 'start' already exists", collision.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_never_crosses_into_another_story_with_the_same_names()
    {
        var secondRoot = Path.Combine(Path.GetTempPath(), $"talewright-lsp-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondRoot);
        try
        {
            File.WriteAllText(Path.Combine(secondRoot, StorySource.ManifestFileName), Manifest + "flags: [lit]\n");
            File.WriteAllText(Path.Combine(secondRoot, "start.tale"), RenameStart);
            File.WriteAllText(Path.Combine(secondRoot, "next.tale"), RenameNext);

            await using var client = new Client();
            File.AppendAllText(client.PathOf(StorySource.ManifestFileName), "flags: [lit]\n");
            File.WriteAllText(client.PathOf("start.tale"), RenameStart);
            File.WriteAllText(client.PathOf("next.tale"), RenameNext);
            var folders = new[]
            {
                new { uri = new Uri(client.Root).AbsoluteUri, name = "first" },
                new { uri = new Uri(secondRoot).AbsoluteUri, name = "second" }
            };
            await client.Initialize(includeRoot: false, workspaceFolders: folders, capabilities: RenameCapabilities);
            await client.Send("textDocument/didOpen", new
            {
                textDocument = new { uri = new Uri(Path.Combine(secondRoot, "start.tale")).AbsoluteUri, languageId = "tale", version = 1, text = RenameStart }
            });
            await client.Open("start.tale", RenameStart, 1);

            var changes = DocumentChanges(await RenameAt(client, "start.tale", 1, 10, "glowing"));
            Assert.Equal(2, changes.Length);
            Assert.All(changes, change => Assert.StartsWith(new Uri(client.Root).AbsoluteUri + "/", change.Uri));
        }
        finally
        {
            Directory.Delete(secondRoot, recursive: true);
        }
    }
}
