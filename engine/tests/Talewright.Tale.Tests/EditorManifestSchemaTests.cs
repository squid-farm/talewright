using System.Text.Json;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Core.World;
using Talewright.Rules.Checks;
using Talewright.Tale.Compilation;

namespace Talewright.Tale.Tests;

public class EditorManifestSchemaTests
{
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
                    Assert.Equal(new Pronouns("xe", "xem", "xyr", "xyrs", "xemself", true), guide.Pronouns);
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
