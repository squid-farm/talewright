using Microsoft.Playwright;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class StoryLoadingTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    public static IEnumerable<object[]> BadArchives => TestArchives.Errors.Select(e => new object[] { e.Key, e.Value });

    [Theory]
    [InlineData("")]
    [InlineData("A story with spaces/")]
    public async Task Multi_file_archives_load_at_root_or_in_a_wrapper(string prefix)
    {
        var files = TestStories.Linear.Files.ToDictionary(p => prefix + p.Key, p => p.Value);
        files[prefix + "chapters/main.tale"] = "=== start\nFirst file.\n-> finish";
        files[prefix + "chapters/ending.tale"] = "=== finish\nSecond file.\n-> END";
        await UploadBytes("multi.zip", TestArchives.Create(files));
        await Observe(new(null, "First file."));
        await Choose("Continue");
        await Observe(new(null, "Second file."));
    }

    [Theory]
    [MemberData(nameof(BadArchives))]
    public async Task Invalid_upload_reports_error_preserves_session_and_recovers(string kind, string error)
    {
        await Upload(TestStories.Linear);
        await Choose("Walk onward");
        await Library();
        await UploadBytes(kind + ".zip", TestArchives.Invalid(kind));
        await AssertState(state => Assert.Contains(error, state.Library));
        await Driver.ClickAsync("Back to Player Test Continue where you left off");
        await Observe(new(null, "Second passage text."));
        await AssertState(state => Assert.Contains("First passage", state.Transcript));
        await Library();
        await Upload(TestStories.Custom);
        await Observe(new(null, "Alex's quiet story."));
        await Library();
        await AssertState(state => Assert.DoesNotContain(error, state.Library));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Compiler_diagnostics_include_file_line_column_and_code()
    {
        await UploadBytes("broken.zip", TestArchives.Invalid("tale"));
        var diagnostics = Page.Locator(".tw-diagnostics");
        await Expect(diagnostics).ToContainTextAsync("TW2002");
        await Expect(diagnostics.Locator(".tw-diagnostic-where").First).ToHaveTextAsync(
            "chapters/main.tale:1:1");
        await Expect(diagnostics.Locator("li").Filter(new() { HasTextString = "TW2002" })).ToContainTextAsync("missing");
        await Expect(diagnostics.Locator(".tw-warning")).ToHaveCountAsync(1);
        var severities = await diagnostics.Locator("li").EvaluateAllAsync<string[]>(
            "items => items.map(i => i.classList.contains('tw-error') ? 'error' : 'warning')");
        Assert.Equal(severities.OrderBy(s => s == "error" ? 0 : 1), severities);
        await Expect(Page.GetByLabel("Story archive")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Exactly_twenty_MiB_is_accepted_by_the_upload_layer()
    {
        var bytes = TestArchives.AtUploadLimit();
        Assert.Equal(20 * 1024 * 1024, bytes.Length);
        await UploadBytes("at-limit.zip", bytes);
        await Observe(new(null, "First passage"));
    }
}
