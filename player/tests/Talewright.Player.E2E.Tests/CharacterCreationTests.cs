using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class CharacterCreationTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    private static StoryFixture Fixture(params string[] pronouns)
    {
        var manifest = TestStories.DefaultManifest($"creation-{Guid.NewGuid():N}", creationName: true);
        manifest.CreationPronouns(pronouns.Length == 0 ? ["they", "she"] : pronouns);
        manifest.CreationStat("str", 8, 12, 16);
        manifest.CreationPreset("explorer", "Explorer", new Dictionary<string, int> { ["str"] = 12 });
        manifest.CreationPreset("scholar", "Scholar", new Dictionary<string, int> { ["str"] = 8 });
        return TestStories.Linear with
        {
            Tale = TestStories.Linear.Tale.Replace("Mara's", "{player:first-name#possessive}"),
            Yaml = manifest.Build(),
        };
    }

    [Theory]
    [InlineData("she", "she/her/herself", "she/her/herself")]
    [InlineData("he", "he/him/himself", "he/him/himself")]
    [InlineData("they", "they/them/themselves", "they/them/themselves")]
    [InlineData("it", "it/itself", "it/it/itself")]
    public async Task Full_pronoun_labels_keep_keyword_values_and_survive_save_load(
        string keyword, string label, string storyForms)
    {
        var fixture = Fixture(new[] { keyword }.Concat(new[] { "she", "he", "they", "it" }.Where(value => value != keyword)).ToArray());
        fixture = fixture with
        {
            Tale = TestStories.Linear.Tale.Replace("Mara's",
                "{player:pronoun}/{player:pronoun:object}/{player:pronoun:reflexive}"),
        };
        await Upload(fixture);
        if (PlayerTarget.IsDesktop)
        {
            await AssertState(state => Assert.Contains(label, state.Library));
        }
        else
        {
            var picker = Page.GetByRole(AriaRole.Combobox, new() { Name = "Pronouns", Exact = true });
            await Expect(picker.Locator("option")).ToHaveCountAsync(4);
            await Expect(picker.Locator($"option[value='{keyword}']")).ToHaveTextAsync(label);
            await picker.SelectOptionAsync(new SelectOptionValue { Label = label });
            await Expect(picker).ToHaveValueAsync(keyword);
        }
        await Driver.ClickAsync("Start story");
        await Observe(new(null, storyForms));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Load My save");
        await Observe(new(null, storyForms));
        await Choose("Walk onward");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, storyForms));
    }

    [Fact]
    public async Task Default_creation_plays_saves_replays_and_restarts_on_both_hosts()
    {
        await Upload(Fixture());
        await Driver.ClickAsync("Start story");
        await Observe(new(null, "Mara's"));
        await Choose("Walk onward");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Load My save");
        await Observe(new(null, "Second passage text."));
        await Restart();
        await Observe(new(null, "Mara's"));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Mara's"));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Quick_checkpoint_keeps_created_identity_and_summarizes_effective_name()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Page.GetByLabel("First name", new() { Exact = true }).FillAsync("Robin");
        await Page.GetByLabel("Last name", new() { Exact = true }).FillAsync("Vale");
        await Driver.ClickAsync("Start story");
        await Choose("Walk onward");
        await Driver.ClickAsync("Quick Save");
        await Expect(SaveStatus).ToContainTextAsync("Quick Save checkpoint saved.");
        await Driver.ClickAsync("Saves");
        await Expect(Page.Locator("[data-save-kind=quick]")).ToContainTextAsync("Robin Vale");
        await ReloadAsync();
        await Upload(fixture);
        await Driver.ClickAsync("Start story");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Quick Save checkpoint");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Second passage text."));
        await AssertState(state => Assert.Contains("Robin Vale", state.Sheet));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Robin's"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Host", "Browser")]
    public async Task Default_name_button_has_readable_contrast_in_each_color_scheme(bool dark)
    {
        await Page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
        await Upload(Fixture());

        var button = Page.GetByRole(AriaRole.Button, new() { Name = "Use default name", Exact = true });
        await Expect(button).ToBeVisibleAsync();
        var contrast = await button.EvaluateAsync<double>("""
            element => {
                const channels = color => color.match(/\d+/g).map(Number).map(value => {
                    value /= 255;
                    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
                });
                const luminance = color => {
                    const [red, green, blue] = channels(color);
                    return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
                };
                const style = getComputedStyle(element);
                const text = luminance(style.color);
                const background = luminance(style.backgroundColor);
                return (Math.max(text, background) + 0.05) / (Math.min(text, background) + 0.05);
            }
            """);

        Assert.True(contrast >= 4.5, $"Button text contrast was {contrast:F2}:1 in {(dark ? "dark" : "light")} mode.");
    }

    [Theory]
    [InlineData(390, false)]
    [InlineData(390, true)]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    [Trait("Host", "Browser")]
    public async Task Creation_actions_are_separated_and_keep_their_keyboard_order(int width, bool dark)
    {
        await Page.SetViewportSizeAsync(width, 900);
        await Page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
        await Upload(Fixture());

        var start = Page.GetByRole(AriaRole.Button, new() { Name = "Start story", Exact = true });
        var library = Page.GetByRole(AriaRole.Button, new() { Name = "Library", Exact = true });
        await Expect(start).ToBeVisibleAsync();
        await Expect(library).ToBeVisibleAsync();
        var startBounds = await start.BoundingBoxAsync();
        var libraryBounds = await library.BoundingBoxAsync();
        Assert.NotNull(startBounds);
        Assert.NotNull(libraryBounds);
        var rootFontSize = await Page.Locator("html").EvaluateAsync<double>("e => parseFloat(getComputedStyle(e).fontSize)");
        var gap = libraryBounds.Y - (startBounds.Y + startBounds.Height);
        Assert.True(gap >= rootFontSize, $"Creation actions need at least 1rem of separation; measured {gap:F2}px.");
        Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"));

        await start.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(library).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Talewright", Exact = true })).ToBeVisibleAsync();
        await Driver.ClickAsync("Back to Player Test Continue where you left off");
        await Expect(start).ToBeVisibleAsync();
        await start.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
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
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Load My save");
        await ReloadAsync();
        await Upload(fixture);
        await Driver.ClickAsync("Start story");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Robin's"));
        await Restart();
        await Observe(new(null, "Robin's"));
    }
}
