using System.Text;
using Talewright.Testing;
using Xunit.Abstractions;

namespace Talewright.Player.E2E.Tests;

[Trait("Host", "Linux")]
public sealed class NativeTranscriptHistoryTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    public async Task History_pages_are_keyboard_readable_and_return_to_the_unchanged_native_playthrough()
    {
        var tale = new StringBuilder();
        for (var index = 0; index < 25; index++)
        {
            tale.AppendLine($"=== passage{index}");
            tale.AppendLine($"Native recorded visit {index}.");
            tale.AppendLine(index == 24 ? "* [Finish] -> END" : $"* [Next] -> passage{index + 1}");
        }
        var fixture = new StoryFixture("native-history",
            "game: {id: native-history, title: Native history, start: passage0, undo: true}\ncharacters: {player: {first-name: Tester}}\n",
            tale.ToString());
        await Upload(fixture);
        for (var index = 0; index < 24; index++)
        {
            await Observe(new(null, $"Native recorded visit {index}."));
            await Choose("Next");
        }
        await Driver.ClickAsync("History");
        var native = Assert.IsType<NativePlayerDriver>(Driver);
        var snapshot = await native.SnapshotAsync();
        Assert.Contains("Native recorded visit 24.", snapshot.Content);
        await Driver.ClickAsync("Earlier passages");
        snapshot = await native.SnapshotAsync();
        Assert.Contains("Native recorded visit 0.", snapshot.Content);
        Assert.DoesNotContain("Native recorded visit 24.", snapshot.Content);
        await Driver.ClickAsync("Return to play");
        await Observe(new(null, "Native recorded visit 24."));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Native recorded visit 23."));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, "Native recorded visit 24."));
    }
}
