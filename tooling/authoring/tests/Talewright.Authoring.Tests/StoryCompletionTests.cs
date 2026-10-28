using Talewright.Core.Rulesets;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Authoring.Tests;

public sealed class StoryCompletionTests
{
    private const string Manifest = """
        game:
          id: complete
          title: Complete
          start: first
        characters:
          player: {first-name: Ada}
          guide: {first-name: Bea}
        items:
          rope: {name: Rope}
        factions:
          guild: {name: Guild}
        flags: [ready, door-open]
        vars: {coins: 0}
        """;

    [Theory]
    [InlineData("-> sec|", "second")]
    [InlineData("* [Go] -> sec|", "second")]
    [InlineData("-> |", "END")]
    [InlineData("> gu|", "guide")]
    [InlineData("@req|", "requires")]
    [InlineData("@requires player:h|", "hp")]
    [InlineData("@requires flag:re|", "ready")]
    [InlineData("@requires var:co|", "coins")]
    [InlineData("@requires faction:gu|", "guild")]
    [InlineData("@requires player has ro|", "rope")]
    [InlineData("@requires visited(sec|", "second")]
    [InlineData("~ give player ro|", "rope")]
    [InlineData("~ give gu|", "guide")]
    [InlineData("~ give |", "guide")]
    [InlineData("@requires   |", "flag:")]
    [InlineData("::: skill-check athletics dc   |", "var:")]
    [InlineData("~ set flag:re|", "ready")]
    [InlineData("{player:first-name#cap|", "capitalize")]
    [InlineData("{gu|", "guide")]
    [InlineData("::: skill-check athletics dc var:co|", "coins")]
    [InlineData("::: skill-check player h|", "hp")]
    [InlineData("::: skill-check player |", "hp")]
    [InlineData("::: skill-check |", "guide")]
    [InlineData("{player:pronoun#ob|", "object")]
    [InlineData("{player:pronoun#|}", "reflexive")]
    [InlineData("{player:pronoun#|}", "capitalize")]
    [InlineData("{player:pronoun#possessive(|)}", "\"independent\"")]
    [InlineData("{player:first-name# cap|", "capitalize")]
    [InlineData("{item:ro|}", "rope")]
    [InlineData("{ima|", "image(")]
    [InlineData("@requires item:ro| == \"Rope\"", "rope")]
    [InlineData("~ give player rope |", "var:")]
    [InlineData("~ take player rope |", "guide")]
    [InlineData("~ take player rope va|", "var:")]
    public void Completes_only_parser_recognized_reference_slots(string line, string expected)
    {
        var (analysis, position) = Analyze("=== first\n" + line);
        Assert.Contains(StoryCompletion.Complete(analysis, position), item => item.Label == expected);
    }

    [Theory]
    [InlineData("Normal prose sec|")]
    [InlineData("// -> sec|")]
    [InlineData("=== sec|")]
    [InlineData("@title sec|")]
    [InlineData("@requires \"sec|\" == \"second\"")]
    [InlineData("{unknown:h|}")]
    [InlineData("~ give |")]
    public void Avoids_irrelevant_scene_suggestions(string line)
    {
        var (analysis, position) = Analyze("=== first\n" + line);
        Assert.DoesNotContain(StoryCompletion.Complete(analysis, position), item => item.Label == "second");
    }

    [Theory]
    [InlineData("""{image("sec|ond", "Map")}""")]
    [InlineData("""{image("map", "{player:|}")}""")]
    [InlineData("""{image("map", "{player:|}")""")]
    public void Literal_image_arguments_do_not_offer_expression_completions(string marked)
    {
        var (analysis, position) = Analyze("=== first\n" + marked);
        Assert.Empty(StoryCompletion.Complete(analysis, position));
    }

