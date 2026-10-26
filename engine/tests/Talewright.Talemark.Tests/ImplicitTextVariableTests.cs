using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Effects;
using Talewright.Talemark.Compilation;

namespace Talewright.Talemark.Tests;

public class ImplicitTextVariableTests
{
    private const string Manifest = """
        game:
          id: implicit-text
          title: Implicit text
          start: a
        characters:
          player: { first-name: Ada }
        """;

    private static CompilationResult Compile(params (string File, string Text)[] files) =>
        StoryCompiler.Compile(new StorySource(files.Prepend(("story.talewright.yaml", Manifest))
            .ToDictionary(pair => pair.Item1, pair => pair.Item2)));

    [Fact]
    public void Discovery_hoists_cross_file_chains_without_executing_assignments()
    {
        var result = Compile(("a.tale", """
            === a
            Before [{var:destination}] [{var:source}].
            ~ var:destination = var:middle
            -> END
            """), ("z.tale", """
            === z
            ~ var:middle = var:source
            ~ var:source = "stormy"
            -> END
            """));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        Assert.Equal(3, story.World.TextVariables.Count);
        Assert.All(story.World.TextVariables.Values, value => Assert.Equal("", value));
        Assert.Contains("Before [] []", new TemplateRenderer()
            .Render(story.Scenes[0].Body, GameState.CreateInitial(story.World, 1)).ToPlainText());
    }

    [Fact]
    public void Discovery_uses_expression_types_and_assignments_copy_values_when_executed()
    {
        var result = Compile(("main.tale", """
            === a
            ~ var:source = player:first-name
            ~ var:copy = var:source
            ~ var:source = "stormy"
            ~ var:counter = var:counter + 1
            ~ var:compound += 1
            {var:copy}/{var:source}/{var:counter}/{var:compound}
            -> END
            """));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        foreach (var effect in story.Scenes[0].EntryEffects)
            EffectExecutor.Execute(effect.Effect, log, story.Predicates);
        Assert.Equal("Ada", log.State.GetTextVariable(new("copy")));
        Assert.Equal("stormy", log.State.GetTextVariable(new("source")));
        Assert.Equal(1, log.State.GetVariable(new("counter")));
        Assert.Equal(1, log.State.GetVariable(new("compound")));
    }

    [Fact]
    public void First_declaration_order_is_ordinal_path_then_source_order_not_input_order()
    {
        var files = new[]
        {
            ("z.tale", "=== z\n~ var:weather = 1\n-> END"),
            ("a.tale", "=== a\n~ var:weather = \"stormy\"\n-> END"),
        };
        var forward = Compile(files);
        var reversed = Compile(files.Reverse().ToArray());
        Assert.False(forward.Success);
        Assert.Equal(forward.Diagnostics, reversed.Diagnostics);
        Assert.Contains(forward.Errors, diagnostic => diagnostic.Code == "TW2003" &&
            diagnostic.Location.File == "z.tale" && diagnostic.Message.Contains("Text variable"));
    }

    [Theory]
    [InlineData("~ var:x = var:y\n~ var:y = var:x")]
    [InlineData("~ var:x = var:missing")]
    [InlineData("~ var:x = true")]
    public void Unresolved_inference_is_an_explicit_error_with_a_manifest_remedy(string effects)
    {
        var result = Compile(("main.tale", $"=== a\n{effects}\n-> END"));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW2007" &&
            diagnostic.Message.Contains("story.talewright.yaml"));
    }

    [Fact]
    public void Manifest_declarations_resolve_cycles_and_take_precedence_over_effects()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest + "\nvars:\n  x: \"seed\"",
            ["main.tale"] = "=== a\n~ var:x = var:y\n~ var:y = var:x\n-> END",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal("seed", result.Story!.World.TextVariables[new("x")]);
        Assert.Equal("", result.Story.World.TextVariables[new("y")]);
    }

    [Fact]
    public void Trigger_assignments_are_discovered_after_tale_files_in_manifest_order()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest + """

                triggers:
                  - id: storm
                    when: var:weather == ""
                    do: var:weather = "stormy"
                    once: true
                """,
            ["main.tale"] = "=== a\n{var:weather}\n-> END",
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal("", result.Story!.World.TextVariables[new("weather")]);
    }
}
