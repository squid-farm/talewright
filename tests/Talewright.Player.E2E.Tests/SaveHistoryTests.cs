using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class SaveHistoryTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    private static StoryFixture Fixture() => TestStories.Linear with
    {
        Yaml = TestStories.DefaultYaml.Replace("id: player-test", $"id: save-{Guid.NewGuid():N}"),
    };

    [Fact]
    public async Task Manual_save_survives_host_reload_and_restores_history()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Choose("Walk onward");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Save game");
        // Opening the panel refreshes asynchronously; wait for the persisted slot control.
        await Driver.ClickAsync("Load My save");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Saved games");
        await Choose("Continue");
        await Observe(new(null, "Finished safely.", Ended: true));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Mara's"));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, "Second passage text."));

        if (PlayerTarget.IsDesktop)
        {
            await Driver.DisposeAsync();
            await Driver.StartAsync();
        }
        else
        {
            await Page.ReloadAsync();
        }

        await Upload(fixture);
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Load My save");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Mara's"));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, "Second passage text."));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Overwrite_requires_confirmation_and_corrupt_load_preserves_play()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Save game");
        await Expect(Page.GetByRole(AriaRole.Status)).ToContainTextAsync("Saved 'My save'.");
        await Choose("Walk onward");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Save game");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Confirm overwrite", Exact = true })).ToBeVisibleAsync();
        await Driver.ClickAsync("Cancel overwrite");
        await Driver.ClickAsync("Load My save");
        await Observe(new(null, "Mara's"));
        await Choose("Walk onward");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Confirm overwrite");
        await Expect(Page.GetByRole(AriaRole.Status)).ToContainTextAsync("Saved 'My save'.");

        await Page.EvaluateAsync("""
            async storyId => {
                const module = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
                await module.write(storyId, 'My save', new TextEncoder().encode('{broken'), true);
            }
            """, fixture.Yaml.Split('\n')[0]["id: ".Length..]);
        await Driver.ClickAsync("Load My save");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("malformed");
        await Observe(new(null, "Second passage text."));
        await Choose("Continue");
        await Observe(new(null, "Finished safely.", Ended: true));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Unavailable_storage_is_a_visible_failure_not_a_success()
    {
        await Upload(Fixture());
        await Page.EvaluateAsync("() => Object.defineProperty(globalThis, 'indexedDB', { configurable: true, value: undefined })");
        await Driver.ClickAsync("Saved games");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("IndexedDB is unavailable");
        await Observe(new(null, "Mara's"));
    }
}
