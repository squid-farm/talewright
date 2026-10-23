using Talewright.Core.Rulesets;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Tale.Analysis;
using Talewright.Tale.Compilation;
using Talewright.Tale.Diagnostics;
using Talewright.Authoring;

namespace Talewright.Authoring.Tests;

public class StoryAnalysisTests
{
    private const string Manifest = """
        id: analysis
        title: Analysis
        start: first
        characters:
          player: {first-name: Ada}
        """;

    private static StorySource Source(string tale = "=== first\n-> END", string yaml = Manifest) => new(
        new Dictionary<string, string> { ["story.yaml"] = yaml, ["main.tale"] = tale });

    [Fact]
    public void Host_diagnostics_do_not_mutate_the_compiler_snapshot()
    {
        var analysis = StoryAnalyzer.Analyze(Source());
        var diagnostic = new Diagnostic(DiagnosticSeverity.Error, "HOST001", "Host error.",
            new SourceLocation("story.yaml", 1, 1));
        var extended = analysis.WithDiagnostic(diagnostic);
        Assert.DoesNotContain(diagnostic, analysis.Diagnostics);
        Assert.Contains(diagnostic, extended.Diagnostics);
        Assert.Same(analysis.Source, extended.Source);
        Assert.Same(analysis.Symbols, extended.Symbols);
        Assert.Same(analysis.Expressions, extended.Expressions);
    }

