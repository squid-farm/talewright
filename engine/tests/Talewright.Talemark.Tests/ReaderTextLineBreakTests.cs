using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Rendering;
using Talewright.Talemark.Compilation;
using Talewright.Testing;

namespace Talewright.Talemark.Tests;

public sealed class ReaderTextLineBreakTests
{
    [Theory]
    [InlineData("Before {if true and\n  true}shown{/if} after", "Before shown after")]
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
}
