using System.Globalization;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Formatting;

namespace Talewright.Talemark.Tests;

public class ExpressionFormatterTests
{
    private const string Manifest = """
        game: { id: formatter, title: Formatter, start: a }
        characters:
          player: { first-name: Mara, last-name: Burns, pronouns: she }
        factions:
          river: { name: River Guild, standing: 12 }
        items:
          lantern: { name: Lantern }
        flags: [ready]
        vars:
          coins: 5
          weather: "rain"
        """;

    private static readonly CompilerOptions Options = new()
    {
        Predicates = new NamedPredicateRegistry().Register("CanGo", _ => true)
            .Register("_Can.Open-door", _ => false),
    };

    private static CompilationResult Compile(string source, bool condition = false) =>
        StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = Manifest,
            ["main.tale"] = condition ? $"=== a\n@requires {source}\n-> END" : $"=== a\n{{{source}}}\n-> END",
        }), Options);

    private static Expr Parse(string source)
    {
        var result = Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var paragraph = Assert.IsType<ParagraphTemplate>(Assert.Single(CompilerTests.Templates(result.Story!.Scenes[0])));
        return Assert.IsType<SubstitutionTemplate>(Assert.Single(paragraph.Inlines)).Value;
    }

    [Theory]
    [InlineData(" 42 ", "42")]
    [InlineData("-2147483648", "-2147483648")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("\"River Guild\"", "\"River Guild\"")]
    [InlineData("\"\"", "\"\"")]
    [InlineData("\"Joel's cat\"", "\"Joel's cat\"")]
    [InlineData("\"a\\\\b # {text} (x)\"", "\"a\\\\b # {text} (x)\"")]
    [InlineData(" \"say \\\"hi\\\"\" ", "\"say \\\"hi\\\"\"")]
    [InlineData("\"\\\\\\\"\"", "\"\\\\\\\"\"")]
    [InlineData("(\"a \\\" } # , ] )\")", "\"a \\\" } # , ] )\"")]
    [InlineData("\"雪\"", "\"雪\"")]
    [InlineData("friendly", "friendly")]
    [InlineData("player:dex", "player:dex")]
    [InlineData("faction:river", "faction:river")]
    [InlineData("faction:river:tier", "faction:river:tier")]
    [InlineData("flag:ready", "flag:ready")]
    [InlineData("var:coins", "var:coins")]
    [InlineData("var:weather", "var:weather")]
    [InlineData("player:first-name", "player:first-name")]
    [InlineData("player:last-name", "player:last-name")]
    [InlineData("player:name", "player:name")]
    [InlineData("player:pronoun", "player:pronoun")]
    [InlineData("player:pronoun:subject", "player:pronoun")]
    [InlineData("player:pronoun:object", "player:pronoun:object")]
    [InlineData("player:pronoun:possessive-pronoun:determiner", "player:pronoun:possessive-pronoun:determiner")]
    [InlineData("player:pronoun:possessive-pronoun:independent", "player:pronoun:possessive-pronoun:independent")]
    [InlineData("player:pronoun:reflexive", "player:pronoun:reflexive")]
    [InlineData("faction:river:name", "faction:river:name")]
    [InlineData("item:lantern", "item:lantern")]
    [InlineData("player has lantern", "player has lantern")]
    [InlineData("visited( a )", "visited(a)")]
    [InlineData("@CanGo", "@CanGo")]
    [InlineData("@_Can.Open-door", "@_Can.Open-door")]
    [InlineData("faction:river >= friendly", "faction:river:tier >= friendly")]
    [InlineData("friendly <= faction:river", "friendly <= faction:river:tier")]
    [InlineData("faction:river:tier == faction:river:tier", "faction:river:tier == faction:river:tier")]
    [InlineData("faction:river:name == \"River Guild\"", "faction:river:name == \"River Guild\"")]
    [InlineData("var:weather != \"sun\"", "var:weather != \"sun\"")]
    [InlineData("player:pronoun:object == \"her\"", "player:pronoun:object == \"her\"")]
    [InlineData("player has lantern and faction:river >= 3", "player has lantern and faction:river >= 3")]
    [InlineData("not (player:dex > 1)", "not player:dex > 1")]
    [InlineData("(not true) == false", "(not true) == false")]
    [InlineData("true == (not false)", "true == (not false)")]
    [InlineData("not (true and false)", "not (true and false)")]
    [InlineData("not (true or false)", "not (true or false)")]
    [InlineData("not not true", "not not true")]
    [InlineData("(true or false) and true", "(true or false) and true")]
    [InlineData("true or false and not true", "true or false and not true")]
    [InlineData("true and (false and true)", "true and (false and true)")]
    [InlineData("(true and false) and true", "true and false and true")]
    [InlineData("true or (false or true)", "true or (false or true)")]
    [InlineData("(true == false) == (false != true)", "(true == false) == (false != true)")]
    [InlineData("(true and false) == (true or false)", "(true and false) == (true or false)")]
    [InlineData("-(var:coins + 1)", "-(var:coins + 1)")]
    [InlineData("-var:coins * 2", "-var:coins * 2")]
    [InlineData("-(var:coins * 2)", "-(var:coins * 2)")]
    [InlineData("--var:coins", "--var:coins")]
    [InlineData("-(1)", "-1")]
    [InlineData("1 + 2 * 3 > -var:coins and not false or @CanGo", "1 + 2 * 3 > -var:coins and not false or @CanGo")]
    [InlineData("(1 + 2) * (3 - 4)", "(1 + 2) * (3 - 4)")]
    [InlineData("2147483647 + (1 + -1)", "2147483647 + (1 + -1)")]
    public void Every_node_and_representative_grouping_round_trips_with_exact_text(string source, string expected)
    {
        var parsed = Parse(source);
        var formatted = ExpressionFormatter.Format(parsed);
        Assert.Equal(expected, formatted);
        Assert.Equal(parsed, Parse(formatted));
        if (parsed.Type == ExprType.Bool)
        {
            var condition = Compile(formatted, condition: true);
            Assert.True(condition.Success, string.Join("\n", condition.Diagnostics));
            Assert.Equal(parsed, condition.Story!.Scenes[0].Requires);
        }
    }

    public static IEnumerable<object[]> OperatorBoundaries()
    {
        string[] arithmetic = ["+", "-", "*", "/"];
        foreach (var parent in arithmetic)
        {
            foreach (var child in arithmetic)
            {
                yield return [$"(var:coins {child} 2) {parent} 3"];
                yield return [$"var:coins {parent} (2 {child} 3)"];
            }
        }

        foreach (var comparison in new[] { "==", "!=", "<", "<=", ">", ">=" })
        {
            yield return [$"var:coins + 1 {comparison} -player:dex * 2"];
            yield return [$"not var:coins {comparison} 2"];
        }

        foreach (var parent in new[] { "and", "or" })
        {
            foreach (var child in new[] { "and", "or" })
            {
                yield return [$"(true {child} false) {parent} @CanGo"];
                yield return [$"true {parent} (false {child} @CanGo)"];
            }
        }
    }

    [Theory]
    [MemberData(nameof(OperatorBoundaries))]
    public void All_operator_precedence_and_associativity_boundaries_preserve_structure(string source)
    {
        var parsed = Parse(source);
        Assert.Equal(parsed, Parse(ExpressionFormatter.Format(parsed)));
    }

    [Theory]
    [InlineData("1 + (2 + 3)", "1 + (2 + 3)")]
    [InlineData("1 * (2 * 3)", "1 * (2 * 3)")]
    [InlineData("1 - (2 - 3)", "1 - (2 - 3)")]
    [InlineData("1 / (2 / 3)", "1 / (2 / 3)")]
    [InlineData("(1 + 2) + 3", "1 + 2 + 3")]
    [InlineData("(1 * 2) * 3", "1 * 2 * 3")]
    [InlineData("1 * -2", "1 * -2")]
    [InlineData("-(-var:coins)", "--var:coins")]
    [InlineData("not (not (true == false))", "not not true == false")]
    public void Parentheses_are_only_emitted_where_required_to_preserve_structure(string source, string expected)
    {
        var parsed = Parse(source);
        Assert.Equal(expected, ExpressionFormatter.Format(parsed));
        Assert.Equal(parsed, Parse(expected));
    }

    [Fact]
    public void Node_coverage_matches_the_complete_builtin_expression_model()
    {
        string[] representatives =
        [
            "1", "true", "\"text\"", "friendly", "player:dex", "faction:river", "faction:river:tier",
            "flag:ready", "var:coins", "var:weather", "player:name", "player:pronoun", "faction:river:name",
            "item:lantern", "player has lantern", "visited(a)", "@CanGo", "not true", "1 + 2",
        ];
        var covered = representatives.Select(source => Parse(source).GetType()).ToHashSet();
        var nodes = typeof(Expr).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(Expr)) && !type.IsAbstract);
        Assert.Equal(nodes.OrderBy(type => type.Name), covered.OrderBy(type => type.Name));
    }

    [Theory]
    [InlineData(UnaryOp.Negate, 1)]
    [InlineData(UnaryOp.Negate, int.MinValue)]
    [InlineData(UnaryOp.Negate, int.MaxValue)]
    public void Constructed_numeric_negations_normalize_without_changing_meaning(UnaryOp op, int number)
    {
        var expression = new UnaryExpr(op, new NumberLiteral(number));
        var reparsed = Parse(ExpressionFormatter.Format(expression));
        Assert.IsType<NumberLiteral>(reparsed);
        var story = Compile("true").Story!;
        var context = EvaluationContext.For(GameState.CreateInitial(story.World, 1));
        Assert.Equal(ExpressionEvaluator.Evaluate(expression, context), ExpressionEvaluator.Evaluate(reparsed, context));
    }

    [Fact]
    public void Saturation_sensitive_grouping_preserves_the_result()
    {
        var expression = Parse("2147483647 + (1 + -1)");
        var flat = Parse("2147483647 + 1 + -1");
        var story = Compile("true").Story!;
        var context = EvaluationContext.For(GameState.CreateInitial(story.World, 1));
        Assert.Equal(int.MaxValue, ExpressionEvaluator.EvaluateNumber(Parse(ExpressionFormatter.Format(expression)), context));
        Assert.Equal(int.MaxValue - 1, ExpressionEvaluator.EvaluateNumber(flat, context));
    }

    [Fact]
    public void Formatting_is_culture_independent()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal("-2147483648", ExpressionFormatter.Format(new NumberLiteral(int.MinValue)));
            Assert.Equal("\"a\\\\b\"", ExpressionFormatter.Format(new TextLiteral("a\\b")));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Host_built_quoted_text_round_trips_even_when_nested()
    {
        var expression = new BinaryExpr(BinaryOp.Equal, new TextVarRef(new("weather")), new TextLiteral("a\"b"));
        var formatted = ExpressionFormatter.Format(expression);
        Assert.Equal("""var:weather == "a\"b" """.TrimEnd(), formatted);
        Assert.Equal(expression, Parse(formatted));
    }

    [Theory]
    [InlineData("\"\\\\n\"", "\\n")]
    [InlineData("\"\\\\u0041\"", "\\u0041")]
    [InlineData("\"\\\\\\\"\"", "\\\"")]
    [InlineData("\"\\\\\"", "\\")]
    [InlineData("\"\\\"\"", "\"")]
    public void Escapes_decode_exactly_once_and_format_canonically(string source, string value)
    {
        var literal = Assert.IsType<TextLiteral>(Parse(source));
        Assert.Equal(value, literal.Value);
        Assert.Equal(source, ExpressionFormatter.Format(literal));
    }

    [Fact]
    public void Invalid_inputs_do_not_produce_success_shaped_text()
    {
        Expr[] invalid =
        [
            new NamedPredicate("Can..Go"),
            new Visited(new("bad scene")),
            new StatRef(EntityId.Player, new("name")),
            new StatRef(new("faction"), new("dex")),
            new NameRef(EntityId.Player, (NamePart)99),
            new PronounRef(EntityId.Player, (PronounForm)99),
            new UnaryExpr((UnaryOp)99, new NumberLiteral(1)),
            new BinaryExpr((BinaryOp)99, new NumberLiteral(1), new NumberLiteral(2)),
            new UnaryExpr(UnaryOp.Not, new NumberLiteral(1)),
            new BinaryExpr(BinaryOp.Add, new BoolLiteral(true), new NumberLiteral(1)),
            new BinaryExpr(BinaryOp.Less, new TextLiteral("a"), new TextLiteral("b")),
            new BinaryExpr(BinaryOp.Equal, new NumberLiteral(1), new TextLiteral("1")),
        ];
        foreach (var expression in invalid)
        {
            Assert.Throws<ArgumentException>(() => ExpressionFormatter.Format(expression));
        }

        Assert.Throws<ArgumentNullException>(() => ExpressionFormatter.Format(null!));
        Assert.Throws<NotSupportedException>(() => ExpressionFormatter.Format(new ExtensionExpr()));
    }

    private sealed record ExtensionExpr : Expr
    {
        public override ExprType Type => ExprType.Bool;
    }
}
