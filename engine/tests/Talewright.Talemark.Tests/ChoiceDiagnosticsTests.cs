using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public sealed class ChoiceDiagnosticsTests
{
    private const string Manifest = """
        game: {id: choice-diagnostics, title: Choice diagnostics, start: start}
        characters:
          player: {first-name: Ada}
        flags: [ready, left]
        vars: {count: 0}
        """;

    private const string LabeledHelp = "Expected (requires: …), { effects }, -> target or skill-check.";
    private const string TextlessHelp = "Use '* [label] ...' or a textless '* ... -> target'.";
    private const string MissingBracket = "Choice label is missing its closing ']'.";
    private const string NeedsBoth = "A skill check needs both a 'success:' and a 'failure:' line.";
    private const string UnclosedOutcome = "Outcome effect block is missing its closing '}'.";
    private const string NeedsTarget = "Every choice and each choice skill-check outcome needs its own '-> target' or '-> END'. There is no scene fallback.";
    private const string UnclosedBrace = "Unclosed '{'. Use '\\{' for a literal brace.";

    private static string MissingBracketBefore(string marker) =>
        $"Choice label is missing its closing ']'. Place ']' before '{marker}'.";

    private static CompilationResult Compile(string tale, IEnumerable<string>? images = null) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest,
            ["main.tale"] = tale,
        }, images));

    private static void Expect(string tale, params Diagnostic[] diagnostics)
    {
        var result = Compile(tale);
        Assert.Equal(diagnostics, result.Diagnostics);
    }

    private static Diagnostic Err(string code, string message, int line, int column) =>
        new(DiagnosticSeverity.Error, code, message, new("main.tale", line, column));

    private static int Col(string line, string marker, int occurrence = 1)
    {
        var index = -1;
        for (var n = 0; n < occurrence; n++)
        {
            index = line.IndexOf(marker, index + 1, StringComparison.Ordinal);
        }

        Assert.True(index >= 0, $"'{marker}' was not found in '{line}'.");
        return index + 1;
    }

    [Fact]
    public void Requires_and_effects_are_accepted_in_either_order_and_once_each()
    {
        var result = Compile("""
            === start
            * [Read] (requires: flag:ready) { set flag:left } -> END
            * [Shove] { set flag:left } (requires: flag:ready) -> END
            * (requires: flag:ready) { set flag:left } -> END
            * { set flag:left } (requires: flag:ready) -> END
            * -> END
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(5, result.Story!.Scenes[0].Choices.Count);
        Assert.All(result.Story.Scenes[0].Choices.Take(4), choice =>
        {
            Assert.NotNull(choice.Requires);
            Assert.Single(choice.Effects);
        });
        Assert.Null(result.Story.Scenes[0].Choices[4].Requires);
        Assert.Empty(result.Story.Scenes[0].Choices[4].Effects);
    }

    [Fact]
    public void Arrow_inside_a_closed_label_is_label_text()
    {
        var result = Compile("=== start\n* [Go -> hall] -> END");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Duplicate_requires_and_effects_are_rejected_at_the_second_clause()
    {
        const string requires = "* [Go] (requires: flag:ready) (requires: flag:left) -> END";
        const string effects = "* [Go] { set flag:left } { set flag:ready } -> END";
        Expect("=== start\n" + requires + "\n* -> END",
            Err("TW1011", "Duplicate '(requires:)' condition. A choice accepts only one.", 2, Col(requires, "(requires:", 2)));
        Expect("=== start\n" + effects,
            Err("TW1011", "Duplicate effect block. A choice accepts only one.", 2, Col(effects, "{", 2)));
        Assert.False(Compile("=== start\n" + requires).Success);
        Assert.False(Compile("=== start\n" + effects).Success);
    }

    [Fact]
    public void Missing_bracket_hints_only_for_one_clear_boundary()
    {
        const string one = "* [Go -> END";
        const string two = "* [Go -> hall -> END";
        const string quoted = "* [Say \"-> hall\"";
        const string balanced = "* [Hello {player:first-name} -> END";
        const string requires = "* [Hello {player:first-name} (requires: flag:ready)";
        const string effects = "* [Go { set flag:left";
        const string effectsThenArrow = "* [Go { set flag:left -> END";
        const string effectsThenRequires = "* [Go { set flag:left (requires: flag:ready";
        const string arrowThenEffects = "* [Go -> END { set flag:left";
        const string requiresThenEffects = "* [Go (requires: flag:ready { set flag:left";
        const string requiresThenArrow = "* [Go (requires: flag:ready -> END";
        const string arrowThenRequires = "* [Go -> END (requires: flag:ready";
        Expect("=== start\n" + one,
            Err("TW1011", MissingBracketBefore("->"), 2, Col(one, "[")));
        Expect("=== start\n" + two,
            Err("TW1011", MissingBracket, 2, Col(two, "[")));
        Expect("=== start\n" + quoted,
            Err("TW1011", MissingBracket, 2, Col(quoted, "[")));
        Expect("=== start\n" + balanced,
            Err("TW1011", MissingBracketBefore("->"), 2, Col(balanced, "[")));
        Expect("=== start\n" + requires,
            Err("TW1011", MissingBracketBefore("(requires:"), 2, Col(requires, "[")));
        Expect("=== start\n" + effects,
            Err("TW1011", MissingBracketBefore("{"), 2, Col(effects, "[")));
        Expect("=== start\n" + effectsThenArrow,
            Err("TW1011", MissingBracket, 2, Col(effectsThenArrow, "[")));
        Expect("=== start\n" + effectsThenRequires,
            Err("TW1011", MissingBracket, 2, Col(effectsThenRequires, "[")));
        Expect("=== start\n" + arrowThenEffects,
            Err("TW1011", MissingBracket, 2, Col(arrowThenEffects, "[")));
        Expect("=== start\n" + requiresThenEffects,
            Err("TW1011", MissingBracket, 2, Col(requiresThenEffects, "[")));
        Expect("=== start\n" + requiresThenArrow,
            Err("TW1011", MissingBracket, 2, Col(requiresThenArrow, "[")));
        Expect("=== start\n" + arrowThenRequires,
            Err("TW1011", MissingBracket, 2, Col(arrowThenRequires, "[")));
    }

    [Fact]
    public void Unclosed_substitution_with_a_later_bracket_is_not_a_missing_bracket()
    {
        const string line = "* [Hello {player:first-name] -> END";
        const string quoted = "* [Hello {name == \"]\"] -> END";
        Expect("=== start\n" + line,
            Err("TW1004", UnclosedBrace, 2, Col(line, "{")));
        Expect("=== start\n" + quoted,
            Err("TW1004", UnclosedBrace, 2, Col(quoted, "{")));
    }

    [Fact]
    public void Unfinished_labels_report_the_bracket_without_substitution_noise()
    {
        const string plain = "* [Go to the";
        const string balanced = "* [Hello {player:first-name}";
        const string unclosed = "* [Hello {player:first-name";
        Expect("=== start\n" + plain, Err("TW1011", MissingBracket, 2, Col(plain, "[")));
        Expect("=== start\n" + balanced, Err("TW1011", MissingBracket, 2, Col(balanced, "[")));
        Expect("=== start\n" + unclosed, Err("TW1011", MissingBracket, 2, Col(unclosed, "[")));
    }

    [Fact]
    public void Bare_target_suggests_inserting_the_divert()
    {
        const string labeled = "* [Go] hall";
        const string textless = "* hall";
        const string extra = "* [Go] hall extra";
        Expect("=== start\n" + labeled,
            Err("TW1011", $"Unexpected 'hall' in choice. Insert '->' before the target. {LabeledHelp}", 2, Col(labeled, "hall")));
        Expect("=== start\n" + textless,
            Err("TW1011", $"Unexpected 'hall' in choice. Insert '->' before the target. {TextlessHelp}", 2, Col(textless, "hall")));
        Expect("=== start\n" + extra,
            Err("TW1011", $"Unexpected 'hall extra' in choice. {LabeledHelp}", 2, Col(extra, "hall")));
    }

    [Fact]
    public void Unexpected_text_excerpt_is_bounded_by_utf16_units_without_splitting_a_surrogate()
    {
        var eighty = new string('a', 79) + "!";
        var eightyOne = new string('a', 80) + "!";
        var cjk = new string('字', 90);
        var emoji = char.ConvertFromUtf32(0x1F991);
        var split = new string('a', 78) + emoji + "ZZZZ";
        Expect("=== start\n* [Go] " + eighty,
            Err("TW1011", $"Unexpected '{eighty}' in choice. {LabeledHelp}", 2, 8));
        var longAscii = Assert.Single(Compile("=== start\n* [Go] " + eightyOne).Diagnostics);
        var longCjk = Assert.Single(Compile("=== start\n* [Go] " + cjk).Diagnostics);
        var longEmoji = Assert.Single(Compile("=== start\n* [Go] " + split).Diagnostics);
        AssertBounded(Excerpt(longAscii.Message), 79, 'a');
        AssertBounded(Excerpt(longCjk.Message), 79, '字');
        var emojiExcerpt = Excerpt(longEmoji.Message);
        AssertBounded(emojiExcerpt, 78, 'a');
        Assert.DoesNotContain(emoji, emojiExcerpt);
        // The 80th unit would be the high surrogate, so the excerpt stops one unit early.
        Assert.Equal(79, emojiExcerpt.Length);
        Assert.True(emojiExcerpt.Length <= 80);
    }

    [Fact]
    public void Requires_whitespace_names_the_exact_marker()
    {
        foreach (var spelling in new[] { "( requires :", "(requires :", "( requires:" })
        {
            var line = $"* [Go] {spelling} flag:ready) -> END";
            Expect("=== start\n" + line,
                Err("TW1011", $"Choice conditions use the exact marker '(requires:)'. '{spelling}' is not recognized.",
                    2, Col(line, spelling)));
        }
    }

    [Fact]
    public void Trailing_text_after_a_divert_is_misplaced_and_the_scene_id_is_still_checked()
    {
        const string end = "* [Go] -> END { set flag:left }";
        const string textless = "* -> END { set flag:left }";
        const string requires = "* [Go] -> END (requires: flag:ready)";
        const string junk = "* [Go] -> END junk";
        const string unknown = "* [Go] -> missing { set flag:left }";
        Expect("=== start\n" + end,
            Err("TW1011", "Effect block is misplaced after the divert. Put '{ effects }' before '->'.", 2, Col(end, "{")));
        Expect("=== start\n" + textless,
            Err("TW1011", "Effect block is misplaced after the divert. Put '{ effects }' before '->'.", 2, Col(textless, "{")));
        Expect("=== start\n" + requires,
            Err("TW1011", "'(requires:)' is misplaced after the divert. Put the condition before '->'.", 2, Col(requires, "(requires:")));
        Expect("=== start\n" + junk,
            Err("TW1011", "Unexpected 'junk' after the divert.", 2, Col(junk, "junk")));
        Expect("=== start\n" + unknown,
            Err("TW2002", "Unknown scene 'missing'.", 2, Col(unknown, "missing")),
            Err("TW1011", "Effect block is misplaced after the divert. Put '{ effects }' before '->'.", 2, Col(unknown, "{")),
            Err("TW3003", NeedsTarget, 2, 1));
    }

    [Fact]
    public void Skill_check_dc_stops_at_a_top_level_clause_and_leaves_other_text_as_an_expression()
    {
        const string effect = "* [Try] skill-check athletics dc 14 { set flag:left }";
        const string divert = "* [Try] skill-check athletics dc 14 -> END";
        const string requires = "* [Try] skill-check athletics dc 14 (requires: flag:ready)";
        const string junk = "* [Try] skill-check athletics dc 14 junk";
        const string quoted = "* [Try] skill-check athletics dc \"a -> b\"";
        const string nested = "* [Try] skill-check athletics dc (14 -> 2)";
        const string block = "::: skill-check athletics dc 14 { set flag:left }";
        const string outcomes = "\nsuccess: -> END\nfailure: -> END";
        Expect("=== start\n" + effect + outcomes,
            Err("TW1011", "Effect block is misplaced after the skill check. Put '{ effects }' before 'skill-check'.", 2, Col(effect, "{")));
        Expect("=== start\n" + divert + outcomes,
            Err("TW1011", "Divert is misplaced after the skill check. Outcomes belong on 'success:' and 'failure:' lines.", 2, Col(divert, "->")));
        Expect("=== start\n" + requires + outcomes,
            Err("TW1011", "'(requires:)' is misplaced after the skill check. Put the condition before 'skill-check'.", 2, Col(requires, "(requires:")));
        Expect("=== start\n" + junk + outcomes,
            Err("TW1001", "Unexpected 'junk'.", 2, Col(junk, "junk")),
            Err("TW3003", NeedsTarget, 2, 1));
        Expect("=== start\n" + quoted + outcomes,
            Err("TW2003", "Expected a number expression but found a text.", 2, Col(quoted, "\"")),
            Err("TW3003", NeedsTarget, 2, 1));
        Expect("=== start\n" + nested + outcomes,
            Err("TW1001", "Unexpected '>'.", 2, Col(nested, ">")),
            Err("TW3003", NeedsTarget, 2, 1));
        Expect("=== start\n" + block + "\nsuccess: -> END\nfailure: -> END\n:::",
            Err("TW1011", "Effect block is misplaced after the skill check. Put '{ effects }' before 'skill-check'.", 2, Col(block, "{")));
    }

    [Fact]
    public void Unclosed_outcome_effect_is_tw1009_without_a_false_missing_line()
    {
        const string choice = """
            === start
            * [Try] skill-check athletics dc 1
            success: { set flag:ready
            failure: -> END
            === other
            Stuck.
            """;
        const string block = """
            === start
            ::: skill-check athletics dc 1
            success: { set flag:ready
            failure: -> END
            :::
            -> END
            === other
            Stuck.
            """;
        const string missing = """
            === start
            * [Try] skill-check athletics dc 1
            success: -> END
            """;
        Expect(choice,
            Err("TW1009", UnclosedOutcome, 3, Col("success: { set flag:ready", "{")),
            Err("TW3003", NeedsTarget, 2, 1),
            Err("TW3002", "Scene 'other' is a dead end: add choices, a '-> target', or '-> END'.", 5, 1));
        Expect(block,
            Err("TW1009", UnclosedOutcome, 3, Col("success: { set flag:ready", "{")),
            Err("TW3002", "Scene 'other' is a dead end: add choices, a '-> target', or '-> END'.", 7, 1));
        Expect(missing,
            Err("TW1009", NeedsBoth, 2, Col("* [Try] skill-check athletics dc 1", "skill-check")),
            Err("TW3003", NeedsTarget, 2, 1));
        Assert.DoesNotContain(Compile(choice).Diagnostics, diagnostic => diagnostic.Message == NeedsBoth);
        Assert.DoesNotContain(Compile(block).Diagnostics, diagnostic => diagnostic.Message == NeedsBoth);
    }

    [Fact]
    public void Closed_but_invalid_outcome_expression_keeps_its_own_code()
    {
        const string effect = "success: { var:count += } -> END";
        Expect("=== start\n* [Try] skill-check athletics dc 1\n" + effect + "\nfailure: -> END",
            Err("TW1001", "Expression is incomplete.", 3, Col(effect, "}")));
    }

    [Fact]
    public void Dropping_a_malformed_choice_does_not_invent_a_dead_end()
    {
        const string tale = """
            === start
            Hello.
            * [Go -> END
            === other
            Stuck.
            """;
        Expect(tale,
            Err("TW1011", MissingBracketBefore("->"), 3, Col("* [Go -> END", "[")),
            Err("TW3002", "Scene 'other' is a dead end: add choices, a '-> target', or '-> END'.", 4, 1));
        var alone = Compile("=== start\nHello.\n* [Go -> END");
        Assert.DoesNotContain(alone.Diagnostics, diagnostic => diagnostic.Code == "TW3002");
        var real = Compile("=== start\nHello.");
        Assert.Contains(real.Diagnostics, diagnostic => diagnostic.Code == "TW3002" && diagnostic.Message.Contains("'start'"));
    }

    [Fact]
    public void Choice_labels_keep_quoted_string_and_image_diagnostics()
    {
        const string badString = """* [{"bad\q"}] -> END""";
        const string unclosedString = """* [{"bad\q""";
        var escape = "Unknown string escape. Use '\\\"' for a quote or '\\\\' for a backslash.";
        Expect("=== start\n" + badString,
            Err("TW1001", escape, 2, Col(badString, "\\")));
        Expect("=== start\n" + unclosedString,
            Err("TW1001", escape, 2, Col(unclosedString, "\\")),
            Err("TW1011", MissingBracket, 2, Col(unclosedString, "[")));

        const string nested = """* [Go {image("portrait", "See {image(\"coin\", \"A coin\")} now")}] -> END""";
        var images = Compile("=== start\n" + nested, ["images/portrait.png", "images/coin.png"]);
        var image = Assert.Single(images.Diagnostics);
        Assert.Equal("TW1014", image.Code);
        Assert.Contains("cannot contain {image(...)}", image.Message);
        Assert.Equal(new SourceLocation("main.tale", 2, nested.LastIndexOf("{image(", StringComparison.Ordinal) + 1), image.Location);
    }

    private static string Excerpt(string message)
    {
        const string open = "Unexpected '";
        var start = message.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, message);
        start += open.Length;
        var end = message.IndexOf('\'', start);
        Assert.True(end > start, message);
        return message[start..end];
    }

    private static void AssertBounded(string excerpt, int prefixLength, char prefix)
    {
        Assert.True(excerpt.Length <= 80, excerpt);
        Assert.EndsWith("…", excerpt);
        Assert.Equal(prefixLength, excerpt.Count(character => character == prefix));
        for (var i = 0; i < excerpt.Length; i++)
        {
            if (!char.IsHighSurrogate(excerpt[i]))
            {
                Assert.False(char.IsLowSurrogate(excerpt[i]), excerpt);
                continue;
            }

            Assert.True(i + 1 < excerpt.Length && char.IsLowSurrogate(excerpt[i + 1]), excerpt);
            i++;
        }
    }
}
