using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Talewright.Runtime;
using Talewright.Runtime.Saves;
using Talewright.Testing;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Talewright.Player.E2E.Tests;

/// <summary>Players reopen an updated story with saves and an autosave from the previous revision.</summary>
public sealed partial class StoryUpdateTests(WebHostFixture host, ITestOutputHelper output) : PlayerTest(host, output)
{
    private const string Title = "River Crossing";
    private const string Incompatible = $"Version 1 of {Title} has changed, and this save is no longer compatible";

    private enum Revision
    {
        Original,
        Compatible,
        ChangedRecordedEffect,
        ChangedRecordedEffectAgain,
        RemovedCheckpointTarget,
    }

    private static StoryFixture Fixture(string id, Revision revision)
    {
        var updated = revision != Revision.Original;
        var manifest = new ManifestFaker(id, Title, "start", 42)
        {
            Version = revision switch
            {
                Revision.ChangedRecordedEffectAgain => "3",
                _ when updated => "2",
                _ => "1",
            },
            Randomness = "checks-only",
            BaseRuleset = "rpg",
        };
        manifest.Character("player", "Mara", "Burns", new Dictionary<string, int> { ["athletics"] = 2 }, "she");
        manifest.Number("rope", 0);
        var ropeEffect = revision switch
        {
            Revision.ChangedRecordedEffect => 2,
            Revision.ChangedRecordedEffectAgain => 3,
            _ => 1,
        };
        var stranger = revision == Revision.Compatible
            ? """
              === stranger
              @title Stranger
              The stranger vanishes into the trees.
              -> END
              """
            : string.Empty;
        var tale = revision == Revision.RemovedCheckpointTarget
            ? $$"""
                === start
                @title Camp
                The camp hums with insects.
                * [Take the new path] -> safe
                * [Stay home] -> END
                === safe
                @title Safe
                You are safe.
                * [Rest] -> END
                """
            : $$"""
            === start
            @title Camp
            {{(updated ? "The camp hums with insects." : "The camp is quiet tonight.")}}
            {{(revision == Revision.Compatible ? "* [Follow the mysterious man] -> stranger" : "")}}
            * [{{(revision == Revision.Compatible ? "Pack the rope" : "Bring the rope")}}] { var:rope += {{ropeEffect}} } -> river
            * [Stay home] -> END
            === river
            @title River
            You reach the river with {var:rope} coil of rope.
            * [Climb the bank] skill-check athletics dc 12
              success: { var:rope += 10 } -> bank
              failure: { var:rope += 100 } -> bank
            === bank
            @title Bank
            {{(updated ? "Coils counted" : "Rope count")}}: {var:rope}.
            * [Rest] -> END
            {{stranger}}
            """;
        return new StoryFixture(id, manifest.Build(), tale);
    }

    private static StoryFixture EffectUpdateFixture(string id, bool updated)
    {
        var manifest = new ManifestFaker(id, "Entry Effects", "start", 42)
        {
            Version = updated ? "2" : "1",
            BaseRuleset = "rpg",
        };
        manifest.Character("player", "Mara");
        manifest.Number("rope", 0);
        var tale = $$"""
            === start
            A quiet road.
            * [Travel] {{(updated ? "{ var:rope += 100 }" : "")}} -> middle
            === middle
            Middle rope: {var:rope}.
            * [Enter the cave] -> cave
            === cave
            {{(updated ? "~ var:rope += 5" : "")}}
            Cave rope: {var:rope}.
            * [Finish] -> END
            """;
        return new StoryFixture(id, manifest.Build(), tale);
    }

    private static byte[] ContinuedSave(string id)
    {
        var original = Fixture(id, Revision.Original).Compile();
        var history = new StoryHistory(original, 42);
        history.Choose("c0");
        var updated = Fixture(id, Revision.ChangedRecordedEffect).Compile();
        var continued = StoryHistory.Restore(updated, history.Save());
        Assert.NotNull(continued.Continuation);
        return continued.Save();
    }

