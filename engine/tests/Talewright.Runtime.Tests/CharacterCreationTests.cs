using System.Text.Json.Nodes;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class CharacterCreationTests
{
    private static StoryDefinition Story(bool creation = true)
    {
        var manifest = new ManifestFaker("creation", "Creation", "a")
        {
            CreationName = creation ? true : null,
        };
        manifest.Character("player", "Default");
        if (creation)
        {
            manifest.CreationPronouns("they", "she", "he");
            manifest.CreationStat("str", 8, 12, 16);
            manifest.CreationPreset("explorer", "Explorer", new Dictionary<string, int> { ["str"] = 12, ["hp"] = 15 });
            manifest.CreationPreset("scholar", "Scholar", new Dictionary<string, int> { ["str"] = 8, ["hp"] = 10 });
        }

        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = string.Join('\n',
                SceneFaker.Header("a"),
                $"{SubstitutionFaker.Character(TaleCharacterRef.Player, "first-name")} {SubstitutionFaker.Character(TaleCharacterRef.Player, "pronoun")} {SubstitutionFaker.Character(TaleCharacterRef.Player, "str")}.",
                ChoiceFaker.Create("b", "Next"),
                SceneFaker.Header("b"),
                "Done.",
                DivertFaker.To("END")),
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
        Assert.Equal("Robin Vale", history.CaptureSummary(DateTimeOffset.UtcNow).CharacterName);
        Assert.NotEqual(history.ProgressKey, new StoryHistory(story, 42, selection with { FirstName = "Other" }).ProgressKey);
        Assert.NotEqual(history.ProgressKey, new StoryHistory(story, 42, selection with { Pronouns = "they" }).ProgressKey);
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
    public void Validated_stats_use_declared_ids_even_with_a_case_insensitive_caller_dictionary()
    {
        var story = Story();
        var selection = story.Creation!.Default(story.World.Characters[EntityId.Player]) with
        {
            Stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["STR"] = 16 },
        };

        var validated = CharacterSelection.Validate(story, selection)!;
        Assert.Equal(["str"], validated.Stats.Keys);
        Assert.Throws<KeyNotFoundException>(() => validated.Stats["STR"]);

        var session = new StorySession(story, 42, character: selection);
        Assert.Equal(16, session.State.GetBaseStat(EntityId.Player, new StatId("str")));
        Assert.Equal(16, new StoryHistory(story, 42, selection).State.GetBaseStat(EntityId.Player, new StatId("str")));
    }

    [Fact]
    public void Predefined_stories_roundtrip_null_character_and_reject_creation_input()
    {
        var story = Story(creation: false);
        var history = new StoryHistory(story, 42);
        var bytes = history.Save();
        Assert.Null(history.Character);
        Assert.Equal(history.Events, StoryHistory.Restore(story, bytes).Events);
        Assert.Throws<ArgumentException>(() => new StoryHistory(story, character: new CharacterSelection()));
    }

    [Fact]
    public void Saves_require_the_character_field_even_for_predefined_stories()
    {
        var story = Story(creation: false);
        var history = new StoryHistory(story, 42);
        var bytes = history.Save();
        var json = JsonNode.Parse(bytes)!.AsObject();
        Assert.True(json.ContainsKey("character"));
        Assert.Null(json["character"]);
        json.Remove("character");
        var error = Assert.Throws<InvalidDataException>(() =>
            StoryHistory.Restore(story, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Contains("malformed", error.Message);
        Assert.Equal(bytes, history.Save());
    }
}
