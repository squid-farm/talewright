using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Rules.Expressions;
using Talewright.Testing;

namespace Talewright.Runtime.Tests;

public sealed class RicherHubTests
{
    private static StoryHistory History(string hub, string extra = "")
    {
        var manifest = new ManifestFaker("hubs", "Hubs", "hub");
        manifest.Character("player", "Tester");
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = string.Join('\n',
                SceneFaker.Header("hub"),
                SceneFaker.Hub(hub),
                SceneFaker.HubLimit(2),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(999),
                ChoiceFaker.Create("END", "Explicit"),
                extra,
                SceneFaker.Header("blocked"),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(100),
                SceneFaker.Requires("false"),
                DivertFaker.To("hub"),
                SceneFaker.Header("first"),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(10),
                SceneFaker.Once,
                DivertFaker.To("hub"),
                SceneFaker.Header("second"),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(10),
                DivertFaker.To("hub"),
                SceneFaker.Header("third"),
                SceneFaker.Tags("a"),
                SceneFaker.Priority(9),
                DivertFaker.To("hub"),
                SceneFaker.Header("fourth"),
                SceneFaker.Tags("a", "b"),
                SceneFaker.Priority(8),
                DivertFaker.To("hub")),
        });
        var compiled = StoryCompiler.Compile(source);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        return new StoryHistory(compiled.Story!, 42);
    }

    [Theory]
    [InlineData("a and b", "fourth")]
    [InlineData("a or b", "third")]
    [InlineData("a", "third")]
    public void Filters_before_limiting_keeps_stable_ties_explicit_choices_and_replays_once(string query, string next)
    {
        var history = History(query);
        Assert.Equal(["c0", "storylet:first", "storylet:second"], history.Current.Choices.Select(c => c.Key));
        history.Choose("storylet:first");
        history.Choose("continue");
        Assert.Equal(["c0", "storylet:second", $"storylet:{next}"], history.Current.Choices.Select(c => c.Key));
        var loaded = StoryHistory.Restore(history.Story, history.Save());
        Assert.Equal(history.Current.Choices.Select(c => c.Key), loaded.Current.Choices.Select(c => c.Key));
        loaded.Back();
        loaded.Back();
        Assert.Contains(loaded.Current.Choices, c => c.Key == "storylet:first");
    }

    [Fact]
    public void Empty_hub_keeps_the_explicit_exit_choice()
    {
        var history = History("absent and b");
        Assert.Single(history.Current.Choices);
        Assert.Equal("c0", history.Current.Choices[0].Key);
        history.Choose("c0");
        Assert.True(history.Current.IsEnded);
    }

    [Fact]
    public void Replay_finds_a_hub_choice_even_when_its_requirement_now_hides_it()
    {
        var available = true;
        var predicates = new NamedPredicateRegistry().Register("Available", _ => available);
        var manifest = new ManifestFaker("hub-replay", "Hub replay", "hub");
        manifest.Character("player", "Tester");
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = manifest.Build(),
            ["main.tale"] = string.Join('\n',
                SceneFaker.Header("hub"),
                SceneFaker.Hub("room"),
                SceneFaker.Header("first"),
                SceneFaker.Tags("room"),
                SceneFaker.Requires("@Available"),
                DivertFaker.To("END"),
                SceneFaker.Header("second"),
                SceneFaker.Tags("room"),
                DivertFaker.To("END")),
        });
        var result = StoryCompiler.Compile(source, new CompilerOptions
        {
            CompatibilityId = "hub-replay-v1",
            Predicates = predicates,
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var history = new StoryHistory(result.Story!, 42);
        Assert.Contains(history.Current.Choices, choice => choice.Key == "storylet:first");
        history.Choose("storylet:first");
        var bytes = history.Save();

        available = false;
        var restored = StoryHistory.Restore(result.Story!, bytes);

        Assert.True(restored.Current.IsEnded);
        Assert.Equal(history.Events, restored.Events);
    }

    [Fact]
    public void Author_fork_rejects_a_restored_hub_choice_that_is_no_longer_offered()
    {
        var original = new StoryHistory(CompileRoom("room"), 42);
        original.Choose("storylet:first");
        var bytes = original.Save();
        Assert.Equal(original.Events, original.ForkAuthor().Events);

        var restored = StoryHistory.Restore(CompileRoom("elsewhere"), bytes);

        Assert.Equal(original.Events, restored.Events);
        var error = Assert.Throws<InvalidOperationException>(() => restored.ForkAuthor());
        Assert.Contains("Choice 1", error.Message);

        static StoryDefinition CompileRoom(string firstTags)
        {
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                ["story.talewright.yaml"] = """
                    game: { id: room-fork, title: Room fork, start: hub }
                    characters:
                      player: { first-name: Ada }
                    """,
                ["main.tale"] = string.Join('\n',
                    "=== hub",
                    "@hub room",
                    "* [Exit] -> END",
                    "=== first",
                    "@tags " + firstTags,
                    "-> END"),
            }));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Story!;
        }
    }

    [Theory]
    [InlineData("storylet-tags", false)]
    [InlineData("storylet-tags", true)]
    [InlineData("hub-query", false)]
    [InlineData("hub-query", true)]
    [InlineData("removed", false)]
    [InlineData("removed", true)]
    public void Hub_replay_ignores_tag_availability_but_requires_the_recorded_destination(string change, bool redo)
    {
        var original = new StoryHistory(Compile(), 42);
        original.Choose("storylet:first");
        if (redo) original.Back();
        var bytes = original.Save();
        var unchanged = bytes.ToArray();
        var updated = change switch
        {
            "storylet-tags" => Compile(firstTags: "elsewhere"),
            "hub-query" => Compile(hubTags: "other"),
            "removed" => Compile(includeFirst: false),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var live = new StoryHistory(updated, 42);
        var activeBefore = live.Save();
        Assert.DoesNotContain(live.Current.Choices, choice => choice.Key == "storylet:first");
        Assert.Throws<InvalidOperationException>(() => live.Choose("storylet:first"));

        if (change == "removed")
        {
            Assert.Throws<InvalidDataException>(() => StoryHistory.Restore(updated, bytes));
        }
        else
        {
            var restored = StoryHistory.Restore(updated, bytes);
            Assert.Equal(original.Cursor, restored.Cursor);
            Assert.Equal(original.Events, restored.Events);
            if (!redo) restored.Back();
            Assert.DoesNotContain(restored.Current.Choices, choice => choice.Key == "storylet:first");
            restored.Forward();
            Assert.True(restored.Current.IsEnded);
            Assert.Equal(restored.Events, StoryHistory.Restore(updated, restored.Save()).Events);
        }
        Assert.Equal(unchanged, bytes);
        Assert.Equal(activeBefore, live.Save());

        static StoryDefinition Compile(string hubTags = "room", string firstTags = "room", bool includeFirst = true)
        {
            var manifest = new ManifestFaker("hub-tags-replay", "Hub tag replay", "hub");
            manifest.Character("player", "Tester");
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                ["story.talewright.yaml"] = manifest.Build(),
                ["main.tale"] = string.Join('\n',
                    SceneFaker.Header("hub"),
                    SceneFaker.Hub(hubTags),
                    ChoiceFaker.Create("END", "Exit"),
                    includeFirst ? string.Join('\n',
                        SceneFaker.Header("first"),
                        SceneFaker.Tags(firstTags),
                        DivertFaker.To("END")) : "",
                    SceneFaker.Header("second"),
                    SceneFaker.Tags("room", "other"),
                    DivertFaker.To("END")),
            }));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return result.Story!;
        }
    }
}
