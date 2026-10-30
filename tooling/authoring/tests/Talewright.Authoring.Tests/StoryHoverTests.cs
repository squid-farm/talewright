using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class StoryHoverTests
{
    private const string Manifest = """
        game:
          id: hover
          title: Hover
          start: first
        characters:
          player: {first-name: Ada}
        items: {rope: Rope}
        factions: {guild: {name: Guild}}
        flags: [ready]
        vars: {coins: 1}
        """;

    [Theory]
    [InlineData("-> se|cond", "Scene", "second")]
    [InlineData("> pla|yer: Hello", "Character", "player")]
    [InlineData("@requires player:h|p > 0", "Stat", "hp")]
    [InlineData("@requires flag:rea|dy", "Flag", "ready")]
    [InlineData("{var:co|ins}", "Variable", "coins")]
    [InlineData("{item:ro|pe}", "Item", "rope")]
    [InlineData("{faction:gu|ild:name}", "Faction", "guild")]
    [InlineData("{faction:guild:na|me}", "Character full name or faction display name", "name")]
    [InlineData("{faction:guild:ti|er}", "Faction standing tier", "tier")]
    [InlineData("{player:first-name#cap|italize}", "Modifier", "capitalize")]
    [InlineData("@req|uires player:hp > 0", "Boolean condition", "requires")]
    [InlineData("@requires vis|ited(second)", "visited(scene)", "visited")]
    [InlineData("@requires player h|as rope", "tests whether", "has")]
    [InlineData("~ gi|ve player rope", "numeric expression", "give")]
    [InlineData("~ player:hp +|= 1", "Adds a numeric", "+=")]
    [InlineData("{player:pronoun:ob|ject}", "Pronoun form: object", "object")]
    [InlineData("{player:pronoun:posse|ssive-pronoun:determiner}", "Possessive pronoun properties", "possessive-pronoun")]
    [InlineData("{player:pronoun:possessive-pronoun:in|dependent}", "stands alone", "independent")]
    [InlineData("{player:pronoun#one-|many(\"has\", \"have\")}", "effective pronoun agreement", "one-many")]
    [InlineData("{item:rope#one-|many(\"is\", \"are\")}", "item name-agreement", "one-many")]
    [InlineData("{player:pro|noun}", "gives the subject form", "pronoun")]
    [InlineData("{im|age(\"maps/camp\", \"Camp\")}", "inserts image media", "image")]
    [InlineData("-> E|ND", "Ends the story", "END")]
    [InlineData("-|> second", "Diverts to", "->")]
    [InlineData("|~ player:hp += 1", "Entry effects", "~")]
    [InlineData("|* [Go] -> second", "Choice:", "*")]
    [InlineData("{i|f flag:ready}yes{/if}", "Begins conditional", "if")]
    [InlineData("{if flag:ready}yes{el|se}no{/if}", "Alternative text", "else")]
    [InlineData("{if flag:ready}yes{/i|f}", "Closes the current", "/if")]
    [InlineData("{i|f flag:ready}", "Begins conditional", "if")]
    [InlineData("::: ski|ll-check athletics dc 12", "selected ruleset", "skill-check")]
    public void Hover_uses_parser_recognized_symbols_and_syntax(string marked, string expected, string token)
    {
        var (analysis, position) = Analyze("=== first\n" + marked);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position));
        Assert.Contains(expected, hover.PlainText);
        var line = analysis.Source.Files["main.tale"].Split('\n')[1];
        Assert.Equal(token, line[(hover.Range.Start.Column - 1)..(hover.Range.End.Column - 1)]);
    }

    [Fact]
    public void Item_grammar_keys_explain_defaults_and_quantity_independence()
    {
        var yaml = Manifest.Replace("items: {rope: Rope}",
            "items:\n  rope: {name: Rope, name-agreement: plural, uncountable: false}");
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml, ["main.tale"] = "=== first\n-> END",
        });
        var analysis = StoryAnalyzer.Analyze(source);
        foreach (var (key, expected) in new[]
                 {
                     ("name-agreement", "never inventory quantity"),
                     ("uncountable", "Does not imply plural agreement"),
                 })
        {
            var before = yaml[..yaml.IndexOf(key, StringComparison.Ordinal)];
            var lines = before.Split('\n');
            var hover = StoryHover.Get(analysis,
                new(StorySource.ManifestFileName, lines.Length, lines[^1].Length + 1));
            Assert.NotNull(hover);
            Assert.Contains(expected, hover.PlainText);
        }
    }

    [Fact]
    public void Governing_attribute_key_hover_explains_category_target_and_formula()
    {
        const string key = "governing-attribute";
        var yaml = Manifest.Replace("  start: first",
            $"  start: first\n  ruleset:\n    stats:\n      - {{id: tracking, category: skill, {key}: str}}");
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml, ["main.tale"] = "=== first\n-> END",
        });
        var analysis = StoryAnalyzer.Analyze(source);
        var hover = StoryHover.Get(analysis, new(StorySource.ManifestFileName, 7,
            yaml.Split('\n')[6].IndexOf(key, StringComparison.Ordinal) + 2));
        Assert.NotNull(hover);
        Assert.Contains("category: skill", hover.PlainText);
        Assert.Contains("enabled attribute", hover.PlainText);
        Assert.Contains("floor", hover.PlainText);
    }

    [Theory]
    [InlineData("Ordinary sec|ond")]
    [InlineData("// -> sec|ond")]
    [InlineData("@title sec|ond")]
    [InlineData("-> miss|ing")]
    [InlineData("{unknown:hp|}")]
    [InlineData("@requires \"rea|dy\" == \"ready\"")]
    [InlineData("{player:unkn|own}")]
    [InlineData("{player:first-name#unkn|own}")]
    [InlineData("@requires @Unkn|own")]
    [InlineData("-> sec|")]
    [InlineData("{tit|le}")]
    [InlineData("{EN|D}")]
    [InlineData("{player:gi|ve}")]
    [InlineData("{player:first-name#gi|ve}")]
    [InlineData("@requires flag:gi|ve")]
    public void Unknown_partial_or_irrelevant_text_has_no_fabricated_help(string marked)
    {
        var (analysis, position) = Analyze("=== first\n" + marked);
        Assert.Null(StoryHover.Get(analysis, position));
    }

    [Fact]
    public void Incomplete_expressions_still_have_precise_utf16_hover_ranges()
    {
        var (analysis, position) = Analyze("=== first\r\nA 🦑 {player:h|p");
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position));
        Assert.Equal(new SourceLocation("main.tale", 2, 14), hover.Range.Start);
        Assert.Equal(new SourceLocation("main.tale", 2, 16), hover.Range.End);
        Assert.Contains("integer", hover.PlainText);
    }

    [Fact]
    public void Cross_file_declarations_include_authored_context()
    {
        var (analysis, position) = Analyze("=== first\n-> sec|ond");
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position));
        Assert.Contains("other.tale:1:5", hover.PlainText);
        Assert.Contains("=== second", hover.PlainText);
        Assert.Contains("Declared at", hover.Markdown);
    }

    [Theory]
    [InlineData("sta|rt", "scene where play begins")]
    [InlineData("rul|eset", "ruleset configuration")]
    [InlineData("dis|able", "Stat IDs to remove")]
    [InlineData("inv|entory", "List of item IDs")]
    public void Manifest_help_comes_from_the_bundled_schema(string marked, string description)
    {
        var yaml = Manifest.Replace("  start: first", "  start: first\n  ruleset: {disable: []}");
        var name = marked.Replace("|", "");
        if (name == "inventory") yaml = yaml.Replace("first-name: Ada", "first-name: Ada, inventory: []");
        var index = yaml.IndexOf(name, StringComparison.Ordinal) + marked.IndexOf('|');
        var analysis = Source("=== first\n-> END", yaml);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, Position(StorySource.ManifestFileName, yaml[..index])));
        Assert.Contains(description, hover.PlainText);
        Assert.Contains(name, hover.PlainText);
    }

    [Fact]
    public void Escaped_manifest_references_and_quoted_declarations_keep_authored_ranges()
    {
        var marked = Manifest.Replace("start: first", "start: \"\\u0073ec|ond\"");
        var caret = marked.IndexOf('|');
        var yaml = marked.Remove(caret, 1);
        var analysis = Source("=== first\n-> END", yaml);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, Position(StorySource.ManifestFileName, marked[..caret])));
        var line = yaml.Split('\n')[3];
        Assert.Equal("\\u0073econd", line[(hover.Range.Start.Column - 1)..(hover.Range.End.Column - 1)]);
        yaml = Manifest.Replace("player:", "\"player\":");
        analysis = Source("=== first\n-> END", yaml);
        var index = yaml.IndexOf("player", StringComparison.Ordinal);
        hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, Position(StorySource.ManifestFileName, yaml[..(index + 2)])));
        Assert.Equal(6, hover.Range.End.Column - hover.Range.Start.Column);
        Assert.Contains("Character", hover.PlainText);
    }

    [Fact]
    public void Markdown_cannot_promote_authored_source_into_links_or_html()
    {
        var (analysis, position) = Analyze("=== first\n-> sec|ond",
            other: "=== second\n@title [click](https://example.invalid) <script> `payload`\n-> END");
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position));
        Assert.Contains("` === second `", hover.Markdown);
        const string title = "`` \"[click](https://example.invalid) <script> `payload`\" ``";
        Assert.Contains("Title: " + title, hover.Markdown);
        Assert.DoesNotContain("https://", hover.Markdown.Replace(title, ""));
        var yaml = Manifest.Replace("player: {first-name: Ada}", "player: {first-name: '[click](https://example.invalid) `x` <b>'}");
        (analysis, position) = Analyze("=== first\n> pla|yer: Hello", yaml);
        hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position));
        Assert.Contains("`` ", hover.Markdown);
        Assert.Contains(" ``", hover.Markdown);
    }

    [Fact]
    public void Exported_registrations_provide_help_without_evaluating_code()
    {
        var (analysis, position) = Analyze("=== first\n@requires @Cus|tom");
        var metadata = AuthoringProjectMetadata.Export(CompilerOptions.Default) with { Predicates = ["Custom"], Modifiers = ["special"], BuiltInModifiers = [] };
        analysis = metadata.Analyze(analysis.Source);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position));
        Assert.Contains("Predicate", hover.PlainText);
        Assert.Contains("registrations", hover.PlainText);
        analysis = analysis.WithDiagnostic(new(DiagnosticSeverity.Error, "TW5001", "Missing", new(StorySource.ManifestFileName, 1, 1)));
        Assert.Throws<InvalidOperationException>(() => StoryHover.Get(analysis, position));
    }

    [Fact]
    public void Invalid_yaml_does_not_invent_schema_locations_but_keeps_partial_scene_help()
    {
        var yaml = "characters: [";
        var analysis = Source("=== first\n-> second", yaml);
        Assert.Null(StoryHover.Get(analysis, new(StorySource.ManifestFileName, 1, 3)));
        Assert.Contains("Scene", Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, new("main.tale", 2, 5))).PlainText);
    }

    [Fact]
    public void Skill_check_hover_uses_the_parsed_actor_and_skill_roles()
    {
        var yaml = Manifest.Replace("player: {first-name: Ada}",
            "player: {first-name: Ada}\n  hp: {first-name: 'Hit Points'}");
        var (analysis, actorPosition) = Analyze("=== first\n::: skill-check h|p athletics dc 12", yaml);
        var actorHover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, actorPosition));
        Assert.Contains("Character", actorHover.PlainText);
        Assert.Contains("Declared at story.talewright.yaml", actorHover.PlainText);

        var (skillAnalysis, skillPosition) = Analyze("=== first\n::: skill-check hp ath|letics dc 12", yaml);
        var skillHover = Assert.IsType<AuthoringHover>(StoryHover.Get(skillAnalysis, skillPosition));
        Assert.Contains("Stat", skillHover.PlainText);
        Assert.Contains("athletics", skillHover.PlainText);
    }

    [Theory]
    [InlineData("=== first\n* [Go] -|> second")]
    [InlineData("=== first\n::: skill-check athletics dc 12\nsuccess: -|> second\nfailure: -> END\n:::")]
    [InlineData("=== first\n::: skill-check athletics dc 12\nsuccess: -> second\nfailure: -|> END\n:::")]
    public void Divert_markers_in_choices_and_check_outcomes_have_syntax_help(string marked)
    {
        var (analysis, position) = Analyze(marked);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position));
        Assert.Contains("Diverts to", hover.PlainText);
        Assert.Equal("->", analysis.Source.Files["main.tale"].Split('\n')[position.Line - 1]
            [(hover.Range.Start.Column - 1)..(hover.Range.End.Column - 1)]);
    }

    [Fact]
    public void Unknown_syntax_properties_have_no_schema_hover()
    {
        var analysis = Source("=== first\n-> END", Manifest + "\nunknown: value");
        var unknown = analysis.Source.Files[StorySource.ManifestFileName].IndexOf("unknown", StringComparison.Ordinal);
        Assert.Null(StoryHover.Get(analysis, Position(StorySource.ManifestFileName,
            analysis.Source.Files[StorySource.ManifestFileName][..(unknown + 2)])));
    }

    [Fact]
    public void Ruleset_and_story_ids_have_typed_help()
    {
        var yaml = Manifest.Replace("  start: first", "  start: first\n  ruleset: {base: generic}");
        var analysis = Source("=== first\n-> END", yaml);
        var id = yaml.IndexOf("hover", StringComparison.Ordinal);
        var ruleset = yaml.IndexOf("generic", StringComparison.Ordinal);
        Assert.Contains("Story", Assert.IsType<AuthoringHover>(StoryHover.Get(analysis,
            Position(StorySource.ManifestFileName, yaml[..id]))).PlainText);
        Assert.Contains("Ruleset", Assert.IsType<AuthoringHover>(StoryHover.Get(analysis,
            Position(StorySource.ManifestFileName, yaml[..ruleset]))).PlainText);
    }

    private const string CardManifest = """
        game:
          id: hover
          title: Hover
          start: first
          ruleset:
            stats:
              - {id: grit, name: Grit, category: attribute, min: 1, max: 20, default: 8}
        characters:
          player: {first-name: Ada, stats: {grit: 12}, inventory: {rope: 2}}
        items:
          rope: {name: Coil of Rope, modifiers: {grit: 1}}
        factions: {guild: {name: Thieves Guild, standing: 0}}
        """;

    [Fact]
    public void Item_card_shows_authored_name_modifiers_and_starting_holders()
    {
        var (analysis, position) = Analyze("=== first\n{item:ro|pe}", CardManifest);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position)).PlainText;
        Assert.Contains("Name: \"Coil of Rope\"", hover);
        Assert.Contains("Stat modifiers: grit +1", hover);
        Assert.Contains("Starting holders: player ×2", hover);
        Assert.Contains("Declared at story.talewright.yaml", hover);
    }

    [Fact]
    public void Stat_card_shows_declared_bounds_default_and_character_starting_values()
    {
        var (analysis, position) = Analyze("=== first\n::: skill-check gr|it dc 12", CardManifest);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position)).PlainText;
        Assert.Contains("Name: \"Grit\"", hover);
        Assert.Contains("Category: Attribute", hover);
        Assert.Contains("Range: 1..20", hover);
        Assert.Contains("Default: 8", hover);
        Assert.Contains("Starting values: player 12", hover);
        Assert.Contains("Declared at story.talewright.yaml", hover);
    }

    [Fact]
    public void Ruleset_stat_card_has_facts_without_a_declaration()
    {
        var (analysis, position) = Analyze("=== first\n::: skill-check ath|letics dc 12");
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position)).PlainText;
        Assert.Contains("Default:", hover);
        Assert.Contains("Range:", hover);
        Assert.Contains("Available from the selected ruleset.", hover);
    }

    [Fact]
    public void Faction_card_shows_name_initial_standing_and_tier()
    {
        var (analysis, position) = Analyze("=== first\n{faction:gu|ild:name}", CardManifest);
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position)).PlainText;
        Assert.Contains("Name: \"Thieves Guild\"", hover);
        Assert.Contains("Initial standing: 0", hover);
        Assert.Contains("Initial tier: ", hover);
    }

    [Fact]
    public void Scene_card_shows_authored_metadata_only_when_present()
    {
        var (analysis, position) = Analyze("=== first\n-> sec|ond", other:
            "=== second\n@title The Market\n@tags town, day\n@priority 5\n@once\n@hub all(camp, records)\n@hub-limit 2\n-> END");
        var hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position)).PlainText;
        Assert.Contains("Title: \"The Market\"", hover);
        Assert.Contains("Tags: day, town", hover);
        Assert.Contains("Priority: 5", hover);
        Assert.Contains("Once: true", hover);
        Assert.Contains("Hub: all(camp, records)", hover);
        Assert.Contains("Hub limit: 2", hover);
        Assert.Contains("Source: === second", hover);

        (analysis, position) = Analyze("=== first\n-> sec|ond");
        hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position)).PlainText;
        Assert.DoesNotContain("Title:", hover);
        Assert.DoesNotContain("Hub", hover);
        Assert.DoesNotContain("Priority:", hover);

        (analysis, position) = Analyze("=== first\n-> sec|ond", other:
            "=== second\n@priority 0\n-> END");
        hover = Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, position)).PlainText;
        Assert.Contains("Priority: 0", hover);
    }

    private static (StoryAnalysis Analysis, SourceLocation Position) Analyze(string marked, string yaml = Manifest,
        string other = "=== second\n-> END")
    {
        var caret = marked.IndexOf('|');
        return (Source(marked.Remove(caret, 1), yaml, other), Position("main.tale", marked[..caret]));
    }

    private static StoryAnalysis Source(string tale, string yaml, string other = "=== second\n-> END") =>
        StoryAnalyzer.Analyze(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml, ["main.tale"] = tale, ["other.tale"] = other
        }));

    private static SourceLocation Position(string file, string prefix)
    {
        var lines = prefix.ReplaceLineEndings("\n").Split('\n');
        return new(file, lines.Length, lines[^1].Length + 1);
    }
}
