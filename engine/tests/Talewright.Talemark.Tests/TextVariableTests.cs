using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Rules.Effects;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public class TextVariableTests
{
    private const string Yaml = """
        game:
          id: text
          title: Text variables
          start: a
        characters:
          player: { first-name: Tester }
        vars:
          weather: "rain"
          copy: ""
          label: "12"
          count: 0
        """;

    private static CompilationResult Compile(string tale, string yaml = Yaml) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = yaml, ["main.tale"] = tale,
        }));

    [Fact]
    public void Text_assignments_are_typed_event_backed_value_copies_and_render_exactly()
    {
        var result = Compile("""
            === a
            ~ var:weather = "orage \\ ciel"
            ~ var:copy = var:weather
            ~ var:weather = "雪"
            ~ var:count += 1
            {var:copy}|{var:weather}|{var:label}|{var:count}
            {if var:weather == "雪"}equal{/if}
            {if var:copy != var:weather}different{/if}
            -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var log = new EventLog(story.World, 1);
        foreach (var batch in story.Scenes[0].Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, story.Predicates);
        var text = new TemplateRenderer().Render(CompilerTests.Templates(story.Scenes[0]), log.State).ToPlainText();
        Assert.Contains("orage \\ ciel|雪|12|1", text);
        Assert.Contains("equal", text);
        Assert.Contains("different", text);
        var replay = GameState.CreateInitial(story.World, 1);
        foreach (var change in log.Events) replay.Apply(change);
        Assert.Equal("orage \\ ciel", replay.GetTextVariable(new("copy")));
        Assert.Equal("雪", replay.GetTextVariable(new("weather")));
        Assert.Contains(log.Events, e => e is TextVariableChanged { OldValue: "rain", NewValue: "orage \\ ciel" });
        Assert.Throws<InvalidOperationException>(() => replay.GetVariable(new("weather")));
        Assert.Throws<InvalidOperationException>(() => replay.Apply(new VariableChanged(new("weather"), 0, 1)));
        Assert.Throws<KeyNotFoundException>(() => replay.GetTextVariable(new("count")));
    }

    [Theory]
    [InlineData("~ var:weather = 1")]
    [InlineData("~ var:count = \"rain\"")]
    [InlineData("~ var:weather += \"rain\"")]
    [InlineData("~ var:weather -= 1")]
    [InlineData("~ var:weather = var:weather + \"rain\"")]
    [InlineData("{if var:weather == 1}bad{/if}")]
    [InlineData("{if 1 == var:weather}bad{/if}")]
    [InlineData("{if var:weather < \"snow\"}bad{/if}")]
    public void Invalid_text_operations_report_located_type_errors(string statement)
    {
        var result = Compile($"=== a\n{statement}\n-> END");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW2003" && diagnostic.Location.Line == 2);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Unsupported_initial_values_produce_manifest_errors(string value)
    {
        var result = Compile("=== a\n-> END", Yaml + $"\n  bad: {value}");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW4003");
    }

    [Fact]
    public void Empty_and_quoted_numeric_text_keep_their_types_and_numeric_discovery_is_unchanged()
    {
        var result = Compile("=== a\n~ var:implicit += 2\n{var:copy}/{var:label}/{var:implicit}\n-> END");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal("", result.Story!.World.TextVariables[new("copy")]);
        Assert.Equal("12", result.Story.World.TextVariables[new("label")]);
        Assert.Equal(0, result.Story.World.Variables[new("implicit")]);
    }
}
