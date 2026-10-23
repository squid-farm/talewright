using Talewright.Core.Identity;
using Talewright.Tale.Compilation;

namespace Talewright.Tale.Tests;

public sealed class SourceMapTests
{
    private const string Yaml = "id: source\ntitle: Source\nstart: first\ncharacters:\n  player: {first-name: Tester}\n";

    private static CompilationResult Compile(string tale, string? other = null)
    {
        var files = new Dictionary<string, string> { ["story.talewright.yaml"] = Yaml, ["a.tale"] = tale };
        if (other is not null) { files["nested/b.TALE"] = other; }
        return StoryCompiler.Compile(new StorySource(files));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Ranges_follow_parser_boundaries_across_files_and_line_endings(string newline)
    {
        string[] lines =
        [
            "// file preamble", "", "  === first", "@title First", "First prose.",
            "-> second", "// === not.a.scene", "", "=== second", "* [Finish] -> other",
        ];
        var result = Compile(string.Join(newline, lines), "=== other\n-> END\n");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var map = result.SourceMap!;
        Assert.Equal(new SceneSourceRange("a.tale", 3, 8), map.Scenes[new("first")]);
        Assert.Equal(new SceneSourceRange("a.tale", 9, 10), map.Scenes[new("second")]);
        Assert.Equal(new SceneSourceRange("nested/b.TALE", 1, 3), map.Scenes[new("other")]);
        Assert.Equal(3, map.Scenes.Count);
        Assert.Equal(string.Join(newline, lines), map.Source.Files["a.tale"]);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public void Eof_includes_the_final_logical_line_even_when_empty(bool terminalNewline, int endLine)
    {
        var result = Compile("=== first\n-> END" + (terminalNewline ? "\n" : ""));
        Assert.True(result.Success);
        Assert.Equal(new SceneSourceRange("a.tale", 1, endLine), Assert.Single(result.SourceMap!.Scenes).Value);
    }

    [Fact]
    public void Check_outcomes_blank_lines_comments_and_adjacent_headers_keep_exact_ranges()
    {
        var result = Compile("""
            === first
            ::: skill-check athletics dc 10

            // check comment
            success: -> second
            failure: -> second
            :::
            === second
            * [Try] skill-check athletics dc 5
              success: -> third
              failure: -> third
            === third
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(new SceneSourceRange("a.tale", 1, 7), result.SourceMap!.Scenes[new("first")]);
        Assert.Equal(new SceneSourceRange("a.tale", 8, 11), result.SourceMap.Scenes[new("second")]);
        Assert.Equal(new SceneSourceRange("a.tale", 12, 13), result.SourceMap.Scenes[new("third")]);
    }

    [Theory]
    [InlineData("=== first\n-> missing")]
    [InlineData("=== first\n-> END\n=== first\n-> END")]
    [InlineData("=== first\n{if flag:missing}\n-> END")]
    public void Failed_compilations_never_publish_an_accepted_map(string tale)
    {
        var result = Compile(tale);
        Assert.False(result.Success);
        Assert.Null(result.SourceMap);
    }

    [Fact]
    public void Source_snapshot_cannot_be_changed_through_the_original_dictionary_or_exposed_views()
    {
        var files = new Dictionary<string, string> { ["story.talewright.yaml"] = Yaml, ["a.tale"] = "=== first\n-> END" };
        var source = new StorySource(files);
        files["a.tale"] = "Not the compiled source";
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success);
        var map = result.SourceMap!;
        Assert.Equal("=== first\n-> END", map.Source.Files["a.tale"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)map.Source.Files)["a.tale"] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<SceneId, SceneSourceRange>)map.Scenes).Clear());
    }
}
