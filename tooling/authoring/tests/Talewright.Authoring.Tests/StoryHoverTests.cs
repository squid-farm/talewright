using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class StoryHoverTests
{
    private const string Manifest = """
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
    [InlineData("{player:pronoun#ob|ject}", "Pronoun form: object", "object")]
    [InlineData("{player:pronoun#posse|ssive}", "Pronoun form: possessive", "possessive")]
    [InlineData("{player:pro|noun}", "Gives the subject form", "pronoun")]
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
        var yaml = Manifest + "\nruleset: {disable: []}";
        var name = marked.Replace("|", "");
        if (name == "inventory") yaml += "\ncreation: {name: true}\n";
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
        var line = yaml.Split('\n')[2];
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
        Assert.DoesNotContain("https://", hover.Markdown);
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
        var metadata = AuthoringProjectMetadata.Export(CompilerOptions.Default) with { Predicates = ["Custom"], Modifiers = ["special"] };
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
        Assert.Null(StoryHover.Get(analysis, new(StorySource.ManifestFileName, 10, 3)));
    }

    [Fact]
    public void Ruleset_and_story_ids_have_typed_help()
    {
        var analysis = Source("=== first\n-> END", Manifest + "\nruleset: {base: generic}");
        Assert.Contains("Story", Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, new(StorySource.ManifestFileName, 1, 6))).PlainText);
        Assert.Contains("Ruleset", Assert.IsType<AuthoringHover>(StoryHover.Get(analysis, new(StorySource.ManifestFileName, 10, 18))).PlainText);
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
