using Talewright.Testing;
using Xunit.Abstractions;

namespace Talewright.Player.E2E.Tests;

public sealed class SharedInteractionTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    [Fact]
    public async Task Window_title_follows_story_and_library_resume()
    {
        await Upload(TestStories.Linear);
        await AssertState(state => Assert.Equal("Player Test", state.WindowTitle));
        await Library();
        await AssertState(state => Assert.Equal("Talewright", state.WindowTitle));
        await Driver.ClickAsync("Back to Player Test Continue where you left off");
        await AssertState(state => Assert.Equal("Player Test", state.WindowTitle));
    }

    [Fact]
    public async Task Keyboard_activates_choices_using_native_focus()
    {
        await Upload(TestStories.Linear);
        await AssertState(state => Assert.True(state.PassageFocused));
        await Driver.PressAsync("Tab");
        await Driver.PressAsync("Enter");
        await Observe(new(null, "Second passage text."));
        await AssertState(state => Assert.True(state.PassageFocused));
        await Driver.PressAsync("Tab");
        await Driver.PressAsync("Space");
        await Observe(new(null, "Finished safely.", Ended: true));
    }

    [Fact]
    public async Task Passage_scroll_resume_and_narrow_layout_work_in_the_real_host()
    {
        await Upload(TestStories.Long);
        await Driver.ResizeAsync(700, 900);
        await AssertState(state =>
        {
            Assert.InRange(state.WindowWidth, 1, 749);
            Assert.NotEmpty(state.Sheet);
            Assert.True(state.SheetStacked);
        });
        await Choose("Next passage");
        await Observe(new(null, "The new passage is visible."));
        await AssertState(state =>
        {
            Assert.True(state.PassageFocused);
            Assert.True(state.PassageInWindow);
        });
        await Library();
        await Driver.ClickAsync("Back to Player Test Continue where you left off");
        await AssertState(state =>
        {
            Assert.True(state.PassageFocused);
            Assert.True(state.PassageInWindow);
        });
        await Driver.ClickAsync("Restart");
        await Observe(new(null, "Long beginning"));
        await AssertState(state => Assert.True(state.PassageFocused));
        await Library();
        await Upload(TestStories.Linear);
        await AssertState(state => Assert.True(state.SheetStacked));
        await Driver.ResizeAsync(1100, 900);
        await AssertState(state =>
        {
            Assert.True(state.WindowWidth > 1000);
            Assert.NotEmpty(state.Sheet);
            Assert.False(state.SheetStacked);
        });
    }
}
