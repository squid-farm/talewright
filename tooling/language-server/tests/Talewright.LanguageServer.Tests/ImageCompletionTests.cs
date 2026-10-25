namespace Talewright.LanguageServer.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task Image_watch_events_refresh_completion_and_diagnostics_without_reopening_or_losing_overlays()
    {
        await using var client = new Client();
        await client.Initialize();
        const string marked = "=== start\r\n🦑 {image(\"maps/ca|mp.png\", \"Map\")}\r\n-> END";
        Directory.CreateDirectory(client.PathOf("images/maps"));
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        Assert.Contains(await client.Diagnostics("start.tale", 1), item => item.GetProperty("code").GetString() == "TW2012");
        Assert.Empty(Items(await Completion(client, "start.tale", marked)));

        // Catalogue entries require no decoding of bytes, in the editor or the shared resolver.
        File.WriteAllBytes(client.PathOf("images/maps/camp.png"), []);
        await client.Watch("images/maps/camp.png", 1);
        Assert.DoesNotContain(await client.Diagnostics("start.tale", 1), item => item.GetProperty("code").GetString() == "TW2012");
        var item = Assert.Single(Items(await Completion(client, "start.tale", marked)));
        Assert.Equal("maps/camp.png", item.GetProperty("label").GetString());
        Assert.Equal(17, item.GetProperty("kind").GetInt32());
        var edit = item.GetProperty("textEdit");
        Assert.Equal("maps/camp.png", edit.GetProperty("newText").GetString());
        Assert.Equal(1, edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, edit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(24, edit.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        File.Move(client.PathOf("images/maps/camp.png"), client.PathOf("images/maps/castle.webp"));
        await client.Send("workspace/didChangeWatchedFiles", new
        {
            changes = new[]
            {
                new { uri = client.UriOf("images/maps/camp.png"), type = 3 },
                new { uri = client.UriOf("images/maps/castle.webp"), type = 1 }
            }
        });
        Assert.Contains(await client.Diagnostics("start.tale", 1), diagnostic => diagnostic.GetProperty("code").GetString() == "TW2012");
        Assert.Equal("maps/castle.webp", Assert.Single(Items(await Completion(client, "start.tale", marked))).GetProperty("label").GetString());
        File.Delete(client.PathOf("images/maps/castle.webp"));
        await client.Watch("images/maps/castle.webp", 3);
        Assert.Contains(await client.Diagnostics("start.tale", 1), diagnostic => diagnostic.GetProperty("code").GetString() == "TW2012");
        Assert.Empty(Items(await Completion(client, "start.tale", marked)));
        Assert.Equal(Valid, File.ReadAllText(client.PathOf("start.tale")));
    }

    [Theory]
    [InlineData("""{image("maps/a\"|b.jpeg", "Literal {player:name}")}""", "maps/a\\\"b.jpeg")]
    [InlineData("""{image("maps/|""", "maps/a\\\"b.jpeg")]
    [InlineData("""* [Choose {image("maps/|""", "maps/a\\\"b.jpeg")]
    [InlineData("""> player: {image("maps/|", "Alt")}""", "maps/a\\\"b.jpeg")]
    public async Task Image_completions_escape_paths_and_support_incomplete_narrative_templates(string line, string expected)
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("images/maps"));
        File.WriteAllBytes(client.PathOf("images/maps/a\"b.jpeg"), []);
        await client.Initialize();
        var marked = "=== start\n" + line;
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        var item = Assert.Single(Items(await Completion(client, "start.tale", marked)));
        Assert.Equal(expected, item.GetProperty("textEdit").GetProperty("newText").GetString());
    }

    [Theory]
    [InlineData("""{image("maps/co|", "Alt")}""")]
    [InlineData("""{image("maps/Coast.webp", "maps/|")}""")]
    [InlineData("""@title {image("maps/|")}""")]
    public async Task Image_completions_do_not_leak_into_alt_titles_or_wrong_case(string line)
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("images/maps"));
        File.WriteAllBytes(client.PathOf("images/maps/Coast.webp"), []);
        await client.Initialize();
        var marked = "=== start\n" + line;
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        Assert.Empty(Items(await Completion(client, "start.tale", marked)));
    }

    [Fact]
    public async Task Independently_owned_child_story_uses_only_its_own_images()
    {
        await using var client = new Client();
        Directory.CreateDirectory(client.PathOf("images"));
        Directory.CreateDirectory(client.PathOf("child/images"));
        File.WriteAllText(client.PathOf("child/story.talewright.yaml"), Manifest);
        File.WriteAllText(client.PathOf("child/start.tale"), Valid);
        File.WriteAllBytes(client.PathOf("images/parent.png"), []);
        File.WriteAllBytes(client.PathOf("child/images/child.png"), []);
        await client.Initialize();
        const string marked = "=== start\n{image(\"|\", \"Alt\")}\n-> END";
        await client.Open("start.tale", marked.Replace("|", ""), 1);
        await client.Open("child/start.tale", marked.Replace("|", ""), 1);
        Assert.Equal("parent.png", Assert.Single(Items(await Completion(client, "start.tale", marked))).GetProperty("label").GetString());
        Assert.Equal("child.png", Assert.Single(Items(await Completion(client, "child/start.tale", marked))).GetProperty("label").GetString());
    }
}
