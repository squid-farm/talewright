using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class ReaderTextLineBreakTests
{
    [Theory]
    [InlineData("Before {if true and\n  true}shown{/if} after", "Before shown after")]
    [InlineData("Before {player:pronoun#one-many(\"has\",\n  \"have\")} after", "Before has after")]
    [InlineData("Before\n{if true and\n  true}shown{/if}\nAfter", "Before\nshown\nAfter")]
    [InlineData("Before\n{if false and\n  true}hidden{/if}\nAfter", "Before\nAfter")]
    [InlineData("Before\n{if false}hidden{/if}\n{if false}hidden{/if}\nAfter", "Before\nAfter")]
    [InlineData("Before\n{if false}one{/if} {if false}two{/if}\nAfter", "Before\nAfter")]
    [InlineData("{if false}one{/if} {if false}two{/if}\nAfter", "After")]
    [InlineData("Before\n{if false}one{/if} {if false}two{/if}", "Before")]
    [InlineData("One\\\\\nTwo", "One\\\nTwo")]
    [InlineData("One\\\\\\\\\nTwo", "One\\\\\nTwo")]
    public void Authored_line_breaks_and_multiline_constructs_render_as_specified(
        string text, string expected)
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = $"=== start\n{text}\n-> END",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var scene = story.GetScene(new SceneId("start"));
        var document = new TemplateRenderer().Render(
            CompilerTests.Templates(scene), GameState.CreateInitial(story.World, 1));

        Assert.Equal(expected, Assert.IsType<RenderedParagraph>(Assert.Single(document.Blocks)).ToPlainText());
    }

    [Fact]
    public void Multiline_image_arguments_do_not_create_a_reader_text_line_break()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = "=== start\nBefore {image(\"camp\",\n  \"A camp\")} after\n-> END",
        }, ["images/camp.png"]);
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var scene = story.GetScene(new SceneId("start"));
        var document = new TemplateRenderer().Render(
            CompilerTests.Templates(scene), GameState.CreateInitial(story.World, 1));
        var paragraph = Assert.IsType<RenderedParagraph>(Assert.Single(document.Blocks));

        Assert.Equal<RenderedInline>(
        [
            new RenderedText("Before ", TextStyle.None),
            new RenderedMedia("images/camp.png", "A camp"),
            new RenderedText(" after", TextStyle.None),
        ], paragraph.Inlines);
    }

    [Fact]
    public void Prose_keeps_indentation_and_space_runs_without_changing_step_identity()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = "  === start\n \tFirst  {player:first-name}  \t\n\t  Second   line\n \t \n" +
                            "  \\* escaped  text\n  -> END",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var story = result.Story!;
        var scene = story.GetScene(new SceneId("start"));
        var steps = scene.Steps.OfType<ProseStep>().ToArray();
        Assert.Equal(["step:2:3", "step:5:3"], steps.Select(step => step.Id));
        var document = new TemplateRenderer().Render(
            CompilerTests.Templates(scene), GameState.CreateInitial(story.World, 1));

        Assert.Equal([" \tFirst  Mara\n\t  Second   line", "  * escaped  text"],
            document.Blocks.Cast<RenderedParagraph>().Select(paragraph => paragraph.ToPlainText()));
    }

    [Fact]
    public void Conditional_empty_lines_are_removed_without_collapsing_authored_spaces()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = TestStories.DefaultYaml,
            ["main.tale"] = "=== start\n  Before  {if false}hidden{/if}  after\n" +
                            "\t{if false}hidden{/if}\n  Last\n-> END",
        });
        var result = StoryCompiler.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var document = new TemplateRenderer().Render(CompilerTests.Templates(story.GetScene(new SceneId("start"))),
            GameState.CreateInitial(story.World, 1));

        Assert.Equal("  Before    after\n  Last", Assert.Single(document.Blocks).ToPlainText());
    }
}
