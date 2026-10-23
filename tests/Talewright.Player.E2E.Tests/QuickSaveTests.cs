using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Talewright.Runtime.Saves;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

public sealed class QuickSaveTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    private static StoryFixture Fixture() => TestStories.Linear with
    {
        Yaml = TestStories.DefaultYaml.Replace("id: player-test", $"id: quick-{Guid.NewGuid():N}"),
    };

    [Theory]
    [InlineData(390)]
    [InlineData(1100)]
    public async Task Quick_save_feedback_stays_visible_while_scrolled_without_moving_the_reader(int width)
    {
        var fixture = TestStories.Long with { Yaml = Fixture().Yaml };
        await Upload(fixture);
        await AssertState(state => Assert.True(state.PassageFocused));
        await Driver.ResizeAsync(width, 900);
        if (PlayerTarget.IsDesktop)
            await Driver.PressAsync("End");
        else
            await Page.EvaluateAsync("() => window.scrollTo({top: document.documentElement.scrollHeight, behavior: 'instant'})");
        var timer = Stopwatch.StartNew();
        while (await PassageTop() >= 0)
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "The reader scrolled down the transcript.");
            await Task.Delay(100);
        }
        var before = await PassageTop();
        await ClickVisibleQuickSave();
        await AssertText(text => Assert.Contains("Quick Save checkpoint saved.", text));
        await AssertFeedbackVisible("Quick Save checkpoint saved.");
        var after = await PassageTop();
        Assert.InRange(Math.Abs(after - before), 0, 160); // Only the feedback row may change layout.
        Assert.True(after < -100, "Saving must not return the reader to the start.");

        string? path = null;
        string? backup = null;
        if (PlayerTarget.IsDesktop)
        {
            var storyId = fixture.Yaml.Split('\n')[0]["id: ".Length..];
            var bytes = await new FileSaveStore(FileSaveStore.DefaultRoot).ReadQuickAsync(storyId);
            path = Directory.EnumerateFiles(FileSaveStore.DefaultRoot, "quick.json", SearchOption.AllDirectories)
                .Single(file => File.ReadAllBytes(file).SequenceEqual(bytes));
            backup = path + ".test-backup";
            File.Move(path, backup);
            Directory.CreateDirectory(path);
        }
        else
        {
            await Page.EvaluateAsync("""
                () => {
                    window.originalTransaction = IDBDatabase.prototype.transaction;
                    IDBDatabase.prototype.transaction = function(...args) {
                        if (args[1] === 'readwrite') throw new DOMException('test failure', 'QuotaExceededError');
                        return window.originalTransaction.apply(this, args);
                    };
                }
                """);
        }
        try
        {
            var beforeFailure = await PassageTop();
            await ClickVisibleQuickSave();
            await AssertText(text => Assert.Contains(PlayerTarget.IsDesktop
                ? "Saved games could not be read or written" : "not enough browser storage", text));
            await AssertFeedbackVisible(PlayerTarget.IsDesktop
                ? "Saved games could not be read or written" : "not enough browser storage");
            Assert.InRange(Math.Abs(await PassageTop() - beforeFailure), 0, 160);
            Assert.True(await PassageTop() < -100);
        }
        finally
        {
            if (path is not null && backup is not null)
            {
                Directory.Delete(path);
                File.Move(backup, path);
            }
            else
            {
                await Page.EvaluateAsync("() => IDBDatabase.prototype.transaction = window.originalTransaction");
            }
        }
        await Driver.ClickAsync("Saves");
        await ClickLoad("quick");
        await Observe(new(null, "Long beginning"));
    }

    private async Task<double> PassageTop() => Driver is NativePlayerDriver native
        ? (await native.SnapshotAsync()).Descendants().First(node => node.Name == "Current passage").Bounds.Y
        : (await Latest.BoundingBoxAsync())!.Y;

    private async Task ClickVisibleQuickSave()
    {
        if (PlayerTarget.IsDesktop)
        {
            await Driver.ClickAsync("Quick Save");
        }
        else
        {
            var button = Page.GetByRole(AriaRole.Button, new() { Name = "Quick Save", Exact = true });
            await Expect(button).ToBeInViewportAsync(new() { Ratio = 1 });
            var bounds = (await button.BoundingBoxAsync())!;
            // Click where the reader would, without Locator.Click's automatic scrollIntoView.
            await Page.Mouse.ClickAsync(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }
    }

    private async Task AssertFeedbackVisible(string text)
    {
        if (Driver is NativePlayerDriver native)
        {
            var snapshot = await native.SnapshotAsync();
            var window = snapshot.Descendants().First(node => node.Role == "frame").Bounds;
            var feedback = snapshot.Descendants().First(node =>
                node.Text.Contains(text, StringComparison.Ordinal) && node.Children.Length == 0).Bounds;
            Assert.True(feedback.Y >= window.Y && feedback.Y + feedback.Height <= window.Y + window.Height);
        }
        else
        {
            var feedback = Page.Locator(".tw-toolbar-feedback");
            await Expect(feedback).ToContainTextAsync(text);
            await Expect(feedback).ToBeInViewportAsync(new() { Ratio = 1 });
            await Expect(Page.Locator(".tw-bar")).ToBeInViewportAsync(new() { Ratio = 1 });
            await Expect(Page.Locator(".tw-bar")).ToHaveCSSAsync("position", "sticky");
            await Page.WaitForFunctionAsync("""
                () => Math.abs(parseFloat(getComputedStyle(document.documentElement).scrollPaddingTop)
                    - document.querySelector('.tw-bar').getBoundingClientRect().height - 18) < .01
                """);
        }
    }

    [Fact]
    public async Task Quick_save_replaces_only_checkpoint_and_restores_redo_after_host_restart()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Driver.ClickAsync("Saves");
        await FillName("Quick Save checkpoint");
        await Driver.ClickAsync("Save game");
        await AssertText(text => Assert.Contains("Saved 'Quick Save checkpoint'.", text));
        await Driver.ClickAsync("Close saves");
        await Choose("Walk onward");
        await QuickSave();
        await Choose("Continue");
        await QuickSave();
        await Driver.ClickAsync("Back");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await QuickSave();
        await Driver.ClickAsync("Saves");
        await AssertText(text =>
        {
            Assert.Contains("One replaceable checkpoint for this story", text);
            Assert.Contains("Second passage", text);
            Assert.Contains("Mara Burns", text);
            var timestamp = Regex.Match(text, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z").Value;
            Assert.True(DateTimeOffset.TryParseExact(timestamp, "u", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var savedAt));
            Assert.InRange(savedAt, before, DateTimeOffset.UtcNow);
        });
        if (!PlayerTarget.IsDesktop)
        {
            await Expect(Page.Locator("[data-save-kind=quick]")).ToHaveCountAsync(1);
            await Expect(Page.Locator("[data-save-kind=named]")).ToHaveCountAsync(1);
            await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Confirm overwrite" })).ToHaveCountAsync(0);
        }
        await ClickLoad("named"); // Current state is safely stored in the checkpoint, so no prompt.
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("Saves");
        await ClickLoad("quick"); // Current state is safely stored in the named save.
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, "Finished safely.", Ended: true));
        await Driver.ClickAsync("Saves");
        await ClickLoad("quick");
        await AssertText(text => Assert.Contains("Load “Quick Save checkpoint”?", text));
        await Driver.ClickAsync("Keep playing");
        await Observe(new(null, "Finished safely.", Ended: true));

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
        await Driver.ClickAsync("Saves");
        await ClickLoad("quick"); // The initial state is still safely stored in the named save.
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, "Finished safely.", Ended: true));
    }

    [Fact]
    public async Task Quick_checkpoints_are_isolated_between_stories_and_failed_restore_keeps_playing()
    {
        var first = Fixture();
        var second = Fixture();
        await Upload(first);
        await Choose("Walk onward");
        await QuickSave();
        await Library();
        await Upload(second);
        await Driver.ClickAsync("Saves");
        await AssertText(text => Assert.Contains("No saved games.", text));
        await Driver.ClickAsync("Close saves");
        await QuickSave();
        await Library();
        await Upload(first);
        await Driver.ClickAsync("Saves");
        await ClickLoad("quick");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Second passage text."));
        var storyId = first.Yaml.Split('\n')[0]["id: ".Length..];
        if (PlayerTarget.IsDesktop)
        {
            await new FileSaveStore(FileSaveStore.DefaultRoot).WriteQuickAsync(storyId, "{broken"u8.ToArray());
        }
        else
        {
            await Page.EvaluateAsync("""
                async storyId => {
                    const module = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
                    await module.write(storyId, '\0quick', new TextEncoder().encode('{broken'), true);
                }
                """, storyId);
        }
        await Driver.ClickAsync("Saves");
        await ClickLoad("quick");
        await Driver.ClickAsync("Load save");
        await AssertText(text => Assert.Contains("malformed", text));
        await Driver.ClickAsync("Close saves");
        await Observe(new(null, "Second passage text."));
        await Choose("Continue");
        await Observe(new(null, "Finished safely.", Ended: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Host", "Browser")]
    public async Task Failed_write_preserves_checkpoint_and_does_not_mark_progress_saved(bool abortTransaction)
    {
        await Upload(Fixture());
        await QuickSave();
        await Choose("Walk onward");
        await Page.EvaluateAsync("""
            abortTransaction => {
                window.originalTransaction = IDBDatabase.prototype.transaction;
                window.originalPut = IDBObjectStore.prototype.put;
                if (abortTransaction) {
                    IDBObjectStore.prototype.put = function(...args) {
                        const request = window.originalPut.apply(this, args);
                        request.addEventListener('success', () => this.transaction.abort());
                        return request;
                    };
                    return;
                }
                IDBDatabase.prototype.transaction = function(...args) {
                    if (args[1] === 'readwrite') throw new DOMException('test failure', 'QuotaExceededError');
                    return window.originalTransaction.apply(this, args);
                };
            }
            """, abortTransaction);
        await Driver.ClickAsync("Quick Save");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync(
            abortTransaction ? "previous save has not been replaced" : "not enough browser storage");
        await Page.EvaluateAsync("""
            () => {
                IDBDatabase.prototype.transaction = window.originalTransaction;
                IDBObjectStore.prototype.put = window.originalPut;
            }
            """);
        await Driver.ClickAsync("Saves");
        await Expect(Page.Locator("[data-save-kind=quick]")).ToContainTextAsync("First passage");
        await ClickLoad("quick");
        await AssertText(text => Assert.Contains("discard unsaved progress", text));
        await Driver.ClickAsync("Keep playing");
        await Observe(new(null, "Second passage text."));
        await QuickSave();
        await Driver.ClickAsync("Saves");
        await ClickLoad("quick"); // Successful retry now safely stores the live state.
        await Observe(new(null, "Second passage text."));
    }

    [Fact]
    [Trait("Host", "Linux")]
    public async Task Native_io_failure_preserves_checkpoint_and_load_protection()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await QuickSave();
        var storyId = fixture.Yaml.Split('\n')[0]["id: ".Length..];
        var store = new FileSaveStore(FileSaveStore.DefaultRoot);
        var original = await store.ReadQuickAsync(storyId);
        var path = Directory.EnumerateFiles(FileSaveStore.DefaultRoot, "quick.json", SearchOption.AllDirectories)
            .Single(file => File.ReadAllBytes(file).SequenceEqual(original));
        await Choose("Walk onward");
        var backup = path + ".test-backup";
        File.Move(path, backup);
        Directory.CreateDirectory(path);
        try
        {
            await Driver.ClickAsync("Quick Save");
            await AssertText(text => Assert.Contains("Saved games could not be read or written", text));
        }
        finally
        {
            Directory.Delete(path);
            File.Move(backup, path);
        }
        Assert.Equal(original, await store.ReadQuickAsync(storyId));
        await Driver.ClickAsync("Saves");
        await ClickLoad("quick");
        await AssertText(text => Assert.Contains("discard unsaved progress", text));
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "First passage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Host", "Browser")]
    public async Task Quick_checkpoint_is_distinguished_in_narrow_themed_dialog(bool dark)
    {
        await Page.SetViewportSizeAsync(320, 800);
        await Page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
        await Upload(Fixture());
        await QuickSave();
        await Driver.ClickAsync("Saves");
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Saves", Exact = true });
        await Expect(dialog.Locator("[data-save-kind=quick]")).ToContainTextAsync("One replaceable checkpoint");
        Assert.True(await dialog.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"));
        Assert.Equal(dark ? "rgb(38, 35, 31)" : "rgb(240, 235, 224)",
            await dialog.EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"));
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Saves", Exact = true })).ToBeFocusedAsync();
    }

    private async Task QuickSave()
    {
        await Driver.ClickAsync("Quick Save");
        await AssertText(text => Assert.Contains("Quick Save checkpoint saved.", text));
    }

    private async Task FillName(string name)
    {
        if (Driver is NativePlayerDriver native)
            await native.FillAsync("Save name", name);
        else
            await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync(name);
    }

    private async Task ClickLoad(string kind)
    {
        // Both names deliberately collide; select by kind rather than a display-name convention.
        if (Driver is NativePlayerDriver native)
        {
            await native.ClickWithinAsync("Load Quick Save checkpoint",
                kind == "quick" ? "Checkpoint: Quick Save checkpoint" : "Named save: Quick Save checkpoint");
        }
        else
        {
            await Page.Locator($"[data-save-kind={kind}]")
                .GetByRole(AriaRole.Button, new() { Name = "Load Quick Save checkpoint", Exact = true }).ClickAsync();
        }
    }

    private async Task AssertText(Action<string> assertion)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var text = Driver is NativePlayerDriver native
                ? (await native.SnapshotAsync()).Content
                : await Page.Locator(".tw-player").InnerTextAsync();
            try { assertion(text); return; }
            catch (Xunit.Sdk.XunitException) when (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(100);
            }
        }
    }
}