    [Fact]
    public void Valid_analysis_matches_compilation_and_preserves_source_ranges()
    {
        var source = Source("  === first\r\n{player:first-name}\r\n~ set flag:ready; var:count += 1\r\n-> END",
            Manifest + "\nitems: {coin: Silver coin}\nfactions: {guild: {}}\nvars: {attempts: 0}");
        var result = StoryAnalyzer.Analyze(source);
        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, result.Diagnostics);
        Assert.Same(source, result.Source);
        var scene = Assert.Single(result.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Scene);
        Assert.Equal(new SourceLocation("main.tale", 1, 7), scene.Declaration!.Start);
        Assert.Equal(new SourceLocation("main.tale", 1, 12), scene.Declaration.End);
        var player = Assert.Single(result.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Character);
        Assert.Equal(new SourceLocation("story.yaml", 5, 3), player.Declaration!.Start);
        Assert.Equal(new SourceLocation("story.yaml", 5, 9), player.Declaration.End);
        Assert.Contains(result.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Item && symbol.Name == "coin");
        Assert.Contains(result.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Faction && symbol.Name == "guild");
        Assert.Contains(result.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Stat && symbol.Name == "str");
        Assert.Contains(result.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Flag && symbol.Name == "ready" &&
            symbol.Declaration!.Start == new SourceLocation("main.tale", 3, 12));
        Assert.Contains(result.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Variable && symbol.Name == "count" &&
            symbol.Declaration!.Start == new SourceLocation("main.tale", 3, 23));
        Assert.Contains(result.Expressions, expression => expression.Kind == ExpressionContextKind.Substitution &&
            expression.Text == "player:first-name" &&
            expression.Range.Start == new SourceLocation("main.tale", 2, 2));
    }

    [Theory]
    [InlineData("=== first\n@requires player:hp >=\n-> END", "player:hp >=")]
    [InlineData("=== first\nHello {player:", "player:")]
    public void Incomplete_expressions_keep_symbols_context_and_compiler_diagnostics(string text, string expression)
    {
        var source = Source(text);
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(analysis.Symbols, symbol => symbol.Name == "first" && symbol.Kind == AuthoringSymbolKind.Scene);
        Assert.Contains(analysis.Symbols, symbol => symbol.Name == "player" && symbol.Kind == AuthoringSymbolKind.Character);
        Assert.Contains(analysis.Expressions, context => context.Text == expression);
    }

    [Theory]
    [InlineData("characters: [")]
    [InlineData("")]
    public void Invalid_yaml_still_retains_tale_declarations(string yaml)
    {
        var source = Source("=== first\n~ set flag:ready\n{player:\n-> END", yaml);
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        Assert.Contains(analysis.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Scene && symbol.Name == "first");
        Assert.Contains(analysis.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Flag && symbol.Name == "ready");
        Assert.Contains(analysis.Expressions, expression => expression.Text == "player:");
    }

    [Fact]
    public void Missing_manifest_and_semantically_invalid_yaml_preserve_partial_symbols()
    {
        var missing = StoryAnalyzer.Analyze(new StorySource(new Dictionary<string, string>
        {
            ["main.tale"] = "=== first\n-> END",
        }));
        Assert.Contains(missing.Symbols, symbol => symbol.Name == "first");
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Code == "TW4002");
        var invalid = StoryAnalyzer.Analyze(Source(yaml: Manifest + "\nruleset:\n  stats:\n    - id: focus\n      min: 8\n      max: 2"));
        Assert.Contains(invalid.Symbols, symbol => symbol.Name == "focus" && symbol.Declaration is not null);
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Code == "TW4003");
    }

    [Fact]
    public void Multiline_inline_expression_ranges_follow_original_indentation_and_utf16_columns()
    {
        var source = Source("=== first\r\n  A 🦑 {player:\r\n    first-name}\r\n-> END");
        var analysis = StoryAnalyzer.Analyze(source);
        var expression = Assert.Single(analysis.Expressions,
            context => context.Kind == ExpressionContextKind.Substitution);
        Assert.Equal(new SourceLocation("main.tale", 2, 9), expression.Range.Start);
        Assert.Equal(new SourceLocation("main.tale", 3, 15), expression.Range.End);
    }

    [Theory]
    [InlineData("=== first\n  A {\n    if player:hp > 0}yes{/if}", "player:hp > 0")]
    [InlineData("=== first\n  A {\n    if player:hp >", "player:hp >")]
    public void Multiline_inline_conditions_keep_their_kind_and_exact_expression_range(string tale, string expressionText)
    {
        var source = Source(tale);
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.Equal(StoryCompiler.Compile(source).Diagnostics, analysis.Diagnostics);
        var expression = Assert.Single(analysis.Expressions);
        Assert.Equal(ExpressionContextKind.Condition, expression.Kind);
        Assert.Equal(expressionText, expression.Text);
        Assert.Equal(new SourceLocation("main.tale", 3, 8), expression.Range.Start);
        Assert.Equal(new SourceLocation("main.tale", 3, 8 + expressionText.Length), expression.Range.End);
    }

    [Fact]
    public async Task Workspace_overlays_invalidate_cross_file_symbols_and_restore_disk_on_close()
    {
        var workspace = new StoryWorkspace(Source("=== first\n-> next"));
        var original = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.True(original.Published);
        Assert.Contains(original.Analysis.Diagnostics, diagnostic => diagnostic.Code == "TW2002");
        workspace.UpdateFile("chapters/next.tale", "=== next\n-> END");
        Assert.Null(workspace.Current);
        var added = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.DoesNotContain(added.Analysis.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        workspace.Open("chapters/next.tale", 1, "=== unsaved\n-> END");
        var changed = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains(changed.Analysis.Symbols, symbol => symbol.Name == "unsaved");
        Assert.Contains(changed.Analysis.Diagnostics, diagnostic => diagnostic.Code == "TW2002");
        Assert.True(workspace.Change("chapters/next.tale", 2, "=== next\n-> END"));
        Assert.False(workspace.Change("chapters/next.tale", 1, "=== old\n-> END"));
        workspace.Open("story.yaml", 1, Manifest.Replace("start: first", "start: next"));
        var overlay = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains("start: next", overlay.Analysis.Source.Files["story.yaml"]);
        Assert.True(workspace.Change("story.yaml", 2, Manifest.Replace("start: first", "start: unknown")));
        var yamlChanged = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains(yamlChanged.Analysis.Diagnostics, diagnostic => diagnostic.Code == "TW3007");
        Assert.True(workspace.Change("story.yaml", 3, Manifest));
        workspace.UpdateFile("chapters/next.tale", null);
        var deletedUnderOverlay = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains(deletedUnderOverlay.Analysis.Symbols, symbol => symbol.Name == "next");
        workspace.Close("chapters/next.tale");
        workspace.Close("story.yaml");
        var deleted = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.DoesNotContain(deleted.Analysis.Symbols, symbol => symbol.Name == "next");
        Assert.Contains(deleted.Analysis.Diagnostics, diagnostic => diagnostic.Code == "TW2002");
        Assert.Contains("-> next", original.Analysis.Source.Files["main.tale"]);
    }

    [Fact]
    public async Task Disk_file_changes_and_invalid_unsaved_yaml_do_not_reuse_old_symbols()
    {
        var workspace = new StoryWorkspace(Source());
        workspace.UpdateFile("extra.tale", "=== old\n-> END");
        await workspace.AnalyzeAsync(TimeSpan.Zero);
        workspace.UpdateFile("extra.tale", "=== changed\n-> END");
        var changed = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains(changed.Analysis.Symbols, symbol => symbol.Name == "changed");
        Assert.DoesNotContain(changed.Analysis.Symbols, symbol => symbol.Name == "old");
        workspace.Open("story.yaml", 1, "characters: [");
        var invalid = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains(invalid.Analysis.Diagnostics, diagnostic => diagnostic.Code == "TW4001");
        Assert.DoesNotContain(invalid.Analysis.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Character);
        Assert.Contains(invalid.Analysis.Symbols, symbol => symbol.Name == "changed");
        workspace.Close("story.yaml");
        var restored = await workspace.AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains(restored.Analysis.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Character);
    }

    [Fact]
    public async Task Superseded_analysis_is_rejected_and_cancellation_propagates()
    {
        var workspace = new StoryWorkspace(Source());
        var stale = workspace.AnalyzeAsync(TimeSpan.FromMilliseconds(50));
        workspace.Open("main.tale", 1, "=== fresh\n-> END");
        var current = await workspace.AnalyzeAsync(TimeSpan.Zero);
        var rejected = await stale;
        Assert.False(rejected.Published);
        Assert.Same(current.Analysis, workspace.Current);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.AnalyzeAsync(TimeSpan.Zero, cancellation.Token));
    }

    [Fact]
    public async Task Metadata_roundtrip_matches_explicit_project_options_without_running_project_delegates()
    {
        var options = new CompilerOptions
        {
            Predicates = new NamedPredicateRegistry().Register("Ready", _ => throw new InvalidOperationException("Must not evaluate")),
            Modifiers = TextModifierRegistry.CreateDefault().Register(
                new DelegateTextModifier("greeting", (_, _) => throw new InvalidOperationException("Must not render"))),
            Rulesets = new Dictionary<string, Func<Ruleset>>(CompilerOptions.DefaultRulesets())
            {
                ["project"] = () => new RulesetBuilder("project").Build(),
            },
        };
        var metadata = AuthoringProjectMetadata.FromJson(AuthoringProjectMetadata.Export(options).ToJson());
        var source = Source("=== first\n@requires @Ready\n{player:first-name#greeting}\n-> END",
            Manifest + "\nruleset: {base: project}");
        var analysis = metadata.Analyze(source);
        Assert.Equal(StoryCompiler.Compile(source, options).Diagnostics, analysis.Diagnostics);
        Assert.Empty(analysis.Diagnostics);
        Assert.Contains(analysis.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Predicate && symbol.Name == "Ready");
        Assert.Contains(analysis.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Modifier && symbol.Name == "greeting");
        Assert.Contains(analysis.Symbols, symbol => symbol.Kind == AuthoringSymbolKind.Ruleset && symbol.Name == "project");
        var missing = await new StoryWorkspace(source, requiresProjectMetadata: true).AnalyzeAsync(TimeSpan.Zero);
        Assert.Contains(missing.Analysis.Diagnostics, diagnostic => diagnostic.Code == "TW5001");
    }

    [Fact]
    public void Exported_default_metadata_preserves_ruleset_and_check_validation()
    {
        var metadata = AuthoringProjectMetadata.FromJson(AuthoringProjectMetadata.Export(CompilerOptions.Default).ToJson());
        var source = Source("""
            === first
            ::: skill-check athletics dc 12
            success: -> END
            failure: -> END
            :::
            """);
        Assert.Equal(StoryCompiler.Compile(source).Diagnostics,
            metadata.Analyze(source).Diagnostics);
        Assert.Equal(StoryAnalyzer.Analyze(source).Symbols,
            metadata.Analyze(source).Symbols);
    }

    [Fact]
    public void Metadata_preserves_ruleset_alias_identity_in_diagnostics()
    {
        var options = new CompilerOptions
        {
            Rulesets = new Dictionary<string, Func<Ruleset>>(CompilerOptions.DefaultRulesets())
            {
                ["alias"] = () => new RulesetBuilder("actual-definition").Build(),
            },
        };
        var metadata = AuthoringProjectMetadata.FromJson(AuthoringProjectMetadata.Export(options).ToJson());
        var source = Source(yaml: Manifest + "\nruleset: {base: alias}\nitems: {charm: {modifiers: {unknown: 1}}}");
        Assert.Equal(StoryCompiler.Compile(source, options).Diagnostics,
            metadata.Analyze(source).Diagnostics);
    }

    [Fact]
    public void Metadata_rejects_unknown_versions_fields_missing_values_and_duplicate_registrations()
    {
        var metadata = AuthoringProjectMetadata.Export(CompilerOptions.Default);
        Assert.Throws<InvalidDataException>(() => (metadata with { FormatVersion = 2 }).ToJson());
        Assert.Throws<InvalidDataException>(() => (metadata with { Predicates = ["Ready", "Ready"] }).ToJson());
        Assert.Throws<System.Text.Json.JsonException>(() => AuthoringProjectMetadata.FromJson("{}"));
        Assert.Throws<System.Text.Json.JsonException>(() => AuthoringProjectMetadata.FromJson(
            metadata.ToJson().Replace("\"FormatVersion\": 1", "\"Command\": \"run project\", \"FormatVersion\": 1")));
        Assert.Throws<InvalidDataException>(() => (metadata with { Rulesets = [] }).ToJson());
    }

    [Fact]
    public async Task Root_discovery_uses_nearest_manifest_and_never_loads_project_code()
    {
        var root = Path.Combine(Path.GetTempPath(), $"talewright-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "nested", "chapters"));
        try
        {
            File.WriteAllText(Path.Combine(root, "story.yaml"), Manifest);
            File.WriteAllText(Path.Combine(root, "example.csproj"), "<Project />");
            var project = StoryWorkspace.FromDirectory(root);
            var analysis = await project.AnalyzeAsync(TimeSpan.Zero);
            Assert.Contains(analysis.Analysis.Diagnostics, diagnostic => diagnostic.Code == "TW5001");
            Assert.Equal(root, StoryWorkspace.FindStoryRoot(Path.Combine(root, "nested", "chapters", "file.tale")));
            File.WriteAllText(Path.Combine(root, "nested", "story.yaml"), Manifest);
            Assert.Equal(Path.Combine(root, "nested"), StoryWorkspace.FindStoryRoot(Path.Combine(root, "nested", "chapters")));
            Assert.Throws<ArgumentException>(() => new StoryWorkspace(Source()).Open("../file.tale", 1, ""));
        }
        finally
        {
            File.Delete(Path.Combine(root, "nested", "story.yaml"));
            File.Delete(Path.Combine(root, "story.yaml"));
            File.Delete(Path.Combine(root, "example.csproj"));
            Directory.Delete(Path.Combine(root, "nested", "chapters"));
            Directory.Delete(Path.Combine(root, "nested"));
            Directory.Delete(root);
        }
    }
}
