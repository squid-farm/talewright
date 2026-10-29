using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Talemark.Compilation;
using System.Text.RegularExpressions;

namespace Talewright.Talemark.Tests;

public sealed class GoverningAttributeTests
{
    private static CompilationResult Compile(string stats, string disable = "[]") =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = $$"""
                game:
                  id: governing-attribute
                  title: Governing attribute
                  start: start
                  ruleset:
                    base: generic
                    disable: {{disable}}
                    stats:
                {{stats}}
                characters:
                  player: {first-name: Ada}
                """,
            ["main.tale"] = "=== start\n-> END",
        }));

    [Theory]
    [InlineData(7, -2)]
    [InlineData(9, -1)]
    [InlineData(10, 0)]
    [InlineData(15, 2)]
    public void Custom_attribute_modifier_is_added_to_skill_value(int score, int modifier)
    {
        var result = Compile($$"""
                      - {id: tracking, category: skill, default: 3, governing-attribute: grit}
                      - {id: grit, category: attribute, min: 1, max: 30, default: {{score}}}
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        var world = result.Story!.World;
        var skill = world.Ruleset.StatsById[new("tracking")];
        Assert.Equal(new StatId("grit"), skill.GoverningAttribute);
        var check = world.Ruleset.SkillCheckRule!.Resolve(GameState.CreateInitial(world, 42),
            new(EntityId.Player, skill.Id, 14, 10));
        Assert.Equal(3 + modifier, check.Modifier);
        Assert.Equal(10 + 3 + modifier, check.Total);
    }

    [Theory]
    [InlineData("str")]
    [InlineData("grit")]
    public void Existing_skills_can_use_enabled_attributes_declared_in_either_order(string target)
    {
        var result = Compile($$"""
                      - {id: athletics, category: skill, governing-attribute: {{target}}}
                      - {id: grit, category: attribute}
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(new StatId(target), result.Story!.World.Ruleset.StatsById[new("athletics")].GoverningAttribute);
    }

    [Fact]
    public void Disabled_attribute_redeclared_as_enabled_attribute_can_be_targeted()
    {
        var result = Compile("""
                      - {id: tracking, category: skill, governing-attribute: str}
                      - {id: str, category: attribute}
            """, "[str]");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(new StatId("str"), result.Story!.World.Ruleset.StatsById[new("tracking")].GoverningAttribute);
    }

    [Theory]
    [InlineData("missing", "[]", "", "TW4005", "unknown")]
    [InlineData("null", "[]", "", "TW4005", "unknown")]
    [InlineData("str", "[str]", "", "TW4005", "disabled")]
    [InlineData("hp", "[]", "", "TW4003", "category: attribute")]
    [InlineData("xp", "[]", "", "TW4003", "category: attribute")]
    [InlineData("courage", "[]", "      - {id: courage, category: custom}", "TW4003", "category: attribute")]
    [InlineData("athletics", "[]", "", "TW4003", "category: attribute")]
    [InlineData("tracking", "[]", "", "TW4003", "category: attribute")]
    [InlineData("str", "[]", "      - {id: str, category: resource}", "TW4003", "category: attribute")]
    public void Targets_are_validated_against_final_enabled_stats(string target, string disable, string replacement, string code, string message)
    {
        var result = Compile($"      - {{id: tracking, category: skill, governing-attribute: {target}}}\n{replacement}", disable);
        Assert.False(result.Success);
        Assert.Null(result.Story);
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.Contains(message, error.Message);
        Assert.Contains(target, error.Message);
        Assert.Equal(StorySource.ManifestFileName, error.Location.File);
        Assert.Equal(9, error.Location.Line);
    }

    [Theory]
    [InlineData("attribute")]
    [InlineData("resource")]
    [InlineData("progression")]
    [InlineData("custom")]
    [InlineData(null)]
    public void Only_skill_category_accepts_a_governing_attribute(string? category)
    {
        var result = Compile($"      - {{id: tracking, {(category is null ? "" : $"category: {category}, ")}governing-attribute: str}}");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "TW4003" && error.Message.Contains("only allowed on stats with category: skill"));
    }

    [Theory]
    [InlineData("''")]
    [InlineData("bad/id")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Invalid_target_values_are_errors(string value)
    {
        var result = Compile($"      - {{id: tracking, category: skill, governing-attribute: {value}}}");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Code == "TW4003" && error.Message.Contains("governing attribute stat id"));
    }

    [Fact]
    public void Camel_case_key_is_diagnosed_as_unknown_and_does_not_link_an_attribute()
    {
        var result = Compile("      - {id: tracking, category: skill, governingAttribute: str}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TW4006", diagnostic.Code);
        Assert.Contains("Unknown key 'governingAttribute'", diagnostic.Message);
        Assert.Contains("governing-attribute", diagnostic.Message);
        Assert.Null(result.Story!.World.Ruleset.StatsById[new("tracking")].GoverningAttribute);
    }

    [Fact]
    public void Disabling_an_attribute_preserves_existing_base_skill_unlinking()
    {
        var result = Compile("      - {id: tracking, category: skill}", "[str]");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Null(result.Story!.World.Ruleset.StatsById[new("athletics")].GoverningAttribute);
    }

    [Fact]
    public void Unlinked_skill_keeps_its_existing_value_only_modifier()
    {
        var result = Compile("      - {id: tracking, category: skill, default: 3}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var world = result.Story!.World;
        Assert.Null(world.Ruleset.StatsById[new("tracking")].GoverningAttribute);
        var check = world.Ruleset.SkillCheckRule!.Resolve(GameState.CreateInitial(world, 42),
            new(EntityId.Player, new("tracking"), 14, 10));
        Assert.Equal(3, check.Modifier);
        Assert.Equal(13, check.Total);
    }

    [Fact]
    public void Documented_tracking_recipe_compiles_and_demonstrates_both_check_totals()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var recipe = File.ReadAllText(Path.Combine(root.FullName, "docs", "guide", "authoring-recipes.md"))
            .Split("### Link a skill to a custom attribute", StringSplitOptions.None)[1];
        var yaml = Assert.Single(Regex.Matches(recipe, @"```yaml\r?\n([\s\S]*?)\r?\n```")).Groups[1].Value;
        var tale = Assert.Single(Regex.Matches(recipe, @"```tale\r?\n([\s\S]*?)\r?\n```")).Groups[1].Value;
        foreach (var linked in new[] { true, false })
        {
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                [StorySource.ManifestFileName] = linked ? yaml : yaml.Replace("        governing-attribute: grit", ""),
                ["main.tale"] = tale,
            }));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Empty(result.Diagnostics);
            var world = result.Story!.World;
            var check = world.Ruleset.SkillCheckRule!.Resolve(GameState.CreateInitial(world, 42),
                new(EntityId.Player, new("tracking"), 14, null));
            Assert.Equal(linked ? 15 : 13, check.Total);
            Assert.Equal(linked, check.Success);
        }
    }
}
