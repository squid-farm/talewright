using Microsoft.Playwright;
using System.Text.Json.Nodes;
using Talewright.Runtime;
using Talewright.Tale.Compilation;
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
    public async Task Edited_save_name_flows_to_parent_and_requires_explicit_overwrite()
    {
        await Upload(Fixture());
        await Driver.ClickAsync("Saved games");
        await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync("Before the door");
        await Driver.ClickAsync("Save game");
        await Expect(Page.GetByRole(AriaRole.Status)).ToContainTextAsync("Saved 'Before the door'.");
        await Driver.ClickAsync("Save game");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Confirm overwrite", Exact = true })).ToBeVisibleAsync();
        await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync("After the door");
        await Page.GetByLabel("Save name", new() { Exact = true }).BlurAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Confirm overwrite", Exact = true })).ToHaveCountAsync(0);
        await Driver.ClickAsync("Save game");
        await Expect(Page.GetByRole(AriaRole.Status)).ToContainTextAsync("Saved 'After the door'.");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Load Before the door", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Load After the door", Exact = true })).ToBeVisibleAsync();
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

    [Theory]
    [Trait("Host", "Browser")]
    [InlineData("QuotaExceededError", "not enough browser storage")]
    [InlineData("SecurityError", "storage is blocked")]
    public async Task Browser_storage_failures_give_actionable_advice_and_preserve_play(string errorName, string expected)
    {
        await Upload(Fixture());
        await Page.EvaluateAsync("""
            name => Object.defineProperty(globalThis, 'indexedDB', {
                configurable: true,
                value: { open() { throw new DOMException('Internal storage detail', name); } }
            })
            """, errorName);
        await Driver.ClickAsync("Saved games");
        var alert = Page.GetByRole(AriaRole.Alert);
        await Expect(alert).ToContainTextAsync(expected);
        await Expect(alert).Not.ToContainTextAsync("Internal storage detail");
        await Expect(alert).Not.ToContainTextAsync("IndexedDbSaveStore.js:");
        await Choose("Walk onward");
        await Observe(new(null, "Second passage text."));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Choice_limit_is_visible_and_keeps_save_back_and_restart_usable()
    {
        var fixture = Fixture() with { Tale = "=== start\nAt the limit.\n* [Again] -> start" };
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.yaml"] = fixture.Yaml,
            ["chapters/main.tale"] = fixture.Tale,
        });
        var story = StoryCompiler.Compile(source).Story!;
        var saved = JsonNode.Parse(new StoryHistory(story).Save())!;
        saved["choices"] = new JsonArray(Enumerable.Range(0, StoryHistory.MaxChoices)
            .Select(_ => JsonValue.Create("c0")).ToArray<JsonNode?>());
        saved["cursor"] = StoryHistory.MaxChoices;
        await Upload(fixture);
        await Page.EvaluateAsync("""
            async ({storyId, json}) => {
                const module = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
                await module.write(storyId, 'Limit', new TextEncoder().encode(json), false);
            }
            """, new { storyId = story.Id, json = saved.ToJsonString() });
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Load Limit");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("choice limit");
        await Expect(Choices).ToBeDisabledAsync();
        await Driver.ClickAsync("Save game");
        await Expect(Page.GetByRole(AriaRole.Status)).ToContainTextAsync("Saved 'My save'");
        await Driver.ClickAsync("Back");
        await Expect(Choices).ToBeEnabledAsync();
        await Expect(Page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        await Driver.ClickAsync("Forward");
        await Expect(Choices).ToBeDisabledAsync();
        await Driver.ClickAsync("Restart");
        await Expect(Choices).ToBeEnabledAsync();
        await Expect(Page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        await Choose("Again");
        await Observe(new(null, "At the limit."));
    }
}
