using System.IO.Compression;
using System.Diagnostics;
using Microsoft.Playwright;
using Talewright.Player.Library;
using Talewright.Talemark.Compilation;
using Xunit.Abstractions;

namespace Talewright.Player.E2E.Tests;

public sealed class DreamArtifactFactAttribute : FactAttribute
{
    public DreamArtifactFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE") != "1" ||
            Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE_CREATION") != "1" ||
            (Environment.GetEnvironmentVariable("TALEWRIGHT_PUBLISHED_TITLE") ?? "The Lost Relic") != "The Lost Relic")
            Skip = "Requires a published Lost Relic artifact with character creation.";
    }
}

public sealed class LostRelicDreamTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    protected override bool StartsInLibrary => Environment.GetEnvironmentVariable("TALEWRIGHT_STANDALONE") != "1";

    private async Task AssertImage(string alt)
    {
        if (Driver is NativePlayerDriver native)
        {
            await native.ScrollToImageAsync(alt);
            var timer = Stopwatch.StartNew();
            while (!(await native.SnapshotAsync()).Descendants().Any(node =>
                node.Role == "image" && node.Name == alt && node.Bounds.Width > 100 && node.Bounds.Height > 100))
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"Image did not display: {alt}");
                await Task.Delay(100);
            }
        }
        else
        {
            var image = Latest.GetByAltText(alt, new() { Exact = true });
            await image.ScrollIntoViewIfNeededAsync();
            await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0",
                await image.ElementHandleAsync());
            Assert.StartsWith("blob:", await image.GetAttributeAsync("src"));
        }
    }

    private async Task UploadFixedRollSample()
    {
        var assembly = typeof(LostRelicDreamTests).Assembly;
        var source = await new EmbeddedStoryLibrary(assembly).LoadAsync("LostRelic");
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in source.Files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(path == StorySource.ManifestFileName ? text.Replace("randomness: checks-only", "randomness: off") : text);
            }
            foreach (var path in source.ImagePaths)
            {
                using var image = assembly.GetManifestResourceStream("stories/LostRelic/" + path)!;
                using var target = zip.CreateEntry(path).Open();
                await image.CopyToAsync(target);
            }
        }
        await UploadBytes("lost-relic-dream.zip", buffer.ToArray());
    }

    private async Task EnterDream(bool journal, bool night)
    {
        await Driver.ClickAsync("Start story");
        await Observe(new(null, "reputation for punctuality"));
        if (!PlayerTarget.IsDesktop)
        {
            var image = Page.GetByAltText(
                "A riverside expedition camp with canvas tents and supplies at the edge of dense jungle.",
                new() { Exact = true });
            await Page.WaitForFunctionAsync("image => image.complete && image.naturalWidth > 0",
                await image.ElementHandleAsync());
        }
        await Choose("Continue");
        await Choose("Ask your guide about the temple");
        await Choose("Continue");
        if (journal)
        {
            await Choose("Study your journal");
            await Choose("Puzzle out the glyphs");
            await Choose("Continue");
        }
        await Choose("Agree on a night watchword");
        await Choose("Keep quiet river");
        await Choose("Keep the recorded watchword");
        await Choose("Close the watch ledger");
        await Choose("Haggle for supplies");
        await Choose("Sign the expedition register");
        await Choose("Continue");
        await Choose("Set out for the temple");
        await AssertImage("A rope stretches across a swollen jungle river toward an ancient green stone stair on the opposite bank.");
        await Choose("Tie off the rope and cross carefully");
        if (night)
        {
            await Choose("Make camp before the climb");
            await AssertImage("A low fire lights an expedition awning and a brass tripwire bell at the foot of a jungle stair.");
            await Choose("Eat a ration");
            await Choose("Share supper by the fire");
            await Choose("Continue");
            await Choose("Stand your ground");
            await AssertImage("A watchful jaguar emerges from wet foliage at the edge of the campfire's light.");
            await Choose("Catch your breath");
            await Choose("Press on at dawn");
        }
        else await Choose("Climb straight to the temple");
        await Choose("Close your eyes");
        await AssertImage("An expedition table and a solitary explorer stand on still dark water in a hall of mist.");
        await Choose("Continue");
    }

    private async Task MeetKeepers(string watcherRoute)
    {
        await AssertState(state => Assert.Equal(
            ["Wake before going further", "Speak to the lost explorer", "Answer the Spiral"], state.Choices));
        await Choose("Speak to the lost explorer");
        await Observe(new(null, "Tell Tomas I remember the river"));
        await Observe(new(null, "10"));
        await Choose("Promise to carry her message");
        await Choose("Answer the Spiral");
        await Observe(new(null, "Xe has a question"));
        await Observe(new(null, "living voice and the written word agree"));
        await Choose("Accept the spiral mark");
        await Observe(new(null, "Glyph marks held: 1"));
        await Choose("Face the Watcher");
        await Observe(new(null, "It has no eyelid"));
        await Choose("Continue");
        await Choose("Continue");
        await Choose(watcherRoute);
        await Choose("Continue");
        await Observe(new(null, "Glyph marks held: 2"));
        await Choose("Hear the guild ghost");
        await Observe(new(null, "They still put your name in the ledger"));
        await Choose("Promise the guild an honest account");
        await Choose("Ask the Hands for passage");
        await Observe(new(null, "The Hands have no owner. They offer the final mark."));
        await Choose("Accept the hand mark and the price");
        await Observe(new(null, "Glyph marks held: 3"));
        await Choose("Approach the Sleeper");
    }

    private async Task Finish(string ending)
    {
        await Choose("Pay the Hands and return to the temple");
        await Observe(new(null, "You keep 2 marks"));
        await Choose("Continue");
        await Choose("Continue");
        if ((await Driver.ObserveAsync()).Choices.Contains("Take the idol")) await Choose("Take the idol");
        await Choose(ending == "leave" ? "Leave it where it lies" : "Take the relic");
        await Choose(ending switch
        {
            "guild" => "Hand the relic to the guild",
            "keep" => "Keep it for yourself",
            _ => "Continue",
        });
        await Observe(new(null, ending switch
        {
            "guild" => "testimony",
            "keep" => "The warning returns in fragments",
            _ => "The guild ghost's testimony earns a hearing",
        }, Ended: true));
    }

    [Fact]
    public async Task Bundled_dream_all_six_roles_marks_and_saved_history_work_on_both_hosts()
    {
        await Driver.OpenBundledAsync();
        await EnterDream(journal: false, night: true);
        await MeetKeepers("Meet its gaze without flinching");
        await SaveSleeperAndFinish();
    }

    [DreamArtifactFact]
    [Trait("Artifact", "Standalone")]
    public async Task Published_dream_cast_marks_save_load_and_replay_survive_both_hosts()
    {
        await EnterDream(journal: false, night: true);
        await MeetKeepers("Meet its gaze without flinching");
        await SaveSleeperAndFinish();
    }

    private async Task SaveSleeperAndFinish()
    {
        await Observe(new(null, "Only its hunger remains"));
        const string alt = "Impenetrable shadow fills the space beneath an expedition table, obscuring whatever lies beyond.";
        await AssertImage(alt);
        await Driver.ClickAsync("Saves");
        const string slot = "Dream before the price";
        if (Driver is NativePlayerDriver native)
            await native.FillAsync("Save name", slot);
        else
            await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync(slot);
        await Driver.ClickAsync("Save game");
        if (Driver is NativePlayerDriver saving)
        {
            var timer = Stopwatch.StartNew();
            while (!(await saving.SnapshotAsync()).Content.Contains($"Saved '{slot}'", StringComparison.Ordinal))
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "Dream checkpoint was not saved.");
                await Task.Delay(100);
            }
        }
        else
        {
            await Microsoft.Playwright.Assertions.Expect(SaveStatus).ToContainTextAsync($"Saved '{slot}'");
        }
        await Driver.ClickAsync("Close saves");
        await Choose("Pay the Hands and return to the temple");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load " + slot);
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Only its hunger remains"));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Glyph marks held: 3"));
        await Driver.ClickAsync("Forward");
        await AssertImage(alt);
        await Finish("keep");
    }

    [Theory]
    [InlineData("explorer", false, "she", "guild", "Only its hunger remains")]
    [InlineData("scholar", false, "they", "keep", "a latch, and a door that must stay shut")]
    [InlineData("scholar", true, "he", "leave", "Never turn it")]
    [Trait("Host", "Browser")]
    public async Task Dream_tiers_pronouns_and_each_ending_work_with_real_creation_choices(
        string preset, bool journal, string pronouns, string ending, string prose)
    {
        await UploadFixedRollSample();
        await Page.GetByLabel("Character preset").SelectOptionAsync(preset);
        await Page.GetByLabel("Pronouns", new() { Exact = true }).SelectOptionAsync(pronouns);
        await Page.GetByLabel("Courage", new() { Exact = true }).SelectOptionAsync("6");
        await EnterDream(journal, night: false);
        await MeetKeepers(preset == "explorer" ? "Meet its gaze without flinching" : "Remember that you are dreaming");
        await Observe(new(null, prose));
        await AssertImage(journal
            ? "Beneath an expedition table, a stone doorway is clearly visible beyond a pale-gold disc on its threshold."
            : preset == "scholar"
                ? "A stone doorway, a pale disc and an indistinct dark shape emerge through mist beneath an expedition table."
                : "Impenetrable shadow fills the space beneath an expedition table, obscuring whatever lies beyond.");
        Assert.Single(await Latest.Locator("img").AllAsync());
        await Finish(ending);
    }
}
