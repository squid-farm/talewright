using Talewright.Core.Identity;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public class CharacterSheetVisibilityTests
{
    [Theory]
    [InlineData(null, "inventory")]
    [InlineData(null, "stats,inventory,standing")]
    [InlineData(false, null)]
    public void Hidden_sheet_sections_keep_item_conditions_effects_and_modifiers(bool? sheet, string? hidden)
    {
        var story = (TestStories.Gates with
        {
            Yaml = TestStories.DefaultManifest(
                characterSheet: sheet,
                characterSheetSections: hidden?.Split(',').ToDictionary(section => section, _ => false)).Build(),
        }).Compile();
        Assert.False(story.CharacterSheet.Inventory);

        var hiddenPlay = Play(story);
        var hiddenHistory = hiddenPlay.History;

        Assert.Contains("Inventory: empty.", hiddenPlay.Frames[0]);
        Assert.DoesNotContain("Use the rope", hiddenPlay.Frames[0]);
        Assert.Contains("Inventory: ready.", hiddenPlay.Frames[^1]);
        Assert.Contains("Use the rope", hiddenPlay.Frames[^1]);
        Assert.Equal(2, hiddenHistory.State.GetInventory(EntityId.Player)
            .Single(item => item.Key == new ItemId("rope")).Value);
        Assert.Equal(12, hiddenHistory.State.GetStat(EntityId.Player, new StatId("wis")));
        Assert.Equal(20, hiddenHistory.State.GetStanding(new FactionId("guild")));

        hiddenHistory.Choose(Assert.Single(hiddenHistory.Current.Choices, choice => choice.Text == "Use the rope").Key);

        var shownPlay = Play(TestStories.Gates.Compile());
        shownPlay.History.Choose(Assert.Single(shownPlay.History.Current.Choices, choice => choice.Text == "Use the rope").Key);
        Assert.Equal(shownPlay.Frames, hiddenPlay.Frames);
        Assert.Equal(shownPlay.History.Events, hiddenHistory.Events);
        Assert.Equal(Describe(shownPlay.History.Current), Describe(hiddenHistory.Current));
    }

    private static (StoryHistory History, List<string> Frames) Play(StoryDefinition story)
    {
        var history = new StoryHistory(story, 42);
        var frames = new List<string> { Describe(history.Current) };
        foreach (var label in new[] { "Collect supplies", "Continue" })
        {
            history.Choose(Assert.Single(history.Current.Choices, choice => choice.Text == label).Key);
            frames.Add(Describe(history.Current));
        }
        return (history, frames);
    }

    private static string Describe(StoryFrame frame) =>
        $"{frame.Document.ToPlainText()}\n{string.Join("\n", frame.Choices.Select(choice => choice.Text))}";
}
