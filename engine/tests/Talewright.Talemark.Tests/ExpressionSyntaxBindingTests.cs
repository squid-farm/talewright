using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Parsing;

namespace Talewright.Talemark.Tests;

public sealed class ExpressionSyntaxBindingTests
{
    [Fact]
    public void One_syntax_tree_can_bind_against_different_variable_types()
    {
        const string text = "var:score";
        var parseDiagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseExpression(Lexer.Tokenize(text), text.Length,
            offset => new SourceLocation("main.tale", 1, offset + 1), parseDiagnostics);

        Assert.False(syntax.HasErrors);
        Assert.Empty(parseDiagnostics.Items);

        var integerDiagnostics = new DiagnosticBag();
        var integer = ExpressionBinder.Bind(syntax, Symbols(textVariable: false), integerDiagnostics,
            offset => new SourceLocation("main.tale", 1, offset + 1));
        var textDiagnostics = new DiagnosticBag();
        var textValue = ExpressionBinder.Bind(syntax, Symbols(textVariable: true), textDiagnostics,
            offset => new SourceLocation("main.tale", 1, offset + 1));

        Assert.IsType<VarRef>(integer);
        Assert.IsType<TextVarRef>(textValue);
        Assert.Empty(integerDiagnostics.Items);
        Assert.Empty(textDiagnostics.Items);
    }

    [Fact]
    public void Syntax_parsing_does_not_resolve_names_but_binding_reports_semantic_errors()
    {
        const string text = "missing:name";
        var diagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseExpression(Lexer.Tokenize(text), text.Length,
            offset => new SourceLocation("main.tale", 1, offset + 1), diagnostics);

        Assert.False(syntax.HasErrors);
        Assert.Empty(diagnostics.Items);

        Assert.Null(ExpressionBinder.Bind(syntax, Symbols(textVariable: false), diagnostics,
            offset => new SourceLocation("main.tale", 1, offset + 1)));
        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Equal("TW2001", diagnostic.Code);
        Assert.Equal(new SourceLocation("main.tale", 1, 1), diagnostic.Location);
    }

    [Theory]
    [InlineData("player:")]
    [InlineData("(")]
    [InlineData("1 +")]
    public void Incomplete_expression_syntax_retains_an_error_node(string text)
    {
        var diagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseExpression(Lexer.Tokenize(text), text.Length,
            offset => new SourceLocation("main.tale", 1, offset + 1), diagnostics);

        Assert.True(syntax.HasErrors);
        Assert.IsType<ErrorExpressionNode>(syntax.Root);
        Assert.Single(diagnostics.Items);
        Assert.Null(ExpressionBinder.Bind(syntax, Symbols(textVariable: false), new DiagnosticBag(),
            offset => new SourceLocation("main.tale", 1, offset + 1)));
    }

    [Fact]
    public void Invalid_modifier_arguments_are_syntax_errors_not_binding_errors()
    {
        const string text = "player#one-many(\"singular\", plural)";
        var diagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseSubstitution(Lexer.Tokenize(text), text.Length,
            offset => new SourceLocation("main.tale", 1, offset + 1), diagnostics);

        Assert.True(syntax.HasErrors);
        Assert.Contains(diagnostics.Items, diagnostic => diagnostic.Code == "TW2006");
        Assert.Null(ExpressionBinder.Bind(syntax, Symbols(textVariable: false), new DiagnosticBag(),
            offset => new SourceLocation("main.tale", 1, offset + 1)));
    }

