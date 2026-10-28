using System.Text.Json;
using System.Text.RegularExpressions;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;
using Talewright.Rules.Checks;
using Talewright.Talemark.Compilation;

namespace Talewright.Talemark.Tests;

public class EditorManifestSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Organization_guide_layouts_compile_with_the_same_characters_items_and_trigger_order(bool withTriggers)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var guide = File.ReadAllText(Path.Combine(root.FullName, "docs", "guide", "organizing-your-story.md"));
        var yaml = Regex.Matches(guide, @"```yaml\r?\n([\s\S]*?)\r?\n```")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(5, yaml.Length);
        var tale = Assert.Single(Regex.Matches(guide, @"```tale\r?\n([\s\S]*?)\r?\n```")).Groups[1].Value;
        var triggers = withTriggers ? "\n" + yaml[4] : "";
        var small = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml[0] + triggers,
            ["story.tale"] = tale,
        }));
        var split = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml[1] + triggers,
            ["characters/player.talewright.yaml"] = yaml[2],
            ["harbour/ferry.talewright.yaml"] = yaml[3],
            ["chapters/story.tale"] = tale,
        }));
        foreach (var result in new[] { small, split })
        {
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Empty(result.Diagnostics);
            var story = result.Story!;
            Assert.Equal("dock", story.Start.Value);
            Assert.Equal("Ada", story.World.Characters[EntityId.Player].FirstName);
            Assert.Equal(1, story.World.Characters[EntityId.Player].Inventory[new ItemId("coin")]);
            Assert.Equal("Wick", story.World.Characters[new EntityId("ferryman")].FirstName);
            Assert.Equal("silver coin", story.World.Items[new ItemId("coin")].Name);
            Assert.Equal(withTriggers ? ["notice-payment", "count-payment"] : Array.Empty<string>(),
                story.Triggers.Select(trigger => trigger.Id));
        }
        Assert.Equal(small.Story!.Scenes.Select(scene => scene.Id), split.Story!.Scenes.Select(scene => scene.Id));
    }

    [Fact]
    public void First_story_guide_starter_compiles_with_its_finished_story()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var guide = File.ReadAllText(Path.Combine(root.FullName, "docs", "guide", "first-story.md"));
        var yaml = Assert.Single(Regex.Matches(guide, @"```yaml\r?\n([\s\S]*?)\r?\n```")).Groups[1].Value;
        var tale = Regex.Matches(guide, @"```tale\r?\n([\s\S]*?)\r?\n```").Last().Groups[1].Value;
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml,
            ["story.tale"] = tale,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Shared_identifier_fixture_matches_compiler_rule()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root.FullName, "tooling", "vscode-talewright", "tests", "fixtures", "identifiers.json")));
        foreach (var (group, valid) in new[] { ("valid", true), ("invalid", false) })
        {
            foreach (var id in fixture.RootElement.GetProperty(group).EnumerateArray().Select(value => value.GetString()!))
            {
                // A JSON string is a valid YAML double-quoted scalar, so the key reaches the compiler unchanged.
                var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
                {
                    ["story.talewright.yaml"] = $$"""
                        game:
                          id: identifiers
                          title: Identifiers
                          start: beginning
                        characters: { player: { first-name: Ada } }
                        factions: { {{JsonSerializer.Serialize(id)}}: { name: Test } }
                        """,
                    ["main.tale"] = "=== beginning\nHello.\n-> END\n",
                }));
                Assert.True(valid == result.Success,
                    $"{JsonSerializer.Serialize(id)} should be {group}: {string.Join("; ", result.Diagnostics.Select(d => d.Message))}");
            }
        }
    }

    [Fact]
    public void Shared_schema_fixtures_preserve_compiler_diagnostics_and_materialized_values()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, "tooling", "vscode-talewright", "tests", "fixtures", "manifests");
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "cases.json")));
        var files = new List<string>();
        foreach (var fixture in cases.RootElement.EnumerateArray())
        {
            var file = fixture.GetProperty("file").GetString()!;
            files.Add(file);
            var expectedCodes = fixture.GetProperty("compilerCodes").EnumerateArray()
                .Select(code => code.GetString()!).Order().ToArray();
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                ["story.talewright.yaml"] = File.ReadAllText(Path.Combine(directory, file)),
                ["main.tale"] = "=== beginning\nHello.\n-> END\n",
            }));
            Assert.Equal(expectedCodes, result.Diagnostics.Select(diagnostic => diagnostic.Code).Order().ToArray());
            Assert.Equal(!expectedCodes.Contains("TW4003"), result.Success);
            if (!result.Success)
            {
                Assert.Null(result.Story);
                continue;
            }

            var story = result.Story!;
            var player = story.World.Characters[EntityId.Player];
            switch (file)
            {
                case "text-variables.yaml":
                    Assert.Equal("stormy", story.World.TextVariables[new("weather")]);
                    Assert.Equal("", story.World.TextVariables[new("empty")]);
                    Assert.Equal("12", story.World.TextVariables[new("numeric-label")]);
                    Assert.Equal("\u96ea", story.World.TextVariables[new("unicode")]);
                    Assert.Equal(1, story.World.Variables[new("count")]);
                    break;
                case "defaults.yaml":
                    Assert.Equal("0.0.0", story.Version);
                    Assert.Equal(RandomnessMode.ChecksOnly, story.Randomness);
                    Assert.Null(story.Seed);
                    Assert.True(story.UndoEnabled);
                    Assert.Equal(string.Empty, player.LastName);
                    Assert.Equal(Pronouns.They, player.Pronouns);
                    Assert.Empty(player.Inventory);
                    Assert.Empty(player.Stats);
                    Assert.Equal("coin", story.World.Items[new ItemId("coin")].Name);
                    Assert.Equal("guild", story.World.Factions[new FactionId("guild")].Name);
                    Assert.Equal(0, story.World.Factions[new FactionId("guild")].InitialStanding);
                    Assert.False(story.Creation!.EditName);
                    Assert.Empty(story.Creation.Pronouns);
                    var defaultPreset = Assert.Single(story.Creation.Presets);
                    Assert.Equal("explorer", defaultPreset.Label);
                    Assert.Empty(defaultPreset.Stats);
                    break;
                case "supported-forms.yaml":
                    Assert.Equal("1.2.3", story.Version);
                    Assert.Equal(RandomnessMode.Off, story.Randomness);
                    Assert.Equal(42UL, story.Seed);
                    Assert.False(story.UndoEnabled);
                    Assert.Equal(Pronouns.They, player.Pronouns);
                    Assert.Equal(4, player.Stats[new StatId("focus")]);
                    Assert.Equal(1, player.Inventory[new ItemId("coin")]);
                    var guide = story.World.Characters[new EntityId("guide")];
                    Assert.Equal(new Pronouns("xe", "xem", "xyr", "xyrs", "xemself", GrammaticalNumber.Plural), guide.Pronouns);
                    Assert.Equal(GrammaticalNumber.Singular, guide.NameAgreement);
                    Assert.Equal(2, guide.Inventory[new ItemId("coin")]);
                    Assert.Equal("Silver coin", story.World.Items[new ItemId("coin")].Name);
                    Assert.Equal("Jade charm", story.World.Items[new ItemId("charm")].Name);
                    Assert.Equal(2, story.World.Items[new ItemId("charm")].Modifiers[new StatId("focus")]);
                    Assert.Equal("Guild", story.World.Factions[new FactionId("guild")].Name);
                    Assert.Equal(0, story.World.Factions[new FactionId("guild")].InitialStanding);
                    Assert.Equal("Ferry Guild", story.World.Factions[new FactionId("ferrymen")].Name);
                    Assert.Equal(2, story.World.Factions[new FactionId("ferrymen")].InitialStanding);
                    var stat = story.World.Ruleset.StatsById[new StatId("focus")];
                    Assert.Equal("focus", stat.Name);
                    Assert.Equal(StatCategory.Custom, stat.Category);
                    Assert.Equal(0, stat.Default);
                    Assert.Equal(8, stat.Max);
                    Assert.Contains(new FlagId("ready"), story.World.Flags);
                    Assert.Contains(new FlagId("done"), story.World.Flags);
                    Assert.Equal(0, story.World.Variables[new VarId("attempts")]);
                    Assert.True(Assert.Single(story.Triggers).Once);
                    Assert.True(story.Creation!.EditName);
                    Assert.Equal(new[] { "they", "she" }, story.Creation.Pronouns);
                    Assert.Equal(new[] { 4, 8 }, story.Creation.Stats[new StatId("focus")]);
                    var preset = Assert.Single(story.Creation.Presets);
                    Assert.Equal("explorer", preset.Label);
                    Assert.Equal(8, preset.Stats[new StatId("focus")]);
                    break;
                case "faction-shorthand.yaml":
                    Assert.Equal("guild", story.World.Factions[new FactionId("guild")].Name);
                    break;
            }
        }

        Assert.Equal(Directory.GetFiles(directory, "*.yaml").Select(Path.GetFileName).Order(), files.Order());
    }
}
