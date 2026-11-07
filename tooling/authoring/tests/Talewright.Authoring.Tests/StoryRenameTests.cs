using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class StoryRenameTests
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
            # The guide waits by the gate.
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
            * [Go] (requires player has rope and player:grit >= 1 and player:str >= 0) -> gate
            * [Climb] skill-check tracking dc 10
              success: -> town.square
              failure: { faction:guild:standing -= 1 } -> gate
            """,
        ["gate.tale"] = """
            === gate
            @hub gate and town
            The town.gate scene is visited {town.gate:player:visits} times.
            {if player visited town.gate}Seen guide, rope, lamp and grit.{/if}
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

    private static StoryAnalysis Analyze(StorySource source, CancellationToken token) =>
        StoryAnalyzer.Analyze(source, null, token);

    private static SourceLocation Find(StorySource story, string file, string marked)
    {
        var caret = marked.IndexOf('|');
        var needle = marked.Remove(caret, 1);
        var lines = story.Files[file].ReplaceLineEndings("\n").Split('\n');
        var line = Assert.Single(Enumerable.Range(0, lines.Length),
            line => lines[line].Contains(needle, StringComparison.Ordinal));
        Assert.Equal(lines[line].IndexOf(needle, StringComparison.Ordinal), lines[line].LastIndexOf(needle, StringComparison.Ordinal));
        return new SourceLocation(file, line + 1, lines[line].IndexOf(needle, StringComparison.Ordinal) + caret + 1);
    }

    private static StoryRenameResult Rename(string file, string marked, string newName,
        LintConfiguration? lint = null) =>
        StoryRename.Rename(Analysis.Value, Find(Story, file, marked), newName, Analyze, lint);

    private static string Refusal(StorySource story, string file, string marked, string newName = "renamed")
    {
        var analysis = StoryAnalyzer.Analyze(story);
        var position = Find(story, file, marked);
        var preparation = StoryRename.Prepare(analysis, position);
        var result = StoryRename.Rename(analysis, position, newName, Analyze);
        Assert.Empty(result.Edits);
        Assert.Null(result.Warning);
        Assert.NotNull(result.Refusal);
        if (preparation.Refusal is not null) Assert.Equal(preparation.Refusal, result.Refusal);
        return result.Refusal!;
    }

    private static Dictionary<string, string> Apply(StorySource story, IReadOnlyList<StoryRenameEdit> edits)
    {
        var files = story.Files.ToDictionary(StringComparer.Ordinal);
        foreach (var group in edits.GroupBy(edit => edit.Range.Start.File))
        {
            var lines = files[group.Key].Split('\n');
            foreach (var edit in group.OrderByDescending(edit => edit.Range.Start.Line).ThenByDescending(edit => edit.Range.Start.Column))
            {
                Assert.Equal(edit.Range.Start.Line, edit.Range.End.Line);
                var line = lines[edit.Range.Start.Line - 1];
                lines[edit.Range.Start.Line - 1] = line[..(edit.Range.Start.Column - 1)] + edit.NewText +
                    line[(edit.Range.End.Column - 1)..];
            }
            files[group.Key] = string.Join('\n', lines);
        }
        return files;
    }

    private static string[] Diagnostics(StoryAnalysis analysis) =>
        analysis.Diagnostics.Select(diagnostic => $"{diagnostic.Severity} {diagnostic.Code} {diagnostic.Location}").Order().ToArray();

    [Fact]
    public void Story_compiles_without_errors()
    {
        Assert.DoesNotContain(Analysis.Value.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("town.tale", "=== |town.gate", "town.portal")]
    [InlineData(Supporting, "  |guide:", "mentor")]
    [InlineData(StorySource.ManifestFileName, "{id: |grit", "nerve")]
    [InlineData(StorySource.ManifestFileName, "  |rope: {name", "cord")]
    [InlineData(StorySource.ManifestFileName, "  |guild:", "union")]
    [InlineData(StorySource.ManifestFileName, "flags: [|gate]", "gate_open")]
    [InlineData(StorySource.ManifestFileName, "|coins: 3", "gold")]
    [InlineData(StorySource.ManifestFileName, "  id: |refs-story", "refs-tale")]
    [InlineData("town.tale", "@tags gate, |town", "village")]
    [InlineData(Supporting, "- id: |rich", "wealthy")]
    [InlineData(StorySource.ManifestFileName, "  '|lamp'", "lantern")]
    public void Every_declared_kind_renames_each_occurrence_without_new_diagnostics(string file, string marked, string newName)
    {
        var position = Find(Story, file, marked);
        var preparation = StoryRename.Prepare(Analysis.Value, position);
        var target = Assert.IsType<StoryRenameTarget>(preparation.Target);
        Assert.Null(preparation.Refusal);

        var result = StoryRename.Rename(Analysis.Value, position, newName, Analyze);

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Warning);
        var expected = StoryReferences.Find(Analysis.Value, position, includeDeclaration: true);
        Assert.Equal(expected.Order(Comparer<SourceRange>.Create(CompareRanges)),
            result.Edits.Select(edit => edit.Range).Order(Comparer<SourceRange>.Create(CompareRanges)));
        Assert.All(result.Edits, edit => Assert.Equal(newName, edit.NewText));
        var renamed = StoryAnalyzer.Analyze(new StorySource(Apply(Story, result.Edits)));
        Assert.Equal(Diagnostics(Analysis.Value), Diagnostics(renamed));
        Assert.Equal(expected.Count, renamed.Names.Occurrences(target.Kind, newName).Count);
        Assert.Empty(renamed.Names.Occurrences(target.Kind, target.Name));
    }

    private static int CompareRanges(SourceRange left, SourceRange right)
    {
        var file = string.CompareOrdinal(left.Start.File, right.Start.File);
        if (file != 0) return file;
        return left.Start.Line != right.Start.Line ? left.Start.Line.CompareTo(right.Start.Line) : left.Start.Column.CompareTo(right.Start.Column);
    }

    [Fact]
    public void Renaming_a_scene_leaves_other_namespaces_prose_comments_and_file_names_unchanged()
    {
        var result = Rename("gate.tale", "=== |gate", "gatehouse");

        Assert.Null(result.Refusal);
        var files = Apply(Story, result.Edits);
        Assert.Equal(Story.Files.Keys.Order(), files.Keys.Order());
        Assert.Equal(Story.Files[StorySource.ManifestFileName], files[StorySource.ManifestFileName]);
        Assert.Equal(Story.Files[Supporting], files[Supporting]);
        Assert.Equal(Story.Files["gate.tale"].Replace("=== gate\n", "=== gatehouse\n", StringComparison.Ordinal), files["gate.tale"]);
        Assert.Equal(Story.Files["town.tale"]
                .Replace(">= 0) -> gate", ">= 0) -> gatehouse", StringComparison.Ordinal)
                .Replace("-= 1 } -> gate", "-= 1 } -> gatehouse", StringComparison.Ordinal),
            files["town.tale"]);
    }

    [Fact]
    public void Renaming_a_tag_updates_tags_entries_and_hub_selectors_only()
    {
        var result = Rename("gate.tale", "@hub gate and |town", "village");

        Assert.Null(result.Refusal);
        var files = Apply(Story, result.Edits);
        Assert.Contains("@tags gate, village", files["town.tale"], StringComparison.Ordinal);
        Assert.Contains("@hub gate and village", files["gate.tale"], StringComparison.Ordinal);
        Assert.Contains("@tags village", files["gate.tale"], StringComparison.Ordinal);
        Assert.Contains("=== town.square", files["gate.tale"], StringComparison.Ordinal);
        Assert.Contains("=== town.gate", files["town.tale"], StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_quoting_and_comments_are_preserved()
    {
        var result = Rename(Supporting, "inventory: [\"|lamp\"]", "lantern");

        Assert.Null(result.Refusal);
        var files = Apply(Story, result.Edits);
        Assert.Contains("  'lantern': Lamp", files[StorySource.ManifestFileName], StringComparison.Ordinal);
        Assert.Equal(Story.Files[Supporting].Replace("[\"lamp\"]", "[\"lantern\"]", StringComparison.Ordinal), files[Supporting]);
        Assert.Contains("lamp and grit", files["gate.tale"], StringComparison.Ordinal);
    }

    [Fact]
    public void Escaped_yaml_spellings_are_replaced_as_a_whole()
    {
        var story = new StorySource(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = """
                game:
                  id: escaped
                  title: Escaped
                  start: start
                characters:
                  player: {first-name: Ada, inventory: ["l\u0061mp"]}
                items:
                  lamp: Lamp
                """,
            ["start.tale"] = "=== start\n{if player has lamp}Lit.{/if}\n-> END\n",
        });
        var analysis = StoryAnalyzer.Analyze(story);

        var result = StoryRename.Rename(analysis, Find(story, "start.tale", "has |lamp"), "lantern", Analyze);

        Assert.Null(result.Refusal);
        var manifest = Apply(story, result.Edits)[StorySource.ManifestFileName];
        Assert.Contains("inventory: [\"lantern\"]", manifest, StringComparison.Ordinal);
        Assert.Contains("  lantern: Lamp", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Story_id_and_trigger_id_rename_from_their_only_declaration()
    {
        var story = Rename(StorySource.ManifestFileName, "  id: |refs-story", "refs-tale");
        var trigger = Rename(Supporting, "- id: |rich", "wealthy");

        Assert.Equal("refs-tale", Assert.Single(story.Edits).NewText);
        Assert.Contains("save identity", story.Warning, StringComparison.Ordinal);
        Assert.Contains("not migrated", story.Warning, StringComparison.Ordinal);
        Assert.Contains("set flag:rich", Apply(Story, trigger.Edits)[Supporting], StringComparison.Ordinal);
        Assert.Contains("not migrated", trigger.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_to_the_same_name_has_no_edits()
    {
        var result = Rename("town.tale", "=== |town.gate", "town.gate");

        Assert.Null(result.Refusal);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void Positions_outside_names_have_no_target_or_refusal()
    {
        var preparation = StoryRename.Prepare(Analysis.Value, Find(Story, "town.tale", "The |gate is open"));

        Assert.Null(preparation.Target);
        Assert.Null(preparation.Refusal);
    }

    [Fact]
    public void Fixed_and_ruleset_owned_names_are_refused()
    {
        Assert.Contains("player character", Refusal(Story, "town.tale", "{|player:first-name}"), StringComparison.Ordinal);
        Assert.Contains("ends the story", Refusal(Story, "gate.tale", "failure: -> |END"), StringComparison.Ordinal);
        Assert.Contains("built into the story's ruleset", Refusal(Story, "town.tale", "player:|str"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_story_stat_that_overrides_a_built_in_stat_is_explained_and_refused()
    {
        var story = new StorySource(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = """
                game:
                  id: override
                  title: Override
                  start: start
                  ruleset:
                    stats:
                      - {id: str, name: Might, category: attribute}
                characters:
                  player: {first-name: Ada, stats: {str: 2}}
                """,
            ["start.tale"] = """
                === start
                {if player:str >= 1}Strong.{/if}
                -> END
                """,
        });

        var refusal = Refusal(story, "start.tale", "player:|str");

        Assert.Contains("overrides a built-in stat", refusal, StringComparison.Ordinal);
        Assert.Equal(refusal, Refusal(story, StorySource.ManifestFileName, "{id: |str"));
    }

    [Theory]
    [InlineData("town.tale", "=== |town.gate", "9gate", "not a valid scene ID")]
    [InlineData("town.tale", "=== |town.gate", "END", "reserved for ending the story")]
    [InlineData(Supporting, "  |guide:", "player", "reserved for the player character")]
    [InlineData(StorySource.ManifestFileName, "|coins: 3", "two words", "not a valid variable ID")]
    [InlineData("town.tale", "@tags gate, |town", "a,b", "not a valid tag")]
    [InlineData("town.tale", "@tags gate, |town", "and", "reserved for combining tags")]
    [InlineData("town.tale", "@tags gate, |town", "old town", "Hub tags cannot contain whitespace")]
    public void Invalid_new_names_are_refused(string file, string marked, string newName, string reason)
    {
        var result = Rename(file, marked, newName);

        Assert.Empty(result.Edits);
        Assert.Contains(reason, result.Refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("town.tale", "=== |town.gate", "gate", "Scene 'gate' already exists")]
    [InlineData(StorySource.ManifestFileName, "flags: [|gate]", "rich", "Flag 'rich' already exists")]
    [InlineData(StorySource.ManifestFileName, "{id: |grit", "str", "built-in stat")]
    [InlineData("town.tale", "@tags |gate, town", "town", "Tag 'town' already exists")]
    public void Collisions_with_names_of_the_same_kind_are_refused(string file, string marked, string newName, string reason)
    {
        var result = Rename(file, marked, newName);

        Assert.Empty(result.Edits);
        Assert.Contains(reason, result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_spelling_in_another_namespace_is_not_a_collision()
    {
        var result = Rename("town.tale", "=== |town.gate", "coins");

        Assert.Null(result.Refusal);
        Assert.NotEmpty(result.Edits);
    }

    [Fact]
    public void Enabled_lint_rules_are_enforced_for_the_new_name()
    {
        var lint = LintConfiguration.Parse("""{"rules": {"TWL001": "warning"}}""");

        var refused = Rename("town.tale", "=== |town.gate", "Town.Gate", lint);
        var allowed = Rename("town.tale", "=== |town.gate", "Town.Gate");

        Assert.Contains("TWL001", refused.Refusal, StringComparison.Ordinal);
        Assert.Null(allowed.Refusal);
    }

    [Fact]
    public void New_names_that_would_add_diagnostics_are_refused()
    {
        // player:first-name is the player's name, so former stat comparisons now compare text with numbers.
        var result = Rename(StorySource.ManifestFileName, "{id: |grit", "first-name");

        Assert.Empty(result.Edits);
        Assert.Contains("would introduce TW2003 at town.tale:7:", result.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_declarations_are_refused_as_ambiguous()
    {
        var story = new StorySource(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = """
                game:
                  id: dupes
                  title: Dupes
                  start: start
                """,
            ["a.tale"] = "=== start\n-> next\n",
            ["b.tale"] = "=== next\nOne.\n-> END\n",
            ["c.tale"] = "=== next\nTwo.\n-> END\n",
        });

        Assert.Contains("declared 2 times", Refusal(story, "a.tale", "-> |next"), StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_mentioning_the_name_block_rename_but_unrelated_errors_do_not()
    {
        var story = new StorySource(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StorySource.ManifestFileName] = """
                game:
                  id: errors
                  title: Errors
                  start: start
                flags: [lit, open]
                """,
            ["start.tale"] = """
                === start
                {if flag:lit}Light.{/if}
                {if flag:lit and}Broken.{/if}
                {if flag:open}Open.{/if}
                -> END
                """,
        });
        Assert.Contains(StoryAnalyzer.Analyze(story).Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        Assert.Contains("could hide a reference", Refusal(story, "start.tale", "{if flag:|lit}Light"), StringComparison.Ordinal);
        var analysis = StoryAnalyzer.Analyze(story);
        var result = StoryRename.Rename(analysis, Find(story, "start.tale", "{if flag:|open}"), "ajar", Analyze);
        Assert.Null(result.Refusal);
        Assert.Equal(2, result.Edits.Count);
    }

    [Fact]
    public void Current_file_analyses_are_rejected()
    {
        var workspace = new StoryWorkspace(Story);
        var current = workspace.AnalyzeCurrentFile("town.tale", Story.Files["town.tale"], null, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() =>
            StoryRename.Prepare(current, Find(Story, "town.tale", "=== |town.gate")));
    }
}
