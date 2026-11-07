using System.Text.Json;
using Talewright.Talemark.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    private const string ReferencesStart = "=== start\n{if flag:lit}Lit.{/if}\n-> next\n";
    private const string ReferencesNext = "=== next\n{if flag:lit}Again.{/if}\n-> END\n";

    private static Task<JsonElement> References(Client client, string name, int line, int character, bool includeDeclaration) =>
        client.Request("textDocument/references", new
        {
            textDocument = new { uri = client.UriOf(name) },
            position = new { line, character },
            context = new { includeDeclaration }
        });

    private static (string Uri, int Line, int Start, int End)[] Locations(JsonElement response) =>
        response.GetProperty("result").EnumerateArray().Select(location =>
        {
            var range = location.GetProperty("range");
            Assert.Equal(range.GetProperty("start").GetProperty("line").GetInt32(), range.GetProperty("end").GetProperty("line").GetInt32());
            return (location.GetProperty("uri").GetString()!, range.GetProperty("start").GetProperty("line").GetInt32(),
                range.GetProperty("start").GetProperty("character").GetInt32(), range.GetProperty("end").GetProperty("character").GetInt32());
        }).ToArray();

    [Fact]
    public async Task References_from_tale_and_manifest_documents_honor_include_declaration()
    {
        await using var client = new Client();
        File.AppendAllText(client.PathOf(StorySource.ManifestFileName), "flags: [lit]\n");
        File.WriteAllText(client.PathOf("start.tale"), ReferencesStart);
        File.WriteAllText(client.PathOf("next.tale"), ReferencesNext);
        var initialized = await client.Initialize();
        Assert.True(initialized.GetProperty("result").GetProperty("capabilities").GetProperty("referencesProvider").GetBoolean());
        await client.Open("start.tale", ReferencesStart, 1);

        var manifestLine = File.ReadAllLines(client.PathOf(StorySource.ManifestFileName)).Length - 1;
        var uses = new[] { (client.UriOf("next.tale"), 1, 9, 12), (client.UriOf("start.tale"), 1, 9, 12) };
        var declaration = (client.UriOf(StorySource.ManifestFileName), manifestLine, 8, 11);
        Assert.Equal(uses.OrderBy(use => use.Item1, StringComparer.Ordinal), Locations(await References(client, "start.tale", 1, 9, false)));
        Assert.Equal(uses.Append(declaration).OrderBy(use => use.Item1, StringComparer.Ordinal),
            Locations(await References(client, "start.tale", 1, 9, true)));

        await client.Open(StorySource.ManifestFileName, File.ReadAllText(client.PathOf(StorySource.ManifestFileName)), 1);
        Assert.Equal(uses.OrderBy(use => use.Item1, StringComparer.Ordinal),
            Locations(await References(client, StorySource.ManifestFileName, manifestLine, 9, false)));

        var scene = Locations(await References(client, "start.tale", 2, 4, true));
        Assert.Equal(new[] { (client.UriOf("next.tale"), 0, 4, 8), (client.UriOf("start.tale"), 2, 3, 7) }, scene);
        Assert.Empty(Locations(await References(client, "start.tale", 1, 15, true)));
    }

    [Fact]
    public async Task References_use_unsaved_text_from_other_open_documents()
    {
        await using var client = new Client();
        File.AppendAllText(client.PathOf(StorySource.ManifestFileName), "flags: [lit]\n");
        File.WriteAllText(client.PathOf("start.tale"), ReferencesStart);
        File.WriteAllText(client.PathOf("next.tale"), ReferencesNext);
        await client.Initialize();
        await client.Open("start.tale", ReferencesStart, 1);
        await client.Open("next.tale", ReferencesNext, 1);
        await client.Change("next.tale", "=== next\n\n{if flag:lit}Again.{/if} {if flag:lit}More.{/if}\n-> END\n", 2);

        Assert.Equal(new[]
            {
                (client.UriOf("next.tale"), 2, 9, 12),
                (client.UriOf("next.tale"), 2, 34, 37),
                (client.UriOf("start.tale"), 1, 9, 12)
            },
            Locations(await References(client, "start.tale", 1, 9, false)));
        Assert.Equal(ReferencesNext, File.ReadAllText(client.PathOf("next.tale")));
    }

    [Fact]
    public async Task References_never_cross_into_another_story_with_the_same_names()
    {
        var secondRoot = Path.Combine(Path.GetTempPath(), $"talewright-lsp-references-{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondRoot);
        try
        {
            File.WriteAllText(Path.Combine(secondRoot, StorySource.ManifestFileName), Manifest + "flags: [lit]\n");
            File.WriteAllText(Path.Combine(secondRoot, "start.tale"), ReferencesStart);
            File.WriteAllText(Path.Combine(secondRoot, "next.tale"), ReferencesNext);

            await using var client = new Client();
            File.AppendAllText(client.PathOf(StorySource.ManifestFileName), "flags: [lit]\n");
            File.WriteAllText(client.PathOf("start.tale"), ReferencesStart);
            File.WriteAllText(client.PathOf("next.tale"), ReferencesNext);
            var folders = new[]
            {
                new { uri = new Uri(client.Root).AbsoluteUri, name = "first" },
                new { uri = new Uri(secondRoot).AbsoluteUri, name = "second" }
            };
            await client.Initialize(includeRoot: false, workspaceFolders: folders);
            var secondStart = new Uri(Path.Combine(secondRoot, "start.tale")).AbsoluteUri;
            await client.Send("textDocument/didOpen", new
            {
                textDocument = new { uri = secondStart, languageId = "tale", version = 1, text = ReferencesStart }
            });
            await client.Open("start.tale", ReferencesStart, 1);

            var first = Locations(await References(client, "start.tale", 1, 9, true));
            Assert.Equal(3, first.Length);
            Assert.All(first, location => Assert.StartsWith(new Uri(client.Root).AbsoluteUri + "/", location.Uri));

            var second = Locations(await client.Request("textDocument/references", new
            {
                textDocument = new { uri = secondStart },
                position = new { line = 1, character = 9 },
                context = new { includeDeclaration = true }
            }));
            Assert.Equal(3, second.Length);
            Assert.All(second, location => Assert.StartsWith(new Uri(secondRoot).AbsoluteUri + "/", location.Uri));
        }
        finally
        {
            Directory.Delete(secondRoot, recursive: true);
        }
    }
}
