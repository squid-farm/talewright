using Talewright.Tale.Compilation;

namespace Talewright.Tale.Tests;

public sealed class RicherHubTests
{
    private static CompilationResult Compile(string metadata) => StoryCompiler.Compile(new StorySource(
        new Dictionary<string, string>
        {
            ["story.yaml"] = "id: hubs\ntitle: Hubs\nstart: hub\ncharacters:\n  player: {first-name: Tester}\n",
            ["main.tale"] = $"=== hub\n{metadata}\n-> END\n=== other\n@tags a,b\n-> END",
        }));

    [Theory]
    [InlineData("@hub all()")]
    [InlineData("@hub any(a,)")]
    [InlineData("@hub all(a,a)")]
    [InlineData("@hub all(a,b")]
    [InlineData("@hub unknown(a,b)")]
    [InlineData("@hub a\n@hub b")]
    [InlineData("@hub a\n@hub-limit 0")]
    [InlineData("@hub a\n@hub-limit -1")]
    [InlineData("@hub a\n@hub-limit 1\n@hub-limit 2")]
    [InlineData("@hub-limit 2")]
    public void Malformed_or_duplicate_directives_are_errors(string metadata) =>
        Assert.Contains(Compile(metadata).Errors, d => d.Code == "TW1006");

    [Theory]
    [InlineData("@hub all(a,b)")]
    [InlineData("@hub any(a,b)\n@hub-limit 1")]
    [InlineData("@hub a")]
    public void Matching_scenes_are_conservatively_reachable(string metadata)
    {
        var result = Compile(metadata);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "TW3008" or "TW3009");
    }
}
