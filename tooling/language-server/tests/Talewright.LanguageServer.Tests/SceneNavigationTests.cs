using System.Text.Json;
using Talewright.Cli;
using Talewright.Talemark.Compilation;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task Document_symbols_include_dotted_duplicate_ids_titles_and_exact_ranges()
    {
        await using var client = new Client();
        await client.Initialize();
        const string text = "Preamble text.\r\n  ===   camp.entrance\r\n@title The Arrival\r\nText.\r\n=== camp.entrance\r\n-> END";
        await client.Open("scenes.tale", text, 1);

        var response = await client.Request("textDocument/documentSymbol", new
        {
            textDocument = new { uri = client.UriOf("scenes.tale") }
        });
        var symbols = response.GetProperty("result").EnumerateArray().ToArray();

        Assert.Equal(2, symbols.Length);
        Assert.All(symbols, symbol => Assert.Equal("camp.entrance", symbol.GetProperty("name").GetString()));
        Assert.Equal("The Arrival", symbols[0].GetProperty("detail").GetString());
        Assert.Equal((1, 2), RangePosition(symbols[0], "range", "start"));
        Assert.Equal((4, 0), RangePosition(symbols[0], "range", "end"));
        Assert.Equal((1, 8), RangePosition(symbols[0], "selectionRange", "start"));
        Assert.Equal((1, 21), RangePosition(symbols[0], "selectionRange", "end"));
        Assert.False(symbols[1].TryGetProperty("detail", out _));
        Assert.Equal((4, 0), RangePosition(symbols[1], "range", "start"));
        Assert.Equal((5, 6), RangePosition(symbols[1], "range", "end"));
        Assert.Equal((4, 4), RangePosition(symbols[1], "selectionRange", "start"));
        Assert.Equal((4, 17), RangePosition(symbols[1], "selectionRange", "end"));

        var diagnostics = await client.Diagnostics("scenes.tale", 1);
        Assert.Contains(diagnostics, diagnostic => diagnostic.GetProperty("code").GetString() == "TW3010");
    }

    [Fact]
    public async Task Document_symbols_are_empty_for_a_file_without_scenes()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("empty.tale", "Preamble only.\n", 1);

        var response = await client.Request("textDocument/documentSymbol", new
        {
            textDocument = new { uri = client.UriOf("empty.tale") }
        });

        Assert.Empty(response.GetProperty("result").EnumerateArray());
    }

    [Fact]
    public async Task Workspace_symbols_search_ids_and_titles_across_workspace_stories()
    {
        var secondRoot = Path.Combine(Path.GetTempPath(), $"talewright-lsp-symbols-{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondRoot);
        try
        {
            File.WriteAllText(Path.Combine(secondRoot, StorySource.ManifestFileName), Manifest);
            File.WriteAllText(Path.Combine(secondRoot, "opening.tale"), "=== other.start\n@title Quiet Forest\n");

            await using var client = new Client();
            File.WriteAllText(client.PathOf("chapter.tale"), "=== garden.path\n@title Garden of Stars\n");
            var folders = new[]
            {
                new { uri = new Uri(client.Root).AbsoluteUri, name = "first" },
                new { uri = new Uri(secondRoot).AbsoluteUri, name = "second" }
            };
            await client.Initialize(includeRoot: false, workspaceFolders: folders);

            var byTitle = await client.Request("workspace/symbol", new { query = "garden of stars" });
            var garden = Assert.Single(byTitle.GetProperty("result").EnumerateArray());
            Assert.Equal("garden.path", garden.GetProperty("name").GetString());
            Assert.Equal("chapter.tale", garden.GetProperty("containerName").GetString());
            Assert.Equal(new Uri(client.PathOf("chapter.tale")).AbsoluteUri,
                garden.GetProperty("location").GetProperty("uri").GetString());

            var byId = await client.Request("workspace/symbol", new { query = "other.start" });
            var opening = Assert.Single(byId.GetProperty("result").EnumerateArray());
            Assert.Equal("other.start", opening.GetProperty("name").GetString());
            Assert.Equal("opening.tale", opening.GetProperty("containerName").GetString());
            Assert.Equal(new Uri(Path.Combine(secondRoot, "opening.tale")).AbsoluteUri,
                opening.GetProperty("location").GetProperty("uri").GetString());
        }
        finally
        {
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    private static (int Line, int Character) RangePosition(JsonElement symbol, string rangeName, string positionName)
    {
        var position = symbol.GetProperty(rangeName).GetProperty(positionName);
        return (position.GetProperty("line").GetInt32(), position.GetProperty("character").GetInt32());
    }
}
