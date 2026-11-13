using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public sealed class ShownClauseTests
{
    private const string Manifest = """
        game: {id: shown-clause, title: Shown clause, start: start, ruleset: rpg}
        characters:
          player: {first-name: Ada}
        flags: [ready]
        vars: {count: 2}
        """;

    private static CompilationResult Compile(string tale) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest,
            ["main.tale"] = tale,
        }));

    [Fact]
    public void Choices_and_hub_scenes_keep_shown_presence_and_reader_text()
    {
        var result = Compile("""
            === start
            @hub camp
            * [Go] (requires flag:ready) -> END
            * [Buy] (requires flag:ready, shown) -> END
            * [Wait] (requires flag:ready, shown "Say \"hello\" \\ path \{ literal, shown later {var:count} {if true}yes{/if} *bold*") -> END

            === offered
            @title Camp
            @tags camp
            @requires flag:ready, shown
            -> END

            === offered-text
            @title Market
            @tags camp
            @requires "yes, shown" == "yes, shown", shown "Need {var:count}"
            -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var start = Assert.Single(result.Story!.Scenes, scene => scene.Id.Value == "start");
        Assert.Null(start.Choices[0].Shown);
        Assert.NotNull(start.Choices[1].Shown);
        Assert.Null(start.Choices[1].Shown!.Text);

        var text = Assert.IsType<ShownDefinition>(start.Choices[2].Shown);
        Assert.Contains(text.Text!, inline => inline is SubstitutionTemplate);
        Assert.Contains(text.Text!, inline => inline is ConditionalInlineTemplate);
        Assert.Contains(text.Text!, inline => inline is StyledTemplate);
        Assert.Contains("Say \"hello\" \\ path { literal",
            string.Concat(text.Text!.OfType<TextTemplate>().Select(item => item.Text)));

        var offered = Assert.Single(result.Story.Scenes, scene => scene.Id.Value == "offered");
        Assert.NotNull(offered.Shown);
        Assert.Null(offered.Shown!.Text);
        var offeredText = Assert.Single(result.Story.Scenes, scene => scene.Id.Value == "offered-text");
        Assert.NotNull(offeredText.Shown);
        Assert.Contains(offeredText.Shown!.Text!, inline => inline is SubstitutionTemplate);
    }

    [Theory]
    [InlineData("* [Go] (requires flag:ready, show) -> END", "TW1011", "shown")]
    [InlineData("* [Go] (requires flag:ready, hidden) -> END", "TW1001", ",")]
    [InlineData("* [Go] (requires flag:ready, shown, shown) -> END", "TW1011", "only once")]
    [InlineData("* [Go] (shown) -> END", "TW1011", "requires a choice requirement")]
    [InlineData("* [Go] (shown \"Not yet\") -> END", "TW1011", "requires a choice requirement")]
    [InlineData("@requires true, hidden", "TW1001", ",")]
    public void Invalid_shown_clauses_report_actionable_diagnostics(string line, string code, string message)
    {
        var tale = line.StartsWith("@", StringComparison.Ordinal)
            ? "=== start\n" + line + "\n* -> END"
            : "=== start\n" + line;
        var diagnostic = Assert.Single(Compile(tale).Diagnostics,
            item => item.Severity == DiagnosticSeverity.Error && item.Code == code);

        Assert.Contains(message, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_shown_literal_is_allowed_with_a_warning()
    {
        var result = Compile("""
            === start
            * [Go] (requires flag:ready, shown "") -> END
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var warning = Assert.Single(result.Diagnostics, item => item.Severity == DiagnosticSeverity.Warning);
        Assert.Equal("TW1011", warning.Code);
        Assert.Contains("empty 'shown' text", warning.Message);
        Assert.Empty(Assert.Single(result.Story!.Scenes).Choices[0].Shown!.Text!);
    }

    [Fact]
    public void Textless_choices_cannot_use_shown()
    {
        var result = Compile("""
            === start
            * (requires flag:ready, shown) -> END
            """);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, item =>
            item.Code == "TW1011" && item.Message.Contains("without a label", StringComparison.Ordinal));
    }
}
