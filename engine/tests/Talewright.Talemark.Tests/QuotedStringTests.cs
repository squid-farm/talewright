using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public class QuotedStringTests
{
    private const string Value = "say \"hi\" \\ {x}, # ] ) ;";
    private const string Encoded = "\"say \\\"hi\\\" \\\\ {x}, # ] ) ;\"";
    private const string Manifest = """
        game: { id: strings, title: Strings, start: a }
        characters:
          player: { first-name: Ada, pronouns: she }
        vars: { word: "" }
        flags: [ready]
        """;

    private static CompilationResult Compile(string body) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest,
            ["main.tale"] = "=== a\n" + body,
        }));

    [Fact]
    public void Every_enclosing_context_respects_strings_and_values_are_decoded_once()
    {
        var result = Compile($$"""
            @requires {{Encoded}} == {{Encoded}}
            ~ var:word = {{Encoded}}
            {if (var:word == {{Encoded}})}
            {var:word}
            {/if}
            {if var:word != ""}{({{Encoded}})#capitalize}{/if}
            {player:pronoun#one-many({{Encoded}}, "other")#capitalize}
            * [{({{Encoded}})}] (requires: (var:word == {{Encoded}})) { var:word = {{Encoded}}; set flag:ready } -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var scene = story.Scenes[0];
        var log = new EventLog(story.World, 1);
        foreach (var batch in scene.Steps.OfType<EffectStep>())
            EffectExecutor.ExecuteAll(batch.Effects, log, story.Predicates);
        Assert.Equal(Value, log.State.GetTextVariable(new("word")));
        var renderer = new TemplateRenderer();
        var text = renderer.Render(CompilerTests.Templates(scene), log.State).ToPlainText();
        Assert.Contains(Value, text);
        Assert.Equal(2, text.Split("Say \"hi\" \\ {x}, # ] ) ;").Length - 1);
        var choice = Assert.Single(scene.Steps.OfType<ChoiceStep>()).Choice;
        Assert.True(ExpressionEvaluator.IsTrue(choice.Requires!, EvaluationContext.For(log.State)));
        Assert.NotNull(choice.Label);
        Assert.Equal(Value, Assert.IsType<RenderedText>(Assert.Single(renderer.RenderInlines(choice.Label, log.State))).Text);
        EffectExecutor.ExecuteAll(choice.Effects, log, story.Predicates);
        Assert.Equal(Value, log.State.GetTextVariable(new("word")));
    }

    [Theory]
    [InlineData("@requires \"bad\\q\" == \"\"", "TW1001")]
    [InlineData("""~ var:word = "bad\q" """, "TW1001")]
    [InlineData("""~ var:implicit = "bad\q" """, "TW1001")]
    [InlineData("""{"bad\q"}""", "TW1001")]
    [InlineData("""{if "bad\q" == ""}yes{/if}""", "TW1001")]
    [InlineData("""{player:pronoun#one-many("bad\q", "ok")}""", "TW2006")]
    [InlineData("""{player:pronoun#one-many("ok", "bad\q")}""", "TW2006")]
    [InlineData("""* [{"bad\q"}] -> END""", "TW1001")]
    [InlineData("""* [Go] (requires: "bad\q" == "") -> END""", "TW1001")]
    [InlineData("""* [Go] { var:word = "bad\q" } -> END""", "TW1001")]
    public void Invalid_escapes_report_the_authored_backslash(string line, string code)
    {
        var result = Compile(line + (line.StartsWith('*') ? "" : "\n-> END"));
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == code &&
            diagnostic.Message.Contains("Unknown string escape") &&
            diagnostic.Location.Line == 2 && diagnostic.Location.Column == line.IndexOf('\\') + 1);
    }

    [Theory]
    [InlineData("~ var:word = \"unfinished")]
    [InlineData("{\"unfinished}")]
    [InlineData("* [{\"unfinished}] -> END")]
    [InlineData("* [Go] (requires: \"unfinished) -> END")]
    [InlineData("* [Go] { var:word = \"unfinished } -> END")]
    public void Unclosed_strings_report_the_opening_quote(string line)
    {
        var result = Compile(line);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW1001" &&
            diagnostic.Message.Contains("closing double quote") &&
            diagnostic.Location.Line == 2 && diagnostic.Location.Column == line.IndexOf('"') + 1);
    }

    [Theory]
    [InlineData("\\")]
    [InlineData("\\n\"")]
    [InlineData("\\u0041\"")]
    public void Trailing_and_unsupported_escapes_do_not_succeed(string suffix)
    {
        var result = Compile("~ var:word = \"bad" + suffix);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == "TW1001" &&
            diagnostic.Location.Line == 2 && diagnostic.Location.Column == 18);
    }

    [Theory]
    [InlineData("{(\n  \"bad\\q\")}", "TW1001", 7)]
    [InlineData("{if true and\n  \"bad\\q\" == \"\"}yes{/if}", "TW1001", 7)]
    [InlineData("{player:pronoun#one-many(\"ok\",\n  \"bad\\q\")}", "TW2006", 7)]
    public void String_diagnostics_in_multiline_templates_use_authored_positions(string text, string code, int column)
    {
        var result = Compile(text + "\n-> END");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, diagnostic => diagnostic.Code == code &&
            diagnostic.Location.Line == 3 && diagnostic.Location.Column == column);
    }
}
