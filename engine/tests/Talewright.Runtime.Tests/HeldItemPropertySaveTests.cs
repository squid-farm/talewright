using Talewright.Core;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Runtime.Saves;
using Talewright.Talemark.Compilation;

namespace Talewright.Runtime.Tests;

public sealed class HeldItemPropertySaveTests
{
    [Fact]
    public void Item_variables_survive_save_load_replay_and_subsequent_crafting()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = """
                game: {id: crafting, title: Crafting, start: start, talemark: 2}
                characters:
                  player: {first-name: Ada}
                items:
                  iron-ore: {name: iron ore}
                  charcoal: {name: charcoal}
                  steel: {name: steel}
                """,
            ["main.tale"] = """
                === start
                ~ give player iron-ore; give player charcoal
                ~ var:first = item:iron-ore; var:second = item:charcoal
                -> forge
                === forge
                {if var:first == iron-ore}
                The ore glows.
                {/if}
                {if player has var:first and player has var:second}
                * [Combine {var:first} and {var:second}] { take player var:first; take player var:second; give player steel } -> after
                {/if}
                === after
                Your chosen ingredient was {var:first}.
                * [Finish] -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var original = new StoryHistory(story, 42);
        Assert.Equal("The ore glows.", original.Current.Document.ToPlainText());
        var loaded = StoryHistory.Restore(story, original.Save());
        Assert.Equal("iron-ore", loaded.State.GetTextVariable(new VarId("first")));
        foreach (var history in new[] { original, loaded })
        {
            history.Choose(history.Current.Choices.Single().Key);
            Assert.Equal(0, history.State.GetItemCount(EntityId.Player, new ItemId("iron-ore")));
            Assert.Equal(0, history.State.GetItemCount(EntityId.Player, new ItemId("charcoal")));
            Assert.Equal(1, history.State.GetItemCount(EntityId.Player, new ItemId("steel")));
            Assert.Equal("[Lost: iron ore]\n\n[Lost: charcoal]\n\n[Gained: steel]\n\nYour chosen ingredient was iron ore.",
                history.Current.Document.ToPlainText());
        }
        var replayed = StoryHistory.Restore(story, loaded.Save());
        Assert.Equal(original.Current.Document.ToPlainText(), replayed.Current.Document.ToPlainText());
        Assert.Equal(original.Events, replayed.Events);
    }

    [Fact]
    public void Changed_groups_survive_history_replay_and_save_load()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = """
                game:
                  id: held-item-save
                  title: Held item save
                  start: start
                  talemark: 2
                characters:
                  player: { first-name: Ada }
                items:
                  sword: { name: sword, sharpness: 5 }
                """,
            ["main.tale"] = """
                === start
                ~ give player sword 3
                ~ player:inventory:sword:sharpness += 2
                ~ player:inventory:sword:sharpness += 2
                * [Take the sharpened sword] { take player sword } -> after
                === after
                Done.
                -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var sword = new ItemId("sword");
        var original = new StoryHistory(story, 42);
        AssertGroups(original.State, (1, 9), (2, 5));

        var loaded = StoryHistory.Restore(story, original.Save());
        AssertGroups(loaded.State, (1, 9), (2, 5));

        original.Choose(original.Current.Choices.Single().Key);
        loaded.Choose(loaded.Current.Choices.Single().Key);
        AssertGroups(original.State, (2, 5));
        AssertGroups(loaded.State, (2, 5));

        var replayed = StoryHistory.Restore(story, original.Save());
        AssertGroups(replayed.State, (2, 5));
        Assert.Equal(2, replayed.State.GetExactItemCount(EntityId.Player, sword).ToInt32RoundedAwayFromZero());
    }

    private static void AssertGroups(IGameStateView state, params (int Quantity, int Sharpness)[] expected)
    {
        var groups = state.GetItemGroups(EntityId.Player, new ItemId("sword"));
        Assert.Equal(expected.Length, groups.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(ExactNumber.FromInteger(expected[index].Quantity), groups[index].Quantity);
            Assert.Equal(new ItemPropertyValue.Number(ExactNumber.FromInteger(expected[index].Sharpness)),
                groups[index].Properties["sharpness"]);
        }
    }
}
