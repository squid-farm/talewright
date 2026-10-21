using System.Text.Json.Nodes;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Tale.Compilation;
using Talewright.Tale.Model;

namespace Talewright.Runtime.Tests;

public sealed class CharacterCreationTests
{
    private static StoryDefinition Story(string creation = """
        creation:
          name: true
          pronouns: [they, she, he]
          stats:
            str: [8, 12, 16]
          presets:
            explorer: {label: Explorer, stats: {str: 12, hp: 15}}
            scholar: {label: Scholar, stats: {str: 8, hp: 10}}
        """)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.yaml"] = "id: creation\ntitle: Creation\nstart: a\ncharacters:\n  player: {first-name: Default}\n" + creation,
            ["main.tale"] = "=== a\n{player:first-name} {player:pronoun.subject} {player:str}.\n* [Next] -> b\n=== b\nDone.\n-> END\n",
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.Story!;
    }

    [Fact]
    public void Selection_initializes_identity_and_stats_before_scene_and_survives_replay()
    {
        var story = Story();
        var selection = story.Creation!.Default(story.World.Characters[EntityId.Player]) with
        {
            FirstName = "Robin", LastName = "Vale", Pronouns = "she", Stats = new Dictionary<string, int> { ["str"] = 16 },
        };
        var history = new StoryHistory(story, 42, selection);
        Assert.IsType<CharacterIdentityChanged>(history.Events[0]);
        Assert.True(history.Events.TakeWhile(e => e is not SceneEntered).All(e => e is CharacterIdentityChanged or StatChanged));
        Assert.Equal("Robin Vale", history.State.GetCharacterDefinition(EntityId.Player).FullName);
        Assert.Equal("she", history.State.GetCharacterDefinition(EntityId.Player).Pronouns.Subject);
        Assert.Equal(16, history.State.GetBaseStat(EntityId.Player, new StatId("str")));
        Assert.Equal(15, history.State.GetBaseStat(EntityId.Player, new StatId("hp")));
        history.Choose("c0");
        history.Back();
        var saved = StoryHistory.Restore(story, history.Save());
        Assert.Equal(history.Events, saved.Events);
        Assert.Equal("Robin Vale", saved.State.GetCharacterDefinition(EntityId.Player).FullName);
        Assert.True(saved.CanForward);
        saved.Forward();
        var replay = GameState.CreateInitial(story.World, 42);
        foreach (var e in saved.Events)
        {
            replay.Apply(e);
        }
        Assert.Equal(saved.State.GetCharacterDefinition(EntityId.Player), replay.GetCharacterDefinition(EntityId.Player));
        Assert.Equal("Default", story.World.Characters[EntityId.Player].FirstName);
    }

    [Fact]
    public void Invalid_inputs_cannot_start_or_restore_and_selection_is_snapshotted()
    {
        var story = Story();
        var valid = story.Creation!.Default(story.World.Characters[EntityId.Player]);
        Assert.Throws<ArgumentException>(() => new StoryHistory(story));
        foreach (var invalid in new[]
        {
            valid with { FirstName = "" }, valid with { FirstName = " padded " },
            valid with { FirstName = new string('x', 81) }, valid with { Pronouns = "invalid" },
            valid with { Preset = "missing" }, valid with { Stats = new Dictionary<string, int> { ["str"] = 999 } },
            valid with { Stats = new Dictionary<string, int>() },
        })
        {
            Assert.Throws<ArgumentException>(() => new StoryHistory(story, character: invalid));
        }
        var stats = new Dictionary<string, int>(valid.Stats);
        var history = new StoryHistory(story, character: valid with { Stats = stats });
        stats["str"] = 999;
        Assert.Equal(12, history.Character!.Stats["str"]);
        var json = JsonNode.Parse(history.Save())!;
        json["character"]!["stats"]!["str"] = 999;
        Assert.Contains("Invalid saved character", Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(story, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()))).Message);
    }

    [Fact]
    public void Predefined_stories_keep_old_save_shape_compatibility_and_reject_creation_input()
    {
        var story = Story("");
        var history = new StoryHistory(story, 42);
        var json = JsonNode.Parse(history.Save())!.AsObject();
        json.Remove("character");
        Assert.Equal(history.Events, StoryHistory.Restore(story, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())).Events);
        Assert.Throws<ArgumentException>(() => new StoryHistory(story, character: new CharacterSelection()));
    }
}
