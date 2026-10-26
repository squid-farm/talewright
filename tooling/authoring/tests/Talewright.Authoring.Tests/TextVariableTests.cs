using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public class TextVariableTests
{
    private const string Manifest = """
        game:
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

    [Fact]
    public async Task Implicit_symbols_track_chains_file_changes_unsaved_edits_and_recovery()
    {
        var workspace = new StoryWorkspace(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest.Replace("  weather: \"sunny\"\n", ""),
            ["a.tale"] = "=== a\n{var:weather}\n~ var:copy = var:weather\n-> b",
            ["b.tale"] = "=== b\n~ var:weather = \"stormy\"\n-> END",
        }));
        async Task<StoryAnalysis> Analyze()
        {
            var analysis = (await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis;
            Assert.Equal(StoryCompiler.Compile(analysis.Source).Diagnostics, analysis.Diagnostics);
            return analysis;
        }
        var text = await Analyze();
        AssertType(text, "text");
        Assert.Contains(text.Symbols, symbol => symbol.Name == "copy" && symbol.Type == "text");
        workspace.Open("b.tale", 1, "=== b\n~ var:weather = 1\n-> END");
        AssertType(await Analyze(), "integer");
        workspace.Change("b.tale", 2, "=== b\n~ var:weather = var:copy\n-> END");
        var cycle = await Analyze();
        AssertType(cycle, "unresolved");
        Assert.Contains(cycle.Diagnostics, diagnostic => diagnostic.Code == "TW2007");
        workspace.Change("b.tale", 3, "=== b\n~ var:weather =");
        var incomplete = await Analyze();
        AssertType(incomplete, "unresolved");
        workspace.Change("b.tale", 4, "=== b\n~ var:weather = \"stormy\"\n-> END");
        AssertType(await Analyze(), "text");
        workspace.Close("b.tale");
        workspace.UpdateFile("b.tale", "=== b\n~ var:weather = 2\n~ var:weather = \"wrong\"\n-> END");
        var conflict = await Analyze();
        AssertType(conflict, "integer");
        Assert.Contains(conflict.Diagnostics, diagnostic => diagnostic.Code == "TW2003");
        workspace.UpdateFile("b.tale", null);
        var removed = await Analyze();
        Assert.DoesNotContain(removed.Symbols, symbol => symbol.Name == "weather");
        Assert.Contains(removed.Diagnostics, diagnostic => diagnostic.Code == "TW2004");
        workspace.UpdateFile("b.tale", "=== b\n~ var:weather = \"restored\"\n-> END");
        AssertType(await Analyze(), "text");
    }
}
