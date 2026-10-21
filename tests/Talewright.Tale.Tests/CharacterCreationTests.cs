using Talewright.Tale.Compilation;

namespace Talewright.Tale.Tests;

public sealed class CharacterCreationTests
{
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
            ["story.yaml"] = $"id: creation\ntitle: Creation\nstart: a\ncharacters:\n  player: {{first-name: Default}}\ncreation:\n  {body}\n",
            ["main.tale"] = "=== a\n-> END",
        }));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code is "TW4003" or "TW4005");
    }
}