    [Theory]
    [InlineData("1 + 2", "(Add 1 2)")]
    [InlineData("3 - 2", "(Subtract 3 2)")]
    [InlineData("2 * 3", "(Multiply 2 3)")]
    [InlineData("6 / 3", "(Divide 6 3)")]
    [InlineData("1 == 2", "(Equal 1 2)")]
    [InlineData("1 != 2", "(NotEqual 1 2)")]
    [InlineData("1 < 2", "(Less 1 2)")]
    [InlineData("1 <= 2", "(LessOrEqual 1 2)")]
    [InlineData("1 > 2", "(Greater 1 2)")]
    [InlineData("1 >= 2", "(GreaterOrEqual 1 2)")]
    [InlineData("true and false", "(And True False)")]
    [InlineData("true or false", "(Or True False)")]
    [InlineData("not true", "(Not True)")]
    [InlineData("-var:score", "(Negate score)")]
    [InlineData("1 + 2 * 3", "(Add 1 (Multiply 2 3))")]
    [InlineData("-var:score * 2", "(Multiply (Negate score) 2)")]
    [InlineData("10 - 4 - 3", "(Subtract (Subtract 10 4) 3)")]
    [InlineData("8 / 4 / 2", "(Divide (Divide 8 4) 2)")]
    [InlineData("1 + 2 < 4", "(Less (Add 1 2) 4)")]
    [InlineData("not 1 < 2", "(Not (Less 1 2))")]
    [InlineData("true or false and not false", "(Or True (And False (Not False)))")]
    [InlineData("true and false or true", "(Or (And True False) True)")]
    [InlineData("true and true and false", "(And (And True True) False)")]
    [InlineData("(true or false) and true", "(And (Or True False) True)")]
    public void Every_operator_binds_with_its_precedence_and_associativity(string text, string expected)
    {
        var diagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseExpression(Lexer.Tokenize(text), text.Length, Locate, diagnostics);
        var bound = ExpressionBinder.Bind(syntax, Symbols(textVariable: false), diagnostics, Locate);

        Assert.Empty(diagnostics.Items);
        Assert.Equal(expected, Shape(Assert.IsAssignableFrom<Expr>(bound)));
    }

    [Fact]
    public void Every_assignment_operator_binds_to_its_typed_operation()
    {
        const string text = "var:score = 1; var:score += 2; var:score -= 3";
        var diagnostics = new DiagnosticBag();
        var syntax = ExpressionSyntaxParser.ParseEffects(Lexer.Tokenize(text), text.Length, Locate, diagnostics);
        var effects = ExpressionBinder.Bind(syntax, Symbols(textVariable: false), diagnostics, Locate);

        Assert.Empty(diagnostics.Items);
        Assert.Equal([AssignOp.Set, AssignOp.Add, AssignOp.Subtract],
            Assert.IsType<List<Effect>>(effects).Cast<ChangeVariable>().Select(effect => effect.Op));
    }

    [Fact]
    public void Unknown_operators_fail_instead_of_binding_as_another_operator()
    {
        var span = new ExpressionSpan(0, 1);
        var one = new NumberExpressionNode(1, span);
        var binary = new ExpressionSyntaxTree(Lexer.Tokenize("1"),
            new BinaryExpressionNode((BinaryOp)99, one, one, span, span), false);
        var unary = new ExpressionSyntaxTree(Lexer.Tokenize("1"), new UnaryExpressionNode((UnaryOp)99, one, span, span), false);

        Assert.Throws<InvalidOperationException>(() =>
            ExpressionBinder.Bind(binary, Symbols(textVariable: false), new DiagnosticBag(), Locate));
        Assert.Throws<InvalidOperationException>(() =>
            ExpressionBinder.Bind(unary, Symbols(textVariable: false), new DiagnosticBag(), Locate));
    }

    private static SourceLocation Locate(int offset) => new("main.tale", 1, offset + 1);

    private static string Shape(Expr expression) => expression switch
    {
        BinaryExpr binary => $"({binary.Op} {Shape(binary.Left)} {Shape(binary.Right)})",
        UnaryExpr unary => $"({unary.Op} {Shape(unary.Operand)})",
        NumberLiteral number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BoolLiteral boolean => boolean.Value ? "True" : "False",
        VarRef variable => variable.Variable.Value,
        _ => expression.GetType().Name,
    };

    private static StorySymbols Symbols(bool textVariable)
    {
        var options = CompilerOptions.Default;
        var score = new VarId("score");
        var numbers = textVariable ? new Dictionary<VarId, int>() : new Dictionary<VarId, int> { [score] = 0 };
        var text = textVariable ? new Dictionary<VarId, string> { [score] = "" } : new Dictionary<VarId, string>();
        var world = new WorldDefinition(options.Rulesets["generic"](), [], [], [], [], numbers, text);
        return new StorySymbols
        {
            World = world,
            Flags = new HashSet<FlagId>(),
            Variables = new HashSet<VarId> { score },
            Scenes = new HashSet<SceneId>(),
            Predicates = options.Predicates,
            Modifiers = options.Modifiers,
        };
    }
}
