using System.Diagnostics;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Xunit.Abstractions;

namespace Talewright.Talemark.Tests;

public sealed class RicherHubTests(ITestOutputHelper output)
{
    private static CompilationResult Compile(string metadata) => StoryCompiler.Compile(new StorySource(
        new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = "game:\n  id: hubs\n  title: Hubs\n  start: hub\ncharacters:\n  player: {first-name: Tester}\n",
            ["main.tale"] = $"=== hub\n{metadata}\n* [Leave] -> END\n=== other\n@tags a,b\n-> END",
        }));

    [Theory]
    [InlineData("@hub unknown(a,b)")]
    [InlineData("@hub a and")]
    [InlineData("@hub a and b or c")]
    [InlineData("@hub a and a")]
    [InlineData("@hub a\n@hub b")]
    [InlineData("@hub a\n@hub-limit 0")]
    [InlineData("@hub a\n@hub-limit -1")]
    [InlineData("@hub a\n@hub-limit 1\n@hub-limit 2")]
    [InlineData("@hub-limit 2")]
    public void Malformed_or_duplicate_directives_are_errors(string metadata) =>
        Assert.Contains(Compile(metadata).Errors, d => d.Code == "TW1006");

    [Theory]
    [InlineData("@hub a and b")]
    [InlineData("@hub a or b\n@hub-limit 1")]
    [InlineData("@hub a")]
    public void Matching_scenes_are_conservatively_reachable(string metadata)
    {
        var result = Compile(metadata);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "TW3008" or "TW3009");
    }

    [Theory]
    [InlineData("a", false, new[] { "a" })]
    [InlineData("a and b", true, new[] { "a", "b" })]
    [InlineData("a and b and c", true, new[] { "a", "b", "c" })]
    [InlineData("a or b", false, new[] { "a", "b" })]
    [InlineData("a or b or c", false, new[] { "a", "b", "c" })]
    public void Hub_forms_compile_to_one_tag_query(string query, bool matchAll, string[] tags)
    {
        var result = Compile($"@hub {query}");
        Assert.True(result.Success);
        var hub = result.Story!.GetScene(new("hub")).Hub!;
        Assert.Equal(tags, hub.Tags);
        Assert.Equal(matchAll, hub.MatchAll);
    }

    [Theory]
    [InlineData("a and b", false, true)]
    [InlineData("a or b", false, false)]
    [InlineData("a and missing", true, true)]
    [InlineData("a or missing", false, true)]
    [InlineData("missing", true, true)]
    [InlineData("self", true, true)]
    [InlineData("A", true, true)]
    public void Indexed_hub_queries_preserve_matching_and_self_exclusion(string query, bool empty, bool bUnreachable)
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = "game:\n  id: hubs\n  title: Hubs\n  start: hub\ncharacters:\n  player: {first-name: Tester}",
            ["main.tale"] = $"""
                === hub
                @tags self
                @hub {query}
                * [Leave] -> END
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
    [InlineData("all(a, b)", "@hub a and b")]
    [InlineData("any(a, b)", "@hub a or b")]
    public void Legacy_forms_include_a_replacement_diagnostic(string query, string replacement)
    {
        var error = Assert.Single(Compile($"@hub {query}").Errors);

        Assert.Equal("TW1006", error.Code);
        Assert.Contains($"use '{replacement}'", error.Message);
    }

    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    public void Hub_operators_cannot_be_declared_as_tags(string tag)
    {
        var error = Assert.Single(Compile($"@tags {tag}").Errors);

        Assert.Equal("TW1006", error.Code);
        Assert.Contains($"'{tag}' is reserved as an @hub selector operator", error.Message);
    }

    [Fact]
    public void Sentence_selector_and_limit_are_preserved_in_the_story_map()
    {
        var result = Compile("@hub a and b\n@hub-limit 1");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var offer = Assert.Single(StoryMap.Build(result.Story!).HubOffers);

        Assert.Equal(["a", "b"], offer.Tags.Tags);
        Assert.True(offer.Tags.MatchAll);
        Assert.Equal(1, result.Story!.GetScene(new("hub")).HubLimit);
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(10_000)]
    public void Sparse_hub_ring_is_fully_reachable(int count)
    {
        var text = string.Join("\n", Enumerable.Range(0, count).Select(i =>
            $"=== s{i}\n@tags t{i}\n@hub t{(i + 1) % count}\n* -> END"));
        var timer = Stopwatch.StartNew();
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = "game:\n  id: ring\n  title: Ring\n  start: s0\ncharacters:\n  player: {first-name: Tester}",
            ["main.tale"] = text,
        }));
        output.WriteLine($"{count} sparse hubs: {timer.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }
}