    [Theory]
    [InlineData("game:\n  start: sec|", "second")]
    [InlineData("game:\n  start: |", "second")]
    [InlineData("game:\n  start: \"sec|ond\"", "second")]
    [InlineData("game:\n  start: \"\\u0073ec|ond\"", "second")]
    [InlineData("game:\n  ruleset: {base: gen|}", "generic")]
    [InlineData("game:\n  ruleset: {disable: [h|]}", "hp")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    inventory: [ro|]", "rope")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    stats: {h|: 10}", "hp")]
    [InlineData("items:\n  rope:\n    name: Rope\n    modifiers: {h|: 1}", "hp")]
    [InlineData("game:\n  creation:\n    stats: {h|: [10, 15]}", "hp")]
    [InlineData("game:\n  creation:\n    presets:\n      strong:\n        stats: {h|: 15}", "hp")]
    [InlineData("characters:\n  player:\n    first-name: Ada\n    inventory: {ro|: 1}", "rope")]
    [InlineData("triggers:\n  - id: check\n    when: 'flag:re|'\n    do: set flag:ready", "ready")]
    public void Completes_manifest_story_values_and_expressions(string fragment, string expected)
    {
        // Replace duplicate top-level declarations so YAML remains parseable.
        var fragmentLines = fragment.Split('\n');
        var yaml = string.Join('\n', Manifest.Split('\n').TakeWhile(line => line != ""));
        var lines = yaml.Split('\n').ToList();
        if (fragmentLines[0] == "game:")
        {
            var property = fragmentLines[1].TrimStart().Split(':')[0];
            var start = lines.FindIndex(line => line.StartsWith($"  {property}:", StringComparison.Ordinal));
            if (start >= 0)
            {
                var end = start + 1;
                while (end < lines.Count && (lines[end].StartsWith("    ", StringComparison.Ordinal) || lines[end].Length == 0)) end++;
                lines.RemoveRange(start, end - start);
            }
            var gameEnd = lines.FindIndex(line => line == "game:") + 1;
            while (gameEnd < lines.Count &&
                (lines[gameEnd].StartsWith("  ", StringComparison.Ordinal) || lines[gameEnd].Length == 0)) gameEnd++;
            lines.InsertRange(gameEnd, fragmentLines.Skip(1));
            yaml = string.Join('\n', lines);
        }
        else
        {
            var key = fragmentLines[0].Split(':')[0];
            var start = lines.FindIndex(line => line.StartsWith(key + ":", StringComparison.Ordinal));
            if (start >= 0)
            {
                var end = start + 1;
                while (end < lines.Count && (lines[end].StartsWith(' ') || lines[end].Length == 0)) end++;
                lines.RemoveRange(start, end - start);
            }
            yaml = string.Join('\n', lines) + "\n" + fragment;
        }
        var caret = yaml.IndexOf('|');
        var text = yaml.Remove(caret, 1);
        var position = Position(StorySource.ManifestFileName, yaml[..caret]);
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = text,
            ["main.tale"] = "=== first\n-> second\n=== second\n-> END"
        });
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.True(StoryCompletion.Complete(analysis, position).Any(item => item.Label == expected),
            $"At {position}: {string.Join("; ", analysis.CompletionSites.Select(site => $"{site.Text} {site.Range}"))}");
    }

    [Fact]
    public void Replaces_the_entire_token_and_preserves_utf16_and_crlf_positions()
    {
        var (analysis, position) = Analyze("=== first\r\nA 🦑 {player:first-name#cap|italize}\r\n-> END");
        var item = Assert.Single(StoryCompletion.Complete(analysis, position), item => item.Label == "capitalize");
        Assert.Equal(new SourceLocation("main.tale", 2, 25), item.Range.Start);
        Assert.Equal(new SourceLocation("main.tale", 2, 35), item.Range.End);
    }

    [Fact]
    public void Missing_project_metadata_is_explicitly_unavailable()
    {
        var (analysis, position) = Analyze("=== first\n-> sec|");
        analysis = analysis.WithDiagnostic(new(DiagnosticSeverity.Error, "TW5001", "Missing metadata", new(StorySource.ManifestFileName, 1, 1)));
        Assert.Throws<InvalidOperationException>(() => StoryCompletion.Complete(analysis, position));
    }

    [Fact]
    public void Broken_yaml_has_no_fabricated_manifest_completions()
    {
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = "game:\n  start: \"sec",
            ["main.tale"] = "=== second\n-> END"
        });
        var analysis = StoryAnalyzer.Analyze(source);
        Assert.NotEmpty(analysis.Diagnostics);
        Assert.Empty(StoryCompletion.Complete(analysis, new(StorySource.ManifestFileName, 1, 12)));
    }

    [Fact]
    public void Numeric_effect_operands_do_not_offer_new_effect_commands()
    {
        var (analysis, position) = Analyze("=== first\n~ player:hp += |");
        var items = StoryCompletion.Complete(analysis, position);
        Assert.Contains(items, item => item.Label == "var:");
        Assert.DoesNotContain(items, item => item.Label is "set" or "clear" or "give" or "take");
    }

    [Theory]
    [InlineData("give")]
    [InlineData("take")]
    public void Inventory_quantity_is_a_numeric_operand_not_an_item_or_effect_slot(string command)
    {
        var (analysis, position) = Analyze($"=== first\n~ {command} player rope |");
        var items = StoryCompletion.Complete(analysis, position);
        Assert.Contains(items, item => item.Label == "var:");
        Assert.Contains(items, item => item.Label == "player");
        Assert.DoesNotContain(items, item => item.Label is "rope" or "set" or "clear" or "give" or "take" or "flag:");
    }

    [Fact]
    public void Pronoun_form_completion_offers_forms_once_and_marks_the_default_possessive()
    {
        var (analysis, position) = Analyze("=== first\n{player:pronoun#|}");
        var forms = StoryCompletion.Complete(analysis, position);
        var possessive = Assert.Single(forms, item => item.Label == "possessive");
        Assert.Contains("Pronoun form", possessive.Detail);
        Assert.Contains(forms, item => item.Label == "capitalize");

        (analysis, position) = Analyze("=== first\n{player:pronoun#possessive(|)}");
        var values = StoryCompletion.Complete(analysis, position);
        Assert.Equal(["\"determiner\"", "\"independent\""], values.Select(item => item.Label));
        Assert.Contains("(default)", Assert.Single(values, item => item.Label == "\"determiner\"").Detail);
        Assert.DoesNotContain("default", Assert.Single(values, item => item.Label == "\"independent\"").Detail);
    }

    [Theory]
    [InlineData("{player:first-name#|}")]
    [InlineData("{player:pronoun#capitalize#|}")]
    [InlineData("{player:pronoun:|}")]
    public void Pronoun_forms_are_only_offered_straight_after_pronoun(string line)
    {
        var (analysis, position) = Analyze("=== first\n" + line);
        Assert.DoesNotContain(StoryCompletion.Complete(analysis, position), item => item.Label is "object" or "reflexive" or "subject");
    }

    [Theory]
    [InlineData("{player:pronoun#|}", true)]
    [InlineData("{player:pronoun#one-|}", true)]
    [InlineData("{player:pronoun#object#|}", true)]
    [InlineData("{player:first-name#|}", false)]
    [InlineData("{item:rope#|}", false)]
    public void One_many_completion_is_pronoun_only(string line, bool offered)
    {
        var (analysis, position) = Analyze("=== first\n" + line);
        var matches = StoryCompletion.Complete(analysis, position).Where(item => item.Label == "one-many").ToArray();
        Assert.Equal(offered ? 1 : 0, matches.Length);
        if (offered) Assert.Contains("singular", matches[0].Detail);
    }

    [Theory]
    [InlineData("~ player:h|p-=3", "hp", "~ player:hp-=3")]
    [InlineData("~ player:h|p+=3", "hp", "~ player:hp+=3")]
    [InlineData("{player:pronoun#|}", "object", "{player:pronoun#object}")]
    [InlineData("{player:pronoun#pos|", "possessive", "{player:pronoun#possessive")]
    [InlineData("{player:pronoun#possessive(|)}", "\"independent\"", "{player:pronoun#possessive(\"independent\")}")]
    [InlineData("@requires flag:door-|", "door-open", "@requires flag:door-open")]
    [InlineData("~ player:hp|-=3", "hp", "~ player:hp-=3")]
    [InlineData("~ player:hp| - 3", "hp", "~ player:hp - 3")]
    [InlineData("~ player:hp|- 3", "hp", "~ player:hp- 3")]
    [InlineData("~ player:hp -= player:h|p-", "hp", "~ player:hp -= player:hp-")]
    [InlineData("~ player:hp -= var:co|ins-", "coins", "~ player:hp -= var:coins-")]
    [InlineData("~ player:hp -= faction:gu|ild-", "guild", "~ player:hp -= faction:guild-")]
    [InlineData("{faction:gu|ild:name}", "guild", "{faction:guild:name}")]
    [InlineData("{faction:gu|ild:tier}", "guild", "{faction:guild:tier}")]
    [InlineData("@requires faction:gu|ild:tier == hostile", "guild", "@requires faction:guild:tier == hostile")]
    [InlineData("{faction:guild:na|me}", "name", "{faction:guild:name}")]
    [InlineData("{faction:guild:|}", "name", "{faction:guild:name}")]
    [InlineData("@requires va|r:coins > 0", "var:", "@requires var:coins > 0")]
    [InlineData("@requires vis|ited(first)", "visited(", "@requires visited(first)")]
    [InlineData("@requires va|", "var:", "@requires var:")]
    [InlineData("@requires vis|", "visited(", "@requires visited(")]
    [InlineData("{item:ro|pe}", "rope", "{item:rope}")]
    [InlineData("~ give player rope va|", "var:", "~ give player rope var:")]
    [InlineData("~ take player rope gu|", "guide", "~ take player rope guide")]
    public void Applying_completion_preserves_operators_and_existing_delimiters(string marked, string label, string expected)
    {
        var (analysis, position) = Analyze("=== first\n" + marked);
        var item = Assert.Single(StoryCompletion.Complete(analysis, position), item => item.Label == label);
        var original = marked.Replace("|", "");
        Assert.Equal(expected, Apply(original, item.Range.Start.Column, item.Range.End.Column, item.NewText));
    }

    [Theory]
    [InlineData("va|r\\u003acoins > 0", "var:", "var\\u003acoins > 0")]
    [InlineData("vis|ited\\u0028first)", "visited(", "visited\\u0028first)")]
    [InlineData("va|", "var:", "var:")]
    [InlineData("flag:door\\u002d|", "door-open", "flag:door-open")]
    [InlineData("faction:gu|ild\\u003aname == item:rope", "guild", "faction:guild\\u003aname == item:rope")]
    public void Applying_yaml_completion_preserves_decoded_delimiters(string expression, string label, string expected)
    {
        var marked = Manifest + "\ntriggers:\n  - id: check\n    when: \"" + expression + "\"\n    do: set flag:ready";
        var caret = marked.IndexOf('|');
        var position = Position(StorySource.ManifestFileName, marked[..caret]);
        var yaml = marked.Remove(caret, 1);
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml, ["main.tale"] = "=== first\n-> END"
        });
        var analysis = StoryAnalyzer.Analyze(source);
        var item = Assert.Single(StoryCompletion.Complete(analysis, position), item => item.Label == label);
        var line = yaml.Split('\n')[position.Line - 1];
        Assert.Equal("    when: \"" + expected + "\"",
            Apply(line, item.Range.Start.Column, item.Range.End.Column, item.NewText));
    }

    [Theory]
    [InlineData("hp")]
    [InlineData("focus")]
    public void Disable_candidates_use_selected_base_not_final_story_stats(string disabled)
    {
        var options = new CompilerOptions
        {
            Rulesets = new Dictionary<string, Func<Ruleset>>(CompilerOptions.DefaultRulesets())
            {
                ["project"] = () => new RulesetBuilder("project")
                    .AddStat(new(new("focus"), "Focus", StatCategory.Custom, 0, 10, 5)).Build()
            }
        };
        var baseName = disabled == "hp" ? "generic" : "project";
        var yaml = Manifest.Replace("  start: first", $"  start: first\n  ruleset:\n    base: {baseName}\n    disable: [{disabled}]\n    stats:\n      - id: added\n        min: 0\n        max: 10");
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml, ["main.tale"] = "=== first\n{player:added}\n-> END"
        });
        var analysis = AuthoringProjectMetadata.Export(options).Analyze(source);
        var disabledIndex = yaml.IndexOf($"[{disabled}]", StringComparison.Ordinal) + 1;
        var items = StoryCompletion.Complete(analysis,
            Position(StorySource.ManifestFileName, yaml[..disabledIndex]));
        Assert.Contains(items, item => item.Label == disabled);
        Assert.DoesNotContain(items, item => item.Label == "added");
        if (baseName == "project") Assert.DoesNotContain(items, item => item.Label == "hp");
        var ordinary = StoryCompletion.Complete(analysis, new("main.tale", 2, 9));
        Assert.Contains(ordinary, item => item.Label == "added");
        Assert.DoesNotContain(ordinary, item => item.Label == disabled);
    }

    [Theory]
    [InlineData("|")]
    [InlineData("gr|")]
    [InlineData("\"gr|\"")]
    public void Governing_attribute_completion_uses_only_final_enabled_attributes(string markedValue)
    {
        var marked = Manifest.Replace("  start: first", """
              start: first
              ruleset:
                base: generic
                disable: [str]
                stats:
                  - {id: tracking, category: skill, governingAttribute: VALUE}
                  - {id: grit, category: attribute}
                  - {id: courage, category: custom}
            """.Replace("VALUE", markedValue));
        var caret = marked.IndexOf('|');
        var analysis = StoryAnalyzer.Analyze(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = marked.Remove(caret, 1),
            ["main.tale"] = "=== first\n-> END",
        }));
        var items = StoryCompletion.Complete(analysis, Position(StorySource.ManifestFileName, marked[..caret]));
        var grit = Assert.Single(items, item => item.Label == "grit");
        Assert.Contains("attribute", grit.Detail);
        Assert.DoesNotContain(items, item => item.Label is "str" or "tracking" or "courage" or "hp");
        if (markedValue == "|") Assert.Contains(items, item => item.Label == "dex");
    }

    [Theory]
    [InlineData("characters:\n  player: {first-name: Ada, inventory: [|]}", "rope", "[rope]")]
    [InlineData("characters:\n  player: {first-name: Ada, inventory: [ | ]}", "rope", "[ rope ]")]
    [InlineData("characters:\n  player: {first-name: Ada, inventory: [  |]}", "rope", "[  rope]")]
    [InlineData("characters:\n  player: {first-name: Ada, inventory: [\n    |]}", "rope", "    rope]")]
    [InlineData("ruleset: {disable: [|]}", "hp", "[hp]")]
    [InlineData("ruleset: {disable: [ | ]}", "hp", "[ hp ]")]
    [InlineData("ruleset: {disable: [|  ]}", "hp", "[hp  ]")]
    [InlineData("ruleset: {disable: [\n  |]}", "hp", "  hp]")]
    public void Empty_flow_reference_lists_offer_insertion_edits(string fragment, string label, string expectedList)
    {
        var marked = "game:\n  id: flow\n  title: Flow\n  start: first\n" +
            (fragment.StartsWith("ruleset", StringComparison.Ordinal)
                ? "  " + fragment.Replace("\n", "\n  ", StringComparison.Ordinal)
                : "items: {rope: Rope}\n" + fragment);
        var caret = marked.IndexOf('|');
        var position = Position(StorySource.ManifestFileName, marked[..caret]);
        var yaml = marked.Remove(caret, 1);
        var analysis = StoryAnalyzer.Analyze(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml, ["main.tale"] = "=== first\n-> END"
        }));
        var item = Assert.Single(StoryCompletion.Complete(analysis, position), item => item.Label == label);
        Assert.Equal(position, item.Range.Start);
        Assert.Equal(position, item.Range.End);
        var line = yaml.Split('\n')[position.Line - 1];
        Assert.Contains(expectedList, Apply(line, item.Range.Start.Column, item.Range.End.Column, item.NewText));
    }

    [Fact]
    public void Empty_disable_list_retains_base_only_candidates()
    {
        var options = new CompilerOptions
        {
            Rulesets = new Dictionary<string, Func<Ruleset>>(CompilerOptions.DefaultRulesets())
            {
                ["project"] = () => new RulesetBuilder("project")
                    .AddStat(new(new("focus"), "Focus", StatCategory.Custom, 0, 10, 5)).Build()
            }
        };
        var marked = Manifest.Replace("  start: first",
            "  start: first\n  ruleset:\n    base: project\n    disable: [ | ]\n    stats: [{id: added, min: 0, max: 10}]");
        var caret = marked.IndexOf('|');
        var analysis = AuthoringProjectMetadata.Export(options).Analyze(new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = marked.Remove(caret, 1), ["main.tale"] = "=== first\n-> END"
        }));
        var items = StoryCompletion.Complete(analysis, Position(StorySource.ManifestFileName, marked[..caret]));
        Assert.Equal("focus", Assert.Single(items).Label);
    }

    private static string Apply(string line, int start, int end, string newText) =>
        line[..(start - 1)] + newText + line[(end - 1)..];

    private static (StoryAnalysis Analysis, SourceLocation Position) Analyze(string marked)
    {
        var caret = marked.IndexOf('|');
        var position = Position("main.tale", marked[..caret]);
        var source = new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = Manifest,
            ["main.tale"] = marked.Remove(caret, 1),
            ["other.tale"] = "=== second\n-> END"
        });
        return (StoryAnalyzer.Analyze(source), position);
    }

    private static SourceLocation Position(string file, string prefix)
    {
        var lines = prefix.ReplaceLineEndings("\n").Split('\n');
        return new(file, lines.Length, lines[^1].Length + 1);
    }
}
