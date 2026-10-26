using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class DistributedConfigurationTests
{
    private const string Root = """
        game:
          id: distributed
          title: Distributed
          start: first
        characters:
          player: {first-name: Ada}
        items: {rope: Rope}
        """;

    private const string Guide = "characters/guide.talewright.yaml";
    private const string GuideText = """
        characters:
          guide:
            first-name: Bea
            inventory: [rope]
        flags: [met-guide]
        vars: {coins: 3}
        """;

    private const string Tale = "=== first\n> guide: Hi.\n{if flag:met-guide}Again.{/if} {var:coins}\n-> END\n";

    private static StorySource Source(params (string Path, string Text)[] files)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = Root,
            ["main.tale"] = Tale,
        };
        foreach (var (path, text) in files) map[path] = text;
        return new StorySource(map);
    }

    private static SourceLocation Find(StorySource source, string file, string marked)
    {
        var caret = marked.IndexOf('|');
        var needle = marked.Remove(caret, 1);
        var lines = source.Files[file].ReplaceLineEndings("\n").Split('\n');
        for (var line = 0; line < lines.Length; line++)
        {
            var column = lines[line].IndexOf(needle, StringComparison.Ordinal);
            if (column >= 0) return new SourceLocation(file, line + 1, column + caret + 1);
        }
        throw new InvalidOperationException($"'{needle}' not found in {file}.");
    }

    [Fact]
    public void Supporting_declarations_keep_their_original_locations_for_hover_and_navigation()
    {
        var source = Source((Guide, GuideText));
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        foreach (var (name, kind) in new[]
                 {
                     ("guide", AuthoringSymbolKind.Character), ("met-guide", AuthoringSymbolKind.Flag),
                     ("coins", AuthoringSymbolKind.Variable),
                 })
        {
            var symbol = Assert.Single(analysis.Symbols, symbol => symbol.Name == name && symbol.Kind == kind);
            Assert.Equal(Guide, symbol.Declaration!.Start.File);
        }

        var reference = Find(source, "main.tale", "> gu|ide:");
        var target = Assert.IsType<SourceRange>(StoryNavigation.Definition(analysis, reference));
        Assert.Equal(new SourceLocation(Guide, 2, 3), target.Start);
        Assert.Equal(new SourceLocation(Guide, 2, 8), target.End);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, reference));
        Assert.Contains($"Declared at {Guide}:2:3", hover.PlainText);
        Assert.Contains("Source: guide:", hover.PlainText);

        var variable = StoryNavigation.Definition(analysis, Find(source, "main.tale", "{var:co|ins}"));
        Assert.Equal(Guide, variable!.Start.File);
        var declaration = StoryNavigation.Definition(analysis, Find(source, Guide, "  gu|ide:"));
        Assert.Equal(target, declaration);
    }

    [Fact]
    public void Supporting_files_receive_cross_file_completion_and_navigation()
    {
        var source = Source((Guide, GuideText));
        var analysis = StoryAnalyzer.Analyze(source);
        var inventory = Find(source, Guide, "inventory: [ro|pe]");
        var completion = StoryCompletion.Complete(analysis, inventory);
        Assert.Contains(completion, item => item.Label == "rope" && item.Range.Start.File == Guide);
        var rope = Assert.IsType<SourceRange>(StoryNavigation.Definition(analysis, inventory));
        Assert.Equal(StorySource.ManifestFileName, rope.Start.File);

        var tale = StoryCompletion.Complete(analysis, Find(source, "main.tale", "> |guide:"));
        Assert.Contains(tale, item => item.Label == "guide");
        var key = StoryHover.Get(analysis, Find(source, Guide, "  first-na|me: Bea"));
        Assert.NotNull(key);
    }

    [Fact]
    public void Duplicate_declarations_navigate_to_the_first_source_reported_by_the_compiler()
    {
        var source = Source(
            ("a/first.talewright.yaml", "items: {lamp: Lamp}\n"),
            ("b/second.talewright.yaml", "items: {lamp: Other lamp}\n"),
            ("main.tale", "=== first\n> player: {item:lamp}\n-> END\n"));
        var analysis = StoryAnalyzer.Analyze(source);
        var compiled = StoryCompiler.Compile(source).Diagnostics;
        Assert.Equal(compiled, analysis.Diagnostics);
        var duplicate = Assert.Single(analysis.Diagnostics, diagnostic => diagnostic.Code == "TW4007");
        Assert.Equal("b/second.talewright.yaml", duplicate.Location.File);
        Assert.Single(analysis.Symbols, symbol => symbol.Name == "lamp" && symbol.Kind == AuthoringSymbolKind.Item);
        var target = StoryNavigation.Definition(analysis, Find(source, "main.tale", "{item:la|mp}"));
        Assert.Equal("a/first.talewright.yaml", target!.Start.File);
    }

    [Fact]
    public void Placement_diagnostics_agree_with_the_compiler_without_fragment_whole_story_requirements()
    {
        foreach (var source in new[]
                 {
                     Source(("characters/extra.talewright.yaml", "items: {lamp: Lamp}\n")),
                     Source(("characters/extra.talewright.yaml", "game:\n  id: nope\n")),
                     Source(("extra.talewright.yaml", "items: {lamp: Lamp}\n")),
                     Source(("characters/extra.talewright.yaml", "triggers:\n  - id: t\n    when: flag:missing\n    do: set other\n")),
                 })
        {
            var analysis = StoryAnalyzer.Analyze(source);
            Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        }

        var clean = StoryAnalyzer.Analyze(Source(("characters/extra.talewright.yaml", "items: {lamp: Lamp}\n")));
        Assert.DoesNotContain(clean.Diagnostics, diagnostic => diagnostic.Location.File == "characters/extra.talewright.yaml");

        var trigger = Source(("rules/triggers.talewright.yaml", "triggers:\n  - id: t\n    when: flag:missing\n    do: set done\n"));
        var triggered = StoryAnalyzer.Analyze(trigger);
        Assert.Equal(StoryCompiler.Compile(trigger).Diagnostics, triggered.Diagnostics);
        var condition = Assert.Single(triggered.Expressions, context =>
            context.Kind == ExpressionContextKind.Condition && context.Text == "flag:missing");
        Assert.Equal(new SourceLocation("rules/triggers.talewright.yaml", 3, 11), condition.Range.Start);
        Assert.Contains(triggered.Diagnostics, diagnostic =>
            diagnostic.Location.File == "rules/triggers.talewright.yaml");
    }

    [Fact]
    public async Task Unsaved_supporting_files_affect_analysis_and_closing_restores_disk()
    {
        var workspace = new StoryWorkspace(Source());
        var before = (await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis;
        Assert.Contains(before.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        workspace.Open(Guide, 1, GuideText);
        var open = (await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis;
        Assert.DoesNotContain(open.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(Guide, StoryNavigation.Definition(open, Find(open.Source, "main.tale", "> gu|ide:"))!.Start.File);

        Assert.True(workspace.Change(Guide, 2, GuideText.Replace("guide:", "mentor:")));
        var renamed = (await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis;
        Assert.Contains(renamed.Symbols, symbol => symbol.Name == "mentor" && symbol.Declaration!.Start.File == Guide);
        Assert.DoesNotContain(renamed.Symbols, symbol => symbol.Name == "guide");

        workspace.Close(Guide);
        var closed = (await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis;
        Assert.False(closed.Source.Files.ContainsKey(Guide));
        Assert.Contains(closed.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        workspace.UpdateFile(Guide, GuideText);
        Assert.DoesNotContain((await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        workspace.UpdateFile(Guide, null);
        Assert.Contains((await workspace.AnalyzeAsync(TimeSpan.Zero)).Analysis.Diagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("characters/guide.yaml")]
    [InlineData("characters/guide.talewright.yml")]
    [InlineData("characters/guide.Talewright.YAML")]
    [InlineData("../guide.talewright.yaml")]
    public void Workspace_rejects_unrecognized_or_unsafe_configuration_paths(string path)
    {
        var workspace = new StoryWorkspace(Source());
        Assert.Throws<ArgumentException>(() => workspace.Open(path, 1, GuideText));
    }
}
