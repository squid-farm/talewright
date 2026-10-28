using Microsoft.Playwright;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;
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
    public async Task Summaries_and_discard_confirmation_follow_persisted_history_including_redo()
    {
        await Upload(Fixture());
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await AssertDialogText(text =>
        {
            Assert.Contains("First passage", text);
            Assert.Contains("Mara Burns", text);
        });
        if (!PlayerTarget.IsDesktop)
        {
            var time = Page.GetByRole(AriaRole.Dialog, new() { Name = "Saves", Exact = true }).Locator("time");
            var timestamp = await time.GetAttributeAsync("datetime");
            Assert.NotNull(timestamp);
            Assert.True(DateTimeOffset.TryParse(timestamp, out var savedAt), "Machine-readable UTC timestamp missing.");
            Assert.EndsWith("Z", timestamp);
            Assert.InRange(savedAt, before, DateTimeOffset.UtcNow);
            var expectedLocalTime = await Page.EvaluateAsync<string>("""
                value => new Intl.DateTimeFormat(undefined, {
                    dateStyle: "medium",
                    timeStyle: "short"
                }).format(new Date(value))
                """, timestamp);
            await Expect(time).ToHaveTextAsync(expectedLocalTime);
        }
        await Driver.ClickAsync("Close saves");
        await Choose("Walk onward");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await AssertDialogText(text => Assert.Contains("Load “My save”?", text));
        await Driver.ClickAsync("Keep playing");
        await Observe(new(null, "Second passage text."));
        await Choose("Continue");
        await Driver.ClickAsync("Back");
        await Driver.ClickAsync("Saves");
        await FillSaveName("With redo");
        await Driver.ClickAsync("Save game");
        await AssertDialogText(text => Assert.Contains("Saved 'With redo'.", text));
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Cancel overwrite");
        await Driver.ClickAsync("Load My save"); // Current state is saved elsewhere, so no prompt.
        await Observe(new(null, "First passage"));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load With redo");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, "Finished safely.", Ended: true));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save"); // Cursor changed since the persisted redo checkpoint.
        await AssertDialogText(text => Assert.Contains("discard unsaved progress", text));
        await Driver.ClickAsync("Keep playing");
        await Observe(new(null, "Finished safely.", Ended: true));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Saves_are_localized_sorted_newest_first_and_visually_grouped()
    {
        await Upload(Fixture());
        await Driver.ClickAsync("Quick Save");
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        await Driver.ClickAsync("Saves");
        await FillSaveName("Quick Save checkpoint");
        await Driver.ClickAsync("Save game");
        await AssertDialogText(text => Assert.Contains("Saved 'Quick Save checkpoint'.", text));

        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Saves", Exact = true });
        var entries = dialog.Locator(".tw-save-list > li");
        await Expect(entries).ToHaveCountAsync(2);
        await Expect(entries.Nth(0)).ToHaveAttributeAsync("data-save-kind", "named");
        await Expect(entries.Nth(1)).ToHaveAttributeAsync("data-save-kind", "quick");
        await Expect(entries.Nth(0).Locator(".tw-save-kind")).ToHaveTextAsync("Named save");
        await Expect(entries.Nth(1).Locator(".tw-save-kind")).ToHaveCountAsync(0);
        var text = await dialog.InnerTextAsync();
        Assert.Contains("Named save", text);
        Assert.DoesNotContain("One replaceable checkpoint for this story", text);

        var form = dialog.Locator(".tw-save-form");
        var section = dialog.Locator(".tw-save-section");
        Assert.True(await form.EvaluateAsync<bool>("e => e.matches('fieldset') && getComputedStyle(e).borderStyle === 'solid' && parseFloat(getComputedStyle(e).paddingTop) >= 12"));
        Assert.True(await section.EvaluateAsync<bool>("e => getComputedStyle(e).borderTopStyle === 'solid' && e.querySelector('h3')?.textContent === 'Saved games'"));
        var cardStyles = await entries.First.EvaluateAsync<bool>("""
            e => getComputedStyle(e).borderStyle === "solid" &&
                parseFloat(getComputedStyle(e).borderRadius) >= 6 &&
                parseFloat(getComputedStyle(e).paddingTop) >= 12
            """);
        Assert.True(cardStyles, "Each save should have a bordered, padded card.");
    }

    [Fact]
    public async Task Legacy_and_unreadable_details_remain_listed_and_failed_load_preserves_session()
    {
        var fixture = Fixture();
        var story = fixture.Compile();
        var history = new StoryHistory(story, 7);
        history.Choose("c0");
        await Upload(fixture);
        await Observe(new(null, "First passage"));
        await Persist(story.Id, "Legacy", history.Save());
        var damaged = JsonNode.Parse(history.Save())!;
        damaged["summary"] = "unreadable metadata";
        await Persist(story.Id, "Damaged details", System.Text.Encoding.UTF8.GetBytes(damaged.ToJsonString()));
        await Persist(story.Id, "Broken replay", "{broken"u8.ToArray());
        await Driver.ClickAsync("Saves");
        await AssertDialogText(text =>
        {
            Assert.Contains("Legacy", text);
            Assert.Contains("Details unavailable", text);
            Assert.Contains("Save details are unreadable", text);
            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z", text);
        });
        await Driver.ClickAsync("Load Damaged details");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Legacy"); // Same persisted replay, despite different metadata.
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Broken replay");
        await AssertDialogText(text => Assert.Contains("malformed", text));
        await Driver.ClickAsync("Close saves");
        await Observe(new(null, "Second passage text."));
        await Choose("Continue");
        await Observe(new(null, "Finished safely.", Ended: true));
    }

    private async Task Persist(string storyId, string slot, byte[] bytes)
    {
        if (PlayerTarget.IsDesktop)
        {
            await new FileSaveStore(FileSaveStore.DefaultRoot).WriteAsync(storyId, slot, bytes);
        }
        else
        {
            await Page.EvaluateAsync("""
                async ({storyId, slot, json}) => {
                    const module = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
                    await module.write(storyId, slot, new TextEncoder().encode(json), false);
                }
                """, new { storyId, slot, json = System.Text.Encoding.UTF8.GetString(bytes) });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Host", "Browser")]
    public async Task Saves_dialog_is_modal_themed_narrow_and_keyboard_contained(bool dark)
    {
        await Page.SetViewportSizeAsync(320, 800);
        await Page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
        await Upload(Fixture());
        await Driver.ClickAsync("Saves");
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Saves", Exact = true });
        var close = dialog.GetByRole(AriaRole.Button, new() { Name = "Close saves" });
        await Expect(close).ToBeFocusedAsync();
        Assert.True(await dialog.EvaluateAsync<bool>("e => e.matches(':modal')"));
        Assert.Equal(dark ? "rgb(38, 35, 31)" : "rgb(240, 235, 224)",
            await dialog.EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"));
        await Page.Keyboard.PressAsync("Shift+Tab");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save game", Exact = true })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Tab");
        await Expect(close).ToBeFocusedAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Refresh saves" })).ToHaveCountAsync(0);
        await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync(new string('S', 64));
        await Driver.ClickAsync("Save game");
        await Expect(dialog.Locator("li")).ToHaveCountAsync(1);
        Assert.True(await dialog.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"),
            await dialog.EvaluateAsync<string>("""
                e => JSON.stringify({client: e.clientWidth, scroll: e.scrollWidth, children:
                    [...e.querySelectorAll('*')].filter(child => child.getBoundingClientRect().right >
                        e.getBoundingClientRect().right).map(child => ({
                            tag: child.tagName, text: child.textContent.slice(0, 80),
                            width: child.getBoundingClientRect().width
                        }))})
                """));
        var bounds = (await dialog.BoundingBoxAsync())!;
        Assert.True(bounds.X >= 0 && bounds.X + bounds.Width <= 320);
        await Page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Saves", Exact = true })).ToBeFocusedAsync();
        await Driver.ClickAsync("Saves");
        await Expect(dialog.Locator("li")).ToHaveCountAsync(1);
        await Driver.ClickAsync("Close saves");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Saves", Exact = true })).ToBeFocusedAsync();
    }

    [Fact]
    [Trait("Host", "Linux")]
    public async Task Native_saves_dialog_contains_keyboard_focus_and_returns_to_trigger()
    {
        await Upload(Fixture());
        await Observe(new(null, "First passage"));
        await Driver.ResizeAsync(390, 800);
        await Driver.ClickAsync("Saves");
        await AssertDialogText(text => Assert.Contains("No saved games.", text));
        await Driver.PressAsync("Tab");
        await Driver.PressAsync("Tab");
        var native = Assert.IsType<NativePlayerDriver>(Driver);
        Assert.Contains((await native.SnapshotAsync()).Descendants(), node => node.Focused && node.Name == "Save game");
        await Driver.PressAsync("Tab");
        Assert.Contains((await native.SnapshotAsync()).Descendants(), node => node.Focused && node.Name == "Close saves");
        await Driver.PressAsync("Escape");
        await Observe(new(null, "First passage"));
        Assert.Contains((await native.SnapshotAsync()).Descendants(), node => node.Focused && node.Name == "Saves");
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Failed_write_does_not_suppress_discard_protection_and_retry_recovers_storage()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Expect(SaveStatus).ToContainTextAsync("Saved 'My save'.");
        await Driver.ClickAsync("Close saves");
        await Choose("Walk onward");
        await Driver.ClickAsync("Saves");
        await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync("Failed save");
        await Page.EvaluateAsync("""
            () => {
                window.originalTransaction = IDBDatabase.prototype.transaction;
                IDBDatabase.prototype.transaction = function(...args) {
                    if (args[1] === "readwrite") throw new DOMException("test failure", "QuotaExceededError");
                    return window.originalTransaction.apply(this, args);
                };
            }
            """);
        await Driver.ClickAsync("Save game");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("not enough browser storage");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Load Failed save" })).ToHaveCountAsync(0);
        await Page.EvaluateAsync("() => IDBDatabase.prototype.transaction = window.originalTransaction");
        await Driver.ClickAsync("Retry save list");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        await Driver.ClickAsync("Load My save");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Load “My save”?" })).ToBeVisibleAsync();
        await Driver.ClickAsync("Keep playing");
        await Observe(new(null, "Second passage text."));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Indexeddb_record_corruption_keeps_catalog_available_for_other_slots()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Driver.ClickAsync("Saves");
        await FillSaveName("Healthy");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Close saves");
        await Page.EvaluateAsync("""
            async storyId => {
                const db = await new Promise((resolve, reject) => {
                    const request = indexedDB.open("talewright-saves", 1);
                    request.onerror = () => reject(request.error);
                    request.onsuccess = () => resolve(request.result);
                });
                await new Promise((resolve, reject) => {
                    const tx = db.transaction("saves", "readwrite");
                    tx.onerror = () => reject(tx.error);
                    tx.objectStore("saves").put({ storyId, slot: "Broken", data: "not-bytes" });
                    tx.oncomplete = () => resolve();
                });
                db.close();
            }
            """, fixture.Compile().Id);

        await Driver.ClickAsync("Saves");
        await AssertDialogText(text =>
        {
            Assert.Contains("Healthy", text);
            Assert.Contains("Broken", text);
            Assert.Contains("invalid or oversized data", text);
        });
        await Driver.ClickAsync("Load Healthy");
        await Observe(new(null, "First passage"));
    }

    [Fact]
    [Trait("Host", "Linux")]
    public async Task Native_failed_replacement_preserves_old_payload_and_load_guard()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await AssertDialogText(text => Assert.Contains("Saved 'My save'.", text));
        await Driver.ClickAsync("Close saves");
        await Choose("Walk onward");
        var store = new FileSaveStore(FileSaveStore.DefaultRoot);
        var storyId = fixture.Compile().Id;
        var original = await store.ReadAsync(storyId, "My save");
        var path = Directory.EnumerateFiles(FileSaveStore.DefaultRoot, "*.json", SearchOption.AllDirectories)
            .Single(file => File.ReadAllBytes(file).SequenceEqual(original));
        var backup = path + ".test-backup";
        File.Move(path, backup);
        Directory.CreateDirectory(path); // A directory cannot be atomically replaced by a save file.
        try
        {
            await Driver.ClickAsync("Saves");
            await Driver.ClickAsync("Save game");
            await AssertDialogText(text => Assert.Contains("Saved games could not be read or written", text));
        }
        finally
        {
            Directory.Delete(path);
            File.Move(backup, path);
        }
        Assert.Equal(original, await store.ReadAsync(storyId, "My save"));
        await Driver.ClickAsync("Retry save list");
        await Driver.ClickAsync("Load My save");
        await AssertDialogText(text => Assert.Contains("discard unsaved progress", text));
        await Driver.ClickAsync("Keep playing");
        await Observe(new(null, "Second passage text."));
    }

    private async Task FillSaveName(string value)
    {
        if (Driver is NativePlayerDriver native)
            await native.FillAsync("Save name", value);
        else
            await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync(value);
    }

    private async Task AssertDialogText(Action<string> assertion)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var text = Driver is NativePlayerDriver native
                ? (await native.SnapshotAsync()).Content
                : await Page.GetByRole(AriaRole.Dialog, new() { Name = "Saves", Exact = true }).InnerTextAsync();
            try { assertion(text); return; }
            catch (Xunit.Sdk.XunitException) when (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(100);
            }
        }
    }

    [Fact]
    public async Task Manual_save_survives_host_reload_and_restores_history()
    {
        var fixture = Fixture();
        await Upload(fixture);
        await Choose("Walk onward");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        // Loading an exactly persisted state needs no discard confirmation.
        await Driver.ClickAsync("Load My save");
        await Observe(new(null, "Second passage text."));
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
            await ReloadAsync();
        }

        await Upload(fixture);
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
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
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Expect(SaveStatus).ToContainTextAsync("Saved 'My save'.");
        await Driver.ClickAsync("Close saves");
        await Choose("Walk onward");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Confirm overwrite", Exact = true })).ToBeVisibleAsync();
        await Driver.ClickAsync("Cancel overwrite");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Mara's"));
        await Choose("Walk onward");
        await Observe(new(null, "Second passage text."));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Driver.ClickAsync("Confirm overwrite");
        await Expect(SaveStatus).ToContainTextAsync("Saved 'My save'.");

        await Page.EvaluateAsync("""
            async storyId => {
                const module = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
                await module.write(storyId, 'My save', new TextEncoder().encode('{broken'), true);
            }
            """, fixture.Compile().Id);
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("malformed");
        await Driver.ClickAsync("Close saves");
        await Observe(new(null, "Second passage text."));
        await Choose("Continue");
        await Observe(new(null, "Finished safely.", Ended: true));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Edited_save_name_flows_to_parent_and_requires_explicit_overwrite()
    {
        await Upload(Fixture());
        await Driver.ClickAsync("Saves");
        await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync("Before the door");
        await Driver.ClickAsync("Save game");
        await Expect(SaveStatus).ToContainTextAsync("Saved 'Before the door'.");
        await Driver.ClickAsync("Save game");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Confirm overwrite", Exact = true })).ToBeVisibleAsync();
        await Page.GetByLabel("Save name", new() { Exact = true }).FillAsync("After the door");
        await Page.GetByLabel("Save name", new() { Exact = true }).BlurAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Confirm overwrite", Exact = true })).ToHaveCountAsync(0);
        await Driver.ClickAsync("Save game");
        await Expect(SaveStatus).ToContainTextAsync("Saved 'After the door'.");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Load Before the door", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Load After the door", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Unavailable_storage_is_a_visible_failure_not_a_success()
    {
        await Upload(Fixture());
        await Page.EvaluateAsync("() => Object.defineProperty(globalThis, 'indexedDB', { configurable: true, value: undefined })");
        await Driver.ClickAsync("Saves");
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("IndexedDB is unavailable");
        await Driver.ClickAsync("Close saves");
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
        await Driver.ClickAsync("Saves");
        var alert = Page.GetByRole(AriaRole.Alert);
        await Expect(alert).ToContainTextAsync(expected);
        await Expect(alert).Not.ToContainTextAsync("Internal storage detail");
        await Expect(alert).Not.ToContainTextAsync("IndexedDbSaveStore.js:");
        await Driver.ClickAsync("Close saves");
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
            ["story.talewright.yaml"] = fixture.Yaml,
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
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Limit");
        await Driver.ClickAsync("Load save");
        // Replay retains the full playthrough while presentation mounts only its recent window.
        await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("choice limit", new() { Timeout = 30_000 });
        await Expect(Page.Locator(".tw-frame")).ToHaveCountAsync(21);
        await Expect(Choices).ToBeDisabledAsync();
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Expect(Page.Locator(".tw-save-status")).ToContainTextAsync("Saved 'My save'");
        await Driver.ClickAsync("Close saves");
        await Driver.ClickAsync("Back");
        await Expect(Page.Locator(".tw-frame")).ToHaveCountAsync(21);
        await Expect(Choices).ToBeEnabledAsync();
        await Expect(Page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        await Driver.ClickAsync("Forward");
        await Expect(Page.Locator(".tw-frame")).ToHaveCountAsync(21);
        await Expect(Choices).ToBeDisabledAsync();
        await Restart();
        await Expect(Page.Locator(".tw-frame")).ToHaveCountAsync(1);
        await Expect(Choices).ToBeEnabledAsync();
        await Expect(Page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        await Choose("Again");
        await Observe(new(null, "At the limit."));
    }
}