    private static StoryFixture LongPlaythroughUpdateFixture(string id, bool updated)
    {
        var manifest = new ManifestFaker(id, "Long Playthrough", "start", 7)
        {
            Version = updated ? "2" : "1",
            BaseRuleset = "rpg",
        };
        manifest.Character("player", "Mara");
        manifest.Number("count", 0);
        var tale = $$"""
            === start
            Looping: {var:count}.
            * [Again] {{(updated ? "{ var:count += 1 }" : "")}} -> start
            """;
        return new StoryFixture(id, manifest.Build(), tale);
    }

    private static byte[] CorruptFinalByte(byte[] bytes)
    {
        var corrupted = bytes.ToArray();
        corrupted[^1] ^= 0xff;
        return corrupted;
    }

    [Fact]
    public async Task Compatible_update_resumes_autosave_and_loads_older_slot_with_current_story()
    {
        var id = $"update-{Guid.NewGuid():N}";
        var rope = await PlayOriginalAndSaveBeforeTheCheck(id);

        await Reopen(Fixture(id, Revision.Compatible), resumeAutosave: true);
        await AssertState(state =>
        {
            Assert.Contains($"Coils counted: {rope}.", state.Passage);
            Assert.Equal(["Rest"], state.Choices);
            Assert.Contains("The camp hums with insects.", state.Transcript);
            Assert.DoesNotContain("The camp is quiet tonight.", state.Transcript);
        });

        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "You reach the river with 1 coil of rope.", ["Climb the bank"]));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "The camp hums with insects.",
            ["Follow the mysterious man", "Pack the rope", "Stay home"]));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, "You reach the river with 1 coil of rope."));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, $"Coils counted: {rope}.", ["Rest"]));
        await Driver.ClickAsync("Back");
        await Driver.ClickAsync("Back");
        await Choose("Follow the mysterious man");
        await Observe(new(null, "The stranger vanishes into the trees.", Ended: true));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Added_effects_are_not_retroactive_and_later_entry_effects_run_once()
    {
        var id = $"update-{Guid.NewGuid():N}";
        var original = EffectUpdateFixture(id, updated: false);
        var history = new StoryHistory(original.Compile(), 42);
        history.Choose("c0");
        var bytes = history.Save();

        var updated = EffectUpdateFixture(id, updated: true);
        await Upload(updated);
        await WriteBrowserSaveAsync(id, "Before update", bytes);
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Before update");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "Middle rope: 0.", ["Enter the cave"]));

        await Choose("Enter the cave");
        await Observe(new(null, "Cave rope: 5.", ["Finish"]));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "Middle rope: 0.", ["Enter the cave"]));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, "Cave rope: 5.", ["Finish"]));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Startup_resume_prompt_shows_summary_and_resumes_a_continued_autosave()
    {
        var id = $"update-{Guid.NewGuid():N}";
        await PlayOriginalAndSaveBeforeTheCheck(id);
        var autosave = await ReadSave(id, SaveEntry.AutosaveSlot);
        var summary = SaveFormat.Read(autosave).Summary!;

        await Reopen(Fixture(id, Revision.ChangedRecordedEffect), resumeAutosave: null);
        await AssertPageText(text =>
        {
            Assert.Contains("Resume this story?", text);
            Assert.Contains(summary.SceneTitle, text);
            Assert.Contains(summary.CharacterName!, text);
        });

        await Driver.ClickAsync("Resume saved game");
        await AssertPageText(text => Assert.Contains(
            "This story has been updated since this save was made.", text));
        await AssertState(state => Assert.Contains("Coils counted:", state.Passage));
    }

    [Fact]
    public async Task Removed_checkpoint_target_rejects_load_without_changing_session_or_saves()
    {
        var id = $"update-{Guid.NewGuid():N}";
        await PlayOriginalAndSaveBeforeTheCheck(id);
        var slot = await ReadSave(id, "My save");
        var autosave = await ReadSave(id, SaveEntry.AutosaveSlot);

        await Reopen(Fixture(id, Revision.RemovedCheckpointTarget), resumeAutosave: null);
        await AssertPageText(text => Assert.Contains(
            "This save cannot continue in the updated story because the scene 'bank' this save continues from no longer exists. Open it with the story version that created it.",
            text));
        await Driver.ClickAsync("Start a new game");
        await Observe(new(null, "The camp hums with insects.", ["Take the new path", "Stay home"]));
        await AssertPageText(text => Assert.Contains("Your earlier autosave was kept as", text));
        var recovered = Assert.Single(await ListSaves(id),
            name => name.StartsWith("Recovered autosave ", StringComparison.Ordinal));
        Assert.Equal(autosave, await ReadSave(id, recovered));

        var before = await Driver.ObserveAsync();
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await AssertPageText(text => Assert.Contains(
            "This save cannot continue in the updated story because the scene 'river' this save continues from no longer exists. Open it with the story version that created it.",
            text));
        await Driver.ClickAsync("Close saves");
        var after = await Driver.ObserveAsync();
        Assert.Equal(before.Passage, after.Passage);
        Assert.Equal(before.Transcript, after.Transcript);
        Assert.Equal(before.Choices, after.Choices);
        Assert.Equal(slot, await ReadSave(id, "My save"));
        Assert.Equal(autosave, await ReadSave(id, recovered));
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Continued_save_import_shows_notice_and_malformed_continuations_preserve_the_slot()
    {
        var id = $"update-{Guid.NewGuid():N}";
        var updated = Fixture(id, Revision.ChangedRecordedEffectAgain);
        var continued = ContinuedSave(id);
        await Upload(updated);
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await Expect(SaveStatus).ToContainTextAsync("Saved 'My save'.");
        var original = await ReadSave(id, "My save");

        await ImportSaveAsync("continued" + SaveFormat.FileExtension, continued);
        var confirmation = Page.GetByRole(AriaRole.Region, new() { Name = "Confirm import" });
        await Expect(confirmation).ToBeVisibleAsync();
        await Driver.ClickAsync("Replace with imported save");
        await Expect(SaveStatus).ToContainTextAsync("Imported 'My save'.");
        Assert.Equal(continued, await ReadSave(id, "My save"));
        Assert.NotEqual(original, continued);

        await Driver.ClickAsync("Close saves");
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await AssertPageText(text => Assert.Contains(
            "This story has been updated since this save was made.", text));

        var invalid = new[]
        {
            continued[..^1],
            CorruptFinalByte(continued),
        };
        foreach (var bytes in invalid)
        {
            await Driver.ClickAsync("Saves");
            await ImportSaveAsync("invalid" + SaveFormat.FileExtension, bytes);
            await Expect(Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("malformed");
            Assert.Equal(continued, await ReadSave(id, "My save"));
            await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Confirm import" })).ToHaveCountAsync(0);
            await Driver.ClickAsync("Close saves");
        }
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Continuation_notice_clears_and_returns_when_loading_saves_again()
    {
        var id = $"update-{Guid.NewGuid():N}";
        await PlayOriginalAndSaveBeforeTheCheck(id);
        await Reopen(Fixture(id, Revision.ChangedRecordedEffect), resumeAutosave: true);
        await AssertPageText(text => Assert.Contains(
            "This story has been updated since this save was made.", text));

        await Choose("Rest");
        await AssertState(state => Assert.True(state.Ended, "The ended story offers Play again."));
        await AssertPageText(text => Assert.DoesNotContain(
            "This story has been updated since this save was made.", text));

        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Autosave");
        await Driver.ClickAsync("Load save");
        await AssertPageText(text => Assert.DoesNotContain(
            "This story has been updated since this save was made.", text));
        await Restart();
        await AssertPageText(text => Assert.DoesNotContain(
            "This story has been updated since this save was made.", text));

        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await AssertPageText(text =>
        {
            Assert.Contains("This story has been updated since this save was made.", text);
            Assert.Contains("Choices you had undone were discarded.", text);
        });
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Continued_playthrough_keeps_playing_past_the_former_choice_cap()
    {
        var id = $"update-long-{Guid.NewGuid():N}";
        var original = LongPlaythroughUpdateFixture(id, updated: false);
        var history = new StoryHistory(original.Compile(), 7);
        for (var index = 0; index < 9_999; index++)
        {
            history.Choose("c0");
        }

        var updated = LongPlaythroughUpdateFixture(id, updated: true);
        var continued = StoryHistory.Restore(updated.Compile(), history.Save());
        Assert.Equal(9_999, continued.PlaythroughLength);
        await Upload(updated);
        await WriteBrowserSaveAsync(id, "Long", continued.Save());
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load Long");
        await Driver.ClickAsync("Load save");
        await Expect(Choices).ToBeEnabledAsync();
        await Choose("Again");
        await Choose("Again");
        await Expect(Choices).ToBeEnabledAsync();
        await Expect(Page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
    }

    [Fact]
    [Trait("Host", "Browser")]
    public async Task Quota_failure_during_a_continued_session_keeps_saves_and_playing_state()
    {
        var id = $"update-quota-{Guid.NewGuid():N}";
        var updated = Fixture(id, Revision.ChangedRecordedEffectAgain);
        var continued = ContinuedSave(id);
        await Upload(updated);
        await WriteBrowserSaveAsync(id, "My save", continued);
        await WriteBrowserSaveAsync(id, "Pinned", continued);
        var autosave = await ReadSave(id, SaveEntry.AutosaveSlot);
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");

        await Page.EvaluateAsync("""
            () => {
                window.originalTransaction = IDBDatabase.prototype.transaction;
                IDBDatabase.prototype.transaction = function(...args) {
                    if (args[1] === "readwrite") throw new DOMException("test failure", "QuotaExceededError");
                    return window.originalTransaction.apply(this, args);
                };
            }
            """);
        try
        {
            await Choose("Climb the bank");
            await AssertPageText(text => Assert.Contains("not enough browser storage", text));
            await AssertState(state => Assert.Contains("Coils counted:", state.Passage));
            Assert.Equal(continued, await ReadSave(id, "My save"));
            Assert.Equal(continued, await ReadSave(id, "Pinned"));
            Assert.Equal(autosave, await ReadSave(id, SaveEntry.AutosaveSlot));
        }
        finally
        {
            await Page.EvaluateAsync("() => IDBDatabase.prototype.transaction = window.originalTransaction");
        }
    }

    [Fact]
    public async Task Changed_recorded_outcome_rejects_replay_only_autosave_and_slot_without_changing_them()
    {
        var id = $"update-{Guid.NewGuid():N}";
        await PlayOriginalAndSaveBeforeTheCheck(id);
        var slot = await MakeReplayOnly(id, "My save");
        var autosave = await MakeReplayOnly(id, SaveEntry.AutosaveSlot);

        await Reopen(Fixture(id, Revision.ChangedRecordedEffect), resumeAutosave: null);
        await AssertPageText(text => Assert.Contains(Incompatible, text));
        await Driver.ClickAsync("Start a new game");
        await Observe(new(null, "The camp hums with insects.", ["Bring the rope", "Stay home"]));
        await AssertPageText(text => Assert.Contains("Your earlier autosave was kept as", text));
        var recovered = Assert.Single(await ListSaves(id), name => name.StartsWith("Recovered autosave ", StringComparison.Ordinal));
        Assert.Equal(autosave, await ReadSave(id, recovered));

        await Choose("Bring the rope");
        await Observe(new(null, "You reach the river with 2 coil of rope."));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await AssertPageText(text => Assert.Contains(Incompatible, text));
        await Driver.ClickAsync("Close saves");
        await Observe(new(null, "You reach the river with 2 coil of rope.", ["Climb the bank"]));
        Assert.Equal(slot, await ReadSave(id, "My save"));
        Assert.Equal(autosave, await ReadSave(id, recovered));
    }

    [Fact]
    public async Task Changed_recorded_outcome_continues_autosave_and_slot_from_the_checkpoint()
    {
        var id = $"update-{Guid.NewGuid():N}";
        var rope = await PlayOriginalAndSaveBeforeTheCheck(id);
        var slot = await ReadSave(id, "My save");
        var autosave = await ReadSave(id, SaveEntry.AutosaveSlot);

        await Reopen(Fixture(id, Revision.ChangedRecordedEffect), resumeAutosave: true);
        await AssertState(state =>
        {
            Assert.Contains($"Coils counted: {rope}.", state.Passage);
            Assert.Equal(["Rest"], state.Choices);
            Assert.Contains("The camp is quiet tonight.", state.Transcript);
            Assert.DoesNotContain("The camp hums with insects.", state.Transcript);
        });
        await AssertPageText(text => Assert.Contains(
            "This story has been updated since this save was made. Your progress and History were kept, and the story continues from here using the new version. You can't go Back before this point.",
            text));
        Assert.Equal(autosave, await ReadSave(id, SaveEntry.AutosaveSlot));

        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Load My save");
        await Driver.ClickAsync("Load save");
        await Observe(new(null, "You reach the river with 1 coil of rope.", ["Climb the bank"]));
        await AssertPageText(text =>
        {
            Assert.Contains("Choices you had undone were discarded.", text);
            Assert.DoesNotContain("Recovered autosave", text);
        });
        await AssertNavigationDisabled();
        Assert.Equal(slot, await ReadSave(id, "My save"));

        await Choose("Climb the bank");
        await Observe(new(null, $"Coils counted: {rope}.", ["Rest"]));
        await AssertPageText(text => Assert.DoesNotContain(
            "This story has been updated since this save was made.", text));
        Assert.Equal(slot, await ReadSave(id, "My save"));
        Assert.DoesNotContain(await ListSaves(id), name => name.StartsWith("Recovered autosave ", StringComparison.Ordinal));

        await Reopen(Fixture(id, Revision.ChangedRecordedEffect), resumeAutosave: true);
        await Observe(new(null, $"Coils counted: {rope}.", ["Rest"]));
        await AssertPageText(text => Assert.DoesNotContain(
            "This story has been updated since this save was made.", text));
        await Driver.ClickAsync("Back");
        await Observe(new(null, "You reach the river with 1 coil of rope.", ["Climb the bank"]));
        await Driver.ClickAsync("Forward");
        await Observe(new(null, $"Coils counted: {rope}.", ["Rest"]));
        Assert.Equal(slot, await ReadSave(id, "My save"));
    }

    private async Task AssertNavigationDisabled()
    {
        if (Driver is NativePlayerDriver native)
        {
            var buttons = (await native.SnapshotAsync()).Descendants()
                .Where(node => node.Role == "push button").ToArray();
            Assert.False(Assert.Single(buttons, node => node.Name == "Back").Enabled);
            Assert.False(Assert.Single(buttons, node => node.Name == "Forward").Enabled);
            return;
        }

        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Back", Exact = true })).ToBeDisabledAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Forward", Exact = true })).ToBeDisabledAsync();
    }

    /// <summary>Saves at the river with the check as redo; the autosave stays at the bank after the check.</summary>
    private async Task<int> PlayOriginalAndSaveBeforeTheCheck(string id)
    {
        await Upload(Fixture(id, Revision.Original));
        await Observe(new(null, "The camp is quiet tonight.", ["Bring the rope", "Stay home"]));
        await Choose("Bring the rope");
        await Observe(new(null, "You reach the river with 1 coil of rope."));
        await Choose("Climb the bank");
        var rope = 0;
        await AssertState(state =>
        {
            var match = RopeCount().Match(state.Passage);
            Assert.True(match.Success, state.Passage);
            rope = int.Parse(match.Groups[1].Value);
        });
        Assert.Contains(rope, new[] { 11, 101 });
        await Driver.ClickAsync("Back");
        await Observe(new(null, "You reach the river with 1 coil of rope."));
        await Driver.ClickAsync("Saves");
        await Driver.ClickAsync("Save game");
        await AssertPageText(text => Assert.Contains("Saved 'My save'.", text));
        await Driver.ClickAsync("Close saves");
        return rope;
    }

    /// <summary>Restarts the host and opens a story revision. A null resume choice leaves any startup prompt open.</summary>
    private async Task Reopen(StoryFixture fixture, bool? resumeAutosave)
    {
        if (PlayerTarget.IsDesktop)
        {
            await Driver.DisposeAsync();
            await Driver.StartAsync();
        }
        else
        {
            await ReloadAsync();
        }

        if (resumeAutosave is { } resume)
        {
            await Upload(fixture, resumeAutosave: resume);
        }
        else
        {
            await UploadBytes(fixture.Id + ".zip", fixture.Zip());
        }
    }

    private async Task<byte[]> ReadSave(string storyId, string slot)
    {
        if (!PlayerTarget.IsDesktop)
        {
            return await ReadBrowserSaveAsync(storyId, slot);
        }

        var store = new FileSaveStore(NativeSaveRoot);
        return slot == SaveEntry.AutosaveSlot ? await store.ReadAutosaveAsync(storyId) : await store.ReadAsync(storyId, slot);
    }

    private async Task ImportSaveAsync(string name, byte[] bytes)
    {
        var input = Page.GetByLabel("Import save file", new() { Exact = true });
        await Expect(input).ToBeEnabledAsync();
        var previous = await input.ElementHandleAsync();
        await input.SetInputFilesAsync(new FilePayload
        {
            Name = name,
            MimeType = "application/octet-stream",
            Buffer = bytes,
        });
        await Page.WaitForFunctionAsync("element => !element.isConnected", previous);
    }

    private async Task<byte[]> MakeReplayOnly(string storyId, string slot)
    {
        var bytes = SaveFormat.Write(SaveFormat.Read(await ReadSave(storyId, slot)) with { Continuation = null });
        if (PlayerTarget.IsDesktop)
        {
            var store = new FileSaveStore(NativeSaveRoot);
            if (slot == SaveEntry.AutosaveSlot)
            {
                await store.WriteAutosaveAsync(storyId, bytes);
            }
            else
            {
                await store.WriteAsync(storyId, slot, bytes, overwrite: true);
            }
        }
        else
        {
            await WriteBrowserSaveAsync(storyId, slot, bytes, overwrite: true);
        }
        return bytes;
    }

    private async Task<IReadOnlyList<string>> ListSaves(string storyId) => PlayerTarget.IsDesktop
        ? await new FileSaveStore(NativeSaveRoot).ListAsync(storyId)
        : await Page.EvaluateAsync<string[]>("""
            async storyId => {
                const module = await import('./_content/Talewright.Player/Saves/IndexedDbSaveStore.js');
                return await module.list(storyId);
            }
            """, storyId);

    private async Task AssertPageText(Action<string> assertion)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var text = Driver is NativePlayerDriver native
                ? (await native.SnapshotAsync()).Content
                : await Page.Locator("body").InnerTextAsync();
            try { assertion(text); return; }
            catch (Xunit.Sdk.XunitException) when (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(100);
            }
        }
    }

    [GeneratedRegex(@"Rope count: (\d+)\.")]
    private static partial Regex RopeCount();
}
