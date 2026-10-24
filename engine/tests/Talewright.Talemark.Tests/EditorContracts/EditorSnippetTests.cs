using System.Text.Json;
using System.Text.RegularExpressions;
using Talewright.Talemark.Compilation;

namespace Talewright.Talemark.Tests;

public class EditorSnippetTests
{
    [Trait("Category", "EditorContract")]
    [Fact]
    public void Every_editor_snippet_compiles_in_an_explicit_story_context()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        using var snippets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName,
            "tooling", "vscode-talewright", "snippets", "tale.json")));
        var count = 0;
        foreach (var snippet in snippets.RootElement.EnumerateObject())
        {
            var body = string.Join("\n", snippet.Value.GetProperty("body").EnumerateArray().Select(line => line.GetString()));
            var expanded = Regex.Replace(body, @"\$\{\d+:([^{}]*)\}", "$1").Replace("$0", "");
            Assert.DoesNotContain("$", expanded);
            var isScene = snippet.Name == "Scene";
            var tale = isScene ? expanded : $"""
                === snippet.start
                {expanded}
                -> END

                === scene.id
                Target.
                -> END

                === scene.success
                Success.
                -> END

                === scene.failure
                Failure.
                -> END
                """;
            var yaml = $"""
                id: editor-snippets
                title: Editor snippets
                start: {(isScene ? "scene.id" : "snippet.start")}
                characters:
                  player:
                    first-name: Mara
                """;
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                ["story.talewright.yaml"] = yaml,
                ["snippet.tale"] = tale,
            }));
            Assert.True(result.Success, $"{snippet.Name}:\n{string.Join("\n", result.Diagnostics)}");
            count++;
        }

        Assert.Equal(5, count);
    }
}
