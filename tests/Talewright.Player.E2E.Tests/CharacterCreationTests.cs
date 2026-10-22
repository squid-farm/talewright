using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class CharacterCreationTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    private static StoryFixture Fixture() => TestStories.Linear with
    {
        Tale = TestStories.Linear.Tale.Replace("Mara's", "{player:first-name#possessive}"),
        Yaml = TestStories.DefaultYaml.Replace("id: player-test", $"id: creation-{Guid.NewGuid():N}") + """

            creation:
              name: true
              pronouns: [they, she]
              stats:
                str: [8, 12, 16]
              presets:
                explorer: {label: Explorer, stats: {str: 12}}
                scholar: {label: Scholar, stats: {str: 8}}
            """,
    };

    [Fact]
    public async Task Default_creation_plays_saves_replays_and_restarts_on_both_hosts()
    {
        await Upload(Fixture());
        await Driver.ClickAsync("Start story");
        await Observe(new(null, "Mara's"));
        await Choose("Walk onward");
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Load My save");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Restart");
        await Observe(new(null, "Mara's"));
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Load My save");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Mara's"));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Default_name_can_be_restored_without_resetting_other_choices()
    {
        await Upload(Fixture());
        await Expect(Page.GetByLabel("First name", new() { Exact = true })).ToHaveValueAsync("Mara");
        await Page.GetByLabel("First name", new() { Exact = true }).FillAsync("Robin");
        await Page.GetByLabel("Last name", new() { Exact = true }).FillAsync("Vale");
        await Page.GetByLabel("Pronouns", new() { Exact = true }).SelectOptionAsync("she");
        await Page.GetByLabel("Character preset").SelectOptionAsync("scholar");
        await Driver.ClickAsync("Use default name");
        await Expect(Page.GetByLabel("First name", new() { Exact = true })).ToHaveValueAsync("Mara");
        await Expect(Page.GetByLabel("Last name", new() { Exact = true })).ToHaveValueAsync("Burns");
        await Expect(Page.GetByLabel("Pronouns", new() { Exact = true })).ToHaveValueAsync("she");
        await Expect(Page.GetByLabel("Character preset")).ToHaveValueAsync("scholar");
        await Driver.ClickAsync("Start story");
        await Observe(new(null, "Mara's"));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Invalid_names_do_not_start_and_selected_identity_survives_reload()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Page.GetByLabel("First name", new() { Exact = true }).FillAsync("");
        await Driver.ClickAsync("Start story");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("A first name is required");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Current passage" })).ToHaveCountAsync(0);
        await Page.GetByLabel("First name", new() { Exact = true }).FillAsync("Robin");
        await Page.GetByLabel("Last name", new() { Exact = true }).FillAsync("Vale");
        await Page.GetByLabel("Pronouns", new() { Exact = true }).SelectOptionAsync("she");
        await Page.GetByLabel("Character preset").SelectOptionAsync("scholar");
        await Driver.ClickAsync("Start story");
        await Observe(new(null, "Robin's"));
        await AssertState(state => Assert.Contains("Robin Vale", state.Sheet));
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Load My save");
        await Page.ReloadAsync();
        await Upload(fixture);
        await Driver.ClickAsync("Start story");
        await Driver.ClickAsync("Saved games");
        await Driver.ClickAsync("Load My save");
        await Observe(new(null, "Robin's"));
        await Driver.ClickAsync("Restart");
        await Observe(new(null, "Robin's"));
    }
}
