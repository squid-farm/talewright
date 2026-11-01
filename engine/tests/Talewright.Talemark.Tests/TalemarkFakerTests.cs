using Talewright.Core.Identity;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class TalemarkFakerTests
{
    [Fact]
    public void Composed_syntax_and_manifest_produce_a_story_the_compiler_and_analyzer_accept()
    {
        var manifest = new ManifestFaker("faker-test", "Faker test", "start", 42);
        var count = manifest.Number("count", 1);
        var ready = manifest.Flag("ready");
        var player = manifest.Character("player", "Tester");

        var tale = string.Join('\n',
            SceneFaker.Header("start"),
            SceneFaker.Title("Start"),
            IfFaker.Block(ready, SceneFaker.Speaker(player, SubstitutionFaker.Variable(count))),
            SkillCheckFaker.Entry("perception", 12, EffectFaker.Increase(count, 1),
                EffectFaker.Decrease(player.Field("hp"), 1)),
            ChoiceFaker.Create("END", "Finish", ExpressionFaker.GreaterThan(count, 0),
                EffectFaker.Increase(count, 1)),
            ChoiceFaker.Create("END", "Leave"));
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest.Build(),
            ["main.tale"] = tale,
        });

        var compiled = StoryCompiler.Compile(source);
        var analysis = StoryAnalyzer.Analyze(source);

        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal(2, Assert.Single(compiled.Story!.Scenes).Choices.Count);
        Assert.Single(analysis.Scenes);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Manifest_values_are_escaped_and_text_variables_remain_text()
    {
        var manifest = new ManifestFaker("faker-test", "A: title # with \"quotes\"", "start", 42)
        {
            Version = "1.0.0",
            BaseRuleset = "generic",
        };
        var count = manifest.Number("count", 3);
        manifest.Text("label", "12");
        manifest.Text("weather", "snow: # \"\u96ea\U0001F642\"");
        var ready = manifest.Flag("ready");
        var coin = manifest.Item("coin", "silver: # \"coin\"");
        var player = manifest.Character("player", "Ada: # \"A\"", "O'Neil",
            new Dictionary<string, int> { ["int"] = 2 });
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest.Build(),
            ["main.tale"] = string.Join('\n', SceneFaker.Header("start"),
                IfFaker.Inline(ready, SubstitutionFaker.Variable(count)),
                ChoiceFaker.Create("END", "Done")),
        });

        var compiled = StoryCompiler.Compile(source);

        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        var story = compiled.Story!;
        Assert.Equal("A: title # with \"quotes\"", story.Title);
        Assert.Equal(3, story.World.Variables[new VarId(count.Id)]);
        Assert.Equal("12", story.World.TextVariables[new VarId("label")]);
        Assert.Equal("snow: # \"\u96ea\U0001F642\"", story.World.TextVariables[new VarId("weather")]);
        Assert.Contains(new FlagId(ready.Id), story.World.Flags);
        Assert.Equal("silver: # \"coin\"", story.World.Items[new ItemId(coin.Id)].Name);
        Assert.Equal("Ada: # \"A\"", story.World.Characters[new EntityId(player.Id)].FirstName);
        Assert.Equal("O'Neil", story.World.Characters[new EntityId(player.Id)].LastName);
        Assert.Equal(manifest.Build(), manifest.Build());
    }

    [Fact]
    public void Manifest_keeps_sections_in_first_declaration_order()
    {
        var manifest = new ManifestFaker("slow-input", "Slow input", "start", 42);
        manifest.Character("player", "Tester");
        manifest.Number("x", 1);
        manifest.Flag("ready");
        manifest.Number("count", 1);

        Assert.Equal("""
            game:
              id: slow-input
              title: Slow input
              start: start
              seed: 42
            characters:
              player: {first-name: Tester}
            vars: {x: 1, count: 1}
            flags: [ready]
            """, manifest.Build());
    }

    [Fact]
    public void Manifest_builds_factions_modifiers_pronouns_and_triggers()
    {
        var manifest = new ManifestFaker("faker-test", "Faker test", "start", 42)
        {
            Randomness = "off",
            Undo = false,
            CreationName = true,
        };
        var ready = new TaleFlagRef("ready");
        manifest.Character("player", "Tester", pronouns: "they");
        manifest.Faction("guild", "Test Guild", 3);
        manifest.Item("charm", "Test charm", new Dictionary<string, int> { ["wis"] = 2 });
        manifest.Trigger("welcome", "faction:guild >= friendly", EffectFaker.Set(ready), once: true);

        var yaml = manifest.Build();
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml,
            ["main.tale"] = $"{SceneFaker.Header("start")}\n{DivertFaker.To("END")}",
        });
        var compiled = StoryCompiler.Compile(source);

        Assert.Contains("randomness: off", yaml);
        Assert.Contains("undo: false", yaml);
        Assert.Contains("creation:", yaml);
        Assert.Contains("pronouns: they", yaml);
        Assert.Contains("factions:", yaml);
        Assert.Contains("modifiers: { wis: 2 }", yaml);
        Assert.Contains("once: true", yaml);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal(3, compiled.Story!.World.Factions[new FactionId("guild")].InitialStanding);
        Assert.True(Assert.Single(compiled.Story.Triggers).Once);
    }

    [Fact]
    public void Manifest_rejects_duplicate_declarations_instead_of_overwriting_them()
    {
        var manifest = new ManifestFaker("faker-test", "Faker test", "start");
        manifest.Number("count", 1);
        manifest.Flag("ready");
        manifest.Item("coin", "Coin");
        manifest.Character("player", "Tester");

        Assert.Throws<ArgumentException>(() => manifest.Text("count", "duplicate"));
        Assert.Throws<ArgumentException>(() => manifest.Flag("ready"));
        Assert.Throws<ArgumentException>(() => manifest.Item("coin", "Another"));
        Assert.Throws<ArgumentException>(() => manifest.Character("player", "Another"));
        Assert.Throws<ArgumentException>(() => manifest.Number("", 0));
        Assert.Throws<ArgumentException>(() => manifest.Flag("bad name"));
        Assert.Throws<ArgumentException>(() => new TaleVariableRef("1invalid"));
        Assert.Throws<ArgumentException>(() => TaleCharacterRef.Player.Field("bad stat"));
        Assert.Throws<ArgumentNullException>(() => IfFaker.Open((string)null!));
        Assert.Throws<ArgumentNullException>(() => DivertFaker.To(null!));
        Assert.Throws<ArgumentNullException>(() => ChoiceFaker.Open(null!));
        Assert.Throws<ArgumentNullException>(() => EffectFaker.Assign(new TaleVariableRef("count"), (string)null!));
    }

    [Fact]
    public void Manifest_quotes_only_core_yaml_keywords()
    {
        var manifest = new ManifestFaker("faker-test", "yes", "start");
        manifest.Character("player", "on");
        manifest.Text("answer", "true");
        manifest.Text("nothing", "null");
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest.Build(),
            ["main.tale"] = $"{SceneFaker.Header("start")}\n{DivertFaker.To("END")}",
        });

        var compiled = StoryCompiler.Compile(source);

        Assert.Contains("title: yes", manifest.Build());
        Assert.Contains("first-name: on", manifest.Build());
        Assert.Contains("answer: \"true\"", manifest.Build());
        Assert.Contains("nothing: \"null\"", manifest.Build());
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal("yes", compiled.Story!.Title);
        Assert.Equal("on", compiled.Story.World.Characters[EntityId.Player].FirstName);
        Assert.Equal("true", compiled.Story.World.TextVariables[new("answer")]);
        Assert.Equal("null", compiled.Story.World.TextVariables[new("nothing")]);
    }

    [Fact]
    public void Manifest_supports_the_full_nonnegative_seed_range()
    {
        var manifest = new ManifestFaker("faker-test", "Faker test", "start", ulong.MaxValue);
        manifest.Character("player", "Tester");
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = manifest.Build(),
            ["main.tale"] = $"{SceneFaker.Header("start")}\n{DivertFaker.To("END")}",
        });

        var compiled = StoryCompiler.Compile(source);

        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal(ulong.MaxValue, compiled.Story!.Seed);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManifestFaker("id", "Title", "start", -1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(57057)]
    public void Seeded_story_fakers_generate_compilable_stories(int seed)
    {
        var fake = new TaleFaker(seed);
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = fake.Manifest("start"),
            ["main.tale"] = fake.Scene("start", fake.Choice("END")),
        });

        var compiled = StoryCompiler.Compile(source);

        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Single(compiled.Story!.Scenes);
    }

    [Fact]
    public void Skill_check_choice_and_entry_fakers_use_the_documented_syntax()
    {
        Assert.Equal("* [Try] skill-check perception dc 12\n  success: -> END\n  failure: -> retry",
            SkillCheckFaker.Choice("Try", "perception", 12, "END", "retry"));
        Assert.Equal("::: skill-check perception dc 12\nsuccess: { var:count += 1 }\nfailure: { player:hp -= 1 }\n:::",
            SkillCheckFaker.Entry("perception", 12, "var:count += 1", "player:hp -= 1"));
    }
}
