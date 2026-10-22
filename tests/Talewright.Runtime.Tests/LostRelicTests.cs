using Talewright.Tale.Compilation;
using Talewright.Tale.Model;
using Talewright.Core.Identity;

namespace Talewright.Runtime.Tests;

public class LostRelicTests
{
    private static StoryDefinition Story()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Talewright.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var result = StoryCompiler.Compile(StorySource.FromDirectory(Path.Combine(directory.FullName, "samples", "LostRelic")));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        return result.Story!;
    }

    private static StoryHistory History(string preset = "explorer")
    {
        var story = Story();
        return new StoryHistory(story, 42, story.Creation!.Default(story.World.Characters[EntityId.Player], preset));
    }

    private static void Choose(StoryHistory history, string label) =>
        history.Choose(Assert.Single(history.Current.Choices, c => c.Text == label).Key);

    [Fact]
    public void Shipped_sample_compiles_without_errors_or_warnings() => Story();

    [Fact]
    public void Workshop_demonstrates_capped_intersection_hub_inventory_arithmetic_and_replay()
    {
        var history = History();
        Assert.Equal(3, history.State.GetBaseStat(EntityId.Player, new StatId("courage")));
        Choose(history, "Continue");
        Choose(history, "Visit the expedition workshop");
        Assert.Equal(["storylet:workshop.packing", "storylet:workshop.ledger"],
            history.Current.Choices.Select(c => c.Key));
        Choose(history, "Pack a trail lunch");
        Assert.Equal(2, history.State.GetItemCount(EntityId.Player, new ItemId("ration")));
        Assert.True(history.State.GetFlag(new FlagId("packing-open")));
        Choose(history, "Eat one ration");
        Assert.Equal(1, history.State.GetItemCount(EntityId.Player, new ItemId("ration")));
        Assert.False(history.State.GetFlag(new FlagId("packing-open")));
        Choose(history, "Continue");
        Assert.Equal(["storylet:workshop.ledger", "storylet:workshop.identity"],
            history.Current.Choices.Select(c => c.Key));
        Choose(history, "Balance the expedition ledger");
        Assert.Equal(10, history.State.GetVariable(new VarId("ledger")));
        Choose(history, "Agree on ten crates");
        Choose(history, "Sign the expedition register");
        Choose(history, "Continue");
        Assert.Equal("workshop.hub", history.Current.Scene!.Value.Value);
        Assert.Equal("continue", Assert.Single(history.Current.Choices).Key);
        Choose(history, "Continue");
        Assert.Equal("camp.hub", history.Current.Scene!.Value.Value);

        var restored = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(history.Events, restored.Events);
        restored.Back();
        restored.Forward();
        Assert.Equal(history.Events, restored.Events);
    }

    [Fact]
    public void Field_union_hub_changes_eligibility_clamps_custom_stat_and_fires_trigger()
    {
        var history = History();
        Choose(history, "Continue");
        Choose(history, "Try the field exercises");
        Assert.Equal("storylet:field.resolve", Assert.Single(history.Current.Choices).Key);
        Choose(history, "Stand your ground");
        Assert.Equal(10, history.State.GetStat(EntityId.Player, new StatId("courage")));
        Assert.Equal(5, history.State.GetBaseStat(EntityId.Player, new StatId("xp")));
        Assert.True(history.State.HasFiredTrigger("courage-earned"));
        Assert.True(history.State.GetFlag(new FlagId("steady")));
        Choose(history, "Thank Tomas for the lesson");
        Assert.Equal("storylet:field.signal", Assert.Single(history.Current.Choices).Key);
        Choose(history, "Follow the signal");
        Choose(history, "Continue");
        Assert.Equal("field.ready", history.Current.Scene!.Value.Value);
        Choose(history, "Continue");
        Choose(history, "Test the watch bell");
        Choose(history, "Ring the bell");
        Assert.Equal(1, history.State.GetVariable(new VarId("bell-count")));
        Choose(history, "Silence the bell");
        Choose(history, "Ring the bell");
        Assert.Equal(2, history.State.GetVariable(new VarId("bell-count")));
        Choose(history, "Finish the watch drill");
        Choose(history, "Continue");
        Assert.Equal("camp.hub", history.Current.Scene!.Value.Value);
    }

    [Theory]
    [InlineData("explorer", "guild", "ending.guild")]
    [InlineData("scholar", "keep", "ending.keep")]
    [InlineData("explorer", "leave", "ending.empty-handed")]
    public void Created_characters_can_finish_each_ending_and_restore(string preset, string outcome, string ending)
    {
        var history = History(preset);
        Choose(history, "Continue");
        Choose(history, "Haggle for supplies");
        Choose(history, "Continue");
        Choose(history, "Set out for the temple");
        Choose(history, "Tie off the rope and cross carefully");
        Choose(history, "Continue");
        Choose(history, "Look for another way in");
        Choose(history, "Squeeze through");
        Choose(history, "Continue");
        if (history.Current.Scene == new SceneId("temple.niche"))
        {
            Choose(history, "Take the idol");
        }
        Choose(history, outcome == "leave" ? "Leave it where it lies" : "Take the relic");
        Choose(history, outcome switch
        {
            "guild" => "Hand the relic to the guild",
            "keep" => "Keep it for yourself",
            _ => "Continue",
        });
        Assert.True(history.Current.IsEnded);
        Assert.Equal(ending, history.Current.Scene!.Value.Value);
        Assert.Equal(outcome == "keep", history.State.GetItemCount(EntityId.Player, new ItemId("relic")) == 1);
        if (outcome == "keep")
        {
            Assert.True(history.State.GetFlag(new FlagId("guild-wary")));
        }
        Assert.Equal(history.Events, StoryHistory.Restore(history.Story, history.Save()).Events);
    }
}
