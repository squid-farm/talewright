using Talewright.Talemark.Compilation;
using Talewright.Core.Identity;

namespace Talewright.Talemark.Tests;

public sealed class CharacterCreationTests
{
    [Theory]
    [InlineData("", "", 20)]
    [InlineData("", "stats: {hp: 0}", 0)]
    [InlineData("stats: {hp: 0}", "", 0)]
    [InlineData("stats: {hp: 20}", "stats: {hp: 0}", 0)]
    [InlineData("stats: {hp: 7}", "", 20)]
    public void Missing_stats_use_first_option_but_explicit_zero_is_preserved(string player, string preset, int expected)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = $$"""
                id: creation
                title: Creation
                start: a
                characters:
                  player:
                    first-name: Default
                    {{player}}
                creation:
                  stats: {hp: [20, 0]}
                  presets:
                    healthy:
                      label: Healthy
                      {{preset}}
                """,
            ["main.tale"] = "=== a\n-> END",
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        Assert.Equal(expected, story.Creation!.Default(story.World.Characters[EntityId.Player]).Stats["hp"]);
    }

    [Theory]
    [InlineData("16", 16)]
    [InlineData("10", 8)]
    public void Partial_preset_falls_back_to_authored_stat_before_legal_default(string strength, int expected)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = $$$"""
                id: creation
                title: Creation
                start: a
                characters:
                  player: {first-name: Default, stats: {str: {{{strength}}}}}
                creation:
                  stats: {str: [8, 12, 16]}
                  presets:
                    healthy: {stats: {hp: 15}}
                """,
            ["main.tale"] = "=== a\n-> END",
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        Assert.Equal(expected, story.Creation!.Default(story.World.Characters[EntityId.Player]).Stats["str"]);
    }

    [Theory]
    [InlineData("name: maybe")]
    [InlineData("pronouns: [she, she]")]
    [InlineData("pronouns: [bad]")]
    [InlineData("stats: {missing: [1]}")]
    [InlineData("stats: {str: []}")]
    [InlineData("stats: {str: [8, 8]}")]
    [InlineData("stats: {str: [999]}")]
    [InlineData("stats: {str: [1.5]}")]
    [InlineData("presets: {bad: {stats: {str: 999}}}")]
    [InlineData("stats: {str: [8, 12]}\n  presets: {bad: {stats: {str: 10}}}")]
    public void Illegal_creation_configuration_is_a_compiler_error(string body)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = $"id: creation\ntitle: Creation\nstart: a\ncharacters:\n  player: {{first-name: Default}}\ncreation:\n  {body}\n",
            ["main.tale"] = "=== a\n-> END",
        }));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code is "TW4003" or "TW4005");
    }
}
