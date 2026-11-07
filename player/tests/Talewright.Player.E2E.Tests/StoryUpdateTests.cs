using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Talewright.Runtime.Saves;
using Talewright.Testing;
using Xunit.Abstractions;

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
    }

    private static StoryFixture Fixture(string id, Revision revision)
    {
        var updated = revision != Revision.Original;
        var manifest = new ManifestFaker(id, Title, "start", 42)
        {
            Version = updated ? "2" : "1",
            Randomness = "checks-only",
        };
        manifest.Character("player", "Mara", "Burns", new Dictionary<string, int> { ["athletics"] = 2 }, "she");
        manifest.Number("rope", 0);
        var ropeEffect = revision == Revision.ChangedRecordedEffect ? 2 : 1;
        var tale = $$"""
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
            === stranger
            @title Stranger
            The stranger vanishes into the trees.
            -> END
            """;
        return new StoryFixture(id, manifest.Build(), tale);
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
    public async Task Changed_recorded_outcome_rejects_autosave_and_slot_without_changing_them()
    {
        var id = $"update-{Guid.NewGuid():N}";
        await PlayOriginalAndSaveBeforeTheCheck(id);
        var slot = await ReadSave(id, "My save");
        var autosave = await ReadSave(id, SaveEntry.AutosaveSlot);

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
