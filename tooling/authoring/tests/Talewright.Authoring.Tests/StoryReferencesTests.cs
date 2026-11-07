using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class StoryReferencesTests
{
    private const string Supporting = "characters/guide.talewright.yaml";

    private static readonly StorySource Story = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [StorySource.ManifestFileName] = """
            game:
              id: refs-story
              title: Refs
              start: town.gate
              ruleset:
                stats:
                  - {id: grit, category: attribute}
                  - {id: tracking, category: skill, governing-attribute: grit}
            characters:
              player:
                first-name: Ada
                stats: {grit: 2, tracking: 1}
                inventory: [rope]
            factions:
              guild: {name: Guild}
            items:
              rope: {name: Rope, modifiers: {grit: 1}}
              'lamp': Lamp
            flags: [gate]
            vars: {gate: 0, coins: 3}
            """,
        [Supporting] = """
            characters:
              guide:
                first-name: Bea
                inventory: ["lamp"]
            triggers:
              - id: rich
                when: var:coins > 5
                do: set flag:rich
            """,
        ["town.tale"] = """
            === town.gate
            @title The Gate
            @tags gate, town
            > guide: The gate opens for {player:first-name}. You carry {var:coins} coins.
            {if flag:gate}The gate is open.{/if} Count {var:gate}.
            ~ set flag:gate; var:coins += 1; give guide rope
            * [Go] (requires player has rope and player:grit >= 1) -> gate
            * [Climb] skill-check tracking dc 10
              success: -> town.square
              failure: { faction:guild:standing -= 1 } -> gate
            """,
        ["gate.tale"] = """
            === gate
            @hub gate and town
            The town.gate scene is visited {town.gate:player:visits} times.
            {if player visited town.gate}Seen guide, rope and grit.{/if}
            -> END

            === town.square
            @tags town
            ::: skill-check guide grit dc 8
            success: -> town.gate
            failure: -> END
            :::
            """,
    });

    private static readonly Lazy<StoryAnalysis> Analysis = new(() => StoryAnalyzer.Analyze(Story));

    private static SourceLocation Find(string file, string marked)
    {
        var caret = marked.IndexOf('|');
        var needle = marked.Remove(caret, 1);
        var lines = Story.Files[file].ReplaceLineEndings("\n").Split('\n');
        var matches = Enumerable.Range(0, lines.Length)
            .Where(line => lines[line].Contains(needle, StringComparison.Ordinal)).ToArray();
        var line = Assert.Single(matches);
        Assert.Equal(lines[line].IndexOf(needle, StringComparison.Ordinal), lines[line].LastIndexOf(needle, StringComparison.Ordinal));
        return new SourceLocation(file, line + 1, lines[line].IndexOf(needle, StringComparison.Ordinal) + caret + 1);
    }

    private static string Text(SourceRange range)
    {
        Assert.Equal(range.Start.Line, range.End.Line);
        var line = Story.Files[range.Start.File].ReplaceLineEndings("\n").Split('\n')[range.Start.Line - 1];
        return $"{range.Start.File}:{range.Start.Line}:{range.Start.Column} " +
            line[(range.Start.Column - 1)..(range.End.Column - 1)];
    }

    private static string At(string file, string marked, string name)
    {
        var location = Find(file, marked);
        return Text(new(location, location.Offset(name.Length)));
    }

    private static string[] References(string file, string marked, bool includeDeclaration) =>
        StoryReferences.Find(Analysis.Value, Find(file, marked), includeDeclaration).Select(Text).ToArray();

    private static void AssertReferences(string name, (string File, string Marked)[] declarations,
        (string File, string Marked)[] references)
    {
        var expectedReferences = references.Select(reference => At(reference.File, reference.Marked, name)).Order().ToArray();
        var expectedAll = expectedReferences.Concat(declarations.Select(declaration => At(declaration.File, declaration.Marked, name)))
            .Order().ToArray();
        // Every declaration and reference resolves to the same identity, so the request position does not matter.
        foreach (var (file, marked) in declarations.Concat(references))
        {
            Assert.Equal(expectedReferences, References(file, marked, includeDeclaration: false).Order());
            Assert.Equal(expectedAll, References(file, marked, includeDeclaration: true).Order());
        }
    }

    [Fact]
    public void Story_compiles_without_errors()
    {
        Assert.DoesNotContain(Analysis.Value.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(StoryCompiler.Compile(Story).Diagnostics, Analysis.Value.Diagnostics);
    }

    [Fact]
    public void Dotted_scene_references_span_diverts_outcomes_visits_and_the_start_scene()
    {
        AssertReferences("town.gate",
            [("town.tale", "=== |town.gate")],
            [
                (StorySource.ManifestFileName, "start: |town.gate"),
                ("gate.tale", "{|town.gate:player:visits}"),
                ("gate.tale", "player visited |town.gate"),
                ("gate.tale", "success: -> |town.gate"),
            ]);
        AssertReferences("town.square",
            [("gate.tale", "=== |town.square")],
            [("town.tale", "success: -> |town.square")]);
    }

    [Fact]
    public void Same_spelling_names_in_separate_namespaces_are_separate_identities()
    {
        AssertReferences("gate",
            [("gate.tale", "=== |gate")],
            [("town.tale", "(requires player has rope and player:grit >= 1) -> |gate"), ("town.tale", "standing -= 1 } -> |gate")]);
        AssertReferences("gate",
            [(StorySource.ManifestFileName, "flags: [|gate]")],
            [("town.tale", "{if flag:|gate}"), ("town.tale", "set flag:|gate;")]);
        AssertReferences("gate",
            [(StorySource.ManifestFileName, "vars: {|gate: 0")],
            [("town.tale", "{var:|gate}")]);
        AssertReferences("gate",
            [("town.tale", "@tags |gate, town")],
            [("gate.tale", "@hub |gate and town")]);
    }

    [Fact]
    public void Tag_identities_span_every_tags_entry_and_hub_selector()
    {
        AssertReferences("town",
            [("town.tale", "@tags gate, |town"), ("gate.tale", "@tags |town")],
            [("gate.tale", "@hub gate and |town")]);
    }

    [Fact]
    public void Flags_and_variables_include_uses_and_effects_that_declare_them()
    {
        AssertReferences("coins",
            [(StorySource.ManifestFileName, "|coins: 3")],
            [("town.tale", "{var:|coins}"), ("town.tale", "var:|coins += 1"), (Supporting, "when: var:|coins > 5")]);
        // Setting an undeclared flag declares it; the set itself is the flag's only occurrence.
        AssertReferences("rich", [], [(Supporting, "set flag:|rich")]);
    }

    [Fact]
    public void Characters_items_factions_and_stats_cover_tale_and_manifest_uses()
    {
        AssertReferences("guide",
            [(Supporting, "  |guide:")],
            [("town.tale", "> |guide:"), ("town.tale", "give |guide rope"), ("gate.tale", "skill-check |guide grit")]);
        AssertReferences("player",
            [(StorySource.ManifestFileName, "  |player:")],
            [
                ("town.tale", "{|player:first-name}"), ("town.tale", "(requires |player has rope"),
                ("town.tale", "and |player:grit"), ("gate.tale", "{if |player visited"),
            ]);
        AssertReferences("rope",
            [(StorySource.ManifestFileName, "  |rope: {name")],
            [
                (StorySource.ManifestFileName, "inventory: [|rope]"), ("town.tale", "player has |rope"),
                ("town.tale", "give guide |rope"),
            ]);
        AssertReferences("guild",
            [(StorySource.ManifestFileName, "  |guild:")],
            [("town.tale", "faction:|guild:standing")]);
        AssertReferences("grit",
            [(StorySource.ManifestFileName, "{id: |grit")],
            [
                (StorySource.ManifestFileName, "governing-attribute: |grit"),
                (StorySource.ManifestFileName, "stats: {|grit: 2"),
                (StorySource.ManifestFileName, "modifiers: {|grit: 1}"),
                ("town.tale", "player:|grit >= 1"), ("gate.tale", "skill-check guide |grit"),
            ]);
        AssertReferences("tracking",
            [(StorySource.ManifestFileName, "{id: |tracking")],
            [(StorySource.ManifestFileName, "|tracking: 1}"), ("town.tale", "skill-check |tracking dc")]);
    }

    [Fact]
    public void Quoted_yaml_names_keep_exact_ranges()
    {
        AssertReferences("lamp",
            [(StorySource.ManifestFileName, "  '|lamp': Lamp")],
            [(Supporting, "inventory: [\"|lamp\"]")]);
    }

    [Fact]
    public void Story_and_trigger_ids_return_their_declarations_only_when_requested()
    {
        AssertReferences("refs-story", [(StorySource.ManifestFileName, "id: |refs-story")], []);
        AssertReferences("rich", [(Supporting, "id: |rich")], []);
    }

    [Theory]
    [InlineData("gate.tale", "The town.|gate scene")]
    [InlineData("gate.tale", "Seen |guide, rope")]
    [InlineData("gate.tale", "Seen guide, |rope")]
    [InlineData("town.tale", "The |gate is open")]
    [InlineData("town.tale", "@title The |Gate")]
    [InlineData("gate.tale", "failure: -> |END")]
    [InlineData("town.tale", "{player:|first-name}")]
    [InlineData("gate.tale", "{town.gate:player:|visits}")]
    public void Prose_keywords_and_properties_are_not_names(string file, string marked)
    {
        Assert.Empty(References(file, marked, includeDeclaration: true));
    }

    [Fact]
    public void Prose_never_appears_among_references()
    {
        var prose = new[] { Find("gate.tale", "The |town.gate scene"), Find("gate.tale", "Seen |guide"), Find("gate.tale", "guide, |rope"), Find("gate.tale", "and |grit.") };
        foreach (var name in new[] { "town.gate", "guide", "rope", "grit" })
        {
            var declaration = Analysis.Value.Symbols.First(symbol => symbol.Name == name && symbol.Declaration is not null).Declaration!;
            var references = StoryReferences.Find(Analysis.Value, declaration.Start, includeDeclaration: true);
            Assert.NotEmpty(references);
            Assert.DoesNotContain(references, range => prose.Any(point => point.File == range.Start.File && point.Line == range.Start.Line &&
                point.Column >= range.Start.Column && point.Column <= range.End.Column));
        }
    }

    [Fact]
    public void Current_file_analyses_do_not_claim_story_wide_references()
    {
        var workspace = new StoryWorkspace(Story);
        var current = workspace.AnalyzeCurrentFile("town.tale", Story.Files["town.tale"], Analysis.Value, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => StoryReferences.Find(current, Find("town.tale", "> |guide:"), true));
    }

    [Fact]
    public void Diagnostic_extended_analysis_preserves_story_wide_references()
    {
        var analysis = Analysis.Value;
        var expected = StoryReferences.Find(analysis, Find("town.tale", "> |guide:"), includeDeclaration: true);
        var extended = analysis.WithDiagnostic(new(DiagnosticSeverity.Warning, "HOST001", "Host warning.",
            new SourceLocation("town.tale", 1, 1)));

        Assert.Same(analysis.Names, extended.Names);
        Assert.Equal(expected, StoryReferences.Find(extended, Find("town.tale", "> |guide:"), includeDeclaration: true));
    }
}
