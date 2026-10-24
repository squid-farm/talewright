using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public class TextVariableTests
{
    private const string Manifest = """
        id: text-editor
        title: Text editor
        start: a
        characters:
          player: { first-name: Ada }
        vars:
          weather: "sunny"
          count: 0
        """;

    [Fact]
    public async Task Explicit_types_follow_unsaved_manifest_changes_and_incomplete_edits()
    {
        var workspace = new StoryWorkspace(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest,
            ["a.tale"] = "=== a\n{var:weather}\n-> b",
            ["b.tale"] = "=== b\n~ var:weather = \"stormy\"\n-> END",
        }));
        var text = (await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis;
        AssertType(text, "text");
        Assert.Empty(text.Diagnostics);
        workspace.Open("story.talewright.yaml", 1, Manifest.Replace("\"sunny\"", "0"));
        var number = (await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis;
        AssertType(number, "integer");
        Assert.Contains(number.Diagnostics, diagnostic => diagnostic.Code == "TW2003");
        workspace.Open("b.tale", 1, "=== b\n~ var:weather =");
        var partial = (await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis;
        AssertType(partial, "integer");
        Assert.NotEmpty(partial.Diagnostics);
        workspace.Change("story.talewright.yaml", 2, Manifest);
        workspace.Change("b.tale", 2, "=== b\n~ var:weather = \"stormy\"\n-> END");
        var recovered = (await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis;
        AssertType(recovered, "text");
        Assert.Empty(recovered.Diagnostics);
        Assert.Equal(StoryCompiler.Compile(recovered.Source).Diagnostics, recovered.Diagnostics);
    }

    private static void AssertType(StoryAnalysis analysis, string type)
    {
        var symbols = analysis.Symbols.Where(symbol => symbol.Kind == AuthoringSymbolKind.Variable && symbol.Name == "weather");
        Assert.NotEmpty(symbols);
        Assert.All(symbols, symbol => Assert.Equal(type, symbol.Type));
        var hover = StoryHover.Get(analysis, new("a.tale", 2, 7));
        Assert.Contains($"({type})", hover!.PlainText);
        Assert.Contains(StoryCompletion.Complete(analysis, new SourceLocation("a.tale", 2, 7)),
            completion => completion.Label == "weather" && completion.Detail == $"Variable: {type}");
    }
}
