using System.Diagnostics;
using Talewright.Tale.Compilation;
using Xunit.Abstractions;

namespace Talewright.Tale.Tests;

public sealed class RicherHubTests(ITestOutputHelper output)
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

    [Theory]
    [InlineData("all(a,b)", false, true)]
    [InlineData("any(a,b)", false, false)]
    [InlineData("all(a,missing)", true, true)]
    [InlineData("any(a,missing)", false, true)]
    [InlineData("missing", true, true)]
    [InlineData("self", true, true)]
    [InlineData("A", true, true)]
    public void Indexed_hub_queries_preserve_matching_and_self_exclusion(string query, bool empty, bool bUnreachable)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.yaml"] = "id: hubs\ntitle: Hubs\nstart: hub\ncharacters:\n  player: {first-name: Tester}",
            ["main.tale"] = $"""
                === hub
                @tags self
                @hub {query}
                -> END
                === a
                @tags a,b
                -> END
                === b
                @tags b
                -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(empty, result.Warnings.Any(d => d.Code == "TW3008"));
        Assert.Equal(bUnreachable, result.Warnings.Any(d => d.Code == "TW3009" && d.Message.Contains("'b'")));
        Assert.Equal(empty, result.Warnings.Any(d => d.Code == "TW3009" && d.Message.Contains("'a'")));
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(10_000)]
    public void Sparse_hub_ring_is_fully_reachable(int count)
    {
        var text = string.Join("\n", Enumerable.Range(0, count).Select(i =>
            $"=== s{i}\n@tags t{i}\n@hub t{(i + 1) % count}"));
        var timer = Stopwatch.StartNew();
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.yaml"] = "id: ring\ntitle: Ring\nstart: s0\ncharacters:\n  player: {first-name: Tester}",
            ["main.tale"] = text,
        }));
        output.WriteLine($"{count} sparse hubs: {timer.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }
}
