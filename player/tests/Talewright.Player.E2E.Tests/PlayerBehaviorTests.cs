using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class PlayerBehaviorTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    public static IEnumerable<object[]> Scenarios => TestStories.Scenarios.Select(s => new object[] { s.Id });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Explicit_story_scenario(string id)
    {
        var scenario = TestStories.Scenarios.Single(s => s.Id == id);
        await Upload(TestStories.Get(scenario.Fixture));
        foreach (var step in scenario.Steps)
        {
            if (step.Choose is not null)
            {
                await Choose(step.Choose);
            }

            await Observe(step);
        }
    }

    [Fact]
    public async Task Ordered_scene_text_and_inline_notes_survive_save_load_and_navigation()
    {
        await Upload(new StoryFixture("ordered-scenes", TestStories.DefaultYaml, """
            === start
            Before {var:visits}.
            {if not flag:ready}
            ~ set flag:ready; give player rope
            ~ var:visits += 1
            {/if}
            After {var:visits}.
            * [Go] -> next
            === next
            ~ var:visits += 1
            Second {var:visits}.
            * [Return] -> start
            """));
        await AssertState(state =>
        {
            var before = state.Passage.IndexOf("Before 0.", StringComparison.Ordinal);
            var note = state.Passage.IndexOf("Gained: Test rope", StringComparison.Ordinal);
            var after = state.Passage.IndexOf("After 1.", StringComparison.Ordinal);
            Assert.True(before >= 0 && before < note && note < after, state.Passage);
        });
        await Choose("Go");
        await Observe(new(null, "Second 2."));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Close saves");
        await Choose("Return");
        await Observe(new(null, "Before 2."));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Second 2."));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, "After 2."));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Second 2."));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Before 0."));
        await AssertState(state => Assert.Contains("After 1.", state.Passage));
    }

    [Fact]
    public async Task Bundled_sample_opens_without_a_sample_specific_route()
    {
        await Driver.OpenBundledAsync();
        await Driver.ClickAsync("Start story");
        await AssertState(state =>
        {
            Assert.True(state.PassageVisible);
            Assert.NotEmpty(state.Passage);
            Assert.NotEmpty(state.Sheet);
        });
    }

    [Fact]
    public async Task Bundled_sample_progresses_from_camp_to_night_watch_and_preserves_save_navigation()
    {
        await Driver.OpenBundledAsync();
        await Driver.ClickAsync("Start story");
        await Choose("Continue");
        await Choose("Haggle for supplies");
        await Observe(new(null, "Two trail rations"));
        await Choose("Sign the expedition register");
        await Observe(new(null, "MARA BURNS"));
        await Choose("Continue");
        await Choose("Set out for the temple");
        await Choose("Tie off the rope and cross carefully");
        await Choose("Make camp before the climb");
        await AssertState(state => Assert.Equal(["Press on at dawn", "Eat a ration"], state.Choices));
        await Choose("Eat a ration");
        await Choose("Share supper by the fire");
        await Observe(new(null, "one ration left"));
        await Choose("Continue");
        await Choose("Stand your ground");
        await Observe(new(null, "Your Courage is 10"));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Close saves");
        await Choose("Catch your breath");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Your Courage is 10"));
        await Driver.ClickAsync("Back");
        await AssertState(state => Assert.Equal(["Press on at dawn", "Stand your ground"], state.Choices));
        await Driver.ClickAsync("Forward");
        await Choose("Catch your breath");
        await Choose("Follow the signal");
        await Choose("Continue");
        await Observe(new(null, "The ledger cannot answer for you"));
        await Choose("Continue");
        await Choose("Check the tripwire bell");
        await Choose("Ring the bell");
        await Choose("Silence the bell");
        await Choose("Ring the bell");
        await Observe(new(null, "The watch bell has sounded 2 times."));
        await Choose("Return to the fire");
        await AssertState(state => Assert.Equal(["Press on at dawn"], state.Choices));
        await Choose("Press on at dawn");
        await Observe(new(null, "Three glyphs"));
        await Choose("Look for another way in");
        await Choose("Squeeze through");
        await Choose("Continue");
        if ((await Driver.ObserveAsync()).Choices.Contains("Take the idol"))
            await Choose("Take the idol");
        await Choose("Take the relic");
        await Choose("Keep it for yourself");
        await Observe(new(null, "The guild's letters stop coming", Ended: true));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Bundled_sample_assets_work_when_served_at_the_selected_base_path()
    {
        await Page.Locator(".tw-story-list").GetByRole(AriaRole.Button).First.ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Start story", Exact = true }).ClickAsync();
        await Expect(Latest).ToBeVisibleAsync();
        Assert.NotEmpty(await Latest.InnerTextAsync());
        await Expect(Sheet).ToBeVisibleAsync();
        await Expect(Page.Locator(".tw-columns")).ToHaveCSSAsync("display", "grid");
        await Choices.First.ClickAsync();
        await Expect(Page.Locator(".tw-past")).ToHaveCountAsync(1);
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Transcript_formats_prose_and_preserves_previous_frames()
    {
        await Upload(TestStories.Linear);
        await Expect(Latest.Locator("strong").First).ToHaveTextAsync("bold");
        await Expect(Latest.Locator("em").First).ToHaveTextAsync("gentle");
        await Expect(Latest.Locator("code")).ToHaveTextAsync("code");
        await Expect(Latest.Locator("strong em")).ToHaveTextAsync("combined");
        await Expect(Latest.Locator("br")).ToHaveCountAsync(1);
        await Expect(Latest.Locator("img")).ToBeHiddenAsync();
        await Expect(Latest.GetByRole(AriaRole.Img, new() { Name = "Reserved artwork", Exact = true })).ToBeVisibleAsync();
        await Expect(Latest.GetByRole(AriaRole.Status)).ToContainTextAsync("This image could not be displayed.");
        await Expect(Latest.Locator(".tw-speaker")).ToHaveTextAsync("Tomas");
        await Choose("Walk onward");
        await Expect(Page.Locator(".tw-past")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".tw-past .tw-chosen")).ToContainTextAsync("Walk onward");
        await Expect(Page.Locator(".tw-past button")).ToHaveCountAsync(0);
        await Choose("Continue");
        await Expect(Page.Locator(".tw-past")).ToHaveCountAsync(2);
        await Expect(Choices).ToHaveCountAsync(0);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Play again", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".tw-past")).ToHaveCountAsync(0);
        await Expect(Latest).ToContainTextAsync("First passage");
        await Expect(Choices).ToHaveCountAsync(2);
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Transcript_render_boundaries_preserve_formatted_replay_and_forked_choices()
    {
        await Upload(TestStories.Linear);
        await Choose("Walk onward");
        await Driver.ClickAsync("Back");
        await Expect(Latest.Locator(".tw-scene-title")).ToHaveTextAsync("First passage");
        await Expect(Latest.Locator(".tw-chosen")).ToHaveCountAsync(0);
        await Expect(Latest.Locator("strong").First).ToHaveTextAsync("bold");
        await Expect(Latest.Locator("em").First).ToHaveTextAsync("gentle");
        await Expect(Latest.Locator("code")).ToHaveTextAsync("code");
        await Expect(Latest.Locator(".tw-speaker")).ToHaveTextAsync("Tomas");
        await Expect(Latest.Locator("br")).ToHaveCountAsync(1);
        await Driver.ClickAsync("Forward");
        await Expect(Latest).ToContainTextAsync("Second passage text.");
        await Expect(Page.Locator(".tw-past .tw-chosen")).ToHaveTextAsync("→ Walk onward");
        await Driver.ClickAsync("Back");
        await Choose("Finish early");
        await Expect(Latest.Locator(".tw-scene-title")).ToHaveTextAsync("Finished");
        await Expect(Page.Locator(".tw-past .tw-chosen")).ToHaveTextAsync("→ Finish early");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Forward", Exact = true })).ToBeDisabledAsync();
        await Restart();
        await Expect(Latest.Locator(".tw-scene-title")).ToHaveTextAsync("First passage");
        await Expect(Page.Locator(".tw-chosen")).ToHaveCountAsync(0);
        await Expect(Latest).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Library_resume_restart_and_story_replacement_do_not_leak_state()
    {
        await Upload(TestStories.Gates);
        await Choose("Collect supplies");
        await Choose("Continue");
        await Library();
        await AssertState(state => Assert.False(state.PassageVisible));
        await Driver.ClickAsync("Back to Player Test Continue where you left off");
        await Observe(new(null, "Inventory: ready.", Sheet: ["Test charm"]));
        await AssertState(state => Assert.Contains("Collect supplies", state.Transcript));
        await Restart();
        await Observe(new(null, "Inventory: empty."));
        await AssertState(state =>
        {
            Assert.DoesNotContain("Test charm", state.Sheet);
            Assert.DoesNotContain("Continue", state.Transcript);
        });
        await Library();
        await Upload(TestStories.Custom);
        await Observe(new(null, "Alex's quiet story."));
        await AssertState(state =>
        {
            Assert.DoesNotContain("Mara", state.Sheet);
            Assert.DoesNotContain("Attributes", state.Sheet);
            Assert.DoesNotContain("Standing", state.Sheet);
        });
        await Choose("Make a friend");
        await Library();
        await Upload(TestStories.Custom);
        await Observe(new(null, "Alex's quiet story.", Sheet: ["3"]));
        await AssertState(state => Assert.DoesNotContain("Friendship grows.", state.Transcript));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Sheet_shows_exact_values_modifiers_and_removal()
    {
        await Upload(TestStories.Gates);
        await Expect(Sheet.Locator("dt", new() { HasTextString = "Athletics" })).ToHaveCountAsync(1);
        await Expect(Sheet.Locator("dt", new() { HasTextString = "Acrobatics" })).ToHaveCountAsync(0);
        await Expect(Sheet.GetByText("Nothing.", new() { Exact = true })).ToBeVisibleAsync();
        await Choose("Collect supplies");
        await Expect(Sheet.Locator("dt").Filter(new() { HasTextString = "Wisdom" })
            .Locator("xpath=following-sibling::dd[1]")).ToHaveTextAsync("12(+2)");
        await Expect(Sheet.Locator("[title='Base 10, modified by items or rules']")).ToBeVisibleAsync();
        await Expect(Sheet).ToContainTextAsync("Test rope ×2");
        await Library();
        await Upload(TestStories.Progression);
        await Choose("Collect relic");
        await Expect(Latest.GetByRole(AriaRole.Status).Filter(new() { HasTextString = "Level 2!" })).ToBeVisibleAsync();
        await Choose("Donate relic");
        await Expect(Sheet).Not.ToContainTextAsync("Test relic");
        await Expect(Sheet.Locator("dt").Filter(new() { HasTextString = "Charisma" })
            .Locator("xpath=following-sibling::dd[1]")).ToHaveTextAsync("10");
    }

    [Fact]
    public async Task Fixed_seed_replays_identical_check_notes()
    {
        await Upload(TestStories.Seeded);
        await Choose("Roll once");
        await Observe(new(null, "Athletics check:"));
        var note = (await Driver.ObserveAsync()).Passage;
        await Restart();
        await Observe(new(null, "", ["Roll once"]));
        await Choose("Roll once");
        await AssertState(state => Assert.Equal(note, state.Passage));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Story_markup_does_not_execute_scripts()
    {
        await Upload(TestStories.Unicode);
        await Expect(Latest).ToContainTextAsync("Café, 日本語");
        await Expect(Latest.Locator("script")).ToHaveCountAsync(0);
        Assert.False(await Page.EvaluateAsync<bool>("() => window.storyExecuted === true"));
    }
}
