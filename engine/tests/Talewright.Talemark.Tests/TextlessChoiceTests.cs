using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public sealed class TextlessChoiceTests
{
    private static CompilationResult Compile(string tale) => StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
    {
        ["story.talewright.yaml"] = """
            game: {id: textless, title: Textless, start: start}
            characters:
              player: {first-name: Ada}
            flags: [ready, left]
            vars: {count: 0}
            """,
        ["main.tale"] = tale,
    }));

    [Theory]
    [InlineData("* -> road", "road")]
    [InlineData("* -> END", "END")]
    public void Textless_choices_parse_only_destinations(string line, string target)
    {
        var result = Compile($"=== start\n{line}\n=== road\n-> END");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var choice = Assert.Single(result.Story!.Scenes[0].Choices);
        Assert.Equal("c0", choice.Key);
        Assert.Null(choice.Label);
        Assert.Equal(target, choice.Divert!.ToString());
        Assert.Null(choice.Requires);
        Assert.Empty(choice.Effects);
    }

    [Fact]
    public void Mixed_choices_keep_source_order_keys_targets_and_explicit_empty_labels()
    {
        var result = Compile("""
            === start
            * [First] -> road
            * [Continue] (requires flag:ready) { set flag:left; var:count += 1 } -> road
            * [] (requires flag:ready) { set flag:left } -> END
            * -> END
            === road
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var scene = result.Story!.Scenes[0];
        Assert.Equal(["c0", "c1", "c2", "c3"], scene.Choices.Select(choice => choice.Key));
        Assert.NotNull(scene.Choices[0].Label);
        Assert.NotNull(scene.Choices[1].Label);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<TemplateInline>>(scene.Choices[2].Label));
        Assert.Null(scene.Choices[3].Label);
        Assert.Equal(2, scene.Choices[1].Effects.Count);
        Assert.NotNull(scene.Choices[2].Requires);
        Assert.Single(scene.Choices[2].Effects);
        Assert.Equal(["road", "road", "END", "END"], SceneSteps.Targets(scene.Steps).Select(target => target.ToString()));
        Assert.Equal(["step:2:1", "step:3:1", "step:4:1", "step:5:1"], scene.Steps.Select(step => step.Id));
    }

    [Theory]
    [InlineData("* (requires flag:ready) -> road", "(requires flag:ready)", "Wrap it in a `{if ...}` block")]
    [InlineData("* { set flag:left } -> road", "{ set flag:left }", "Add a label")]
    public void Textless_choice_clauses_report_replacements_at_exact_source_ranges(
        string line, string clause, string replacement)
    {
        var result = Compile($"=== start\n{line}\n=== road\n-> END");
        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW1011");
        var start = line.IndexOf(clause, StringComparison.Ordinal) + 1;

        Assert.Equal(new SourceLocation("main.tale", 2, start), diagnostic.Location);
        Assert.Equal(new SourceLocation("main.tale", 2, start + clause.Length), diagnostic.EndLocation);
        Assert.Contains(replacement, diagnostic.Message);
        Assert.Contains("* [Continue]", diagnostic.Message);
    }

    [Fact]
    public void Textless_choices_reject_requirements_and_effects_independently()
    {
        const string line = "* (requires flag:ready) { set flag:left } -> road";
        var diagnostics = Compile($"=== start\n{line}\n=== road\n-> END").Errors
            .Where(item => item.Code == "TW1011")
            .ToArray();
        const string requires = "(requires flag:ready)";
        const string effects = "{ set flag:left }";

        Assert.Equal(2, diagnostics.Length);
        var requiresStart = line.IndexOf(requires, StringComparison.Ordinal) + 1;
        var effectsStart = line.IndexOf(effects, StringComparison.Ordinal) + 1;
        Assert.Equal(new SourceLocation("main.tale", 2, requiresStart), diagnostics[0].Location);
        Assert.Equal(new SourceLocation("main.tale", 2, requiresStart + requires.Length), diagnostics[0].EndLocation);
        Assert.Equal(new SourceLocation("main.tale", 2, effectsStart), diagnostics[1].Location);
        Assert.Equal(new SourceLocation("main.tale", 2, effectsStart + effects.Length), diagnostics[1].EndLocation);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*   ")]
    public void Textless_choices_without_a_divert_report_tw1011(string line)
    {
        var result = Compile($"=== start\n{line}");
        var error = Assert.Single(result.Errors, diagnostic => diagnostic.Code == "TW1011");
        Assert.Equal(2, error.Location.Line);
        Assert.Equal(1, error.Location.Column);
        Assert.Contains("->", error.Message);
    }

    [Theory]
    [InlineData("* ->", "TW1010")]
    [InlineData("* -> missing", "TW2002")]
    [InlineData("* (requires flag:ready -> END", "TW1011")]
    [InlineData("* { set flag:left -> END", "TW1011")]
    [InlineData("* skill-check athletics dc 1", "TW1011")]
    [InlineData("* missing label", "TW1011")]
    public void Textless_choices_retain_target_and_suffix_diagnostics(string line, string code)
    {
        var result = Compile($"=== start\n{line}");
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == code && diagnostic.Location.Line == 2);
    }

    [Theory]
    [InlineData("* { ~ set flag:left } -> END")]
    [InlineData("* [Leave] { ~ set flag:left } -> END")]
    public void Choice_effects_keep_the_same_existing_syntax_with_or_without_a_label(string line)
    {
        var result = Compile($"=== start\n{line}");
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW1001" && diagnostic.Message.Contains("~"));
    }

    [Fact]
    public void Leading_italic_prose_is_not_a_textless_choice()
    {
        var result = Compile("=== start\n*Quietly*.\n* -> END");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.IsType<ProseStep>(result.Story!.Scenes[0].Steps[0]);
        Assert.Null(Assert.IsType<ChoiceStep>(result.Story.Scenes[0].Steps[1]).Choice.Label);
    }
}
