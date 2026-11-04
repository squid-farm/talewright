using System.Text.Json;

namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task Trailing_reader_text_backslash_diagnostic_clears_after_an_edit()
    {
        await using var client = new Client();
        await client.Initialize();
        await client.Open("start.tale", "=== start\r\nHello\\\r\nNext\r\n-> END", 1);
        var diagnostic = Assert.Single(await client.Diagnostics("start.tale", 1));
        Assert.Equal("TW1017", diagnostic.GetProperty("code").GetString());
        var start = diagnostic.GetProperty("range").GetProperty("start");
        Assert.Equal(1, start.GetProperty("line").GetInt32());
        Assert.Equal(5, start.GetProperty("character").GetInt32());

        await client.Change("start.tale", "=== start\r\nHello\r\nNext\r\n-> END", 2);
        Assert.Empty(await client.Diagnostics("start.tale", 2));
        await client.Change("start.tale", "=== start\r\nHello\\\\\r\nNext\r\n-> END", 3);
        Assert.Empty(await client.Diagnostics("start.tale", 3));
    }

    [Fact]
    public async Task Literal_backticks_do_not_offer_syntax_hover()
    {
        await using var client = new Client();
        await client.Initialize();
        const string marked = "=== start\n|`ordinary text`\n-> END";
        await client.Open("start.tale", marked.Replace("|", ""), 1);

        Assert.Equal(JsonValueKind.Null, (await Hover(client, "start.tale", marked)).GetProperty("result").ValueKind);
    }
}
