using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Talewright.Runtime;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class SavePortabilityTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    private static StoryFixture Fixture() => TestStories.Linear with
    {
        Yaml = TestStories.DefaultManifest($"portable-{Guid.NewGuid():N}").Build(),
    };

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Downloaded_save_can_be_imported_without_changing_the_active_playthrough()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Driver.ClickAsync("Saves");
        await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync("Before the door");
        await Driver.ClickAsync("Save game");
        await Expect(SaveStatus).ToContainTextAsync("Saved 'Before the door'.");

        var download = await Page.RunAndWaitForDownloadAsync(() => Driver.ClickAsync("Export Before the door"));
        Assert.Equal("Player Test - Before the door.json", download.SuggestedFilename);
        await using var stream = await download.CreateReadStreamAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        Assert.Equal(await ReadStoredAsync(fixture.Compile().Id, "Before the door"), bytes);

        await Driver.ClickAsync("Close saves");
        await Choose("Walk onward");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Saves");
        await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync("Imported");
        await ImportAsync("backup.json", bytes);
        await Expect(SaveStatus).ToContainTextAsync("Imported 'Imported'.");
        await Driver.ClickAsync("Close saves");
        await Observe(new(null, "Second passage text."));

        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Imported");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "First passage"));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Invalid_imports_and_cancelled_overwrites_preserve_the_slot_and_playthrough()
    {
        var fixture = Fixture();
        var story = fixture.Compile();
        var advanced = new StoryHistory(story, 7);
        advanced.Choose("c0");
        var valid = advanced.Save();
        await Upload(fixture);
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Expect(SaveStatus).ToContainTextAsync("Saved 'My save'.");
        var original = await ReadStoredAsync(story.Id, "My save");

        var unsupported = JsonNode.Parse(valid)!;
        unsupported["formatVersion"] = 999;
        var incompatible = JsonNode.Parse(valid)!;
        incompatible["contentFingerprint"] = "different";
        var otherStory = JsonNode.Parse(valid)!;
        otherStory["storyId"] = "different";
        var otherVersion = JsonNode.Parse(valid)!;
        otherVersion["storyVersion"] = "different";
        var badReplay = JsonNode.Parse(valid)!;
        badReplay["choices"] = new JsonArray("not-a-choice");
        var invalid = new (byte[] Bytes, string Message)[]
        {
            ("{broken"u8.ToArray(), "malformed"),
            (Encoding.UTF8.GetBytes(unsupported.ToJsonString()), "unsupported format"),
            (Encoding.UTF8.GetBytes(incompatible.ToJsonString()), "incompatible"),
            (Encoding.UTF8.GetBytes(otherStory.ToJsonString()), "different story"),
            (Encoding.UTF8.GetBytes(otherVersion.ToJsonString()), "incompatible"),
            (Encoding.UTF8.GetBytes(badReplay.ToJsonString()), "cannot continue"),
            (new byte[StoryHistory.MaxSaveBytes + 1], "too large"),
        };
        foreach (var (bytes, message) in invalid)
        {
            await ImportAsync("invalid.json", bytes);
            await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync(message);
            Assert.Equal(original, await ReadStoredAsync(story.Id, "My save"));
            await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Confirm import" })).ToHaveCountAsync(0);
        }

        await ImportAsync("valid.json", valid);
        var confirmation = Page.GetByRole(AriaRole.Region, new() { Name = "Confirm import" });
        await Expect(confirmation).ToBeVisibleAsync();
        await Driver.ClickAsync("Keep existing save");
        Assert.Equal(original, await ReadStoredAsync(story.Id, "My save"));
        await ImportAsync("valid.json", valid);
        await Driver.ClickAsync("Replace with imported save");
        await Expect(SaveStatus).ToContainTextAsync("Imported 'My save'.");
        Assert.Equal(valid, await ReadStoredAsync(story.Id, "My save"));
        await Driver.ClickAsync("Close saves");
        await Observe(new(null, "First passage"));

        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Second passage text."));
    }

    [Fact]
    [Trait("Host", "Linux")]
    public async Task Desktop_import_uses_the_native_file_chooser_and_preserves_play_until_load()
    {
        var fixture = Fixture();
        var story = fixture.Compile();
        var advanced = new StoryHistory(story, 7);
        advanced.Choose("c0");
        await Upload(fixture);
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("Saves");
        var native = Assert.IsType<NativePlayerDriver>(Driver);
        await native.FillAsync("Save name", "Imported");
        await native.UploadSaveAsync("backup.json", advanced.Save());
        var timer = Stopwatch.StartNew();
        while (!(await native.SnapshotAsync()).Content.Contains("Imported 'Imported'.", StringComparison.Ordinal))
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "Imported save did not appear.");
            await Task.Delay(100);
        }
        await Driver.ClickAsync("Close saves");
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Imported");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Second passage text."));
    }

    private async Task ImportAsync(string name, byte[] bytes)
    {
        var input = Page.GetByLabel("Import save file", new() { Exact = true });
        var previous = await input.ElementHandleAsync();
        await input.SetInputFilesAsync(new FilePayload
        {
            Name = name,
            MimeType = "application/json",
            Buffer = bytes,
        });
        await Page.WaitForFunctionAsync("element => !element.isConnected", previous);
    }

    private async Task<byte[]> ReadStoredAsync(string storyId, string slot) =>
        (await Page.EvaluateAsync<int[]>("""
            async ({ storyId, slot }) => {
                const store = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
                return Array.from(await store.read(storyId, slot));
            }
            """, new { storyId, slot })).Select(Convert.ToByte).ToArray();
}
